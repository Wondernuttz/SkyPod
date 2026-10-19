using System.Text.RegularExpressions;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Skypod.Core;

public static partial class SkyrimVrDataLocator
{
    public static string? TryFindDataFolder()
    {
        if (!OperatingSystem.IsWindows()) return null;

        foreach (var candidate in CandidateDataFolders())
        {
            if (IsValidDataFolder(candidate)) return Path.GetFullPath(candidate);
        }
        return null;
    }

    public static bool IsValidDataFolder(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        Directory.Exists(path) &&
        File.Exists(Path.Combine(path, "Skyrim.esm"));

    [SupportedOSPlatform("windows")]
    private static IEnumerable<string> CandidateDataFolders()
    {
        var installedPath = ReadRegistryString(
            Registry.LocalMachine,
            @"SOFTWARE\WOW6432Node\Bethesda Softworks\Skyrim VR",
            "Installed Path") ?? ReadRegistryString(
            Registry.LocalMachine,
            @"SOFTWARE\Bethesda Softworks\Skyrim VR",
            "Installed Path");
        if (!string.IsNullOrWhiteSpace(installedPath))
        {
            yield return Path.Combine(installedPath, "Data");
        }

        foreach (var steamRoot in SteamRoots())
        {
            yield return Path.Combine(steamRoot, "steamapps", "common", "SkyrimVR", "Data");
            yield return Path.Combine(steamRoot, "steamapps", "common", "Skyrim VR", "Data");
        }

        yield return @"C:\Games\steamapps\common\SkyrimVR\Data";
        yield return @"C:\Games\steamapps\common\Skyrim VR\Data";
        yield return @"C:\Program Files (x86)\Steam\steamapps\common\SkyrimVR\Data";
        yield return @"C:\Program Files (x86)\Steam\steamapps\common\Skyrim VR\Data";
    }

    [SupportedOSPlatform("windows")]
    private static IEnumerable<string> SteamRoots()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var steamPath = ReadRegistryString(
            Registry.CurrentUser,
            @"SOFTWARE\Valve\Steam",
            "SteamPath") ?? ReadRegistryString(
            Registry.LocalMachine,
            @"SOFTWARE\WOW6432Node\Valve\Steam",
            "InstallPath");
        if (!string.IsNullOrWhiteSpace(steamPath)) roots.Add(steamPath.Replace('/', Path.DirectorySeparatorChar));

        foreach (var root in roots.ToArray())
        {
            var librariesPath = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(librariesPath)) continue;
            foreach (Match match in SteamLibraryPathRegex().Matches(File.ReadAllText(librariesPath)))
            {
                var path = match.Groups[1].Value.Replace("\\\\", "\\");
                if (!string.IsNullOrWhiteSpace(path)) roots.Add(path);
            }
        }
        return roots;
    }

    [SupportedOSPlatform("windows")]
    private static string? ReadRegistryString(RegistryKey root, string subKey, string valueName)
    {
        try
        {
            using var key = root.OpenSubKey(subKey);
            return key?.GetValue(valueName) as string;
        }
        catch
        {
            return null;
        }
    }

    [GeneratedRegex("\\\"path\\\"\\s+\\\"([^\\\"]+)\\\"", RegexOptions.IgnoreCase)]
    private static partial Regex SteamLibraryPathRegex();
}
