using Mutagen.Bethesda.Environments;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Order;
using Mutagen.Bethesda.Skyrim;
using System.Text.RegularExpressions;

namespace Skypod.Core;

public static class BossEncounterIndexer
{
    // Boss is distinct from BossContainer and BossTreasureMarker.
    private static readonly FormKey BossMarker = new(ModKey.FromNameAndExtension("Skyrim.esm"), 0x130F7);
    internal static string Key(FormKey key) => $"{key.ModKey.FileName}|{key.ID:X6}";
    private static string Friendly(string? value) => Regex.Replace(value ?? "", @"(?<=[a-z0-9])(?=[A-Z])|[_]+", " ").Trim();

    internal static IReadOnlyList<BossEncounter> Build(IGameEnvironment<ISkyrimMod, ISkyrimModGetter> environment)
        => Build(environment.LinkCache,
            environment.LoadOrder.PriorityOrder.Location().WinningOverrides(),
            environment.LoadOrder.PriorityOrder.PlacedNpc().WinningOverrides(),
            environment.LoadOrder.PriorityOrder.Npc().WinningOverrides());

    internal static IReadOnlyList<BossEncounter> Build(ILinkCache<ISkyrimMod, ISkyrimModGetter> cache,
        IEnumerable<ILocationGetter> locationRecords, IEnumerable<IPlacedNpcGetter> actorRecords, IEnumerable<INpcGetter> npcRecords)
    {
        var locations = locationRecords.Where(location => !location.IsDeleted).ToArray();
        var result = new Dictionary<string, BossEncounter>(StringComparer.OrdinalIgnoreCase);
        foreach (var location in locations)
        {
            var removed = (location.LocationRefTypeReferencesRemoved ?? []).Select(link => link.FormKey).ToHashSet();
            var references = (location.LocationRefTypeReferencesStatic ?? [])
                .Concat(location.LocationRefTypeReferencesAdded ?? [])
                .Where(reference => reference.LocationRefType.FormKey == BossMarker && !removed.Contains(reference.Ref.FormKey));
            var dungeon = (location.Keywords ?? []).Any(link => cache.TryResolve<IKeywordGetter>(link.FormKey, out var keyword) &&
                keyword.EditorID is "LocTypeDungeon" or "LocTypeClearable" or "LocTypeDragonLair");
            foreach (var reference in references)
            {
                if (!cache.TryResolve<IPlacedNpcGetter>(reference.Ref.FormKey, out var actor) || actor.IsDeleted) continue;
                cache.TryResolve<INpcGetter>(actor.Base.FormKey, out var npc);
                var place = location.Name?.String ?? Friendly(location.EditorID);
                var name = npc?.Name?.String;
                if (string.IsNullOrWhiteSpace(name)) name = $"{place} boss";
                var id = $"boss:ref:{Key(actor.FormKey)}".ToLowerInvariant();
                var entry = new BossEncounter(id, name, place, actor.FormKey.ModKey.FileName.ToString(),
                    dungeon ? "Dungeon bosses" : "Other game-tagged actors", "Game Boss marker",
                    [Key(actor.FormKey)], []);
                // A parent location may repeat the same reference. Keep one selectable actor.
                if (!result.TryGetValue(id, out var previous) ||
                    (previous.Group != "Dungeon bosses" && dungeon)) result[id] = entry;
            }
        }

        // Include winning actor tags even when a mod did not regenerate its
        // location's cached reference table. Deleted actors never become targets.
        foreach (var actor in actorRecords)
        {
            if (actor.IsDeleted || !(actor.LocationRefTypes ?? []).Any(type => type.FormKey == BossMarker)) continue;
            var id = $"boss:ref:{Key(actor.FormKey)}".ToLowerInvariant();
            if (result.ContainsKey(id)) continue;
            cache.TryResolve<INpcGetter>(actor.Base.FormKey, out var npc);
            var locationKey = actor.PersistentLocation.IsNull ? actor.LocationReference.FormKey : actor.PersistentLocation.FormKey;
            cache.TryResolve<ILocationGetter>(locationKey, out var location);
            var place = location?.Name?.String ?? (location?.EditorID is { } editor ? Friendly(editor) : "Location not specified");
            var dungeon = (location?.Keywords ?? []).Any(link => cache.TryResolve<IKeywordGetter>(link.FormKey, out var keyword) &&
                keyword.EditorID is "LocTypeDungeon" or "LocTypeClearable" or "LocTypeDragonLair");
            var name = npc?.Name?.String;
            if (string.IsNullOrWhiteSpace(name)) name = location is null ? "Game-marked boss" : $"{place} boss";
            result[id] = new(id, name, place, actor.FormKey.ModKey.FileName.ToString(),
                dungeon ? "Dungeon bosses" : "Other game-tagged actors", "Game Boss marker", [Key(actor.FormKey)], []);
        }

        // Scripted major fights do not consistently use the location Boss marker.
        // Restrict these entries to the game's combat actors, never their dialogue,
        // cinematic, test, or player-summoned variants. Resolve installed winners.
        var npcs = npcRecords
            .Where(npc => !npc.IsDeleted && npc.EditorID is not null)
            .GroupBy(npc => (npc.FormKey.ModKey.FileName.ToString().ToLowerInvariant(), npc.EditorID!.ToLowerInvariant()))
            .ToDictionary(group => group.Key, group => group.First());
        void Scripted(string slug, string name, string place, string plugin, params string[] editorIds)
        {
            var bases = editorIds.Select(id => npcs.GetValueOrDefault((plugin.ToLowerInvariant(), id.ToLowerInvariant())))
                .OfType<INpcGetter>().Select(npc => Key(npc.FormKey)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (bases.Length == 0) return;
            var id = $"boss:encounter:{plugin.ToLowerInvariant()}:{slug}";
            result[id] = new(id, name, place, plugin, "Named encounters", "Scripted combat encounter", [], bases);
        }
        Scripted("alduin-mountain", "Alduin: Throat of the World", "Throat of the World", "Skyrim.esm", "MQ206Alduin");
        Scripted("alduin-sovngarde", "Alduin: Sovngarde", "Sovngarde", "Skyrim.esm", "MQ304Alduin");
        Scripted("harkon", "Lord Harkon", "Castle Volkihar", "Dawnguard.esm", "DLC1HarkonCombat", "DLC1HarkonCombatMagic", "DLC1HarkonCombatMelee");
        Scripted("miraak", "Miraak", "Apocrypha", "Dragonborn.esm", "DLC2MiraakMQ06");
        Scripted("ebony-warrior", "The Ebony Warrior", "Last Vigil", "Dragonborn.esm", "DLC2EbonyWarrior");
        Scripted("karstaag", "Karstaag", "Castle Karstaag Ruins", "Dragonborn.esm", "DLC2dunKarstaag");
        return result.Values.OrderBy(entry => entry.Group == "Named encounters" ? 0 : entry.Group == "Dungeon bosses" ? 1 : 2)
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.Location, StringComparer.OrdinalIgnoreCase).ThenBy(entry => entry.Id, StringComparer.Ordinal).ToArray();
    }
}
