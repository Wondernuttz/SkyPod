using System.IO.Compression;
using System.Text.Json;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace Skypod.Core;

/// <summary>Packages only runtime files explicitly referenced by the generated soundtrack.</summary>
public static class PlayerSoundtrackPackager
{
    public static string Create(string outputDirectory, string archivePath,
        SoundtrackPackMetadata? metadata = null, bool requirePlugin = true)
    {
        outputDirectory = Path.GetFullPath(outputDirectory);
        archivePath = Path.GetFullPath(archivePath);
        if (archivePath.StartsWith(outputDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Save the ZIP outside the output mod folder.");
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "SKSE/Plugins/Skypod.library.json" };
        using var manifest = JsonDocument.Parse(File.ReadAllText(PackArchive.SafePath(outputDirectory, "SKSE/Plugins/Skypod.library.json")));
        if (!manifest.RootElement.TryGetProperty("schemaVersion", out var schema) || schema.GetInt32() != 3 ||
            !manifest.RootElement.TryGetProperty("tracks", out var tracks) || tracks.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Build a current SkyPod output before exporting it.");
        var required = new HashSet<string>(metadata?.RequiredPlugins ?? [], StringComparer.OrdinalIgnoreCase);
        var plugins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var runtimeMap = Path.Combine(outputDirectory, "SKSE", "Plugins", "Skypod.runtime.tsv");
        if (File.Exists(runtimeMap))
        {
            files.Add("SKSE/Plugins/Skypod.runtime.tsv");
            foreach (var line in File.ReadLines(runtimeMap).Where(line => line.Length > 0 && !line.StartsWith('#')))
            {
                var columns = line.Split('\t');
                if (columns.Length is not (3 or 5) || !SoundtrackDependencies.IsPluginName(columns[1]))
                    throw new InvalidDataException("Invalid SkyPod runtime map. Rebuild the output.");
                plugins.Add(columns[1]);
                if (columns.Length == 5 && SoundtrackDependencies.IsPluginName(columns[3])) required.Add(columns[3]);
            }
        }
        var bossMap = Path.Combine(outputDirectory, "SKSE", "Plugins", BossMusicBuilder.FileName);
        if (File.Exists(bossMap))
        {
            files.Add("SKSE/Plugins/" + BossMusicBuilder.FileName);
            foreach (var line in File.ReadLines(bossMap).Where(line => line.Length > 0 && !line.StartsWith('#')))
            {
                var columns = line.Split('\t');
                if (columns.Length != 7 || !SoundtrackDependencies.IsPluginName(columns[1]) ||
                    !SoundtrackDependencies.IsPluginName(columns[4]) || columns[3] is not ("ref" or "base"))
                    throw new InvalidDataException("Invalid boss assignments. Rebuild the output.");
                plugins.Add(columns[1]);
                required.Add(columns[4]);
            }
        }
        if (File.Exists(Path.Combine(outputDirectory, "Skypod.esp"))) plugins.Add("Skypod.esp");
        if (requirePlugin && (plugins.Count == 0 || !File.Exists(runtimeMap)))
            throw new InvalidDataException("Generate the load-order-aware plugin before exporting a player soundtrack.");
        foreach (var pluginName in plugins)
        {
            files.Add(pluginName);
            using var plugin = SkyrimMod.CreateFromBinaryOverlay(
                new ModPath(ModKey.FromNameAndExtension(pluginName), PackArchive.SafePath(outputDirectory, pluginName)),
                SkyrimRelease.SkyrimVR);
            foreach (var master in plugin.ModHeader.MasterReferences) required.Add(master.Master.FileName.ToString());
        }
        foreach (var track in tracks.EnumerateArray())
        {
            foreach (var field in new[] { "sourcePlugin", "winnerPlugin" })
                if (track.TryGetProperty(field, out var value) && value.GetString() is { } name && SoundtrackDependencies.IsPluginName(name))
                    required.Add(name);
            var paths = new List<string>();
            if (track.TryGetProperty("relativePath", out var relative) && relative.GetString() is { } path) paths.Add(path);
            if (track.TryGetProperty("audioPaths", out var audioPaths))
                paths.AddRange(audioPaths.EnumerateArray().Select(value => value.GetString()).OfType<string>());
            foreach (var audio in paths.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var normalized = audio.Replace('\\', '/');
                if (!normalized.StartsWith("music/Skypod/", StringComparison.OrdinalIgnoreCase)) continue;
                if (!Path.GetExtension(normalized).Equals(".xwm", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("A generated soundtrack references unsupported audio.");
                _ = PackArchive.SafePath(outputDirectory, normalized);
                files.Add(normalized);
            }
        }
        var exclusions = "SKSE/Plugins/" + SkypodExclusionStore.FileName;
        if (File.Exists(PackArchive.SafePath(outputDirectory, exclusions))) files.Add(exclusions);
        foreach (var file in files)
            if (!File.Exists(PackArchive.SafePath(outputDirectory, file)))
                throw new FileNotFoundException($"The built soundtrack is incomplete: {file}");
        required.ExceptWith(plugins);
        var nameText = manifest.RootElement.TryGetProperty("name", out var nameValue) ? nameValue.GetString() : "SkyPod Soundtrack";
        var readme = $"{nameText}\nAuthor: {metadata?.Author ?? ""}\nVersion: {metadata?.Version ?? "1.0.0"}\n\n" +
            $"{metadata?.Description}\n\n" +
            "Requires SkyPod and its runtime dependencies, plus the same music mods and load order used by the author.\n" +
            "Required plugins:\n" + string.Join('\n', required.Order(StringComparer.OrdinalIgnoreCase)) + "\n\n" +
            $"Music dependencies / credits:\n{metadata?.MusicDependencies}\n\n" +
            "Install this ZIP as a normal mod in MO2 or Vortex. Let it overwrite the base SkyPod output/plugin; enable its plugin. Deploy in Vortex.\n" +
            "Use ONE generated SkyPod output. To combine author projects, import/merge them in Studio and rebuild against the final modlist.\n" +
            "Players do not need Studio to use this soundtrack. Rebuild after changing its required mods or music routing.\n" +
            "Wabbajack authors: include this archive in your list's downloads with a supported download source and metadata, then install it as a normal mod.\n";
        PackArchive.Create(archivePath, archive =>
        {
            foreach (var file in files.Order(StringComparer.OrdinalIgnoreCase))
                archive.CreateEntryFromFile(PackArchive.SafePath(outputDirectory, file), file, CompressionLevel.Optimal);
            PackArchive.WriteText(archive, "README.txt", readme);
        });
        using var audit = ZipFile.OpenRead(archivePath);
        if (audit.Entries.Count != files.Count + 1 || files.Any(file => audit.GetEntry(file) is null))
            throw new InvalidDataException("Soundtrack archive verification failed.");
        return archivePath;
    }
}
