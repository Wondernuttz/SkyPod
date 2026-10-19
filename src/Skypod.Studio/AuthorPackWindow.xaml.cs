using System.Windows;
using Skypod.Core;

namespace Skypod.Studio;

public enum AuthorPackAction { Player, Project, Import }

public partial class AuthorPackWindow : Window
{
    public SkypodLibrary Library { get; }
    public AuthorPackAction Action { get; private set; }
    public bool IncludeAudio => IncludeAudioBox.IsChecked == true;
    public string RequiredPluginsText { get; set; }

    public AuthorPackWindow(SkypodLibrary library)
    {
        Library = AuthorProjectPackager.Snapshot(library);
        RequiredPluginsText = string.Join(Environment.NewLine, SoundtrackDependencies.FromLibrary(Library));
        InitializeComponent();
        DataContext = this;
    }

    private void Select(AuthorPackAction action)
    {
        var plugins = RequiredPluginsText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (action != AuthorPackAction.Import && (string.IsNullOrWhiteSpace(Library.Name) || string.IsNullOrWhiteSpace(Library.Pack.Version) ||
                plugins.Any(plugin => !SoundtrackDependencies.IsPluginName(plugin))))
        {
            MessageBox.Show(this, "Enter a soundtrack name and version. Required plugins must be filenames ending in .esm, .esp or .esl.", "Soundtrack details");
            return;
        }
        Library.Pack.RequiredPlugins = plugins.ToList();
        Action = action;
        DialogResult = true;
    }
    private void Player_Click(object sender, RoutedEventArgs e) => Select(AuthorPackAction.Player);
    private void Project_Click(object sender, RoutedEventArgs e) => Select(AuthorPackAction.Project);
    private void Import_Click(object sender, RoutedEventArgs e) => Select(AuthorPackAction.Import);
}
