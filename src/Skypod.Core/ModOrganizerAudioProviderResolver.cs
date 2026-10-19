using System.Text.RegularExpressions;

namespace Skypod.Core;

/// <summary>
/// Resolves loose-file ownership from a portable MO2 instance.  Archive
/// inspection is intentionally not guessed: when no loose winner exists the
/// caller retains the winning plugin/archive as the fallback owner.
/// </summary>
public sealed class ModOrganizerAudioProviderResolver
{
    private static readonly Regex SelectedProfilePattern = new(
        @"^\s*selected_profile\s*=\s*(?:@ByteArray\()?(.+?)(?:\))?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly IReadOnlyList<(string Name, string Directory)> _mods;
    private readonly Dictionary<string, string?> _cache = new(StringComparer.OrdinalIgnoreCase);

    private ModOrganizerAudioProviderResolver(IReadOnlyList<(string Name, string Directory)> mods)
    {
        _mods = mods;
    }

    public static ModOrganizerAudioProviderResolver? TryCreate(
        string outputDirectory,
        string? instanceDirectory = null, string? explicitModsDirectory = null, string? explicitProfileDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        if (!string.IsNullOrWhiteSpace(explicitModsDirectory) && !string.IsNullOrWhiteSpace(explicitProfileDirectory))
            return FromProfile(explicitModsDirectory, Path.Combine(explicitProfileDirectory,"modlist.txt"));
        if (string.IsNullOrWhiteSpace(instanceDirectory))
        {
            var outputParent = Directory.GetParent(Path.GetFullPath(outputDirectory));
            if (outputParent is null ||
                !string.Equals(outputParent.Name, "mods", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            instanceDirectory = outputParent.Parent?.FullName;
        }
        if (string.IsNullOrWhiteSpace(instanceDirectory)) return null;
        instanceDirectory = Path.GetFullPath(instanceDirectory);

        var iniPath = Path.Combine(instanceDirectory, "ModOrganizer.ini");
        if (!File.Exists(iniPath)) return null;

        var selectedProfile = File.ReadLines(iniPath)
            .Select(line => SelectedProfilePattern.Match(line))
            .Where(match => match.Success)
            .Select(match => match.Groups[1].Value.Trim())
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(selectedProfile)) return null;

        var modListPath = Path.Combine(instanceDirectory, "profiles", selectedProfile, "modlist.txt");
        var modsDirectory = Path.Combine(instanceDirectory, "mods");
        return FromProfile(modsDirectory, modListPath);
    }

    private static ModOrganizerAudioProviderResolver? FromProfile(string modsDirectory,string modListPath)
    {
        if (!File.Exists(modListPath) || !Directory.Exists(modsDirectory)) return null;

        // MO2 writes the highest-priority mod first in modlist.txt.  Separators
        // can also be enabled, so only retain entries backed by a real folder.
        var mods = new List<(string Name, string Directory)>();
        foreach (var rawLine in File.ReadLines(modListPath))
        {
            var line = rawLine.Trim();
            if (line.Length < 2 || line[0] != '+') continue;

            var name = line[1..].Trim();
            var directory = Path.Combine(modsDirectory, name);
            if (!Directory.Exists(directory)) continue;
            mods.Add((name, directory));
        }

        return mods.Count == 0 ? null : new ModOrganizerAudioProviderResolver(mods);
    }

    public string? ResolveLooseProvider(string dataRelativeAudioPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRelativeAudioPath);
        var normalized = NormalizeDataRelativePath(dataRelativeAudioPath);
        if (_cache.TryGetValue(normalized, out var cached)) return cached;

        var candidates = CandidatePaths(normalized);
        foreach (var mod in _mods)
        {
            if (candidates.Any(relative => FileExists(mod.Directory, relative)))
            {
                _cache[normalized] = mod.Name;
                return mod.Name;
            }
        }

        _cache[normalized] = null;
        return null;
    }

    private static bool FileExists(string root,string relative)
    {
        if(File.Exists(Path.Combine(root,relative)))return true;
        if(OperatingSystem.IsWindows())return false;
        foreach(var part in relative.Split(Path.DirectorySeparatorChar,StringSplitOptions.RemoveEmptyEntries))
        {
            if(part is "." or ".." || !Directory.Exists(root))return false;
            var found=Directory.EnumerateFileSystemEntries(root).FirstOrDefault(p=>Path.GetFileName(p).Equals(part,StringComparison.OrdinalIgnoreCase));
            if(found is null)return false;root=found;
        }
        return File.Exists(root);
    }

    internal static string NormalizeDataRelativePath(string path)
    {
        var normalized = path.Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar)
            .TrimStart(Path.DirectorySeparatorChar);
        var dataPrefix = "Data" + Path.DirectorySeparatorChar;
        if (normalized.StartsWith(dataPrefix, StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[dataPrefix.Length..];
        }
        return normalized;
    }

    private static IReadOnlyList<string> CandidatePaths(string normalized)
    {
        var extension = Path.GetExtension(normalized);
        if (string.Equals(extension, ".wav", StringComparison.OrdinalIgnoreCase))
        {
            // Bethesda's records commonly retain .wav while the shipped or
            // replaced asset is encoded as XWM under the same stem.
            return [Path.ChangeExtension(normalized, ".xwm"), normalized];
        }
        if (string.Equals(extension, ".xwm", StringComparison.OrdinalIgnoreCase))
        {
            return [normalized, Path.ChangeExtension(normalized, ".wav")];
        }
        return [normalized];
    }
}
