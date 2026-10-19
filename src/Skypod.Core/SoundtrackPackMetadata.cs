namespace Skypod.Core;

public sealed class SoundtrackPackMetadata
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Author { get; set; } = string.Empty;
    public string Version { get; set; } = "1.0.0";
    public string Description { get; set; } = string.Empty;
    public List<string> RequiredPlugins { get; set; } = [];
    public string MusicDependencies { get; set; } = string.Empty;
}

public static class SoundtrackDependencies
{
    public static string[] FromLibrary(SkypodLibrary library) => library.Tracks
        .Select(track => track.AutomaticPlayback)
        .Concat(library.CatalogRoutingOverrides.Select(entry => entry.AutomaticPlayback))
        .SelectMany(rule => rule.SelectedPlaceIds.Concat(rule.SelectedMusicTypeFormKeys).Concat(rule.SelectedBossIds))
        .Concat(library.CatalogRoutingOverrides.Select(entry => entry.TrackId))
        .SelectMany(value => value.Split([':', '|']))
        .Concat(library.Pack.RequiredPlugins)
        .Where(IsPluginName)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Order(StringComparer.OrdinalIgnoreCase).ToArray();

    public static bool IsPluginName(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.IndexOfAny(['/', '\\', ':']) < 0 &&
        Path.GetExtension(value).ToLowerInvariant() is ".esm" or ".esp" or ".esl";
}
