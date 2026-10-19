using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Skypod.Core;

namespace Skypod.Studio;

public partial class MapPickerWindow : Window
{
    private SoundtrackWorldIndex _index;
    private readonly HashSet<string> _selectedIds;
    private IReadOnlyList<MapLocationItem> _locations = [];
    private IReadOnlyList<RealmItem> _realms = [];
    private readonly Func<Task<SoundtrackWorldIndex?>>? _refreshIndexAsync;
    private MapLocationItem? _focusedLocation;
    private RealmItem? _displayedRealm;
    private Dictionary<string, CityMapOverview> _cityOverviews = new(StringComparer.OrdinalIgnoreCase);
    private CityMapOverview? _cityView;
    private (RealmItem Realm, SoundtrackMapCanvas.ViewState View, MapLocationItem? Focus)? _cityReturn;
    private bool _syncingScopes;
    private bool _syncingRealms;
    private bool _isRefreshing;

    public MapPickerWindow(
        SoundtrackWorldIndex index,
        IEnumerable<string> selectedPlaceIds,
        IEnumerable<AtlasSongOption> songOptions,
        Func<Task<SoundtrackWorldIndex?>>? refreshIndexAsync = null)
    {
        InitializeComponent();
        _index = index ?? SoundtrackWorldIndex.Empty;
        _refreshIndexAsync = refreshIndexAsync;
        _selectedIds = selectedPlaceIds
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        SongOptions = songOptions.ToArray();
        if (SongOptions.Count > 0 && SongOptions.All(song => !song.IsSelected)) SongOptions[0].IsSelected = true;
        foreach (var song in SongOptions) song.SelectionChanged += SongOption_SelectionChanged;
        SongList.ItemsSource = SongOptions;
        DataContext = this;

        MapCanvas.PlaceActivated += (_, place) => ActivatePlace(place, center: false);
        MapCanvas.PlaceHovered += (_, place) => ShowHover(place);
        MapCanvas.ViewChanged += (_, _) => UpdateTerrainStatus();
        MapCanvas.ZoomRequested += MapCanvas_ZoomRequested;
        MapCanvas.SetTerrainLibrary(AtlasTerrainLibrary.TryOpenDefault(out _));
        Closed += (_, _) => MapCanvas.ReleaseTerrain();
        ReplaceIndex(_index);
        RefreshSelectionVisuals();
        RefreshSongSummary();
    }

    public IReadOnlyList<TrackCategoryOption> CategoryOptions { get; } = Enum.GetValues<TrackCategory>()
        .Select(category => new TrackCategoryOption(category, category.GetDisplayName()))
        .ToArray();
    public IReadOnlyList<AtlasSongOption> SongOptions { get; }
    public IReadOnlyList<string> SelectedPlaceIds => _selectedIds
        .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
        .ToArray();
    public IReadOnlyList<string> SelectedTrackIds => SongOptions
        .Where(song => song.IsSelected)
        .Select(song => song.TrackId)
        .ToArray();

    private void ReplaceIndex(SoundtrackWorldIndex index)
    {
        _index = index ?? SoundtrackWorldIndex.Empty;
        _focusedLocation = null;
        PlaceNameText.Text = "Choose a location";
        PlaceContextText.Text = string.Empty;
        AccuracyText.Text = "Choose a place on the map or in the Places list. Use the playback scope below to choose where its music plays.";
        ScopeList.ItemsSource = null;
        _locations = BuildLocations();
        _cityOverviews = _locations.Select(item => CityMapOverview.Create(_index, item.Place))
            .Where(overview => overview is not null).Cast<CityMapOverview>()
            .ToDictionary(overview => overview.City.Id, StringComparer.OrdinalIgnoreCase);
        MapCanvas.CityOverviews = _cityOverviews.Values.ToArray();
        EndCityView();
        BuildRealms();
        RefreshSearch();
        MapReadyPanel.Visibility = _index.Maps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        MapStatusText.Text = _index.Maps.Count == 0
            ? "This cached index predates Atlas map geometry. Build the map from your active load order."
            : $"{_index.Maps.Count:N0} realm map{(_index.Maps.Count == 1 ? string.Empty : "s")} • {_locations.Count:N0} selectable locations";
    }

