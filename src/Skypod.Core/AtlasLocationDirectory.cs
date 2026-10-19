namespace Skypod.Core;

/// <summary>Presentation groups retain every routing scope without counting weather records as places.</summary>
public sealed record AtlasLocationEntry(SoundtrackPlace Place, IReadOnlyList<string> RootIds)
{
    public string Name => AtlasLocationDirectory.Name(Place);
}

public static class AtlasLocationDirectory
{
    public static IReadOnlyList<AtlasLocationEntry> Create(SoundtrackWorldIndex index)
    {
        var roots = index.Places.Where(p => p.RootPlaceId is null &&
            (p.Kind is SoundtrackPlaceKind.Location or SoundtrackPlaceKind.Region or SoundtrackPlaceKind.Worldspace ||
             p.Scope == SoundtrackPlaceScope.MapMarker)).ToArray();
        var groups = roots.Where(p => p.Scope != SoundtrackPlaceScope.MapMarker)
            .ToDictionary(p => p.Id, p => new List<SoundtrackPlace> { p }, StringComparer.OrdinalIgnoreCase);
        foreach (var marker in roots.Where(p => p.Scope == SoundtrackPlaceScope.MapMarker))
        {
            var matches = roots.Where(p => p.Kind == SoundtrackPlaceKind.Location &&
                p.DisplayName.Equals(marker.DisplayName, StringComparison.OrdinalIgnoreCase) &&
                p.Map is not null && marker.Map is not null &&
                p.Map.WorldspaceFormKey.Equals(marker.Map.WorldspaceFormKey, StringComparison.OrdinalIgnoreCase) &&
                Math.Abs(p.Map.X - marker.Map.X) < 8192 && Math.Abs(p.Map.Y - marker.Map.Y) < 8192).ToArray();
            var match = matches.Length == 1 ? matches[0] : groups.Values.Select(g => g[0]).FirstOrDefault(p =>
                p.Scope == SoundtrackPlaceScope.MapMarker && p.DisplayName == marker.DisplayName &&
                p.Map?.WorldspaceFormKey == marker.Map?.WorldspaceFormKey &&
                p.ConditionFormKeys.SequenceEqual(marker.ConditionFormKeys, StringComparer.OrdinalIgnoreCase));
            if (match is not null) groups[match.Id].Add(marker);
            else groups[marker.Id] = [marker];
        }
        return groups.Values.Select(g => new AtlasLocationEntry(g[0], g.Select(p => p.Id).ToArray())).ToArray();
    }

    public static bool BelongsToMap(SoundtrackPlace place, SoundtrackWorldMap map) =>
        place.Map?.WorldspaceFormKey.Equals(map.FormKey, StringComparison.OrdinalIgnoreCase) == true ||
        (place.Map is null && (place.WorldspaceFormKey?.Equals(map.FormKey, StringComparison.OrdinalIgnoreCase) == true ||
         place.FormKey.Equals(map.LocationFormKey, StringComparison.OrdinalIgnoreCase) ||
         place.Kind == SoundtrackPlaceKind.Worldspace && place.FormKey.Equals(map.FormKey, StringComparison.OrdinalIgnoreCase)));

    public static string Name(SoundtrackPlace place) => place.EditorId.ToLowerInvariant() switch
    {
        "dlc2apocrypamiraakstowerlocation" => "Miraak's Tower",
        "dlc2apoislandalocation" => "Apocrypha Island",
        "dlc2book01dungeonlocation" => "Apocrypha - Black Book dungeon",
        "weatherbluepalacewingarena" => "Arena region",
        "weatherbluepalacewingfear" => "Fear region",
        "weatherbluepalacewingnmare" => "Nightmare region",
        "weatherbluepalacewing" => "Pelagius's mind region",
        _ => place.DisplayName
    };
}
