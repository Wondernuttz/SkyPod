using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace Skypod.Core;

public sealed class AuthorProjectDocument
{
    public int SchemaVersion { get; set; } = 1;
    public string Kind { get; set; } = "skypod-author-project";
    public bool IncludesAudio { get; set; }
    public List<AuthorAudioFile> Audio { get; set; } = [];
}

public sealed record AuthorAudioFile(Guid TrackId, string Path, string FileName, string Sha256);
public sealed record AuthorProjectPreview(SkypodLibrary Library, AuthorProjectDocument Document);
public sealed record AuthorProjectImport(SkypodLibrary Library, string ProjectPath);
public sealed record LibraryMergeResult(SkypodLibrary Library, int Added, int Updated, int Kept);

public static class AuthorProjectPackager
{
    public const string ProjectFile = "Soundtrack.skypod.json";

    public static SkypodLibrary Snapshot(SkypodLibrary source) =>
        JsonSerializer.Deserialize<SkypodLibrary>(JsonSerializer.Serialize(source, LibraryStore.CreateJsonOptions()),
            LibraryStore.CreateJsonOptions())!;

    public static void Export(SkypodLibrary source, string archivePath, bool includeAudio,
        CancellationToken cancellationToken = default)
    {
        var library = Snapshot(source);
        ValidateLibrary(library);
        library.Pack.RequiredPlugins = SoundtrackDependencies.FromLibrary(library).ToList();
        var document = new AuthorProjectDocument { IncludesAudio = includeAudio };
        var files = new Dictionary<string, string>();
        foreach (var track in library.Tracks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(track.SourcePath)) throw new FileNotFoundException($"Missing audio for '{track.Title}'.", track.SourcePath);
            if (!AudioConverter.IsSupported(track.SourcePath)) throw new InvalidDataException($"Unsupported audio for '{track.Title}'.");
            var path = $"audio/{track.Id:N}{Path.GetExtension(track.SourcePath).ToLowerInvariant()}";
            var original = Path.GetFullPath(track.SourcePath);
            if (original.Equals(Path.GetFullPath(archivePath), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The export cannot replace a source audio file.");
            document.Audio.Add(new(track.Id, path, Path.GetFileName(original), Hash(original)));
            files.Add(path, original);
            track.SourcePath = path;
        }
        PackArchive.Create(archivePath, archive =>
        {
            PackArchive.WriteText(archive, "pack.json", JsonSerializer.Serialize(document, LibraryStore.CreateJsonOptions()));
            PackArchive.WriteText(archive, ProjectFile, JsonSerializer.Serialize(library, LibraryStore.CreateJsonOptions()));
            PackArchive.WriteText(archive, "README.txt", $"{library.Name} - editable SkyPod project\n\n" +
                "In Studio, choose Modlist Author > Import and merge project. Choose a permanent project folder.\n" +
                (includeAudio ? "Imported audio is included.\n" : "Audio is not included. Select the folder containing the original audio files when importing; Studio checks their contents.\n") +
                "Existing game/music-mod tracks remain dependencies; install the listed mods before rebuilding.\n" +
                "Merge projects in Studio, then build ONE output against your modlist. Generated player outputs replace one another.\n" +
                "For portable editor maps, put a terrain library (catalog.json and its images) in AtlasTerrain beside Skypod.Studio.exe.\n" +
                "This is an authoring project. Export a player soundtrack ZIP to install in MO2 or Vortex.\n");
            if (includeAudio)
                foreach (var (relative, original) in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    archive.CreateEntryFromFile(original, relative, CompressionLevel.Optimal);
                }
        });
    }

    public static AuthorProjectPreview Inspect(string archivePath)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        return Inspect(archive);
    }

