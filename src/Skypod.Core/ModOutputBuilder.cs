using System.Diagnostics;
using System.Text.Json;

namespace Skypod.Core;

public sealed class ModOutputBuilder(AudioConverter converter)
{
    public async Task<BuildResult> BuildAsync(
        SkypodLibrary library,
        BuildSettings settings,
        IProgress<BuildProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(settings);
        PathSanitizer.EnsureSafeOutputDirectory(settings.OutputDirectory);
        settings.RequiredPlugins = SoundtrackDependencies.FromLibrary(library);

        var timer = Stopwatch.StartNew();
        var outputDirectory = Path.GetFullPath(settings.OutputDirectory);
        var parentDirectory = Directory.GetParent(outputDirectory)?.FullName
            ?? throw new InvalidOperationException("The output directory must have a parent directory.");
        Directory.CreateDirectory(parentDirectory);

        var excludedTrackIds = await SkypodExclusionStore.LoadForBuildAsync(settings, cancellationToken);
        // Pass the normalized snapshot into the CPU-heavy plugin build. Studio creates
        // fresh settings for each build, so a later UI re-enable is read from disk.
        settings.ExcludedTrackIds = excludedTrackIds;

        var stagingDirectory = Path.Combine(parentDirectory, $".skypod-staging-{Guid.NewGuid():N}");
        var enabledTracks = library.Tracks.Where(track => track.Enabled && !track.Blocked).ToList();
        if (!settings.GeneratePlugin && enabledTracks.Select(track => track.AutomaticPlayback)
                .Concat(library.CatalogRoutingOverrides.Select(entry => entry.AutomaticPlayback))
                .Any(rule => rule.Mode != AutomaticPlaybackMode.ManualOnly && rule.SelectedBossIds.Count > 0))
            throw new InvalidOperationException("Boss fight assignments need a generated plugin. Enable Generate plugin and build again.");
        var runtimeTracks = new List<RuntimeTrack>(enabledTracks.Count);
        var converted = 0;
        var copied = 0;
        PluginBuildResult? pluginResult = null;

        try
        {
            Directory.CreateDirectory(stagingDirectory);
            for (var index = 0; index < enabledTracks.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var track = enabledTracks[index];
                progress?.Report(new BuildProgress(index, enabledTracks.Count, $"Building {track.Title}"));

                var relativePath = Path.Combine("music", "Skypod", track.Category.ToString(), track.GetOutputFileName());
                var destination = Path.Combine(stagingDirectory, relativePath);
                var wasConverted = await converter.ConvertAsync(track.SourcePath, destination, settings, cancellationToken);
                if (wasConverted) converted++; else copied++;

                runtimeTracks.Add(new RuntimeTrack(
                    track.Id,
                    track.Title,
                    track.Artist,
                    track.Category,
                    relativePath.Replace(Path.DirectorySeparatorChar, '/'),
                    Math.Clamp(track.Weight, 1, 100),
                    Math.Clamp(track.GainDb, -24, 12),
                    track.Favorite));
            }

            var runtimeDirectory = Path.Combine(stagingDirectory, "SKSE", "Plugins");
            Directory.CreateDirectory(runtimeDirectory);
            var manifestPath = Path.Combine(runtimeDirectory, "Skypod.library.json");
            var projectPath = Path.Combine(runtimeDirectory, "Skypod.project.skypod.json");
            await SkypodExclusionStore.SaveAsync(
                Path.Combine(runtimeDirectory, SkypodExclusionStore.FileName),
                excludedTrackIds,
                cancellationToken);
            await using (var stream = File.Create(projectPath))
            {
                // This is the editable project, not the stripped runtime view:
                // SourcePath and disabled/blocked tracks intentionally survive.
                await JsonSerializer.SerializeAsync(
                    stream,
                    library,
                    LibraryStore.CreateJsonOptions(),
                    cancellationToken);
            }

            await WriteBuildReadmeAsync(stagingDirectory, library, runtimeTracks.Count, settings, cancellationToken);
            if (settings.GeneratePlugin)
            {
                progress?.Report(new BuildProgress(enabledTracks.Count, enabledTracks.Count, "Patching the active Skyrim music playlists"));
                var pluginTracks = runtimeTracks.Select(track => new PluginTrack(
                    track.Id,
                    track.Title,
                    track.Category,
                    "Data\\" + track.RelativePath.Replace('/', '\\'),
                    track.Weight,
                    enabledTracks.First(source => source.Id == track.Id).DurationSeconds,
                    track.Artist,
                    track.RelativePath,
                    track.GainDb,
                    track.Favorite,
                    enabledTracks.First(source => source.Id == track.Id).AutomaticPlayback)).ToArray();
                // Mutagen walks the entire active load order here. Keep that
                // CPU and disk-heavy pass off WPF's UI thread so Studio stays
                // responsive while large mod-manager profiles are cataloged.
                pluginResult = await Task.Run(
                    () => new SkyrimMusicPluginBuilder().Build(
                        stagingDirectory,
                        pluginTracks,
                        settings,
                        library.CatalogRoutingOverrides),
                    cancellationToken).ConfigureAwait(false);
                var runtimeMap = new[] { "# Skypod runtime music map v2" }
                    .Concat(pluginResult.RuntimeMusicTypes.Select(entry =>
                        $"{entry.TrackId}\t{settings.PluginName}\t{entry.LocalFormId:X8}\t{entry.OriginalTrackPlugin}\t{entry.OriginalTrackLocalFormId:X8}"));
                await File.WriteAllLinesAsync(
                    Path.Combine(runtimeDirectory, "Skypod.runtime.tsv"),
                    runtimeMap,
                    cancellationToken);
                await SoundtrackWorldIndexStore.SaveAsync(
                    pluginResult.WorldIndex,
                    Path.Combine(runtimeDirectory, SoundtrackWorldIndexStore.FileName),
                    cancellationToken);
                if (pluginResult.Warnings.Count > 0)
                {
                    await File.WriteAllLinesAsync(
                        Path.Combine(stagingDirectory, "SKYPOD_WARNINGS.txt"),
                        pluginResult.Warnings,
                        cancellationToken);
                }
            }

            var catalogTracks = pluginResult?.CatalogTracks ?? runtimeTracks.Select(track =>
                new SoundtrackCatalogTrack(
                    track.Id.ToString("D"),
                    track.Title,
                    track.Artist,
                    track.Category,
                    track.RelativePath,
                    track.Weight,
                    track.GainDb,
                    track.Favorite,
                    "skypod",
                    settings.PluginName,
                    settings.PluginName,
                    null,
                    null,
                    "single",
                    [],
                    [track.RelativePath],
                    [],
                    false,
                    false,
                    null,
                    null,
                    "Skypod",
                    "loose")).ToArray();
            await using (var stream = File.Create(manifestPath))
            {
                await JsonSerializer.SerializeAsync(stream, new RuntimeLibrary(
                    3,
                    library.Name,
                    DateTimeOffset.UtcNow,
                    catalogTracks), LibraryStore.CreateJsonOptions(), cancellationToken);
            }

            InstallStagingDirectory(stagingDirectory, outputDirectory, settings.PreserveExistingOutput);
            progress?.Report(new BuildProgress(enabledTracks.Count, enabledTracks.Count, "Skypod output ready"));

            return new BuildResult(
                outputDirectory,
                converted,
                copied,
                catalogTracks.Count,
                library.Tracks.Count - enabledTracks.Count,
                timer.Elapsed,
                Path.Combine(outputDirectory, "SKSE", "Plugins", "Skypod.library.json"),
                pluginResult is null ? null : Path.Combine(outputDirectory, Path.GetFileName(pluginResult.PluginPath)),
                pluginResult?.Warnings ?? []);
        }
        catch
        {
            if (Directory.Exists(stagingDirectory)) Directory.Delete(stagingDirectory, true);
            throw;
        }
    }

