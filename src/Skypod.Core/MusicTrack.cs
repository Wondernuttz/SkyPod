using System.Text.Json.Serialization;

namespace Skypod.Core;

public sealed class MusicTrack
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string SourcePath { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public TrackCategory Category { get; set; } = TrackCategory.ExplorationDay;
    public bool Enabled { get; set; } = true;
    public bool Favorite { get; set; }
    public bool Blocked { get; set; }
    public int Weight { get; set; } = 1;
    public double GainDb { get; set; }
    public double? DurationSeconds { get; set; }
    public PlaybackRoutingRule AutomaticPlayback { get; set; } = new();

    [JsonIgnore]
    public string CategoryName => Category.GetDisplayName();

    [JsonIgnore]
    public string AutomaticPlaybackName => AutomaticPlayback.Mode switch
    {
        AutomaticPlaybackMode.CategoryDefault => "Anywhere",
        AutomaticPlaybackMode.AnywhereAmbient => "Anywhere Compatible",
        AutomaticPlaybackMode.SpecificPlaces => "Specific Location",
        AutomaticPlaybackMode.ExactPlaylistAdvanced => "Exact Playlist",
        AutomaticPlaybackMode.ManualOnly => "Manual Only",
        AutomaticPlaybackMode.BossEncounters => "Boss Fights",
        _ => AutomaticPlayback.Mode.ToString()
    };

    public string GetOutputFileName()
    {
        var stem = PathSanitizer.ToSafeFileName(string.IsNullOrWhiteSpace(Title)
            ? Path.GetFileNameWithoutExtension(SourcePath)
            : Title);
        return $"{stem}-{Id:N}.xwm";
    }
}