    private static AuthorProjectPreview Inspect(ZipArchive archive)
    {
        if (archive.Entries.Count > 10003 || archive.Entries.Sum(entry => entry.Length) > 64L * 1024 * 1024 * 1024)
            throw new InvalidDataException("Author project exceeds the pack size limit.");
        if (archive.Entries.Select(entry => entry.FullName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != archive.Entries.Count)
            throw new InvalidDataException("Author project contains duplicate paths.");
        var document = PackArchive.ReadJson<AuthorProjectDocument>(archive, "pack.json");
        var library = PackArchive.ReadJson<SkypodLibrary>(archive, ProjectFile);
        if (document.SchemaVersion != 1 || document.Kind != "skypod-author-project" || document.Audio is null)
            throw new InvalidDataException("Unsupported SkyPod author project.");
        ValidateLibrary(library);
        if (document.Audio.Count != library.Tracks.Count || document.Audio.Select(file => file.TrackId).Distinct().Count() != document.Audio.Count)
            throw new InvalidDataException("Project audio references do not match its tracks.");
        var allowed = new HashSet<string>(StringComparer.Ordinal) { "pack.json", ProjectFile, "README.txt" };
        foreach (var track in library.Tracks)
        {
            var audio = document.Audio.SingleOrDefault(file => file.TrackId == track.Id)
                ?? throw new InvalidDataException("Missing track audio reference.");
            var expected = $"audio/{track.Id:N}{Path.GetExtension(audio.Path).ToLowerInvariant()}";
            if (audio.Path != expected || track.SourcePath != expected || !AudioConverter.IsSupported(expected) ||
                string.IsNullOrWhiteSpace(audio.FileName) || audio.FileName.IndexOfAny(['/', '\\', ':']) >= 0 ||
                audio.Sha256 is null || audio.Sha256.Length != 64 || !audio.Sha256.All(Uri.IsHexDigit))
                throw new InvalidDataException("Invalid project audio reference.");
            if (document.IncludesAudio && archive.GetEntry(expected) is null)
                throw new InvalidDataException($"Missing bundled audio for '{track.Title}'.");
            if (document.IncludesAudio) allowed.Add(expected);
        }
        if (archive.Entries.Any(entry => !allowed.Contains(entry.FullName)))
            throw new InvalidDataException("Author project contains unexpected files.");
        return new(library, document);
    }

    public static async Task<AuthorProjectImport> ImportAsync(string archivePath, string parentDirectory,
        string? originalAudioDirectory = null, CancellationToken cancellationToken = default)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var preview = Inspect(archive);
        var folderName = $"{PathSanitizer.ToSafeFileName(preview.Library.Name)}-{Guid.NewGuid():N}";
        var destination = Path.Combine(Path.GetFullPath(parentDirectory), folderName);
        var staging = destination + ".importing";
        Directory.CreateDirectory(staging);
        try
        {
            string[]? originals = null;
            foreach (var audio in preview.Document.Audio)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var target = PackArchive.SafePath(staging, audio.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (preview.Document.IncludesAudio)
                {
                    await using var input = archive.GetEntry(audio.Path)!.Open();
                    await using var output = File.Create(target);
                    await input.CopyToAsync(output, cancellationToken);
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(originalAudioDirectory) || !Directory.Exists(originalAudioDirectory))
                        throw new InvalidDataException("This project needs its original audio. Choose the folder containing those files.");
                    originals ??= Directory.GetFiles(originalAudioDirectory, "*", SearchOption.AllDirectories);
                    var original = originals.FirstOrDefault(file =>
                        Path.GetFileName(file).Equals(audio.FileName, StringComparison.OrdinalIgnoreCase) &&
                        Hash(file).Equals(audio.Sha256, StringComparison.OrdinalIgnoreCase));
                    if (original is null) throw new InvalidDataException($"Could not find matching audio: {audio.FileName}");
                    File.Copy(original, target);
                }
                if (!Hash(target).Equals(audio.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Audio contents do not match the project: {audio.FileName}");
            }
            // Save the relative project before rebasing it for the running editor.
            await new LibraryStore().SaveAsync(preview.Library, Path.Combine(staging, ProjectFile), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(staging, destination);
            var projectPath = Path.Combine(destination, ProjectFile);
            return new(await new LibraryStore().LoadAsync(projectPath, cancellationToken), projectPath);
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }

    public static LibraryMergeResult Merge(SkypodLibrary current, SkypodLibrary incoming, bool replaceMatching)
    {
        ValidateLibrary(current);
        ValidateLibrary(incoming);
        var result = Snapshot(current);
        var source = Snapshot(incoming);
        var added = 0;
        var updated = 0;
        var kept = 0;
        if (result.Tracks.Count == 0 && result.CatalogRoutingOverrides.Count == 0)
        {
            result.Name = source.Name;
            result.Pack = source.Pack;
        }
        foreach (var track in source.Tracks)
        {
            var index = result.Tracks.FindIndex(existing => existing.Id == track.Id);
            if (index < 0) { result.Tracks.Add(track); added++; }
            else if (replaceMatching) { result.Tracks[index] = track; updated++; }
            else kept++;
        }
        foreach (var route in source.CatalogRoutingOverrides)
        {
            var index = result.CatalogRoutingOverrides.FindIndex(existing => existing.TrackId.Equals(route.TrackId, StringComparison.OrdinalIgnoreCase));
            if (index < 0) { result.CatalogRoutingOverrides.Add(route); added++; }
            else if (replaceMatching) { result.CatalogRoutingOverrides[index] = route; updated++; }
            else kept++;
        }
        result.Pack.RequiredPlugins = SoundtrackDependencies.FromLibrary(result).Concat(SoundtrackDependencies.FromLibrary(source))
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
        if (!string.IsNullOrWhiteSpace(source.Pack.MusicDependencies) &&
            !result.Pack.MusicDependencies.Contains(source.Pack.MusicDependencies, StringComparison.Ordinal))
            result.Pack.MusicDependencies = (result.Pack.MusicDependencies + "\n" + source.Pack.MusicDependencies).Trim();
        return new(result, added, updated, kept);
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void ValidateLibrary(SkypodLibrary library)
    {
        if (library.SchemaVersion != 1 || library.Tracks is null || library.CatalogRoutingOverrides is null || library.Pack is null ||
            library.Pack.RequiredPlugins is null || library.Tracks.Any(track => track is null || track.Id == Guid.Empty) ||
            library.Tracks.Select(track => track.Id).Distinct().Count() != library.Tracks.Count ||
            library.CatalogRoutingOverrides.Any(route => route is null || string.IsNullOrWhiteSpace(route.TrackId)) ||
            library.CatalogRoutingOverrides.Select(route => route.TrackId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != library.CatalogRoutingOverrides.Count)
            throw new InvalidDataException("Invalid or duplicate tracks in the author project.");
        foreach (var rule in library.Tracks.Select(track => track.AutomaticPlayback)
                     .Concat(library.CatalogRoutingOverrides.Select(route => route.AutomaticPlayback)))
            if (rule is null || rule.SelectedPlaceIds is null || rule.SelectedMusicTypeFormKeys is null || rule.SelectedBossIds is null)
                throw new InvalidDataException("Invalid routing in the author project.");
    }
}