    private static void InstallStagingDirectory(string stagingDirectory, string outputDirectory, bool preserveExisting)
    {
        if (Directory.Exists(outputDirectory))
        {
            if (preserveExisting)
            {
                var backup = $"{outputDirectory}.backup-{DateTime.Now:yyyyMMdd-HHmmss}";
                Directory.Move(outputDirectory, backup);
            }
            else
            {
                Directory.Delete(outputDirectory, true);
            }
        }

        Directory.Move(stagingDirectory, outputDirectory);
    }

    private static async Task WriteBuildReadmeAsync(
        string stagingDirectory,
        SkypodLibrary library,
        int trackCount,
        BuildSettings settings,
        CancellationToken cancellationToken)
    {
        var installInstruction = settings.ModManager == ModManagerKind.Vortex
            ? "Generated by Skypod Studio. Install the sibling -Vortex.zip through Vortex, enable it, and deploy mods before launching Skyrim."
            : "Generated by Skypod Studio. Enable this folder as a mod in Mod Organizer 2.";
        var lines = new[]
        {
            "Skypod Output",
            "==============",
            string.Empty,
            $"Library: {library.Name}",
            $"Tracks: {trackCount}",
            $"Built: {DateTimeOffset.Now:O}",
            string.Empty,
            installInstruction,
            "The runtime manifest is stored at SKSE\\Plugins\\Skypod.library.json.",
            "The editable project is stored at SKSE\\Plugins\\Skypod.project.skypod.json."
        };
        await File.WriteAllLinesAsync(Path.Combine(stagingDirectory, "SKYPOD_BUILD.txt"), lines, cancellationToken);
    }

    private sealed record RuntimeLibrary(
        int SchemaVersion,
        string Name,
        DateTimeOffset BuiltUtc,
        IReadOnlyList<SoundtrackCatalogTrack> Tracks);

    private sealed record RuntimeTrack(
        Guid Id,
        string Title,
        string Artist,
        TrackCategory Category,
        string RelativePath,
        int Weight,
        double GainDb,
        bool Favorite);
}