    private IReadOnlyList<MapLocationItem> BuildLocations()
    {
        var entries = AtlasLocationDirectory.Create(_index);

        return entries.Select(entry =>
            {
                var root = entry.Place;
                var scopes = _index.Places
                    .Where(candidate => entry.RootIds.Contains(candidate.RootPlaceId ?? candidate.Id, StringComparer.OrdinalIgnoreCase))
                    .Where(candidate => candidate.ConditionFormKeys.Count > 0)
                    .OrderBy(ScopeSortOrder)
                    .ThenBy(candidate => candidate.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .Select(candidate => new ScopeChoice(
                        candidate,
                        root.Group is "city" or "town" && candidate.Scope == SoundtrackPlaceScope.InteriorOnly
                            ? "All city interiors" : ScopeName(candidate),
                        ScopeDetail(candidate),
                        _selectedIds.Contains(candidate.Id)))
                    .ToArray();
                var worldName = root.WorldspaceName ?? _index.Maps.FirstOrDefault(map =>
                    root.Map?.WorldspaceFormKey.Equals(map.FormKey, StringComparison.OrdinalIgnoreCase) == true)?.DisplayName
                    ?? "unplaced";
                var detail = $"{GroupLabel(root)} • {(worldName == "Tamriel" ? "Skyrim" : worldName)}";
                return new MapLocationItem(root, scopes, detail) { RootIds = entry.RootIds };
            })
            .Where(item => item.Scopes.Count > 0)
            .OrderByDescending(item => PlaceImportance(item.Place))
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private void BuildRealms()
    {
        var visibleMaps = _index.Maps
            .Where(map => _locations.Any(item =>
                AtlasLocationDirectory.BelongsToMap(item.Place, map)))
            .ToArray();
        if (visibleMaps.Length == 0) visibleMaps = _index.Maps.ToArray();

        _realms = visibleMaps.Select(map =>
            {
                var members = _locations.Where(item => AtlasLocationDirectory.BelongsToMap(item.Place, map)).ToArray();
                var selectable = members.Count(item => item.Place.Kind is not (SoundtrackPlaceKind.Region or SoundtrackPlaceKind.Worldspace));
                var regions = members.Count(item => item.Place.Kind == SoundtrackPlaceKind.Region);
                return new RealmItem(
                    map,
                    RealmGlyph(map.Style),
                    $"{selectable:N0} location{(selectable == 1 ? "" : "s")}" +
                    (regions == 0 ? "" : $" · {regions} region{(regions == 1 ? "" : "s")}") + (map.IsModAdded ? " · MOD" : ""),
                    OrbBrush(map.Style),
                    AccentBrush(map.Style),
                    IsPrimaryRealm(map));
            })
            .ToArray();
        RealmList.ItemsSource = _realms.Where(realm => !IsInternalRealm(realm.Map)).OrderByDescending(realm => realm.IsPrimary).ToArray();
        OtherRealmCombo.ItemsSource = _realms
            .Where(realm => !realm.IsPrimary && !IsInternalRealm(realm.Map))
            .OrderBy(realm => realm.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (_realms.Count == 0)
        {
            MapCanvas.Map = null;
            MapCanvas.Places = [];
            return;
        }
        RealmList.SelectedItem = _realms.FirstOrDefault(realm =>
                                     realm.Map.EditorId.Equals("Tamriel", StringComparison.OrdinalIgnoreCase))
                                 ?? _realms.FirstOrDefault(realm => realm.IsPrimary)
                                 ?? _realms.FirstOrDefault();
    }

    private void RealmList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingRealms || RealmList.SelectedItem is not RealmItem realm) return;
        _syncingRealms = true;
        OtherRealmCombo.SelectedItem = null;
        _syncingRealms = false;
        ShowRealm(realm);
    }

    private void OtherRealmCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingRealms || OtherRealmCombo.SelectedItem is not RealmItem realm) return;
        _syncingRealms = true;
        RealmList.SelectedItem = null;
        _syncingRealms = false;
        ShowRealm(realm);
    }

    private void ShowRealm(RealmItem realm, bool preserveFocus = false)
    {
        EndCityView();
        if (!preserveFocus) ClearFocusedLocation();
        _displayedRealm = realm;
        RealmNameText.Text = realm.Name;
        MapCanvas.Map = realm.Map;
        var members = _locations.Where(item => AtlasLocationDirectory.BelongsToMap(item.Place, realm.Map)).ToArray();
        MapCanvas.Places = members.Select(item => item.Place).ToArray();
        BoundariesToggle.Visibility = realm.Map.Style == SoundtrackMapStyle.Solstheim || members.Any(item => item.Place.Group == "hold")
            ? Visibility.Visible : Visibility.Collapsed;
        BoundariesToggle.Content = realm.Map.Style == SoundtrackMapStyle.Solstheim ? "Region highlights" : "Hold highlights";
        UpdateCanvasSelection();
        CityPlacesList.ItemsSource = members.OrderBy(item => item.Place.Kind == SoundtrackPlaceKind.Worldspace ? 2 : item.Place.Kind == SoundtrackPlaceKind.Region ? 1 : 0)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        DirectorySummary.Text = realm.Detail;
        var city = _cityOverviews.Values.FirstOrDefault(overview => overview.Map.FormKey == realm.Map.FormKey &&
            overview.City.FormKey == realm.Map.LocationFormKey);
        if (city is not null) OpenCity(city, null, preserveFocus);
        else if (!realm.Map.EditorId.Equals("Tamriel", StringComparison.OrdinalIgnoreCase)) NavigationTabs.SelectedItem = CityPlacesTab;
        RefreshWholeRealmButton();
    }

    private void EndCityView()
    {
        _cityView = null;
        _cityReturn = null;
        CityBackButton.Visibility = Visibility.Collapsed;
        CityPlacesList.ItemsSource = null;
        BoundariesToggle.Visibility = Visibility.Visible;
        CityMapButton.Visibility = Visibility.Collapsed;
        ExteriorMapButton.Visibility = Visibility.Collapsed;
    }

    private void CityMap_Click(object sender, RoutedEventArgs e)
    {
        if (_focusedLocation is null || _displayedRealm is null ||
            !_cityOverviews.TryGetValue(_focusedLocation.Place.Id, out var overview)) return;
        OpenCity(overview, (_displayedRealm, MapCanvas.CaptureView(), _focusedLocation));
    }

    private void ExteriorMap_Click(object sender, RoutedEventArgs e)
    {
        if (_focusedLocation is not { Place.Map: { } position } item) return;
        var realm = _realms.FirstOrDefault(candidate =>
            candidate.Map.FormKey.Equals(position.WorldspaceFormKey, StringComparison.OrdinalIgnoreCase));
        if (realm is null) return;
        ShowRealm(realm, true);
        FocusLocation(item with { IsOnCityMap = true }, true);
        _syncingRealms = true;
        RealmList.SelectedItem = realm;
        _syncingRealms = false;
    }

    private void OpenCity(CityMapOverview overview, (RealmItem Realm, SoundtrackMapCanvas.ViewState View, MapLocationItem? Focus)? previous, bool preserveFocus = false)
    {
        if (!preserveFocus) ClearFocusedLocation();
        ShowHover(null);
        _cityReturn = previous;
        _cityView = overview;
        MapCanvas.ShowCity(overview);
        MapCanvas.FocusedRootId = null;
        RealmNameText.Text = overview.City.DisplayName;
        CityBackButton.Content = $"Back to {previous?.Realm.Name ?? "Skyrim"}";
        CityBackButton.Visibility = Visibility.Visible;
        CityMapButton.Visibility = Visibility.Collapsed;
        BoundariesToggle.Visibility = Visibility.Collapsed;
        var ids = overview.Buildings.Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        CityPlacesList.ItemsSource = _locations.Where(item => ids.Contains(item.Place.Id))
            .Select(item => item with { IsOnCityMap = overview.MapPlaces.Any(p => p.Id == item.Place.Id) })
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        var entrances = overview.MapPlaces.Count(p => p.RootPlaceId is null);
        DirectorySummary.Text = $"{entrances} building entrances" +
            (overview.Buildings.Count == entrances ? "" : $" · {overview.Buildings.Count - entrances} nearby / unplaced");
        CityPlacesTab.Visibility = Visibility.Visible;
        NavigationTabs.SelectedItem = CityPlacesTab;
        if (!preserveFocus)
            AccuracyText.Text = "Choose a building on the map or in the Places list. Its full interior is selected together, including linked rooms and cellars.";
        RefreshWholeRealmButton();
    }

    private void CityBack_Click(object sender, RoutedEventArgs e)
    {
        if (_cityReturn is not { } previous)
        {
            var parent = _cityView is { } city ? ParentRealm(city) : null;
            if (parent is not null) { ShowRealm(parent); SelectRealmWithoutNavigation(parent); NavigationTabs.SelectedIndex = 1; }
            return;
        }
        ShowRealm(previous.Realm);
        SelectRealmWithoutNavigation(previous.Realm);
        if (previous.Focus is not null) FocusLocation(previous.Focus, false);
        MapCanvas.RestoreView(previous.View);
    }

    private RealmItem? ParentRealm(CityMapOverview city) => _realms.FirstOrDefault(realm =>
        realm.Map.FormKey.Equals(city.City.Map?.WorldspaceFormKey, StringComparison.OrdinalIgnoreCase));

    private void SelectRealmWithoutNavigation(RealmItem realm)
    {
        _syncingRealms = true;
        RealmList.SelectedItem = RealmList.Items.Contains(realm) ? realm : null;
        OtherRealmCombo.SelectedItem = null;
        _syncingRealms = false;
    }

    private static Vector CityParentOffset(CityMapOverview city, RealmItem parent)
    {
        var pin = city.City.Map!;
        if (city.Map.FormKey.Equals(parent.Map.FormKey, StringComparison.OrdinalIgnoreCase) ||
            city.Map.TerrainParentEditorId?.Equals(parent.Map.EditorId, StringComparison.OrdinalIgnoreCase) == true ||
            city.Map.ParentWorldspaceFormKey?.Equals(parent.Map.FormKey, StringComparison.OrdinalIgnoreCase) == true &&
            pin.X >= city.MinX - 4096 && pin.X <= city.MaxX + 4096 && pin.Y >= city.MinY - 4096 && pin.Y <= city.MaxY + 4096)
            return default;
        // A separate city world may use its own coordinate origin.
        return new Vector(pin.X - (city.MinX + city.MaxX) / 2, pin.Y - (city.MinY + city.MaxY) / 2);
    }

    private void MapCanvas_ZoomRequested(object? sender, SoundtrackMapCanvas.ZoomRequest e)
    {
        var scale = MapCanvas.PixelsPerWorldUnit * e.Factor;
        var world = MapCanvas.ScreenToWorld(e.Anchor);
        if (_cityView is { } current)
        {
            // Separate entry and exit thresholds prevent flickering between views.
            if (e.Factor >= 1 || MapCanvas.Zoom * e.Factor >= .55) return;
            var parent = _cityReturn?.Realm ?? ParentRealm(current);
            if (parent is null) return;
            world += CityParentOffset(current, parent);
            ShowRealm(parent);
            SelectRealmWithoutNavigation(parent);
            ShowHover(null);
            MapCanvas.MatchScaleAt(world, scale, e.Anchor);
            e.Handled = true;
            return;
        }
        if (e.Factor <= 1 || _displayedRealm is not { } realm) return;
        var candidates = _cityOverviews.Values.Where(city =>
            city.City.Map?.WorldspaceFormKey.Equals(realm.Map.FormKey, StringComparison.OrdinalIgnoreCase) == true);
        var target = candidates.Where(city =>
        {
            if (scale / MapCanvas.CityFitScale(city) < .7) return false;
            var offset = CityParentOffset(city, realm);
            var bounds = new Rect(MapCanvas.WorldToScreen(city.MinX + offset.X, city.MaxY + offset.Y),
                MapCanvas.WorldToScreen(city.MaxX + offset.X, city.MinY + offset.Y));
            bounds.Inflate(32, 32);
            var pin = MapCanvas.WorldToScreen(city.City.Map!.X, city.City.Map.Y);
            return bounds.Contains(e.Anchor) || (pin - e.Anchor).Length <= 48;
        }).OrderBy(city => (MapCanvas.WorldToScreen(city.City.Map!.X, city.City.Map.Y) - e.Anchor).Length).FirstOrDefault();
        if (target is null) return;
        world -= CityParentOffset(target, realm);
        var previous = (realm, MapCanvas.CaptureView(), _focusedLocation);
        OpenCity(target, previous);
        MapCanvas.MatchScaleAt(world, scale, e.Anchor);
        e.Handled = true;
    }

    private void CityPlaces_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(CityPlacesList, e.OriginalSource as DependencyObject)
            is not ListBoxItem { DataContext: MapLocationItem item }) return;
        CityPlacesList.SelectedItem = item;
        ActivateLocation(item, center: true);
        e.Handled = true;
    }

    private void CityPlaces_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || CityPlacesList.SelectedItem is not MapLocationItem item) return;
        ActivateLocation(item, center: true);
        e.Handled = true;
    }

    private void UpdateTerrainStatus()
    {
        if (TerrainStatusText is null || ZoomText is null) return;
        TerrainStatusText.Text = MapCanvas.TerrainStatus;
        ZoomText.Text = $"{MapCanvas.Zoom:0.0}×";
    }
    private void FitMap_Click(object sender, RoutedEventArgs e) => MapCanvas.FitMap();
    private void ZoomIn_Click(object sender, RoutedEventArgs e) => MapCanvas.ZoomBy(1.35);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => MapCanvas.ZoomBy(1 / 1.35);
    private void Boundaries_Changed(object sender, RoutedEventArgs e)
    {
        if (MapCanvas is not null) MapCanvas.ShowHoldBoundaries = BoundariesToggle.IsChecked == true;
    }
    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Down && SearchResults.Items.Count > 0)
        {
            SearchResults.SelectedIndex = 0;
            SearchResults.Focus();
            e.Handled = true;
        }
        else if (e.Key is Key.Enter && SearchResults.Items.Count > 0)
        {
            ActivateLocation((MapLocationItem)SearchResults.Items[0], center: true);
            e.Handled = true;
        }
    }
    private void SearchResults_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter && SearchResults.SelectedItem is MapLocationItem item)
        {
            ActivateLocation(item, center: true);
            e.Handled = true;
        }
    }
    private void TerrainLibrary_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose the terrain library containing catalog.json" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var library = AtlasTerrainLibrary.Open(dialog.FolderName);
            library.Remember();
            MapCanvas.SetTerrainLibrary(library);
            UpdateTerrainStatus();
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException)
        {
            MessageBox.Show(this, $"That folder could not be loaded as a terrain library.\n\n{ex.Message}",
                "Terrain library", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshSearch();

    private void RefreshSearch()
    {
        var query = SearchBox?.Text.Trim() ?? string.Empty;
        var results = query.Length == 0
            ? _locations.Where(item => PlaceImportance(item.Place) >= 55).Take(120).ToArray()
            : _locations.Where(item => item.SearchText.Contains(query, StringComparison.OrdinalIgnoreCase))
                .OrderBy(item => item.Name.Equals(query, StringComparison.OrdinalIgnoreCase) ? 0 :
                    item.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 1 :
                    item.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ? 2 : 3)
                .ThenByDescending(item => PlaceImportance(item.Place)).Take(350).ToArray();
        SearchResults.ItemsSource = results;
        SearchResults.Visibility = query.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        SearchCountText.Text = query.Length == 0
            ? $"{_locations.Count:N0} selectable locations indexed"
            : $"{results.Length:N0} matches • {_locations.Count:N0} total";
    }

    private void SearchResults_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(SearchResults, e.OriginalSource as DependencyObject)
            is not ListBoxItem { DataContext: MapLocationItem item }) return;

        SearchResults.SelectedItem = item;
        ActivateLocation(item, center: true);
        e.Handled = true;
    }

    private void ActivatePlace(SoundtrackPlace place, bool center)
    {
        var rootId = place.RootPlaceId ?? place.Id;
        var item = _locations.FirstOrDefault(candidate =>
            candidate.RootIds.Contains(rootId, StringComparer.OrdinalIgnoreCase));
        if (item is not null) ActivateLocation(item, center);
    }

    private void ActivateLocation(MapLocationItem item, bool center)
    {
        FocusLocation(item, center);
        if (item.Scopes.Any(scope => scope.IsSelected))
        {
            foreach (var scope in item.Scopes)
            {
                scope.IsSelected = false;
                _selectedIds.Remove(scope.Place.Id);
            }
            RefreshSelectionVisuals();
            return;
        }

        SelectRecommendedScope(item);
    }

    private void ClearFocusedLocation()
    {
        _focusedLocation = null;
        PlaceNameText.Text = "Choose a location";
        PlaceContextText.Text = string.Empty;
        PlaceAssignmentText.Text = string.Empty;
        PlaceAssignmentText.Visibility = Visibility.Collapsed;
        AccuracyText.Text = "Choose a place on the map or in the Places list. Use the playback scope below to choose where its music plays.";
        ScopeList.ItemsSource = null;
        SearchResults.SelectedItem = null;
        MapCanvas.FocusedRootId = null;
        CityMapButton.Visibility = Visibility.Collapsed;
        ExteriorMapButton.Visibility = Visibility.Collapsed;
    }

    private void FocusLocation(MapLocationItem item, bool center)
    {
        _focusedLocation = item;
        PlaceNameText.Text = item.Name;
        PlaceContextText.Text = item.Detail;
        AccuracyText.Text = AccuracyDescription(item.Place);
        ScopeList.ItemsSource = item.Scopes;
        UpdateFocusedAssignment();
        ExteriorMapButton.Visibility = Visibility.Collapsed;
        MapCanvas.FocusedRootId = item.Place.Id;
        CityMapButton.Visibility = _cityView is null && _cityOverviews.ContainsKey(item.Place.Id)
            ? Visibility.Visible : Visibility.Collapsed;
        if (_cityOverviews.ContainsKey(item.Place.Id))
            AccuracyText.Text = "Click the city to assign music or remove its assignment. Scroll in or open the city map to choose individual buildings. Use the scope below to change where its music plays.";
        if (_cityView?.Buildings.Any(p => p.Id.Equals(item.Place.Id, StringComparison.OrdinalIgnoreCase)) == true)
        {
            // The directory also includes linked buildings without a pin or
            // with an entrance in another world. Keep navigation stable.
            if (_cityView.MapPlaces.Any(p => p.Id.Equals(item.Place.Id, StringComparison.OrdinalIgnoreCase)))
            {
                if (center) MapCanvas.CenterOn(item.Place);
            }
            else if (item.Place.Map is { } exterior && !exterior.WorldspaceFormKey.Equals(_cityView.Map.FormKey, StringComparison.OrdinalIgnoreCase))
            {
                AccuracyText.Text += " Its entrance is outside the city. View its exterior location to see the map pin.";
                ExteriorMapButton.Visibility = Visibility.Visible;
            }
            else AccuracyText.Text += " This building has no entrance position on this overview. You can still select its interior below.";
            return;
        }

        var worldspaceKey = item.Place.Map?.WorldspaceFormKey;
        if (!string.IsNullOrWhiteSpace(worldspaceKey))
        {
            var realm = _realms.FirstOrDefault(candidate =>
                candidate.Map.FormKey.Equals(worldspaceKey, StringComparison.OrdinalIgnoreCase));
            if (realm is not null)
            {
                if (_cityView is not null || !ReferenceEquals(_displayedRealm, realm)) ShowRealm(realm, true);
                if (RealmList.Items.Contains(realm))
                {
                    _syncingRealms = true;
                    if (!ReferenceEquals(RealmList.SelectedItem, realm)) RealmList.SelectedItem = realm;
                    _syncingRealms = false;
                    RealmList.ScrollIntoView(realm);
                }
                else
                {
                    // An explicitly searched internal world remains selectable without cluttering the realm list.
                    _syncingRealms = true;
                    RealmList.SelectedItem = null;
                    _syncingRealms = false;
                    ShowRealm(realm, true);
                }
            }
            else
            {
                _syncingRealms = true;
                RealmList.SelectedItem = null;
                _syncingRealms = false;
                _displayedRealm = null;
                MapCanvas.Map = null;
                MapCanvas.Places = [];
                RealmNameText.Text = "Unmapped location";
                AccuracyText.Text = "This place can receive music, but its world has no map in the current location index.";
            }
            if (center) MapCanvas.CenterOn(item.Place);
        }
        MapCanvas.FocusedRootId = item.Place.Id;
        CityMapButton.Visibility = _cityView is null && _cityOverviews.ContainsKey(item.Place.Id)
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ScopeCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncingScopes || sender is not CheckBox { Tag: ScopeChoice choice } || _focusedLocation is null) return;
        _syncingScopes = true;
        try
        {
            if (choice.IsSelected)
            {
                // One logical place gets one precision scope. Selecting both
                // Whole and Interior would only duplicate runtime conditions.
                foreach (var sibling in _focusedLocation.Scopes.Where(sibling => !ReferenceEquals(sibling, choice)))
                {
                    sibling.IsSelected = false;
                    _selectedIds.Remove(sibling.Place.Id);
                }
                _selectedIds.Add(choice.Place.Id);
            }
            else
            {
                _selectedIds.Remove(choice.Place.Id);
            }
        }
        finally
        {
            _syncingScopes = false;
        }
        RefreshSelectionVisuals();
    }

    private void SelectRecommendedScope(MapLocationItem item)
    {
        if (item.Scopes.Count == 0 || item.Scopes.Any(scope => scope.IsSelected)) return;
        var recommended = item.Scopes.FirstOrDefault(scope => scope.Place.Scope == SoundtrackPlaceScope.InteriorOnly &&
                              item.Place.Group is not ("city" or "town" or "hold"))
                          ?? item.Scopes.FirstOrDefault(scope => scope.Place.Scope == SoundtrackPlaceScope.WholeLocation)
                          ?? item.Scopes.First();
        recommended.IsSelected = true;
        _selectedIds.Add(recommended.Place.Id);
        RefreshSelectionVisuals();
    }

    private void SongOption_SelectionChanged(object? sender, EventArgs e)
    {
        if (SongOptions.All(song => !song.IsSelected) && sender is AtlasSongOption song) song.IsSelected = true;
        RefreshSongSummary();
    }

    private void RefreshSongSummary()
    {
        var count = SongOptions.Count(song => song.IsSelected);
        SongCountText.Text = $"{count} song{(count == 1 ? string.Empty : "s")} will receive these locations";
        ApplyButton.Content = count == 1 ? "APPLY TO SONG" : $"APPLY TO {count} SONGS";
    }

    private void SelectAllSongs_Click(object sender, RoutedEventArgs e)
    {
        foreach (var song in SongOptions) song.IsSelected = true;
        RefreshSongSummary();
    }

    private async void RefreshMap_Click(object sender, RoutedEventArgs e)
    {
        if (_isRefreshing || _refreshIndexAsync is null) return;
        _isRefreshing = true;
        RefreshMapButton.IsEnabled = false;
        RefreshMapButton.Content = "BUILDING MAP…";
        MapStatusText.Text = "Reading the active load order and resolving winning worldspaces, markers, cells, and locations…";
        try
        {
            var refreshed = await _refreshIndexAsync();
            if (refreshed is not null) ReplaceIndex(refreshed);
            if (_index.Maps.Count == 0)
            {
                MapStatusText.Text = "No map geometry was produced. Launch Studio through the active MO2 profile, then try again.";
            }
        }
        finally
        {
            _isRefreshing = false;
            RefreshMapButton.IsEnabled = true;
            RefreshMapButton.Content = "Refresh locations";
        }
    }

    private void ClearAll_Click(object sender, RoutedEventArgs e)
    {
        _selectedIds.Clear();
        foreach (var scope in _locations.SelectMany(location => location.Scopes)) scope.IsSelected = false;
        ClearFocusedLocation();
        RefreshSelectionVisuals();
    }

    private void RefreshSelectionVisuals()
    {
        SelectionCountText.Text = _selectedIds.Count == 0
            ? "No place assignments yet"
            : $"{_selectedIds.Count:N0} precise place assignment{(_selectedIds.Count == 1 ? string.Empty : "s")}";
        UpdateCanvasSelection();
        UpdateFocusedAssignment();
        RefreshWholeRealmButton();
    }

    private void UpdateCanvasSelection()
    {
        MapCanvas.SelectedIds = new HashSet<string>(_selectedIds, StringComparer.OrdinalIgnoreCase);
        MapCanvas.SelectedRootIds = _locations.Where(item => item.Scopes.Any(scope => _selectedIds.Contains(scope.Place.Id)))
            .SelectMany(item => item.RootIds).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private void UpdateFocusedAssignment()
    {
        var scopes = _focusedLocation?.Scopes.Where(scope => _selectedIds.Contains(scope.Place.Id)).ToArray() ?? [];
        PlaceAssignmentText.Visibility = _focusedLocation is null ? Visibility.Collapsed : Visibility.Visible;
        PlaceAssignmentText.Text = scopes.Length == 0 ? "Viewing only · not assigned"
            : "Music assigned: " + string.Join(", ", scopes.Select(scope => scope.Name));
        PlaceAssignmentText.Foreground = (Brush)FindResource(scopes.Length == 0 ? "MutedBrush" : "GoldBrush");
    }

    private void RouteEntireRealm_Click(object sender, RoutedEventArgs e)
    {
        if (_cityView is not null)
        {
            var city = _locations.First(item => item.Place.Id == _cityView.City.Id);
            var whole = city.Scopes.FirstOrDefault(scope => scope.Place.Scope == SoundtrackPlaceScope.WholeLocation);
            if (whole is null) return;
            var wasSelected = whole.IsSelected;
            foreach (var scope in city.Scopes) { scope.IsSelected = false; _selectedIds.Remove(scope.Place.Id); }
            if (!wasSelected) { whole.IsSelected = true; _selectedIds.Add(whole.Place.Id); }
            RefreshSelectionVisuals();
            return;
        }
        var realm = CurrentRealm();
        if (realm is null) return;
        var item = WholeRealmLocation(realm);
        if (item is not null) ActivateLocation(item, center: false);
    }

    private void RefreshWholeRealmButton()
    {
        if (RouteEntireRealmButton is null) return;
        if (_cityView is not null)
        {
            RouteEntireRealmButton.IsEnabled = true;
            RouteEntireRealmButton.Content = _selectedIds.Contains(_cityView.City.Id) ? "Remove entire city" : "Select entire city";
            return;
        }
        var realm = CurrentRealm();
        var item = realm is null ? null : WholeRealmLocation(realm);
        RouteEntireRealmButton.IsEnabled = item is not null;
        if (realm is null)
        {
            RouteEntireRealmButton.Content = "SELECT ENTIRE MAP";
            return;
        }
        var selected = item?.Scopes.Any(scope => scope.IsSelected) == true;
        RouteEntireRealmButton.Content = selected
            ? $"Remove entire realm"
            : $"Select entire realm";
    }

    private RealmItem? CurrentRealm() => _displayedRealm;

    private MapLocationItem? WholeRealmLocation(RealmItem realm) => _locations.FirstOrDefault(item =>
        item.Place.Kind == SoundtrackPlaceKind.Worldspace &&
        item.Place.FormKey.Equals(realm.Map.FormKey, StringComparison.OrdinalIgnoreCase));

    private void ShowHover(SoundtrackPlace? place)
    {
        if (place is null)
        {
            HoverCard.Visibility = Visibility.Collapsed;
            return;
        }
        HoverName.Text = place.DisplayName;
        HoverDetail.Text = $"{GroupLabel(place)} • {place.Plugin}";
        if (_cityView is null && _cityOverviews.ContainsKey(place.RootPlaceId ?? place.Id))
            HoverDetail.Text += " · Keep scrolling in to open the city map";
        HoverCard.Visibility = Visibility.Visible;
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private static int ScopeSortOrder(SoundtrackPlace place) => place.Scope switch
    {
        SoundtrackPlaceScope.InteriorOnly => 0,
        SoundtrackPlaceScope.WholeLocation => 1,
        SoundtrackPlaceScope.ExteriorOnly => 2,
        SoundtrackPlaceScope.MapMarker => 3,
        _ when place.Kind == SoundtrackPlaceKind.Region => 4,
        _ => 5
    };

    private static string ScopeName(SoundtrackPlace place) => place.Scope switch
    {
        _ when place.Kind == SoundtrackPlaceKind.Worldspace => "Entire map / realm",
        SoundtrackPlaceScope.InteriorOnly => "Full interior",
        SoundtrackPlaceScope.ExteriorOnly => "Exterior grounds only",
        SoundtrackPlaceScope.WholeLocation => "Entire named location",
        SoundtrackPlaceScope.MapMarker => "This exterior map location",
        _ when place.Kind == SoundtrackPlaceKind.Region => "Entire game region",
        _ => place.DisplayName
    };

    private static string ScopeDetail(SoundtrackPlace place)
    {
        var cells = place.ConditionFormKeys.Count;
        return place.Scope switch
        {
            _ when place.Kind == SoundtrackPlaceKind.Worldspace => "Routes everywhere in this worldspace, including its exterior and named locations.",
            SoundtrackPlaceScope.InteriorOnly => $"All {cells:N0} linked interior cell{(cells == 1 ? string.Empty : "s")}, including rooms and cellars. No outdoor areas.",
            SoundtrackPlaceScope.ExteriorOnly => "Plays in the outdoor areas assigned to this place.",
            SoundtrackPlaceScope.WholeLocation => "Plays throughout this location, including its interiors and connected areas.",
            SoundtrackPlaceScope.MapMarker => "The mod supplied a marker but no precise named location; routing uses its containing exterior cell.",
            _ when place.Kind == SoundtrackPlaceKind.Region => "Plays throughout this region as defined by the game.",
            _ => $"Routes through {cells:N0} concrete game record{(cells == 1 ? string.Empty : "s")}."
        };
    }

    private static string AccuracyDescription(SoundtrackPlace place)
    {
        if (place.EditorId.StartsWith("BYOHHouse", StringComparison.OrdinalIgnoreCase) &&
            place.FormKey.StartsWith("HearthFires.esm|", StringComparison.OrdinalIgnoreCase))
            return "You can assign this home before building it. Full interior covers the house and cellar as they become accessible. Choose exterior grounds to include the plot.";
        if (place.Kind == SoundtrackPlaceKind.Worldspace)
            return "Assign this soundtrack across the selected realm.";
        if (place.Map is null)
        {
            return "You can assign music here, but this location has no known position on the map.";
        }
        return "Choose where the music plays below. Click the same place again to remove its assignment.";
    }

    private static string GroupLabel(SoundtrackPlace place)
    {
        if (place.DisplayName.Contains("Sanctuary", StringComparison.OrdinalIgnoreCase) ||
            place.EditorId.Contains("Sanctuary", StringComparison.OrdinalIgnoreCase)) return "LOCATION";
        return place.Group.ToLowerInvariant() switch
        {
            "hold" => "HOLD",
            "city" => "CITY",
            "town" => "TOWN",
            "dungeon" => "DUNGEON",
            "regionbiome" => "REGION",
            "interior" => "INTERIOR",
            "exteriorarea" => "EXTERIOR",
            _ => "LOCATION"
        };
    }

    private static int PlaceImportance(SoundtrackPlace place) => place.Group.ToLowerInvariant() switch
    {
        "hold" => 100,
        "city" => 90,
        "town" => 85,
        "regionbiome" => 75,
        "dungeon" => 60,
        _ when place.Map is not null => 50,
        _ => 20
    };

    private static string RealmGlyph(SoundtrackMapStyle style) => style switch
    {
        SoundtrackMapStyle.Skyrim => "▲",
        SoundtrackMapStyle.Solstheim => "♨",
        SoundtrackMapStyle.Apocrypha => "ϟ",
        SoundtrackMapStyle.Sovngarde => "✦",
        SoundtrackMapStyle.SoulCairn => "◇",
        SoundtrackMapStyle.ForgottenVale => "❄",
        _ => "◈"
    };

    private static Brush OrbBrush(SoundtrackMapStyle style) => new RadialGradientBrush(
        ColorFor(style, true),
        ColorFor(style, false));

    private static Brush AccentBrush(SoundtrackMapStyle style) => new SolidColorBrush(ColorFor(style, true));

    private static Color ColorFor(SoundtrackMapStyle style, bool bright) => style switch
    {
        SoundtrackMapStyle.Solstheim => Parse(bright ? "#E29655" : "#3B1C16"),
        SoundtrackMapStyle.Apocrypha => Parse(bright ? "#8CE96D" : "#102A18"),
        SoundtrackMapStyle.Sovngarde => Parse(bright ? "#9DF5DB" : "#242B68"),
        SoundtrackMapStyle.SoulCairn => Parse(bright ? "#D78AFF" : "#31184B"),
        SoundtrackMapStyle.ForgottenVale => Parse(bright ? "#C0F0FF" : "#173C52"),
        SoundtrackMapStyle.ArcaneRealm => Parse(bright ? "#E4A2FF" : "#351D42"),
        _ => Parse(bright ? "#F1C86D" : "#28394D")
    };

    private static Color Parse(string value) => (Color)ColorConverter.ConvertFromString(value)!;

    private static bool IsPrimaryRealm(SoundtrackWorldMap map) => map.EditorId.Equals("Tamriel", StringComparison.OrdinalIgnoreCase) ||
        map.EditorId.Equals("DLC2SolstheimWorld", StringComparison.OrdinalIgnoreCase) ||
        map.EditorId.Equals("DLC2ApocryphaWorld", StringComparison.OrdinalIgnoreCase) ||
        map.EditorId.Equals("Sovngarde", StringComparison.OrdinalIgnoreCase) ||
        map.EditorId.Equals("DLC01SoulCairn", StringComparison.OrdinalIgnoreCase) ||
        map.EditorId.Equals("DLC01FalmerValley", StringComparison.OrdinalIgnoreCase) ||
        map.EditorId.Equals("Blackreach", StringComparison.OrdinalIgnoreCase) ||
        map.EditorId.Equals("SkuldafnWorld", StringComparison.OrdinalIgnoreCase);

    private static bool IsInternalRealm(SoundtrackWorldMap map) =>
        map.EditorId.Contains("Test", StringComparison.OrdinalIgnoreCase) ||
        map.EditorId.Equals("FXLightWorldSpace", StringComparison.OrdinalIgnoreCase) ||
        map.EditorId.Equals("VRPlayroom", StringComparison.OrdinalIgnoreCase);

    private sealed record RealmItem(
        SoundtrackWorldMap Map,
        string Glyph,
        string Detail,
        Brush OrbBrush,
        Brush AccentBrush,
        bool IsPrimary)
    {
        public string Name => Map.EditorId.Equals("Tamriel", StringComparison.OrdinalIgnoreCase) ? "Skyrim" : Map.DisplayName;
    }

    private sealed record MapLocationItem(
        SoundtrackPlace Place,
        IReadOnlyList<ScopeChoice> Scopes,
        string Detail)
    {
        public string Name => AtlasLocationDirectory.Name(Place);
        public string SearchText => $"{Place.SearchText} {Detail}";
        public IReadOnlyList<string> RootIds { get; init; } = [Place.Id];
        public bool IsOnCityMap { get; init; } = true;
        public string BuildingDetail => (Place.Kind == SoundtrackPlaceKind.Region ? "Region coverage · not a separate building" :
            Place.Kind == SoundtrackPlaceKind.Worldspace ? "Entire realm" :
            Scopes.FirstOrDefault(scope => scope.Place.Scope == SoundtrackPlaceScope.InteriorOnly) is { } inside
            ? $"Interior · {inside.Place.ConditionFormKeys.Count} cell{(inside.Place.ConditionFormKeys.Count == 1 ? "" : "s")}" +
                (Scopes.Any(s => s.Place.Scope is SoundtrackPlaceScope.ExteriorOnly or SoundtrackPlaceScope.MapMarker) ? " · exterior available" : "")
            : "Exterior / location coverage") +
            (Place.Map is null ? " · no map position" : !IsOnCityMap ? " · outside this view" : "");
    }

    private sealed class ScopeChoice(
        SoundtrackPlace place,
        string name,
        string detail,
        bool isSelected) : INotifyPropertyChanged
    {
        private bool _isSelected = isSelected;
        public SoundtrackPlace Place { get; } = place;
        public string Name { get; } = name;
        public string Detail { get; } = detail;
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value) return;
                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
