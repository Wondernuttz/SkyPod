using Skypod.Core;

namespace Skypod.Studio;

public sealed class CatalogTrackRow
{
    public CatalogTrackRow(SoundtrackCatalogTrack track)
    {
        Track = track;
        var playlistText = string.Join(' ', track.Playlists.Select(playlist =>
            $"{playlist.EditorId} {playlist.Plugin} {playlist.Category.GetDisplayName()} {playlist.FormKey}"));
        SearchText = string.Join(' ', new[]
        {
            track.Title,
            track.Artist,
            track.Category.GetDisplayName(),
            track.SourcePlugin,
            track.WinnerPlugin,
            track.AudioProvider ?? string.Empty,
            track.SourceFormKey ?? string.Empty,
            track.MusicTrackEditorId ?? string.Empty,
            string.Join(' ', track.AudioPaths),
            playlistText
        }).ToLowerInvariant();
    }

    public SoundtrackCatalogTrack Track { get; }
    public string Id => Track.Id;
    public string Title => Track.Title;
    public string Artist => string.IsNullOrWhiteSpace(Track.Artist) ? "Skyrim soundtrack" : Track.Artist;
    public string CategoryName => Track.Category.GetDisplayName();
    public string Provider => Track.AudioProvider ?? Track.WinnerPlugin ?? Track.SourcePlugin;
    public string PlaylistCountText => Track.Playlists.Count == 0
        ? "No discovered playlist"
        : $"{Track.Playlists.Count} playlist{(Track.Playlists.Count == 1 ? string.Empty : "s")}";
    public string ConditionsText => Track.HadConditions
        ? "The winning track graph contains playback conditions. Exact conditions remain owned by Skyrim and its source mod."
        : "No track-level conditions were discovered.";
    public string SearchText { get; }
}
