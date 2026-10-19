using System.Text.Json;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Installs;

namespace Skypod.Core;

public static class SkypodExclusionStore
{
    public const string FileName = "Skypod.exclusions.json";

    public static async Task<IReadOnlySet<string>> LoadForBuildAsync(
        BuildSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (settings.ExcludedTrackIds is not null)
        {
            return Normalize(settings.ExcludedTrackIds);
        }

        foreach (var path in CandidatePaths(settings))
        {
            if (!File.Exists(path)) continue;

            await using var stream = File.OpenRead(path);
            var document = await JsonSerializer.DeserializeAsync<ExclusionDocument>(
                stream,
                LibraryStore.CreateJsonOptions(),
                cancellationToken) ?? throw new InvalidDataException($"'{path}' did not contain valid Skypod exclusions.");
            if (document.SchemaVersion != 1)
            {
                throw new InvalidDataException(
                    $"Unsupported Skypod exclusion schema version {document.SchemaVersion} in '{path}'.");
            }
            return Normalize(document.ExcludedTrackIds ?? []);
        }

        return new HashSet<string>(StringComparer.Ordinal);
    }

    public static async Task SaveAsync(
        string path,
        IEnumerable<string> excludedTrackIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(excludedTrackIds);

        var normalized = Normalize(excludedTrackIds)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await using var stream = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            16 * 1024,
            useAsync: true);
        await JsonSerializer.SerializeAsync(
            stream,
            new ExclusionDocument { ExcludedTrackIds = normalized },
            LibraryStore.CreateJsonOptions(),
            cancellationToken);
    }

    internal static IReadOnlySet<string> Normalize(IEnumerable<string> values) =>
        values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim().ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);

    private static IEnumerable<string> CandidatePaths(BuildSettings settings)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(settings.ExclusionsPath))
        {
            candidates.Add(Path.GetFullPath(settings.ExclusionsPath));
        }
        if (!string.IsNullOrWhiteSpace(settings.TargetDataFolder))
        {
            candidates.Add(UnderDataFolder(settings.TargetDataFolder));
        }
        else if (GameLocations.TryGetDataFolder(GameRelease.SkyrimVR, out var dataFolder))
        {
            // When Studio runs under MO2, opening this physical Data path is
            // intercepted by USVFS and sees the active profile's winning file.
            candidates.Add(UnderDataFolder(dataFolder.Path));
        }
        if (!string.IsNullOrWhiteSpace(settings.OutputDirectory))
        {
            candidates.Add(Path.Combine(
                Path.GetFullPath(settings.OutputDirectory),
                "SKSE",
                "Plugins",
                FileName));
        }

        return candidates.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static string UnderDataFolder(string dataFolder) => Path.Combine(
        Path.GetFullPath(dataFolder),
        "SKSE",
        "Plugins",
        FileName);

    private sealed class ExclusionDocument
    {
        public int SchemaVersion { get; set; } = 1;
        public IReadOnlyList<string>? ExcludedTrackIds { get; set; } = [];
    }
}
