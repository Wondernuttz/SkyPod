using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace Skypod.Core;

public static class BossMusicBuilder
{
    public const string FileName = "Skypod.bosses.tsv";

    internal static IReadOnlyList<BossMusicAssignment> Build(SkyrimMod patch, SoundtrackWorldIndex index,
        IReadOnlyList<SoundtrackCatalogTrack> catalog, IReadOnlyDictionary<string, PlaybackRoutingRule> rules,
        IReadOnlySet<string> excluded, ICollection<string> warnings)
    {
        var encounters = index.Bosses.ToDictionary(boss => boss.Id, StringComparer.OrdinalIgnoreCase);
        var selected = new Dictionary<string, List<SoundtrackCatalogTrack>>(StringComparer.OrdinalIgnoreCase);
        foreach (var track in catalog)
        {
            if (excluded.Contains(track.Id) || !rules.TryGetValue(track.Id, out var rule) ||
                rule.Mode == AutomaticPlaybackMode.ManualOnly) continue;
            foreach (var id in rule.SelectedBossIds.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!encounters.ContainsKey(id))
                {
                    warnings.Add($"{track.Title}: selected boss is absent from the active load order: {id}");
                    continue;
                }
                if (!selected.TryGetValue(id, out var tracks)) selected[id] = tracks = [];
                tracks.Add(track);
            }
        }
        var output = new List<BossMusicAssignment>();
        foreach (var (id, tracks) in selected.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id))).ToLowerInvariant()[..16];
            var links = new ExtendedList<IFormLinkGetter<IMusicTrackGetter>>();
            foreach (var track in tracks)
            {
                if (track.ChildTrackIds.Any(excluded.Contains))
                {
                    warnings.Add($"{track.Title}: this palette contains excluded music and was omitted from boss playback.");
                    continue;
                }
                if (!track.PreviewAvailable || !uint.TryParse(track.PreviewLocalFormId, NumberStyles.HexNumber,
                        CultureInfo.InvariantCulture, out var localId)) continue;
                var preview = patch.MusicTypes.FirstOrDefault(type => type.FormKey.ID == localId);
                if (preview?.Tracks is null) continue;
                foreach (var link in preview.Tracks)
                    for (var weight = 0; weight < Math.Clamp(track.Weight, 1, 10); weight++) links.Add(link);
            }
            if (links.Count == 0)
            {
                warnings.Add($"{encounters[id].Name}: no playable songs remain for this boss.");
                continue;
            }
            var music = patch.MusicTypes.AddNew($"SKYPOD_BOSS_{hash}");
            music.Flags = MusicType.Flag.CycleTracks | MusicType.Flag.DucksCurrentTrack;
            music.Data = new MusicTypeData { Priority = 1, DuckingDecibel = 100f };
            music.FadeDuration = 2f;
            music.Tracks = links;
            output.Add(new(encounters[id], BossEncounterIndexer.Key(music.FormKey)));
        }
        return output;
    }

    internal static void Write(string directory, IReadOnlyList<BossMusicAssignment> assignments)
    {
        var lines = new List<string> { "# Skypod boss encounters v1" };
        foreach (var assignment in assignments)
        {
            static string Safe(string value) => value.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
            void Add(string kind, string target)
            {
                if (!MutagenMusicRouting.TryParseFormKey(target, out var targetKey) ||
                    !MutagenMusicRouting.TryParseFormKey(assignment.MusicTypeFormKey, out var musicKey))
                    throw new InvalidDataException("Invalid boss encounter reference.");
                lines.Add(string.Join('\t', Safe(assignment.Encounter.Id), musicKey.ModKey.FileName,
                    musicKey.ID.ToString("X6"), kind, targetKey.ModKey.FileName,
                    targetKey.ID.ToString("X6"), Safe(assignment.Encounter.Name)));
            }
            foreach (var actor in assignment.Encounter.ActorReferences) Add("ref", actor);
            foreach (var actor in assignment.Encounter.ActorBases) Add("base", actor);
        }
        var runtime = Path.Combine(directory, "SKSE", "Plugins");
        Directory.CreateDirectory(runtime);
        File.WriteAllLines(Path.Combine(runtime, FileName), lines, new UTF8Encoding(false));
    }
}
