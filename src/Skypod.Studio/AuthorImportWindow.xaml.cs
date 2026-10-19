using System.IO;
using System.Windows;
using Microsoft.Win32;
using Skypod.Core;

namespace Skypod.Studio;

public partial class AuthorImportWindow : Window
{
    public AuthorProjectPreview Preview { get; }
    public string Summary { get; }
    public string Requirements { get; }
    public string Conflicts { get; }
    public string Destination { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SkyPod Projects");
    public string ProjectDirectory => DestinationBox.Text.Trim();
    public string OriginalAudioDirectory => AudioFolderBox.Text.Trim();
    public bool ReplaceMatching => ReplaceBox.IsChecked == true;
    public Visibility OriginalAudioVisibility => !Preview.Document.IncludesAudio && Preview.Library.Tracks.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public AuthorImportWindow(AuthorProjectPreview preview, SkypodLibrary current)
    {
        Preview = preview;
        Summary = $"{preview.Library.Pack.Author} · {preview.Library.Pack.Version}\n{preview.Library.Tracks.Count} imported songs · {preview.Library.CatalogRoutingOverrides.Count} game/mod track assignments · Audio {(preview.Document.IncludesAudio ? "included" : "referenced")}";
        Requirements = string.Join(Environment.NewLine, SoundtrackDependencies.FromLibrary(preview.Library)) + "\n" + preview.Library.Pack.MusicDependencies;
        var matching = current.Tracks.Count(track => preview.Library.Tracks.Any(other => other.Id == track.Id)) +
            current.CatalogRoutingOverrides.Count(route => preview.Library.CatalogRoutingOverrides.Any(other => other.TrackId.Equals(route.TrackId, StringComparison.OrdinalIgnoreCase)));
        Conflicts = $"{matching} matching songs / routes already in your library";
        InitializeComponent();
        DataContext = this;
    }
    private void Folder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose a permanent folder for author projects" };
        if (dialog.ShowDialog(this) == true) DestinationBox.Text = dialog.FolderName;
    }
    private void Audio_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Find the original music files" };
        if (dialog.ShowDialog(this) == true) AudioFolderBox.Text = dialog.FolderName;
    }
    private void Import_Click(object sender, RoutedEventArgs e)
    {
        if (!Path.IsPathFullyQualified(ProjectDirectory) ||
            (OriginalAudioVisibility == Visibility.Visible && !Directory.Exists(OriginalAudioDirectory)))
        {
            MessageBox.Show(this, "Choose a permanent project folder and, when needed, the original audio folder.", "Import project");
            return;
        }
        DialogResult = true;
    }
}
