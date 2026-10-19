using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Skypod.Core;

namespace Skypod.Studio;

public partial class BossPickerWindow : Window
{
    private readonly List<BossPickerRow> _rows;
    private List<BossPickerRow> _visible = [];
    public IReadOnlyList<string> SelectedBossIds => _rows.Where(row => row.IsSelected).Select(row => row.Id).ToArray();
    public bool KeepExistingRouting => KeepRouting.IsChecked == true;

    public BossPickerWindow(SoundtrackWorldIndex index, string song, PlaybackRoutingRule rule)
    {
        var selected = rule.SelectedBossIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        _rows = index.Bosses.Select(boss => new BossPickerRow(boss, selected.Contains(boss.Id))).ToList();
        foreach (var missing in selected.Where(id => _rows.All(row => !row.Id.Equals(id, StringComparison.OrdinalIgnoreCase))))
            _rows.Add(new(new(missing, "Unavailable saved encounter", "Enable its plugin or clear this assignment.",
                missing.Split(':').FirstOrDefault(part => SoundtrackDependencies.IsPluginName(part)) ?? "Missing plugin",
                "Unavailable", "Unavailable", [], []), true));
        InitializeComponent();
        SongLabel.Text = $"Choose where {song} should play.";
        GroupFilter.ItemsSource = new[] { "All encounters", "Named encounters", "Dungeon bosses", "Other game-tagged actors", "Selected encounters" };
        GroupFilter.SelectedIndex = 0;
        KeepRouting.IsChecked = rule.Mode is AutomaticPlaybackMode.SpecificPlaces or AutomaticPlaybackMode.ExactPlaylistAdvanced ||
            rule.SelectedBossIds.Count > 0 && rule.Mode is AutomaticPlaybackMode.CategoryDefault or AutomaticPlaybackMode.AnywhereAmbient;
        foreach (var row in _rows) row.PropertyChanged += (_, _) =>
        {
            if (GroupFilter.SelectedItem as string == "Selected encounters") Filter(); else UpdateCounts();
        };
        Filter();
    }

    private void SearchChanged(object sender, TextChangedEventArgs e) => Filter();
    private void FilterChanged(object sender, SelectionChangedEventArgs e) => Filter();
    private void Filter()
    {
        if (BossList is null || _rows is null) return;
        var text = SearchBox.Text.Trim();
        var group = GroupFilter.SelectedItem as string ?? "All encounters";
        _visible = _rows.Where(row => (group == "All encounters" || group == row.Group ||
                group == "Selected encounters" && row.IsSelected) &&
                (text.Length == 0 || row.SearchText.Contains(text, StringComparison.OrdinalIgnoreCase))).ToList();
        BossList.ItemsSource = _visible;
        EmptyLabel.Visibility = _visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateCounts();
    }
    private void UpdateCounts() => CountLabel.Text = $"Showing {_visible.Count} of {_rows.Count} encounters  |  {_rows.Count(row => row.IsSelected)} selected";
    private void SelectShown(object sender, RoutedEventArgs e) { foreach (var row in _visible) row.IsSelected = true; UpdateCounts(); }
    private void ClearAll(object sender, RoutedEventArgs e) { foreach (var row in _rows) row.IsSelected = false; UpdateCounts(); }
    private void Apply(object sender, RoutedEventArgs e) { DialogResult = true; }
}

public sealed class BossPickerRow(BossEncounter encounter, bool selected) : INotifyPropertyChanged
{
    private bool _selected = selected;
    public string Id => encounter.Id;
    public string Name => encounter.Name;
    public string Location => encounter.Location;
    public string Plugin => encounter.Plugin;
    public string Group => encounter.Group;
    public string SearchText => encounter.SearchText;
    public bool IsSelected
    {
        get => _selected;
        set { if (_selected == value) return; _selected = value; PropertyChanged?.Invoke(this, new(nameof(IsSelected))); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}
