using System.Text;

namespace Skypod.Core;

public static class PathSanitizer
{
    public static string ToSafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var builder = new StringBuilder(value.Length);

        foreach (var character in value.Trim())
        {
            if (invalid.Contains(character) || char.IsControl(character))
            {
                builder.Append('_');
            }
            else if (char.IsWhiteSpace(character))
            {
                builder.Append('-');
            }
            else
            {
                builder.Append(character);
            }
        }

        var result = builder.ToString().Trim('.', '-', '_');
        while (result.Contains("--", StringComparison.Ordinal))
        {
            result = result.Replace("--", "-", StringComparison.Ordinal);
        }

        return string.IsNullOrWhiteSpace(result) ? "untitled" : result;
    }

    public static void EnsureSafeOutputDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("The output directory is required.", nameof(path));
        }

        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        if (string.Equals(fullPath.TrimEnd(Path.DirectorySeparatorChar), root?.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("A drive root cannot be used as Skypod output.");
        }

        if (!Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar)).Contains("Skypod", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("For safety, the final output folder name must contain 'Skypod'.");
        }
    }
}
