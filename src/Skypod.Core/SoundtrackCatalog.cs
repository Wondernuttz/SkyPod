namespace Skypod.Core;

/// <summary>
/// A playable track in the active Skyrim music graph.  The record form key is
/// deliberately kept separate from the generated preview form so callers can
/// compare the catalog with Skyrim's currently selected MUST record.
/// </summary>
public sealed record SoundtrackCatalogTrack(
    string Id,
    string Title,
    string Artist,
    TrackCategory Category,
    string RelativePath,
    int Weight,
    double GainDb,
    bool Favorite,
    string SourceKind,
    string SourcePlugin,
    string WinnerPlugin,
    string? SourceFormKey,
    string? MusicTrackEditorId,
    string RecordKind,
    IReadOnlyList<string> ChildTrackIds,
    IReadOnlyList<string> AudioPaths,
    IReadOnlyList<SoundtrackPlaylist> Playlists,
    bool HadConditions,
    bool PreviewAvailable,
    string? PreviewPlugin,
    string? PreviewLocalFormId,
    string? AudioProvider,
    string AudioProviderKind);

public sealed record SoundtrackPlaylist(
    string FormKey,
    string EditorId,
    string Plugin,
    TrackCategory Category);
