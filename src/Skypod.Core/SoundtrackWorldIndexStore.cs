using System.Text.Json;

namespace Skypod.Core;

public static class SoundtrackWorldIndexStore
{
    public const string FileName = "Skypod.world-index.json";

    public static async Task<SoundtrackWorldIndex?> TryLoadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path)) return null;
        await using var stream = File.OpenRead(path);
        var index = await JsonSerializer.DeserializeAsync<SoundtrackWorldIndex>(
            stream,
            LibraryStore.CreateJsonOptions(),
            cancellationToken);
        if (index is null || index.SchemaVersion is < 1 or > 5)
        {
            throw new InvalidDataException($"'{path}' did not contain a supported Skypod world index.");
        }
        return index;
    }

    public static async Task SaveAsync(
        SoundtrackWorldIndex index,
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(index);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporaryPath = path + ".tmp";
        await using (var stream = new FileStream(
                         temporaryPath,
                         FileMode.Create,
                         FileAccess.Write,
                         FileShare.None,
                         64 * 1024,
                         useAsync: true))
        {
            await JsonSerializer.SerializeAsync(
                stream,
                index,
                LibraryStore.CreateJsonOptions(),
                cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }
        File.Move(temporaryPath, path, overwrite: true);
    }
}
