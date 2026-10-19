using System.ComponentModel;
using System.Runtime.CompilerServices;
using Skypod.Core;

namespace Skypod.Studio;

public sealed record AutomaticPlaybackModeOption(
    AutomaticPlaybackMode Mode,
    string Name,
    string Summary);

public sealed record TrackCategoryOption(
    TrackCategory Value,
    string Name)
{
    public override string ToString() => Name;
}

public sealed class AtlasSongOption : INotifyPropertyChanged
{
    private bool _isSelected;
    private TrackCategory _category;

    public AtlasSongOption(
        string trackId,
        string title,
        string artist,
        TrackCategory category,
        bool isSelected,
        bool isCategoryEditable = true)
    {
        TrackId = trackId;
        Title = string.IsNullOrWhiteSpace(title) ? "Untitled track" : title;
        Artist = artist ?? string.Empty;
        _category = category;
        _isSelected = isSelected;
        IsCategoryEditable = isCategoryEditable;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? SelectionChanged;

    public string TrackId { get; }
    public string Title { get; }
    public string Artist { get; }
    public string Subtitle => string.IsNullOrWhiteSpace(Artist) ? Category.GetDisplayName() : $"{Artist} • {Category.GetDisplayName()}";
    public bool IsCategoryEditable { get; }

    public TrackCategory Category
    {
        get => _category;
        set
        {
            if (_category == value) return;
            _category = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Subtitle));
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            OnPropertyChanged();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class RoutingTargetItem : INotifyPropertyChanged
{
    private bool _isSelected;

    public RoutingTargetItem(
        string value,
        string name,
        string details,
        string searchText,
        MusicTypeSafety safety,
        bool isSelected)
    {
        Value = value;
        Name = name;
        Details = details;
        SearchText = searchText;
        Safety = safety;
        _isSelected = isSelected;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? SelectionChanged;

    public string Value { get; }
    public string Name { get; }
    public string Details { get; }
    public string SearchText { get; }
    public MusicTypeSafety Safety { get; }
    public bool IsAdvanced => Safety is MusicTypeSafety.Contextual or MusicTypeSafety.Event or MusicTypeSafety.Unknown;
    public string SafetyLabel => Safety switch
    {
        MusicTypeSafety.Ambient => "SAFE AMBIENT",
        MusicTypeSafety.Contextual => "ADVANCED CONTEXT",
        MusicTypeSafety.Event => "ADVANCED EVENT",
        MusicTypeSafety.Unknown => "UNKNOWN / ADVANCED",
        _ => Safety.ToString().ToUpperInvariant()
    };

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
