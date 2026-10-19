namespace Skypod.Core;

public sealed class SkypodLibrary
{
    public int SchemaVersion { get; set; } = 1;
    public string Name { get; set; } = "My Skypod";
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ModifiedUtc { get; set; } = DateTimeOffset.UtcNow;
    public List<MusicTrack> Tracks { get; set; } = [];
    public List<CatalogTrackRoutingOverride> CatalogRoutingOverrides { get; set; } = [];
    public SoundtrackPackMetadata Pack { get; set; } = new();
}
