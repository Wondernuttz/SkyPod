namespace Skypod.Core;

public sealed record BuildResult(
    string OutputDirectory,
    int ConvertedTracks,
    int CopiedTracks,
    int CatalogTracks,
    int DisabledTracks,
    TimeSpan Elapsed,
    string ManifestPath,
    string? PluginPath,
    IReadOnlyList<string> PluginWarnings);
