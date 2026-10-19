using System.Text.Json;
using System.Text.Json.Serialization;

namespace Skypod.Core;

public sealed class LibraryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public async Task<SkypodLibrary> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        var library = await JsonSerializer.DeserializeAsync<SkypodLibrary>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidDataException($"'{path}' did not contain a valid Skypod library.");

        if (library.SchemaVersion != 1)
        {
            throw new InvalidDataException($"Unsupported Skypod schema version {library.SchemaVersion}.");
        }

        library.Tracks ??= [];
        library.CatalogRoutingOverrides ??= [];
        library.Pack ??= new();
        library.Pack.RequiredPlugins ??= [];
        foreach (var track in library.Tracks)
        {
            if (!string.IsNullOrWhiteSpace(track.SourcePath) && !Path.IsPathRooted(track.SourcePath) &&
                !(track.SourcePath.Length >= 3 && char.IsAsciiLetter(track.SourcePath[0]) && track.SourcePath[1] == ':' && track.SourcePath[2] is '\\' or '/'))
                track.SourcePath = PackArchive.SafePath(Path.GetDirectoryName(Path.GetFullPath(path))!, track.SourcePath);
            track.AutomaticPlayback ??= new PlaybackRoutingRule();
            track.AutomaticPlayback.SelectedPlaceIds ??= [];
            track.AutomaticPlayback.SelectedBossIds ??= [];
            track.AutomaticPlayback.SelectedMusicTypeFormKeys ??= [];
        }
        foreach (var catalogOverride in library.CatalogRoutingOverrides)
        {
            catalogOverride.AutomaticPlayback ??= new PlaybackRoutingRule();
            catalogOverride.AutomaticPlayback.SelectedPlaceIds ??= [];
            catalogOverride.AutomaticPlayback.SelectedBossIds ??= [];
            catalogOverride.AutomaticPlayback.SelectedMusicTypeFormKeys ??= [];
        }

        return library;
    }

    public async Task SaveAsync(SkypodLibrary library, string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(library);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        library.ModifiedUtc = DateTimeOffset.UtcNow;

        var temporaryPath = path + ".tmp";
        await using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, true))
        {
            await JsonSerializer.SerializeAsync(stream, library, JsonOptions, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }

        File.Move(temporaryPath, path, true);
    }

    public static JsonSerializerOptions CreateJsonOptions() => new(JsonOptions);
}
