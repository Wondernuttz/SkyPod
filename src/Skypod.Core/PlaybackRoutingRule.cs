namespace Skypod.Core;

/// <summary>
/// Controls where an imported track is inserted into Skyrim's automatic
/// music graph. Manual preview remains available independently of this rule.
/// </summary>
public sealed class PlaybackRoutingRule
{
    public AutomaticPlaybackMode Mode { get; set; } = AutomaticPlaybackMode.CategoryDefault;

    /// <summary>
    /// Stable IDs emitted by <see cref="SoundtrackWorldIndexer"/> in the form
    /// place:&lt;kind&gt;:&lt;plugin&gt;:&lt;local-form-id&gt;.
    /// </summary>
    public List<string> SelectedPlaceIds { get; set; } = [];

    public List<string> SelectedBossIds { get; set; } = [];

    /// <summary>
    /// Canonical MusicType FormKeys in the same Plugin|ID6 format used by the
    /// soundtrack catalog.
    /// </summary>
    public List<string> SelectedMusicTypeFormKeys { get; set; } = [];
}

/// <summary>
/// A routing change for a soundtrack record discovered in the active load
/// order. TrackId is the stable opaque ID from SoundtrackCatalogTrack.Id.
/// </summary>
public sealed class CatalogTrackRoutingOverride
{
    public string TrackId { get; set; } = string.Empty;
    public PlaybackRoutingRule AutomaticPlayback { get; set; } = new();
}

public enum AutomaticPlaybackMode
{
    CategoryDefault,
    AnywhereAmbient,
    SpecificPlaces,
    ExactPlaylistAdvanced,
    ManualOnly,
    BossEncounters
}
