using System.IO.Compression;
using System.Text.Json;

namespace Skypod.Core;

internal static class PackArchive
{
    public static string SafePath(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Contains(':') || relative.Contains('\\') ||
            relative.Split('/').Any(part => part is "" or "." or "..") || Path.IsPathRooted(relative))
            throw new InvalidDataException($"Invalid pack path: {relative}");
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Pack path escapes its folder.");
        return path;
    }

    public static void Create(string path, Action<ZipArchive> write)
    {
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using (var archive = ZipFile.Open(temporary, ZipArchiveMode.Create)) write(archive);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static void WriteText(ZipArchive archive, string name, string text)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name, CompressionLevel.Optimal).Open());
        writer.Write(text);
    }

    public static T ReadJson<T>(ZipArchive archive, string name)
    {
        var entry = archive.GetEntry(name) ?? throw new InvalidDataException($"Pack is missing {name}.");
        if (entry.Length > 32 * 1024 * 1024) throw new InvalidDataException($"{name} exceeds the size limit.");
        using var stream = entry.Open();
        return JsonSerializer.Deserialize<T>(stream, LibraryStore.CreateJsonOptions())
            ?? throw new InvalidDataException($"Invalid {name}.");
    }
}
