namespace Skypod.Core;

/// <summary>A city view uses real location ancestry and exterior entrance coordinates.</summary>
public sealed record CityMapOverview(
    SoundtrackPlace City,
    SoundtrackWorldMap Map,
    IReadOnlyList<SoundtrackPlace> Buildings,
    IReadOnlyList<SoundtrackPlace> MapPlaces,
    double MinX, double MinY, double MaxX, double MaxY)
{
    public static CityMapOverview? Create(SoundtrackWorldIndex index, SoundtrackPlace city)
    {
        if (city.Kind != SoundtrackPlaceKind.Location || city.Group is not ("city" or "town") ||
            city.EditorId.Contains("Sanctuary", StringComparison.OrdinalIgnoreCase)) return null;
        var locations = index.Places.Where(p => p.Kind == SoundtrackPlaceKind.Location)
            .ToDictionary(p => p.FormKey, StringComparer.OrdinalIgnoreCase);
        bool BelongsToCity(SoundtrackPlace place)
        {
            var parent = place.ParentLocationFormKey;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (parent is not null && seen.Add(parent))
            {
                if (parent.Equals(city.FormKey, StringComparison.OrdinalIgnoreCase)) return true;
                parent = locations.TryGetValue(parent, out var ancestor) ? ancestor.ParentLocationFormKey : null;
            }
            return false;
        }
        var interiorRoots = index.Places.Where(p => p.Scope == SoundtrackPlaceScope.InteriorOnly)
            .Select(p => p.RootPlaceId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var buildings = locations.Values.Where(p => BelongsToCity(p) && interiorRoots.Contains(p.Id))
            .OrderBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray();
        if (buildings.Length == 0) return null;
        var map = index.Maps.FirstOrDefault(m => m.LocationFormKey?.Equals(city.FormKey, StringComparison.OrdinalIgnoreCase) == true)
            ?? index.Maps.FirstOrDefault(m => index.Places.Any(p => p.Kind == SoundtrackPlaceKind.Worldspace &&
                p.FormKey.Equals(m.FormKey, StringComparison.OrdinalIgnoreCase) &&
                p.ParentLocationFormKey?.Equals(city.FormKey, StringComparison.OrdinalIgnoreCase) == true))
            ?? index.Maps.FirstOrDefault(m => m.FormKey.Equals(city.Map?.WorldspaceFormKey, StringComparison.OrdinalIgnoreCase));
        if (map is null) return null;
        var worlds = index.Maps.ToDictionary(m => m.FormKey, StringComparer.OrdinalIgnoreCase);
        bool SharesCoordinates(SoundtrackPlace p)
        {
            if (p.Map is null) return false;
            if (p.Map.WorldspaceFormKey.Equals(map.FormKey, StringComparison.OrdinalIgnoreCase)) return true;
            // Include adjacent child worldspaces such as the Dragonsreach porch,
            // only when both use the same parent terrain coordinates.
            return map.TerrainParentEditorId is not null &&
                worlds.TryGetValue(p.Map.WorldspaceFormKey, out var other) &&
                other.TerrainParentEditorId?.Equals(map.TerrainParentEditorId, StringComparison.OrdinalIgnoreCase) == true;
        }
        var pins = buildings.Where(SharesCoordinates).ToArray();
        if (pins.Length == 0) return null;
        var minX = pins.Min(p => p.Map!.X); var maxX = pins.Max(p => p.Map!.X);
        var minY = pins.Min(p => p.Map!.Y); var maxY = pins.Max(p => p.Map!.Y);
        var margin = Math.Max(1800, Math.Max(maxX - minX, maxY - minY) * .12);
        var ids = pins.Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var mapPlaces = index.Places.Where(p => ids.Contains(p.RootPlaceId ?? p.Id)).ToArray();
        return new(city, map, buildings, mapPlaces, minX-margin, minY-margin, maxX+margin, maxY+margin);
    }
}
