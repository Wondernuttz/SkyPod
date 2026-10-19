using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using Skypod.Core;

namespace Skypod.Studio;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly LibraryStore _libraryStore = new();
    private readonly ModOutputBuilder _outputBuilder = new(new AudioConverter(new ProcessRunner()));
    private readonly SoundtrackWorldIndexer _worldIndexer = new();
    private readonly System.Media.SoundPlayer _soundPlayer = new();
    private CancellationTokenSource? _previewCancellation;
    private readonly SemaphoreSlim _routingSaveGate = new(1, 1);
    private readonly string _recentLibraryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Skypod",
        "last-library.txt");
    private CancellationTokenSource? _buildCancellation;
    private string? _libraryPath;
    private string _libraryName = "My Skypod";
    private string _outputDirectory;
    private ModManagerKind _selectedModManager;
    private string _gameDataDirectory;
    private string _statusText = "Ready. Drop some music in here.";
    private double _buildPercentage;
    private int _bitrateKbps = 192;
    private bool _preserveExistingOutput = true;
    private bool _generatePlugin = true;
    private bool _isDirty;
    private readonly List<CatalogTrackRoutingOverride> _catalogRoutingOverrides = [];
    private IReadOnlyList<CatalogTrackRow> _catalogTracks = [];
    private IReadOnlyList<CatalogTrackRow> _visibleCatalogTracks = [];
    private SoundtrackWorldIndex _worldIndex = SoundtrackWorldIndex.Empty;
    private IReadOnlyList<RoutingTargetDefinition> _placeTargetDefinitions = [];
    private IReadOnlyList<RoutingTargetDefinition> _musicTypeTargetDefinitions = [];
    private IReadOnlyList<RoutingTargetItem> _routingTargets = [];
    private IReadOnlyList<string> _currentGameRoutingDetails = [];
    private MusicTrack? _selectedImportedTrack;
    private CatalogTrackRow? _selectedCatalogTrack;
    private AutomaticPlaybackMode _selectedRoutingMode = AutomaticPlaybackMode.CategoryDefault;
    private string _routingSearch = string.Empty;
    private string _catalogSearch = string.Empty;
    private string _worldIndexStatus = "Load-order places have not been scanned yet.";
    private bool _catalogTabActive;
    private bool _isRefreshingWorldIndex;
    private bool _showAdvancedPlaces;
    private bool _syncingRoutingMode;
    private bool _openingMapPicker;
    private int _routingSaveVersion;
    private SoundtrackPackMetadata _pack = new();
    private bool _authorOperation;
    public bool CanEditLibrary => !_authorOperation && _buildCancellation is null;

    public MainWindow()
    {
        _selectedModManager = ModOrganizerDetector.IsRunningUnderUsvfs()
            ? ModManagerKind.ModOrganizer2
            : ModManagerKind.Vortex;
        _gameDataDirectory = SkyrimVrDataLocator.TryFindDataFolder() ?? string.Empty;
        _outputDirectory = _selectedModManager == ModManagerKind.ModOrganizer2 &&
                           Directory.Exists(@"C:\SkyrimVRmods\mods")
            ? @"C:\SkyrimVRmods\mods\Skypod Output"
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Skypod Output");

        InitializeComponent();
        DataContext = this;
        Loaded += MainWindow_Loaded;
        RefreshRuntimeStatus();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<MusicTrack> Tracks { get; } = [];
    public IReadOnlyList<TrackCategoryOption> CategoryOptions { get; } = Enum.GetValues<TrackCategory>()
        .Select(category => new TrackCategoryOption(category, category.GetDisplayName()))
        .ToArray();
    public IReadOnlyList<ModManagerOption> ModManagerOptions { get; } =
    [
        new(ModManagerKind.ModOrganizer2, "Mod Organizer 2"),
        new(ModManagerKind.Vortex, "Vortex")
    ];
    public IReadOnlyList<AutomaticPlaybackModeOption> AutomaticPlaybackModes { get; } =
    [
        new(AutomaticPlaybackMode.CategoryDefault, "Anywhere", "Play anywhere that matches the song category."),
        new(AutomaticPlaybackMode.BossEncounters, "Boss Fights", "Choose individual boss encounters."),
        new(AutomaticPlaybackMode.SpecificPlaces, "Specific Location", "Open the Atlas and choose exact regions, cities, interiors, homes, dungeons, or mod-added places.")
    ];

    public IReadOnlyList<CatalogTrackRow> VisibleCatalogTracks
    {
        get => _visibleCatalogTracks;
        private set => SetField(ref _visibleCatalogTracks, value);
    }

    public IReadOnlyList<RoutingTargetItem> RoutingTargets
    {
        get => _routingTargets;
        private set => SetField(ref _routingTargets, value);
    }

    public IReadOnlyList<string> CurrentGameRoutingDetails
    {
        get => _currentGameRoutingDetails;
        private set => SetField(ref _currentGameRoutingDetails, value);
    }

    public string CatalogSearch
    {
        get => _catalogSearch;
        set
        {
            if (SetField(ref _catalogSearch, value)) FilterCatalogTracks();
        }
    }

    public string RoutingSearch
    {
        get => _routingSearch;
        set
        {
            if (SetField(ref _routingSearch, value)) RefreshRoutingTargets();
        }
    }

    public AutomaticPlaybackMode SelectedRoutingMode
    {
        get => _selectedRoutingMode;
        set
        {
            if (!SetField(ref _selectedRoutingMode, value)) return;
            if (!_syncingRoutingMode)
            {
                ApplyRoutingMode(value);
            }
            RefreshRoutingTargets();
            NotifyRoutingEditorChanged();
        }
    }

    public TrackCategory SelectedRoutingCategory
    {
        get => _selectedImportedTrack?.Category ?? _selectedCatalogTrack?.Track.Category ?? TrackCategory.ExplorationDay;
        set
        {
            if (_catalogTabActive || _selectedImportedTrack is null ||
                _selectedImportedTrack.Category == value) return;
            _selectedImportedTrack.Category = value;
            TrackGrid.Items.Refresh();
            OnPropertyChanged();
            OnPropertyChanged(nameof(RoutingModeExplanation));
            QueueRoutingSave($"Category set to {value.GetDisplayName()}");
        }
    }

    public string SelectedRoutingTrackTitle => CurrentTrackTitle ?? "No song selected";
    public string SelectedRoutingTrackSource => CurrentTrackSource ?? "Choose an imported track or an entry from Active Soundtrack.";
    public bool RoutingEditorEnabled => CurrentTrackId is not null;
    public string LocationActionText => RoutingEditorEnabled
        ? "Click here to make it play at a location  ↗" : "Select a song to choose its location";
    public string BossActionText => RoutingEditorEnabled ? "Choose the fights for this song" : "Select a song to choose bosses";
    public string BossSelectionText
    {
        get
        {
            var ids = CurrentRoutingRule()?.SelectedBossIds ?? [];
            if (ids.Count == 0) return "No boss fights assigned.";
            var names = ids.Take(3).Select(id => _worldIndex.Bosses.FirstOrDefault(boss => boss.Id.Equals(id, StringComparison.OrdinalIgnoreCase))?.Name ?? "Saved encounter");
            return $"{ids.Count} assigned: {string.Join(", ", names)}" + (ids.Count > 3 ? $" and {ids.Count - 3} more" : "");
        }
    }
    public bool CategoryEditorEnabled => !_catalogTabActive && _selectedImportedTrack is not null;
    public Visibility RoutingPickerVisibility => RoutingEditorEnabled &&
        SelectedRoutingMode is AutomaticPlaybackMode.SpecificPlaces or AutomaticPlaybackMode.ExactPlaylistAdvanced
        ? Visibility.Visible
        : Visibility.Collapsed;
    public Visibility CurrentGameRoutingVisibility => _catalogTabActive && _selectedCatalogTrack is not null
        ? Visibility.Visible
        : Visibility.Collapsed;
    public string RoutingTargetHeader => SelectedRoutingMode == AutomaticPlaybackMode.SpecificPlaces
        ? "SAFE PLACES AND AREAS"
        : "EXACT MUSIC PLAYLISTS";
    public string RoutingSelectionText
    {
        get
        {
            var rule = CurrentRoutingRule();
            var selected = rule?.SelectedPlaceIds.Count ?? 0;
            return selected == 0
                ? "No locations selected yet"
                : $"{selected} precise location{(selected == 1 ? string.Empty : "s")} selected";
        }
    }
    public string RoutingModeExplanation => GetRoutingModeExplanation();
    public string WorldIndexStatus
    {
        get => _worldIndexStatus;
        private set => SetField(ref _worldIndexStatus, value);
    }
    public bool CanRefreshWorldIndex => !_isRefreshingWorldIndex;
    public bool ShowAdvancedPlaces
    {
        get => _showAdvancedPlaces;
        set
        {
            if (SetField(ref _showAdvancedPlaces, value)) RefreshRoutingTargets();
        }
    }
    public Visibility SpecificPlacesOptionsVisibility => SelectedRoutingMode == AutomaticPlaybackMode.SpecificPlaces
        ? Visibility.Visible
        : Visibility.Collapsed;
    public string CatalogCountText => $"{VisibleCatalogTracks.Count} of {_catalogTracks.Count} tracks";
    public string CurrentCatalogConditionsText => _selectedCatalogTrack?.ConditionsText ?? string.Empty;

    public string LibraryName
    {
        get => _libraryName;
        set => SetField(ref _libraryName, value, markDirty: true);
    }

    public string OutputDirectory
    {
        get => _outputDirectory;
        set => SetField(ref _outputDirectory, value);
    }

    public ModManagerKind SelectedModManager
    {
        get => _selectedModManager;
        set
        {
            if (!SetField(ref _selectedModManager, value)) return;
            OnPropertyChanged(nameof(VortexSettingsVisibility));
            OnPropertyChanged(nameof(ModManagerHelpText));
            OnPropertyChanged(nameof(RefreshManagerButtonText));
            RefreshRuntimeStatus();
        }
    }

    public string GameDataDirectory
    {
        get => _gameDataDirectory;
        set
        {
            if (SetField(ref _gameDataDirectory, value)) RefreshRuntimeStatus();
        }
    }

    public Visibility VortexSettingsVisibility => SelectedModManager == ModManagerKind.Vortex
        ? Visibility.Visible
        : Visibility.Collapsed;

    public string ModManagerHelpText => SelectedModManager == ModManagerKind.Vortex
        ? "Deploy mods in Vortex before scanning or building. Studio reads the physical winning files, then creates a sibling -Vortex.zip for installation."
        : "Launch Studio through MO2 so it reads the active profile's virtual files and winning load order.";

    public string RefreshManagerButtonText => SelectedModManager == ModManagerKind.Vortex
        ? "REFRESH FROM DEPLOYED VORTEX MODS"
        : "REFRESH FROM ACTIVE MO2";

    public string StatusText
    {
        get => _statusText;
        set => SetField(ref _statusText, value);
    }

    public double BuildPercentage
    {
        get => _buildPercentage;
        set => SetField(ref _buildPercentage, value);
    }

    public int BitrateKbps
    {
        get => _bitrateKbps;
        set => SetField(ref _bitrateKbps, value);
    }

    public bool PreserveExistingOutput
    {
        get => _preserveExistingOutput;
        set => SetField(ref _preserveExistingOutput, value);
    }

    public bool GeneratePlugin
    {
        get => _generatePlugin;
        set
        {
            if (SetField(ref _generatePlugin, value)) RefreshRuntimeStatus();
        }
    }

    public string TrackCountText => $"{Tracks.Count} track{(Tracks.Count == 1 ? string.Empty : "s")}";
    public string RuntimeStatus { get; private set; } = "Checking tools";
    public Brush RuntimeStatusBrush { get; private set; } = Brushes.Goldenrod;

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MainWindow_Loaded;
        await LoadActiveSoundtrackAsync();
        await LoadWorldIndexCacheAsync();

        var candidate = FindStartupLibrary();
        if (candidate is null) return;

        try
        {
            await LoadLibraryAsync(candidate);
            StatusText = $"Reopened {Tracks.Count} tracks from {Path.GetFileName(candidate)}.";
        }
        catch (Exception exception)
        {
            StatusText = $"Could not reopen {Path.GetFileName(candidate)}: {exception.Message}";
        }
    }

    private void NewLibrary_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscardChanges()) return;
        Tracks.Clear();
        _catalogRoutingOverrides.Clear();
        _libraryPath = null;
        _libraryName = "My Skypod";
        _pack = new();
        _isDirty = false;
        StatusText = "New library ready.";
        NotifyLibraryChanged();
    }

    private async void OpenLibrary_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscardChanges()) return;
        var dialog = new OpenFileDialog
        {
            Title = "Open Skypod Library",
            Filter = "Skypod Library (*.skypod.json)|*.skypod.json|JSON (*.json)|*.json"
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            await LoadLibraryAsync(dialog.FileName);
            StatusText = $"Loaded {Tracks.Count} tracks from {Path.GetFileName(dialog.FileName)}.";
        }
        catch (Exception exception)
        {
            ShowError("Could not open that library", exception);
        }
    }

    private async void SaveLibrary_Click(object sender, RoutedEventArgs e)
    {
        await SaveLibraryAsync(forceChoosePath: false);
    }

    private async void AuthorPack_Click(object sender, RoutedEventArgs e)
    {
        if (!CanEditLibrary) return;
        TrackGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        TrackGrid.CommitEdit(DataGridEditingUnit.Row, true);
        var author = new AuthorPackWindow(CreateLibrary()) { Owner = this };
        if (author.ShowDialog() != true) return;
        try
        {
            if (author.Action == AuthorPackAction.Import)
            {
                var open = new OpenFileDialog { Title = "Import a SkyPod author project", Filter = "SkyPod author project (*.skypod-pack.zip)|*.skypod-pack.zip|ZIP archives (*.zip)|*.zip" };
                if (open.ShowDialog(this) != true) return;
                var preview = AuthorProjectPackager.Inspect(open.FileName);
                var import = new AuthorImportWindow(preview, CreateLibrary()) { Owner = this };
                if (import.ShowDialog() != true) return;
                SetAuthorBusy(true);
                StatusText = "Checking audio and importing the author project…";
                var projectDirectory = import.ProjectDirectory;
                var originalAudioDirectory = import.OriginalAudioDirectory;
                var imported = await Task.Run(() => AuthorProjectPackager.ImportAsync(open.FileName,
                    projectDirectory, originalAudioDirectory));
                var merge = AuthorProjectPackager.Merge(CreateLibrary(), imported.Library, import.ReplaceMatching);
                ++_routingSaveVersion;
                await _routingSaveGate.WaitAsync();
                try
                {
                    Tracks.Clear();
                    foreach (var track in merge.Library.Tracks) Tracks.Add(track);
                    _catalogRoutingOverrides.Clear();
                    _catalogRoutingOverrides.AddRange(merge.Library.CatalogRoutingOverrides);
                    _libraryName = merge.Library.Name;
                    _pack = merge.Library.Pack;
                    _isDirty = true;
                    NotifyLibraryChanged();
                    RefreshRoutingEditor();
                }
                finally { _routingSaveGate.Release(); }
                StatusText = $"Project merged: {merge.Added} added, {merge.Updated} replaced, {merge.Kept} kept. Save your library, then rebuild.";
                MessageBox.Show(this, $"{StatusText}\n\nEditable project and audio:\n{Path.GetDirectoryName(imported.ProjectPath)}",
                    "Author project imported", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var player = author.Action == AuthorPackAction.Player;
            var save = new SaveFileDialog
            {
                Title = player ? "Export player soundtrack" : "Export portable author project",
                Filter = "ZIP archive (*.zip)|*.zip",
                FileName = PathSanitizer.ToSafeFileName(author.Library.Name) + "-" + PathSanitizer.ToSafeFileName(author.Library.Pack.Version) +
                    (player ? "-Player.zip" : ".skypod-pack.zip")
            };
            if (save.ShowDialog(this) != true) return;
            LibraryName = author.Library.Name;
            _pack = author.Library.Pack;
            _isDirty = true;
            if (player)
            {
                await BuildOutputAsync(save.FileName);
                return;
            }
            SetAuthorBusy(true);
            StatusText = "Exporting portable author project…";
            var snapshot = AuthorProjectPackager.Snapshot(CreateLibrary());
            var includeAudio = author.IncludeAudio;
            await Task.Run(() => AuthorProjectPackager.Export(snapshot, save.FileName, includeAudio));
            StatusText = $"Author project exported: {Path.GetFileName(save.FileName)}";
            MessageBox.Show(this, $"Portable author project is ready.\n\n{save.FileName}\n\n" +
                (includeAudio ? "Imported audio is included." : "Settings are included. Recipients select their matching original audio when importing."),
                "Author project exported", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception) { ShowError("SkyPod could not finish the author operation", exception); StatusText = "Author export/import failed. Your current library is still available."; }
        finally { SetAuthorBusy(false); }
    }

    private void SetAuthorBusy(bool busy)
    {
        _authorOperation = busy;
        BuildButton.IsEnabled = !busy;
        OnPropertyChanged(nameof(CanEditLibrary));
    }

    private void ImportTracks_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import Music",
            Multiselect = true,
            Filter = "Audio|*.aac;*.flac;*.m4a;*.mp3;*.ogg;*.opus;*.wav;*.wma;*.xwm|All files|*.*"
        };
        if (dialog.ShowDialog(this) == true) AddTracks(dialog.FileNames);
    }

    private void RemoveTracks_Click(object sender, RoutedEventArgs e)
    {
        var selected = TrackGrid.SelectedItems.Cast<MusicTrack>().ToList();
        foreach (var track in selected) Tracks.Remove(track);
        if (selected.Count > 0)
        {
            _isDirty = true;
            StatusText = $"Removed {selected.Count} track{(selected.Count == 1 ? string.Empty : "s")}.";
            NotifyLibraryChanged();
        }
    }

    private async void PreviewTrack_Click(object sender, RoutedEventArgs e)
    {
        if (TrackGrid.SelectedItem is not MusicTrack track) return;
        StopAudioPreview();
        using var cancellation = new CancellationTokenSource();
        _previewCancellation = cancellation;
        var wave = Path.Combine(Path.GetTempPath(), $"skypod-preview-{Guid.NewGuid():N}.wav");
        try
        {
            var encoder = ToolLocator.FindAudioEncoder() ?? throw new FileNotFoundException("The bundled audio encoder is missing.");
            StatusText = $"Preparing {track.Title}.";
            await new ProcessRunner().RunAsync(encoder, ["-nostdin", "-y", "-v", "error", "-i", track.SourcePath,
                "-map", "0:a:0", "-c:a", "pcm_s16le", "-ar", "44100", "-ac", "2", "-f", "wav", wave], cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            _soundPlayer.SoundLocation = wave;
            StatusText = $"Previewing {track.Title}.";
            await Task.Run(() => _soundPlayer.PlaySync(), cancellation.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StatusText = $"Preview failed: {ex.Message}"; }
        finally
        {
            if (ReferenceEquals(_previewCancellation, cancellation)) _previewCancellation = null;
            if (File.Exists(wave)) File.Delete(wave);
        }
    }

    private void StopAudioPreview()
    {
        _previewCancellation?.Cancel();
        _soundPlayer.Stop();
    }

    private void StopPreview_Click(object sender, RoutedEventArgs e)
    {
        StopAudioPreview();
        StatusText = "Preview stopped.";
    }

    private void BrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select Skypod Output Folder",
            InitialDirectory = Directory.Exists(OutputDirectory) ? OutputDirectory : Path.GetDirectoryName(OutputDirectory)
        };
        if (dialog.ShowDialog(this) == true) OutputDirectory = dialog.FolderName;
    }

    private void BrowseGameData_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select Skyrim VR Data Folder",
            InitialDirectory = Directory.Exists(GameDataDirectory) ? GameDataDirectory : null
        };
        if (dialog.ShowDialog(this) == true) GameDataDirectory = dialog.FolderName;
    }

    private async void BuildOutput_Click(object sender, RoutedEventArgs e) => await BuildOutputAsync();

    private async Task BuildOutputAsync(string? playerArchivePath = null)
    {
        if (!CanEditLibrary) return;
        TrackGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        TrackGrid.CommitEdit(DataGridEditingUnit.Row, true);
        StopAudioPreview();
        BuildButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        BuildPercentage = 0;
        _buildCancellation = new CancellationTokenSource();
        OnPropertyChanged(nameof(CanEditLibrary));

        try
        {
            ++_routingSaveVersion;
            await _routingSaveGate.WaitAsync();
            _routingSaveGate.Release();
            var library = AuthorProjectPackager.Snapshot(CreateLibrary());
            var settings = new BuildSettings
            {
                OutputDirectory = OutputDirectory,
                BitrateKbps = BitrateKbps,
                PreserveExistingOutput = PreserveExistingOutput,
                GeneratePlugin = playerArchivePath is not null || GeneratePlugin,
                ModManager = SelectedModManager,
                RequireModOrganizer = SelectedModManager == ModManagerKind.ModOrganizer2,
                TargetDataFolder = SelectedModManager == ModManagerKind.Vortex
                    ? GameDataDirectory
                    : null
            };
            var progress = new Progress<BuildProgress>(value =>
            {
                BuildPercentage = value.Percentage;
                StatusText = value.Message;
            });

            var result = await _outputBuilder.BuildAsync(library, settings, progress, _buildCancellation.Token);
            var vortexArchivePath = playerArchivePath is not null
                ? await Task.Run(() => PlayerSoundtrackPackager.Create(result.OutputDirectory, playerArchivePath, library.Pack))
                : SelectedModManager == ModManagerKind.Vortex
                    ? await Task.Run(() => VortexOutputPackager.Create(result.OutputDirectory)) : null;
            var builtProject = Path.Combine(result.OutputDirectory, "SKSE", "Plugins", "Skypod.project.skypod.json");
            if (File.Exists(builtProject))
            {
                _libraryPath ??= builtProject;
                if (!Path.GetFullPath(_libraryPath).Equals(Path.GetFullPath(builtProject), StringComparison.OrdinalIgnoreCase))
                    await _libraryStore.SaveAsync(library, _libraryPath);
                RememberLibraryPath(_libraryPath);
                _isDirty = false;
            }
            BuildPercentage = 100;
            StatusText = $"Built {result.CopiedTracks + result.ConvertedTracks} imported tracks and cataloged {result.CatalogTracks} active soundtrack entries in {result.Elapsed.TotalSeconds:F1}s.";
            MessageBox.Show(this,
                $"Skypod output is ready.\n\n{result.OutputDirectory}\n" +
                (vortexArchivePath is null ? string.Empty : $"{(playerArchivePath is null ? "Vortex archive" : "Player soundtrack ZIP")}: {vortexArchivePath}\n") +
                $"\nImported tracks: {result.ConvertedTracks + result.CopiedTracks}\nActive soundtrack entries: {result.CatalogTracks}\nPlugin: {(result.PluginPath is null ? "not generated" : Path.GetFileName(result.PluginPath))}\nWarnings: {result.PluginWarnings.Count}",
                "Skypod Build Complete", MessageBoxButton.OK, MessageBoxImage.Information);
            await LoadActiveSoundtrackAsync();
            await LoadWorldIndexCacheAsync();
        }
        catch (OperationCanceledException)
        {
            StatusText = "Build cancelled.";
        }
        catch (Exception exception)
        {
            StatusText = "Build or export failed. Check the error before using the output.";
            ShowError("Skypod could not build the output", exception);
        }
        finally
        {
            _buildCancellation.Dispose();
            _buildCancellation = null;
            OnPropertyChanged(nameof(CanEditLibrary));
            BuildButton.IsEnabled = true;
            CancelButton.IsEnabled = false;
        }
    }

    private void CancelBuild_Click(object sender, RoutedEventArgs e) => _buildCancellation?.Cancel();

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (!CanEditLibrary) return;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) AddTracks(paths);
    }

    private void TrackGrid_CellEditEnding(object sender, System.Windows.Controls.DataGridCellEditEndingEventArgs e)
    {
        _isDirty = true;
    }

    private void TrackCategory_DropDownClosed(object sender, EventArgs e)
    {
        if (sender is not ComboBox { DataContext: MusicTrack track } || !IsLoaded) return;
        if (ReferenceEquals(track, _selectedImportedTrack))
        {
            OnPropertyChanged(nameof(SelectedRoutingCategory));
            OnPropertyChanged(nameof(RoutingModeExplanation));
        }
        TrackGrid.Items.Refresh();
        QueueRoutingSave($"Category set to {track.Category.GetDisplayName()}");
    }

    private void TrackRoute_DropDownClosed(object sender, EventArgs e)
    {
        if (sender is not ComboBox { DataContext: MusicTrack track } || !IsLoaded) return;

        // Editing an inline ComboBox does not reliably select its DataGrid row.
        // Make the row and the map panel refer to the same track before applying
        // the route so the sidebar immediately reflects the dropdown choice.
        TrackGrid.SelectedItem = track;
        _selectedImportedTrack = track;
        _syncingRoutingMode = true;
        SelectedRoutingMode = track.AutomaticPlayback.Mode;
        _syncingRoutingMode = false;
        ApplyRoutingMode(track.AutomaticPlayback.Mode);
        RefreshRoutingTargets();
        NotifyRoutingEditorChanged();
    }

    private void TrackGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedImportedTrack = TrackGrid.SelectedItem as MusicTrack;
        if (!_catalogTabActive) RefreshRoutingEditor();
    }

    private void CatalogGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedCatalogTrack = CatalogGrid.SelectedItem as CatalogTrackRow;
        if (_catalogTabActive) RefreshRoutingEditor();
    }

    private void LibraryTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, LibraryTabs)) return;
        _catalogTabActive = LibraryTabs.SelectedIndex == 1;
        RefreshRoutingEditor();
    }

    private async void OpenBossPicker_Click(object sender, RoutedEventArgs e)
    {
        if (!CanEditLibrary || !RoutingEditorEnabled || _openingMapPicker) return;
        _openingMapPicker = true;
        var originalTrack = CurrentTrackId;
        try
        {
            if (_worldIndex.SchemaVersion < 5 || _worldIndex.Bosses.Count == 0)
            {
                StatusText = "Finding bosses in the active load order...";
                if (await RefreshWorldIndexAsync() is null) { StatusText = WorldIndexStatus; return; }
            }
            if (CurrentTrackId != originalTrack) { StatusText = "Song selection changed. Open Bosses again for the selected song."; return; }
            var rule = CurrentRoutingRule() ?? new PlaybackRoutingRule();
            var picker = new BossPickerWindow(_worldIndex, CurrentTrackTitle ?? "this song", rule) { Owner = this };
            if (picker.ShowDialog() != true) return;
            rule = EnsureCurrentRoutingRule();
            if (rule is null) return;
            rule.SelectedBossIds = picker.SelectedBossIds.ToList();
            if (!picker.KeepExistingRouting) rule.Mode = AutomaticPlaybackMode.BossEncounters;
            else if (rule.Mode is AutomaticPlaybackMode.BossEncounters or AutomaticPlaybackMode.ManualOnly)
                rule.Mode = rule.SelectedPlaceIds.Count > 0 ? AutomaticPlaybackMode.SpecificPlaces : AutomaticPlaybackMode.CategoryDefault;
            TrackGrid.Items.Refresh();
            RefreshRoutingEditor();
            QueueRoutingSave($"{rule.SelectedBossIds.Count} boss fights assigned. Rebuild SkyPod Output to use them in game.");
        }
        catch (Exception exception) { ShowError("Could not open boss encounters", exception); }
        finally { _openingMapPicker = false; }
    }

    private async void ReloadCatalog_Click(object sender, RoutedEventArgs e)
    {
        await LoadActiveSoundtrackAsync();
        await LoadWorldIndexCacheAsync();
    }

    private async void RefreshWorldIndex_Click(object sender, RoutedEventArgs e) =>
        await RefreshWorldIndexAsync();

    private async Task<SoundtrackWorldIndex?> RefreshWorldIndexAsync()
    {
        if (_isRefreshingWorldIndex) return _worldIndex;
        _isRefreshingWorldIndex = true;
        OnPropertyChanged(nameof(CanRefreshWorldIndex));
        WorldIndexStatus = SelectedModManager == ModManagerKind.Vortex
            ? "Scanning the deployed Vortex files and active Skyrim VR load order in the background."
            : "Scanning the active MO2 load order in the background. Large lists can take about a minute.";

        try
        {
            var settings = new BuildSettings
            {
                OutputDirectory = OutputDirectory,
                GeneratePlugin = true,
                ModManager = SelectedModManager,
                RequireModOrganizer = SelectedModManager == ModManagerKind.ModOrganizer2,
                TargetDataFolder = SelectedModManager == ModManagerKind.Vortex
                    ? GameDataDirectory
                    : null
            };
            var index = await Task.Run(() => _worldIndexer.Build(settings));
            ApplyWorldIndex(index);
            await SaveWorldIndexCacheAsync(index);
            WorldIndexStatus = $"Active {SelectedModManager switch { ModManagerKind.Vortex => "Vortex", _ => "MO2" }} routes refreshed: {index.Places.Count} places and {index.MusicTypes.Count} playlists. Rebuild Output to apply routing changes.";
            return index;
        }
        catch (Exception exception)
        {
            WorldIndexStatus = $"Could not refresh active mod-manager routes: {exception.Message}";
            return null;
        }
        finally
        {
            _isRefreshingWorldIndex = false;
            OnPropertyChanged(nameof(CanRefreshWorldIndex));
        }
    }

    private void ClearRoutingTargets_Click(object sender, RoutedEventArgs e)
    {
        var rule = EnsureCurrentRoutingRule();
        if (rule is null) return;

        if (SelectedRoutingMode == AutomaticPlaybackMode.SpecificPlaces)
        {
            rule.SelectedPlaceIds.Clear();
        }
        else if (SelectedRoutingMode == AutomaticPlaybackMode.ExactPlaylistAdvanced)
        {
            rule.SelectedMusicTypeFormKeys.Clear();
        }
        else
        {
            return;
        }

        QueueRoutingSave("Routing targets cleared");
        RefreshRoutingTargets();
    }

    private async void OpenMapPicker_Click(object sender, RoutedEventArgs e) =>
        await OpenMapPickerAsync(SelectedRoutingMode, revertEmptySelectionOnCancel: false);

    private async Task OpenMapPickerAsync(
        AutomaticPlaybackMode previousMode,
        bool revertEmptySelectionOnCancel)
    {
        if (_openingMapPicker || CurrentTrackId is null) return;
        _openingMapPicker = true;
        var rule = EnsureCurrentRoutingRule();
        if (rule is null)
        {
            _openingMapPicker = false;
            return;
        }

        try
        {
            // Older caches can contain place records without the map geometry added by
            // newer Studio builds. Refresh automatically instead of opening a blank Atlas.
            if (_worldIndex.Places.Count == 0 || _worldIndex.Maps.Count == 0 || _worldIndex.SchemaVersion < 4)
            {
                WorldIndexStatus = "Preparing the interactive map from the active load order…";
                await RefreshWorldIndexAsync();
            }

            var songOptions = BuildAtlasSongOptions();
            var picker = new MapPickerWindow(
                _worldIndex,
                rule.SelectedPlaceIds,
                songOptions,
                RefreshWorldIndexAsync)
            {
                Owner = this
            };
            if (picker.ShowDialog() != true)
            {
                if (revertEmptySelectionOnCancel && rule.SelectedPlaceIds.Count == 0)
                {
                    _syncingRoutingMode = true;
                    SelectedRoutingMode = previousMode;
                    _syncingRoutingMode = false;
                    ApplyRoutingMode(previousMode);
                    NotifyRoutingEditorChanged();
                }
                return;
            }

            var selectedSongIds = picker.SelectedTrackIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var option in picker.SongOptions)
            {
                var importedTrack = Tracks.FirstOrDefault(track =>
                    track.Id.ToString("D").Equals(option.TrackId, StringComparison.OrdinalIgnoreCase));
                if (importedTrack is null || !selectedSongIds.Contains(option.TrackId)) continue;
                importedTrack.Category = option.Category;
                importedTrack.AutomaticPlayback.Mode = AutomaticPlaybackMode.SpecificPlaces;
                importedTrack.AutomaticPlayback.SelectedPlaceIds.Clear();
                importedTrack.AutomaticPlayback.SelectedPlaceIds.AddRange(picker.SelectedPlaceIds);
            }

            if (_catalogTabActive && _selectedCatalogTrack is not null &&
                selectedSongIds.Contains(_selectedCatalogTrack.Id))
            {
                rule.Mode = AutomaticPlaybackMode.SpecificPlaces;
                rule.SelectedPlaceIds.Clear();
                rule.SelectedPlaceIds.AddRange(picker.SelectedPlaceIds);
            }

            QueueRoutingSave($"Atlas locations applied to {selectedSongIds.Count} song{(selectedSongIds.Count == 1 ? string.Empty : "s")}");
            RefreshRoutingEditor();
            TrackGrid.Items.Refresh();
        }
        finally
        {
            _openingMapPicker = false;
        }
    }

    private IReadOnlyList<AtlasSongOption> BuildAtlasSongOptions()
    {
        var selectedId = CurrentTrackId;
        var songs = Tracks.Select(track => new AtlasSongOption(
                track.Id.ToString("D"),
                track.Title,
                track.Artist,
                track.Category,
                track.Id.ToString("D").Equals(selectedId, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (_catalogTabActive && _selectedCatalogTrack is not null)
        {
            songs.Insert(0, new AtlasSongOption(
                _selectedCatalogTrack.Id,
                _selectedCatalogTrack.Title,
                _selectedCatalogTrack.Artist,
                _selectedCatalogTrack.Track.Category,
                true,
                isCategoryEditable: false));
        }

        return songs;
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_authorOperation)
        {
            StatusText = "Finishing the author export/import. Keep Studio open until it completes.";
            e.Cancel = true;
            return;
        }
        if (_buildCancellation is not null)
        {
            var result = MessageBox.Show(this, "A build is running. Cancel it and close Skypod?", "Skypod",
                MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
            _buildCancellation.Cancel();
        }

        if (!ConfirmDiscardChanges()) e.Cancel = true;
        StopAudioPreview();
    }

    private void AddTracks(IEnumerable<string> paths)
    {
        var files = paths.SelectMany(path => Directory.Exists(path)
                ? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                : [path])
            .Where(File.Exists)
            .Where(AudioConverter.IsSupported)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var existing = Tracks.Select(track => Path.GetFullPath(track.SourcePath)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = 0;
        foreach (var file in files)
        {
            var fullPath = Path.GetFullPath(file);
            if (!existing.Add(fullPath)) continue;
            var (artist, title) = ParseFileName(Path.GetFileNameWithoutExtension(file));
            Tracks.Add(new MusicTrack
            {
                SourcePath = fullPath,
                Artist = artist,
                Title = title,
                Category = InferCategory(fullPath)
            });
            added++;
        }

        if (added > 0)
        {
            _isDirty = true;
            StatusText = $"Imported {added} track{(added == 1 ? string.Empty : "s")}.";
            NotifyLibraryChanged();
        }
        else
        {
            StatusText = "No new supported audio files were found.";
        }
    }

    private async Task<bool> SaveLibraryAsync(bool forceChoosePath)
    {
        if (forceChoosePath || string.IsNullOrWhiteSpace(_libraryPath))
        {
            var dialog = new SaveFileDialog
            {
                Title = "Save Skypod Library",
                Filter = "Skypod Library (*.skypod.json)|*.skypod.json",
                FileName = $"{PathSanitizer.ToSafeFileName(LibraryName)}.skypod.json"
            };
            if (dialog.ShowDialog(this) != true) return false;
            _libraryPath = dialog.FileName;
        }

        try
        {
            await _libraryStore.SaveAsync(CreateLibrary(), _libraryPath);
            RememberLibraryPath(_libraryPath);
            _isDirty = false;
            StatusText = $"Saved {Path.GetFileName(_libraryPath)}.";
            return true;
        }
        catch (Exception exception)
        {
            ShowError("Could not save the library", exception);
            return false;
        }
    }

    private SkypodLibrary CreateLibrary() => new()
    {
        Name = string.IsNullOrWhiteSpace(LibraryName) ? "My Skypod" : LibraryName.Trim(),
        Tracks = Tracks.ToList(),
        CatalogRoutingOverrides = _catalogRoutingOverrides.ToList(),
        Pack = _pack
    };

    private async Task LoadLibraryAsync(string path)
    {
        var library = await _libraryStore.LoadAsync(path);
        Tracks.Clear();
        foreach (var track in library.Tracks)
        {
            track.AutomaticPlayback ??= new PlaybackRoutingRule();
            track.AutomaticPlayback.SelectedPlaceIds ??= [];
            track.AutomaticPlayback.SelectedBossIds ??= [];
            track.AutomaticPlayback.SelectedMusicTypeFormKeys ??= [];
            Tracks.Add(track);
        }
        _catalogRoutingOverrides.Clear();
        foreach (var catalogOverride in library.CatalogRoutingOverrides ?? [])
        {
            if (string.IsNullOrWhiteSpace(catalogOverride.TrackId)) continue;
            catalogOverride.AutomaticPlayback ??= new PlaybackRoutingRule();
            catalogOverride.AutomaticPlayback.SelectedPlaceIds ??= [];
            catalogOverride.AutomaticPlayback.SelectedBossIds ??= [];
            catalogOverride.AutomaticPlayback.SelectedMusicTypeFormKeys ??= [];
            _catalogRoutingOverrides.Add(catalogOverride);
        }
        _libraryName = library.Name;
        _pack = library.Pack;
        _libraryPath = Path.GetFullPath(path);
        _isDirty = false;
        RememberLibraryPath(_libraryPath);
        NotifyLibraryChanged();
    }

    private string? FindStartupLibrary()
    {
        try
        {
            if (File.Exists(_recentLibraryPath))
            {
                var remembered = File.ReadAllText(_recentLibraryPath).Trim();
                if (File.Exists(remembered)) return remembered;
            }
        }
        catch
        {
            // Fall through to the generated project and common save locations.
        }

        var generated = Path.Combine(OutputDirectory, "SKSE", "Plugins", "Skypod.project.skypod.json");
        if (File.Exists(generated)) return generated;

        var folders = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
        };
        return folders
            .Where(Directory.Exists)
            .SelectMany(folder => Directory.EnumerateFiles(folder, "*.skypod.json", SearchOption.TopDirectoryOnly))
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Select(file => file.FullName)
            .FirstOrDefault();
    }

    private void RememberLibraryPath(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_recentLibraryPath)!);
            File.WriteAllText(_recentLibraryPath, Path.GetFullPath(path));
        }
        catch
        {
            // A read-only settings directory must not block opening or building a library.
        }
    }

    private bool ConfirmDiscardChanges()
    {
        if (!_isDirty) return true;
        var result = MessageBox.Show(this, "Discard unsaved library changes?", "Skypod",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        return result == MessageBoxResult.Yes;
    }

    private void RefreshRuntimeStatus()
    {
        var hasEncoder = ToolLocator.FindAudioEncoder() is not null;
        var underModOrganizer = ModOrganizerDetector.IsRunningUnderUsvfs();

        if (GeneratePlugin && SelectedModManager == ModManagerKind.ModOrganizer2 && !underModOrganizer)
        {
            RuntimeStatus = "Run through MO2 to build ESP";
            RuntimeStatusBrush = Brushes.OrangeRed;
        }
        else if (GeneratePlugin && SelectedModManager == ModManagerKind.Vortex &&
                 !SkyrimVrDataLocator.IsValidDataFolder(GameDataDirectory))
        {
            RuntimeStatus = "Select deployed Skyrim VR Data";
            RuntimeStatusBrush = Brushes.OrangeRed;
        }
        else if (hasEncoder)
        {
            RuntimeStatus = SelectedModManager == ModManagerKind.Vortex
                ? "Vortex deployment and audio ready"
                : "MO2 and audio pipeline ready";
            RuntimeStatusBrush = Brushes.MediumSpringGreen;
        }
        else
        {
            RuntimeStatus = "Included encoder missing - extract the complete Studio ZIP";
            RuntimeStatusBrush = Brushes.Goldenrod;
        }
        OnPropertyChanged(nameof(RuntimeStatus));
        OnPropertyChanged(nameof(RuntimeStatusBrush));
    }

    private static (string Artist, string Title) ParseFileName(string fileName)
    {
        var pieces = fileName.Split(" - ", 2, StringSplitOptions.TrimEntries);
        return pieces.Length == 2 ? (pieces[0], pieces[1]) : (string.Empty, fileName);
    }

    private static TrackCategory InferCategory(string path)
    {
        var value = path.ToLowerInvariant();
        if (value.Contains("dragon", StringComparison.Ordinal)) return TrackCategory.DragonCombat;
        if (value.Contains("boss", StringComparison.Ordinal)) return TrackCategory.BossCombat;
        if (value.Contains("combat", StringComparison.Ordinal) || value.Contains("battle", StringComparison.Ordinal)) return TrackCategory.Combat;
        if (value.Contains("dungeon", StringComparison.Ordinal) || value.Contains("cave", StringComparison.Ordinal)) return TrackCategory.Dungeon;
        if (value.Contains("tavern", StringComparison.Ordinal) || value.Contains("inn", StringComparison.Ordinal)) return TrackCategory.Tavern;
        if (value.Contains("castle", StringComparison.Ordinal)) return TrackCategory.Castle;
        if (value.Contains("town", StringComparison.Ordinal)) return TrackCategory.Town;
        if (value.Contains("city", StringComparison.Ordinal)) return TrackCategory.City;
        if (value.Contains("night", StringComparison.Ordinal)) return TrackCategory.ExplorationNight;
        if (value.Contains("menu", StringComparison.Ordinal)) return TrackCategory.MainMenu;
        return TrackCategory.ExplorationDay;
    }

    private string? CurrentTrackId => _catalogTabActive
        ? _selectedCatalogTrack?.Id
        : _selectedImportedTrack?.Id.ToString("D");

    private string? CurrentTrackTitle => _catalogTabActive
        ? _selectedCatalogTrack?.Title
        : _selectedImportedTrack?.Title;

    private string? CurrentTrackSource => _catalogTabActive
        ? _selectedCatalogTrack is null
            ? null
            : $"ACTIVE SOUNDTRACK | {_selectedCatalogTrack.Provider}"
        : _selectedImportedTrack is null
            ? null
            : $"IMPORTED | {Path.GetFileName(_selectedImportedTrack.SourcePath)}";

    private PlaybackRoutingRule? CurrentRoutingRule()
    {
        if (!_catalogTabActive) return _selectedImportedTrack?.AutomaticPlayback;
        if (_selectedCatalogTrack is null) return null;
        return _catalogRoutingOverrides.FirstOrDefault(entry =>
            entry.TrackId.Equals(_selectedCatalogTrack.Id, StringComparison.OrdinalIgnoreCase))?.AutomaticPlayback;
    }

    private PlaybackRoutingRule? EnsureCurrentRoutingRule()
    {
        if (!_catalogTabActive)
        {
            if (_selectedImportedTrack is null) return null;
            return _selectedImportedTrack.AutomaticPlayback ??= new PlaybackRoutingRule();
        }

        if (_selectedCatalogTrack is null) return null;
        var existing = _catalogRoutingOverrides.FirstOrDefault(entry =>
            entry.TrackId.Equals(_selectedCatalogTrack.Id, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) return existing.AutomaticPlayback;

        var created = new CatalogTrackRoutingOverride
        {
            TrackId = _selectedCatalogTrack.Id,
            AutomaticPlayback = new PlaybackRoutingRule()
        };
        _catalogRoutingOverrides.Add(created);
        return created.AutomaticPlayback;
    }

    private void ApplyRoutingMode(AutomaticPlaybackMode mode)
    {
        if (CurrentTrackId is null) return;

        if (_catalogTabActive && mode == AutomaticPlaybackMode.CategoryDefault && (CurrentRoutingRule()?.SelectedBossIds.Count ?? 0) == 0)
        {
            _catalogRoutingOverrides.RemoveAll(entry =>
                entry.TrackId.Equals(_selectedCatalogTrack!.Id, StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            var rule = EnsureCurrentRoutingRule();
            if (rule is null) return;
            rule.Mode = mode;
        }

        TrackGrid.Items.Refresh();
        var modeName = AutomaticPlaybackModes.FirstOrDefault(option => option.Mode == mode)?.Name
                       ?? mode.ToString();
        QueueRoutingSave(mode == AutomaticPlaybackMode.CategoryDefault && _catalogTabActive
            ? "Original game or mod routing restored"
            : $"Location set to {modeName}");
    }

    private void RefreshRoutingEditor()
    {
        var rule = CurrentRoutingRule();
        _syncingRoutingMode = true;
        SelectedRoutingMode = rule?.Mode ?? AutomaticPlaybackMode.CategoryDefault;
        _syncingRoutingMode = false;
        RefreshRoutingTargets();
        RefreshCurrentGameRoutingDetails();
        OnPropertyChanged(nameof(SelectedRoutingCategory));
        NotifyRoutingEditorChanged();
    }

    private void NotifyRoutingEditorChanged()
    {
        OnPropertyChanged(nameof(BossActionText));
        OnPropertyChanged(nameof(BossSelectionText));
        OnPropertyChanged(nameof(SelectedRoutingTrackTitle));
        OnPropertyChanged(nameof(SelectedRoutingTrackSource));
        OnPropertyChanged(nameof(RoutingEditorEnabled));
        OnPropertyChanged(nameof(LocationActionText));
        OnPropertyChanged(nameof(CategoryEditorEnabled));
        OnPropertyChanged(nameof(SelectedRoutingCategory));
        OnPropertyChanged(nameof(RoutingPickerVisibility));
        OnPropertyChanged(nameof(SpecificPlacesOptionsVisibility));
        OnPropertyChanged(nameof(CurrentGameRoutingVisibility));
        OnPropertyChanged(nameof(RoutingTargetHeader));
        OnPropertyChanged(nameof(RoutingSelectionText));
        OnPropertyChanged(nameof(RoutingModeExplanation));
        OnPropertyChanged(nameof(CurrentCatalogConditionsText));
    }

    private string GetRoutingModeExplanation()
    {
        if (CurrentTrackId is null)
        {
            return "Select a row on the left. Imported tracks and discovered game or mod tracks can each have their own automatic playback rule.";
        }

        return SelectedRoutingMode switch
        {
            AutomaticPlaybackMode.CategoryDefault when _catalogTabActive =>
                "Keep this existing soundtrack entry in its original game or mod locations.",
            AutomaticPlaybackMode.CategoryDefault =>
                $"Play anywhere Skyrim uses the {_selectedImportedTrack!.Category.GetDisplayName()} category.",
            AutomaticPlaybackMode.AnywhereAmbient =>
                "Add ambient tracks to every safe ambient palette, or combat tracks to compatible combat palettes. Quest events, one-shots, menus, and silence palettes are excluded.",
            AutomaticPlaybackMode.SpecificPlaces =>
                "Use the Atlas to choose exact holds, cities, interiors, homes, dungeons, regions, DLC realms, or mod-added places.",
            AutomaticPlaybackMode.ExactPlaylistAdvanced =>
                "Choose exact MusicType records. Contextual combat, event, and unclassified mod palettes are marked ADVANCED because a bad target can interrupt scripted music. Silence targets are rejected.",
            AutomaticPlaybackMode.BossEncounters =>
                "Play only during the boss fights selected with the dragon button. Rebuild SkyPod Output after changing assignments.",
            AutomaticPlaybackMode.ManualOnly =>
                "Remove this track from Skyrim's automatic rotation while retaining its condition-free manual Play Now preview.",
            _ => string.Empty
        };
    }

    private void RefreshRoutingTargets()
    {
        if (SelectedRoutingMode is not AutomaticPlaybackMode.SpecificPlaces and
            not AutomaticPlaybackMode.ExactPlaylistAdvanced)
        {
            RoutingTargets = [];
            OnPropertyChanged(nameof(RoutingSelectionText));
            return;
        }

        var rule = CurrentRoutingRule();
        var selectedValues = SelectedRoutingMode == AutomaticPlaybackMode.SpecificPlaces
            ? (rule?.SelectedPlaceIds ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : (rule?.SelectedMusicTypeFormKeys ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var query = RoutingSearch.Trim();
        var definitions = SelectedRoutingMode == AutomaticPlaybackMode.SpecificPlaces
            ? _placeTargetDefinitions
            : _musicTypeTargetDefinitions;

        var items = definitions
            .Where(definition => SelectedRoutingMode != AutomaticPlaybackMode.SpecificPlaces ||
                                 ShowAdvancedPlaces || !definition.IsAdvanced)
            .Where(definition => query.Length == 0 || definition.SearchText.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Select(definition =>
            {
                var item = new RoutingTargetItem(
                    definition.Value,
                    definition.Name,
                    definition.Details,
                    definition.SearchText,
                    definition.Safety,
                    selectedValues.Contains(definition.Value));
                item.SelectionChanged += RoutingTarget_SelectionChanged;
                return item;
            })
            .ToArray();
        RoutingTargets = items;
        OnPropertyChanged(nameof(RoutingSelectionText));
    }

    private void RoutingTarget_SelectionChanged(object? sender, EventArgs e)
    {
        if (sender is not RoutingTargetItem item) return;
        var rule = EnsureCurrentRoutingRule();
        if (rule is null) return;

        var values = SelectedRoutingMode == AutomaticPlaybackMode.SpecificPlaces
            ? rule.SelectedPlaceIds
            : rule.SelectedMusicTypeFormKeys;
        if (item.IsSelected)
        {
            if (!values.Contains(item.Value, StringComparer.OrdinalIgnoreCase)) values.Add(item.Value);
        }
        else
        {
            values.RemoveAll(value => value.Equals(item.Value, StringComparison.OrdinalIgnoreCase));
        }

        QueueRoutingSave("Automatic playback targets updated");
        OnPropertyChanged(nameof(RoutingSelectionText));
    }

    private void ApplyWorldIndex(SoundtrackWorldIndex index)
    {
        _worldIndex = index;
        _placeTargetDefinitions = index.Places
            .Where(place => place.ConditionFormKeys.Count > 0)
            .Select(place =>
            {
                var assignments = place.Assignments
                    .Where(assignment => assignment.Safety == MusicTypeSafety.Ambient)
                    .Select(assignment => assignment.MusicTypeEditorId)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(3)
                    .ToArray();
                if (assignments.Length == 0) assignments = ["safe ambient resolver"];
                var hierarchy = new[]
                    {
                        string.IsNullOrWhiteSpace(place.WorldspaceName) ? place.WorldspaceFormKey : place.WorldspaceName,
                        place.ParentLocationFormKey
                    }
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var context = hierarchy.Length == 0
                    ? place.Plugin
                    : $"{string.Join(" > ", hierarchy)} | {place.Plugin}";
                return new RoutingTargetDefinition(
                    place.Id,
                    place.DisplayName,
                    $"{PlaceGroupLabel(place.Group, place.Kind)} | {context} | {string.Join(", ", assignments)}",
                    place.SearchText,
                    MusicTypeSafety.Ambient,
                    place.IsAdvanced,
                    place.Kind == SoundtrackPlaceKind.Area ? 0 : place.Kind == SoundtrackPlaceKind.Worldspace ? 1 : 2);
            })
            .OrderBy(definition => definition.IsAdvanced)
            .ThenBy(definition => definition.SortOrder)
            .ThenBy(definition => definition.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        _musicTypeTargetDefinitions = index.MusicTypes
            .Where(type => type.Safety is MusicTypeSafety.Ambient or MusicTypeSafety.Contextual or MusicTypeSafety.Event or MusicTypeSafety.Unknown)
            .Select(type => new RoutingTargetDefinition(
                type.FormKey,
                type.EditorId,
                $"{type.Category.GetDisplayName()} | {type.Plugin} | {type.SafetyReason}",
                type.SearchText,
                type.Safety,
                type.Safety is MusicTypeSafety.Contextual or MusicTypeSafety.Event or MusicTypeSafety.Unknown,
                type.Safety == MusicTypeSafety.Ambient ? 0 : type.Safety == MusicTypeSafety.Contextual ? 1 : type.Safety == MusicTypeSafety.Event ? 2 : 3))
            .OrderBy(definition => definition.SortOrder)
            .ThenBy(definition => definition.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        RefreshRoutingTargets();
        RefreshCurrentGameRoutingDetails();
    }

    private static string PlaceGroupLabel(string group, SoundtrackPlaceKind kind) => group.ToLowerInvariant() switch
    {
        "hold" => "HOLD",
        "city" when kind == SoundtrackPlaceKind.Worldspace => "CITY WORLDSPACE",
        "city" => "CITY",
        "dungeon" => "DUNGEON",
        "location" => "LOCATION",
        "worldspace" => "WORLDSPACE",
        "exteriorarea" => "OUTDOOR AREA",
        "regionbiome" => "BIOME REGION",
        "cell" => "CELL",
        _ => kind.ToString().ToUpperInvariant()
    };

    private void BuildFallbackMusicTypeTargets()
    {
        if (_worldIndex.MusicTypes.Count > 0) return;
        _musicTypeTargetDefinitions = _catalogTracks
            .SelectMany(row => row.Track.Playlists)
            .GroupBy(playlist => playlist.FormKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Select(playlist =>
            {
                var safety = playlist.Category switch
                {
                    TrackCategory.Combat or TrackCategory.BossCombat or TrackCategory.DragonCombat => MusicTypeSafety.Contextual,
                    TrackCategory.Special or TrackCategory.MainMenu => MusicTypeSafety.Event,
                    _ => MusicTypeSafety.Ambient
                };
                return new RoutingTargetDefinition(
                    playlist.FormKey,
                    playlist.EditorId,
                    $"{playlist.Category.GetDisplayName()} | {playlist.Plugin} | from last Output catalog",
                    $"{playlist.FormKey} {playlist.EditorId} {playlist.Plugin} {playlist.Category} {playlist.Category.GetDisplayName()}".ToLowerInvariant(),
                    safety,
                    safety is MusicTypeSafety.Contextual or MusicTypeSafety.Event,
                    safety == MusicTypeSafety.Ambient ? 0 : safety == MusicTypeSafety.Contextual ? 1 : 2);
            })
            .OrderBy(definition => definition.SortOrder)
            .ThenBy(definition => definition.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        RefreshRoutingTargets();
    }

    private async Task LoadActiveSoundtrackAsync()
    {
        var path = Path.Combine(OutputDirectory, "SKSE", "Plugins", "Skypod.library.json");
        if (!File.Exists(path))
        {
            _catalogTracks = [];
            FilterCatalogTracks();
            return;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            var document = await JsonSerializer.DeserializeAsync<RuntimeCatalogDocument>(
                stream,
                LibraryStore.CreateJsonOptions());
            _catalogTracks = (document?.Tracks ?? [])
                .Where(track => track.SourceKind.Equals("game", StringComparison.OrdinalIgnoreCase))
                .Select(track => new CatalogTrackRow(track))
                .OrderBy(row => row.Title, StringComparer.OrdinalIgnoreCase)
                .ThenBy(row => row.Provider, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            FilterCatalogTracks();
            BuildFallbackMusicTypeTargets();
        }
        catch (Exception exception)
        {
            _catalogTracks = [];
            FilterCatalogTracks();
            StatusText = $"Could not load the active soundtrack catalog: {exception.Message}";
        }
    }

    private void FilterCatalogTracks()
    {
        var query = CatalogSearch.Trim();
        VisibleCatalogTracks = query.Length == 0
            ? _catalogTracks
            : _catalogTracks.Where(track => track.SearchText.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        OnPropertyChanged(nameof(CatalogCountText));
    }

    private async Task LoadWorldIndexCacheAsync()
    {
        var path = Path.Combine(OutputDirectory, "SKSE", "Plugins", SoundtrackWorldIndexStore.FileName);
        if (!File.Exists(path))
        {
            WorldIndexStatus = "No cached place index yet. Exact playlists came from the last Output catalog. Refresh from the active mod manager to discover named places.";
            return;
        }

        try
        {
            var index = await SoundtrackWorldIndexStore.TryLoadAsync(path);
            if (index is null) throw new InvalidDataException("The place index was empty.");
            ApplyWorldIndex(index);
            WorldIndexStatus = $"Cached active routes loaded: {index.Places.Count} places and {index.MusicTypes.Count} playlists.";
        }
        catch (Exception exception)
        {
            WorldIndexStatus = $"Could not load the cached place index: {exception.Message}";
        }
    }

    private async Task SaveWorldIndexCacheAsync(SoundtrackWorldIndex index)
    {
        var path = Path.Combine(OutputDirectory, "SKSE", "Plugins", SoundtrackWorldIndexStore.FileName);
        await SoundtrackWorldIndexStore.SaveAsync(index, path);
    }

    private void RefreshCurrentGameRoutingDetails()
    {
        if (!_catalogTabActive || _selectedCatalogTrack is null)
        {
            CurrentGameRoutingDetails = [];
            return;
        }

        var details = new List<string>();
        foreach (var playlist in _selectedCatalogTrack.Track.Playlists)
        {
            details.Add($"PLAYLIST | {playlist.EditorId}\n{playlist.Plugin} | {playlist.FormKey} | {playlist.Category.GetDisplayName()}");
            var places = _worldIndex.Places
                .Where(place => !place.IsAdvanced)
                .Where(place => place.Assignments.Any(assignment =>
                    assignment.MusicTypeFormKey.Equals(playlist.FormKey, StringComparison.OrdinalIgnoreCase)))
                .Select(place => place.DisplayName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (places.Length > 0)
            {
                var shown = string.Join(", ", places.Take(8));
                details.Add($"PLACES | {shown}{(places.Length > 8 ? $" +{places.Length - 8}" : string.Empty)}");
            }
        }

        if (details.Count == 0)
        {
            details.Add("No owning MusicType playlist was discovered for this track. It may be triggered directly by a quest or script.");
        }
        else if (_worldIndex.Places.Count == 0)
        {
            details.Add("Named place mapping is not cached yet. Refresh from the active mod manager to expose it.");
        }
        CurrentGameRoutingDetails = details;
    }

    private void QueueRoutingSave(string message)
    {
        _isDirty = true;
        StatusText = $"{message}. Rebuild Skypod Output and restart Skyrim to apply it.";
        var version = ++_routingSaveVersion;
        _ = SaveRoutingAfterDelayAsync(version);
    }

    private async Task SaveRoutingAfterDelayAsync(int version)
    {
        await Task.Delay(350);
        if (version != _routingSaveVersion) return;
        if (string.IsNullOrWhiteSpace(_libraryPath))
        {
            StatusText = "Routing updated in memory. Save this new library, then rebuild Skypod Output and restart Skyrim.";
            return;
        }

        await _routingSaveGate.WaitAsync();
        try
        {
            if (version != _routingSaveVersion) return;
            await _libraryStore.SaveAsync(CreateLibrary(), _libraryPath);
            RememberLibraryPath(_libraryPath);
            if (version == _routingSaveVersion)
            {
                _isDirty = false;
                StatusText = "Routing saved to the project. Rebuild Skypod Output and restart Skyrim to apply it.";
            }
        }
        catch (Exception exception)
        {
            StatusText = $"Routing changed but the project could not be saved: {exception.Message}";
        }
        finally
        {
            _routingSaveGate.Release();
        }
    }

    private void NotifyLibraryChanged()
    {
        OnPropertyChanged(nameof(LibraryName));
        OnPropertyChanged(nameof(TrackCountText));
    }

    private static void ShowError(string title, Exception exception)
    {
        MessageBox.Show($"{title}.\n\n{exception.Message}", "Skypod", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private bool SetField<T>(ref T field, T value, bool markDirty = false, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        if (markDirty) _isDirty = true;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private sealed class RuntimeCatalogDocument
    {
        public List<SoundtrackCatalogTrack> Tracks { get; set; } = [];
    }

    private sealed record RoutingTargetDefinition(
        string Value,
        string Name,
        string Details,
        string SearchText,
        MusicTypeSafety Safety,
        bool IsAdvanced,
        int SortOrder);
}

public sealed record ModManagerOption(ModManagerKind Value, string Label)
{
    public override string ToString() => Label;
}
