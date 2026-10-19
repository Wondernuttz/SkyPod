namespace Skypod.Core;

public static class ToolLocator
{
    public static string? FindAudioEncoder()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "tools", OperatingSystem.IsWindows() ? "Skypod.AudioEncoder.exe" : "Skypod.AudioEncoder");
        return File.Exists(bundled) ? bundled : null;
    }

    public static string? FindFfmpeg(string? configuredPath = null)
    {
        return FindTool("ffmpeg.exe", configuredPath, [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Virtual Desktop Streamer", "ffmpeg.exe")
        ]);
    }

    public static string? FindXwmaEncoder(string? configuredPath = null)
    {
        return FindTool("xWMAEncode.exe", configuredPath, [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft DirectX SDK (June 2010)", "Utilities", "bin", "x86", "xWMAEncode.exe")
        ]);
    }

    private static string? FindTool(string fileName, string? configuredPath, IEnumerable<string> additionalCandidates)
    {
        var candidates = new List<string?>
        {
            configuredPath,
            Path.Combine(AppContext.BaseDirectory, "tools", fileName),
            Path.Combine(AppContext.BaseDirectory, fileName),
            Path.Combine(FindProjectRoot() ?? AppContext.BaseDirectory, "tools", fileName)
        };
        candidates.AddRange(additionalCandidates);

        foreach (var candidate in candidates.Where(candidate => !string.IsNullOrWhiteSpace(candidate)))
        {
            if (File.Exists(candidate)) return Path.GetFullPath(candidate!);
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        return path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(directory => Path.Combine(directory, fileName))
            .FirstOrDefault(File.Exists);
    }

    private static string? FindProjectRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Skypod.slnx"))) return current.FullName;
            current = current.Parent;
        }
        return null;
    }
}
