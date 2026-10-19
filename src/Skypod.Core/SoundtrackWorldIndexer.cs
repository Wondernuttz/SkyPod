using System.Globalization;
using System.Text.RegularExpressions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Environments;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Order;
using Mutagen.Bethesda.Skyrim;

namespace Skypod.Core;

/// <summary>
/// Discovers the winning place-to-MUSC assignments from the active Skyrim load
/// order.  It does not write a patch and can therefore be used by Studio before
/// an Output build as well as by <see cref="SkyrimMusicPluginBuilder"/>.
/// </summary>
public sealed class SoundtrackWorldIndexer
{
    private static readonly Regex WordBoundary = new(
        @"(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])|[_\-]+",
        RegexOptions.Compiled);
    private static readonly Regex ExteriorCellName = new(
        @"^(?<name>.+?)Exterior\d+$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public SoundtrackWorldIndex Build(BuildSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ModManagerBuildValidator.Validate(settings, "place discovery");

        var environmentBuilder = GameEnvironment.Typical
            .Builder<ISkyrimMod, ISkyrimModGetter>(GameRelease.SkyrimVR);
        if (!string.IsNullOrWhiteSpace(settings.TargetDataFolder))
        {
            environmentBuilder = environmentBuilder.WithTargetDataFolder(settings.TargetDataFolder);
        }
        if (settings.ExplicitLoadOrder is { Count: > 0 })
        {
            environmentBuilder = environmentBuilder.WithLoadOrder(settings.ExplicitLoadOrder
                .Select(name => ModKey.FromNameAndExtension(name))
                .ToArray());
        }

        using var environment = environmentBuilder.Build();
        return Build(environment);
    }

    internal SoundtrackWorldIndex Build(IGameEnvironment<ISkyrimMod, ISkyrimModGetter> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var loadOrder = environment.LoadOrder;

        var musicTypeContexts = loadOrder.PriorityOrder
            .MusicType()
            .WinningContextOverrides()
            .ToArray();
        var musicTypes = musicTypeContexts.ToDictionary(
            context => context.Record.FormKey,
            context => context.Record);
        var musicTypePlugins = musicTypeContexts.ToDictionary(
            context => context.Record.FormKey,
            context => context.ModKey.FileName.ToString());
        var musicByKey = musicTypes.Values
            .ToDictionary(
                type => type.FormKey,
                type => DescribeMusicType(type, musicTypePlugins[type.FormKey]));
        var musicIndex = musicByKey.Values
            .OrderBy(type => type.EditorId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(type => type.FormKey, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var locationContexts = loadOrder.PriorityOrder.Location().WinningContextOverrides().ToArray();
        var worldspaceContexts = loadOrder.PriorityOrder.Worldspace().WinningContextOverrides().ToArray();
        var cellContexts = loadOrder.PriorityOrder.Cell().WinningContextOverrides(environment.LinkCache).ToArray();
        var regionContexts = loadOrder.PriorityOrder.Region().WinningContextOverrides().ToArray();
        var keywordEditorIds = loadOrder.PriorityOrder.Keyword().WinningOverrides()
            .ToDictionary(keyword => keyword.FormKey, keyword => keyword.EditorID ?? string.Empty);
        var locationsByKey = locationContexts.ToDictionary(context => context.Record.FormKey);
        var worldspacesByKey = worldspaceContexts.ToDictionary(context => context.Record.FormKey);
        var placeBuilders = new Dictionary<string, PlaceBuilder>(StringComparer.OrdinalIgnoreCase);

        PlaceBuilder EnsurePlace(
            FormKey formKey,
            string plugin,
            SoundtrackPlaceKind kind,
            string? editorId,
            string? fullName,
            string? worldspaceFormKey = null,
            string? worldspaceName = null,
            string? parentLocationFormKey = null)
        {
            var id = StablePlaceId(kind, formKey);
            if (placeBuilders.TryGetValue(id, out var existing)) return existing;
            var builder = new PlaceBuilder(
                id,
                FriendlyPlaceName(fullName, editorId, kind, formKey),
                editorId ?? string.Empty,
                FormatFormKey(formKey),
                plugin,
                kind,
                worldspaceFormKey,
                worldspaceName,
                parentLocationFormKey);
            placeBuilders.Add(id, builder);
            return builder;
        }

        void AddAssignment(
            PlaceBuilder target,
            PlaceBuilder source,
            IFormLinkNullableGetter<IMusicTypeGetter> music,
            string provenance,
            bool isDirect)
        {
            if (music.IsNull || !musicByKey.TryGetValue(music.FormKey, out var musicType)) return;
            target.Assignments.Add(new SoundtrackMusicAssignment(
                provenance,
                isDirect,
                source.Id,
                source.FormKey,
                source.DisplayName,
                musicType.Id,
                musicType.FormKey,
                musicType.EditorId,
                musicType.Plugin,
                musicType.Category,
                musicType.Safety,
                musicType.SafetyReason));
        }

        void AddToLocationAndAncestors(
            FormKey locationKey,
            PlaceBuilder source,
            IFormLinkNullableGetter<IMusicTypeGetter> music,
            string provenance,
            bool directAtFirst)
        {
            var visited = new HashSet<FormKey>();
            var current = locationKey;
            var direct = directAtFirst;
            while (!current.IsNull && visited.Add(current) && locationsByKey.TryGetValue(current, out var context))
            {
                var location = context.Record;
                var target = EnsurePlace(
                    location.FormKey,
                    context.ModKey.FileName.ToString(),
                    SoundtrackPlaceKind.Location,
                    location.EditorID,
                    location.Name?.String,
                    parentLocationFormKey: location.ParentLocation.IsNull
                        ? null
                        : FormatFormKey(location.ParentLocation.FormKey));
                AddAssignment(target, source, music, provenance, direct);
                if (location.ParentLocation.IsNull) break;
                current = location.ParentLocation.FormKey;
                direct = false;
            }
        }

        // Named locations and worldspaces must remain selectable even when they
        // inherit their music. Most Skyrim LCTNs have no NAM1, and Tamriel's
        // ZNAM deliberately points to silence while its REGNs provide music.
        foreach (var context in locationContexts)
        {
            var location = context.Record;
            var place = EnsurePlace(
                location.FormKey,
                context.ModKey.FileName.ToString(),
                SoundtrackPlaceKind.Location,
                location.EditorID,
                location.Name?.String,
                parentLocationFormKey: location.ParentLocation.IsNull
                    ? null
                    : FormatFormKey(location.ParentLocation.FormKey));
            place.GroupOverride ??= InferLocationGroup(location, keywordEditorIds);
        }
        foreach (var context in worldspaceContexts)
        {
            var worldspace = context.Record;
            EnsurePlace(
                worldspace.FormKey,
                context.ModKey.FileName.ToString(),
                SoundtrackPlaceKind.Worldspace,
                worldspace.EditorID,
                worldspace.Name?.String,
                FormatFormKey(worldspace.FormKey),
                worldspace.Name?.String,
                worldspace.Location.IsNull ? null : FormatFormKey(worldspace.Location.FormKey));
        }
        foreach (var context in regionContexts)
        {
            var region = context.Record;
            var worldspaceName = !region.Worldspace.IsNull && worldspacesByKey.TryGetValue(region.Worldspace.FormKey, out var worldContext)
                ? worldContext.Record.Name?.String
                : null;
            EnsurePlace(
                region.FormKey,
                context.ModKey.FileName.ToString(),
                SoundtrackPlaceKind.Region,
                region.EditorID,
                null,
                region.Worldspace.IsNull ? null : FormatFormKey(region.Worldspace.FormKey),
                worldspaceName);
        }

        foreach (var context in locationContexts)
        {
            var location = context.Record;
            if (location.Music.IsNull) continue;
            var source = EnsurePlace(
                location.FormKey,
                context.ModKey.FileName.ToString(),
                SoundtrackPlaceKind.Location,
                location.EditorID,
                location.Name?.String,
                parentLocationFormKey: location.ParentLocation.IsNull
                    ? null
                    : FormatFormKey(location.ParentLocation.FormKey));
            AddToLocationAndAncestors(location.FormKey, source, location.Music, "LCTN.NAM1", true);
        }

        foreach (var context in worldspaceContexts)
        {
            var worldspace = context.Record;
            var source = EnsurePlace(
                worldspace.FormKey,
                context.ModKey.FileName.ToString(),
                SoundtrackPlaceKind.Worldspace,
                worldspace.EditorID,
                worldspace.Name?.String,
                FormatFormKey(worldspace.FormKey),
                worldspace.Name?.String,
                worldspace.Location.IsNull ? null : FormatFormKey(worldspace.Location.FormKey));
            AddAssignment(source, source, worldspace.Music, "WRLD.ZNAM", true);
            if (!worldspace.Location.IsNull)
            {
                AddToLocationAndAncestors(
                    worldspace.Location.FormKey,
                    source,
                    worldspace.Music,
                    "WRLD.ZNAM",
                    false);
            }
        }

        foreach (var context in cellContexts)
        {
            var cell = context.Record;
            if (cell.Music.IsNull) continue;
            var parentContext = context.Parent;
            while (parentContext is not null && parentContext.Record is not IWorldspaceGetter)
            {
                parentContext = parentContext.Parent;
            }
            var parentWorldspace = parentContext?.Record as IWorldspaceGetter;
            var source = EnsurePlace(
                cell.FormKey,
                context.ModKey.FileName.ToString(),
                SoundtrackPlaceKind.Cell,
                cell.EditorID,
                cell.Name?.String,
                parentWorldspace is null ? null : FormatFormKey(parentWorldspace.FormKey),
                parentWorldspace?.Name?.String,
                cell.Location.IsNull ? null : FormatFormKey(cell.Location.FormKey));
            AddAssignment(source, source, cell.Music, "CELL.XCMO", true);

            if (!cell.Location.IsNull)
            {
                AddToLocationAndAncestors(cell.Location.FormKey, source, cell.Music, "CELL.XCMO", false);
            }
            if (parentWorldspace is not null && worldspacesByKey.TryGetValue(parentWorldspace.FormKey, out var worldContext))
            {
                var target = EnsurePlace(
                    parentWorldspace.FormKey,
                    worldContext.ModKey.FileName.ToString(),
                    SoundtrackPlaceKind.Worldspace,
                    parentWorldspace.EditorID,
                    parentWorldspace.Name?.String,
                    FormatFormKey(parentWorldspace.FormKey),
                    parentWorldspace.Name?.String,
                    parentWorldspace.Location.IsNull ? null : FormatFormKey(parentWorldspace.Location.FormKey));
                AddAssignment(target, source, cell.Music, "CELL.XCMO", false);
            }
        }

        foreach (var context in regionContexts)
        {
            var region = context.Record;
            if (region.Sounds is null || region.Sounds.Music.IsNull) continue;
            var worldspaceFormKey = region.Worldspace.IsNull ? null : FormatFormKey(region.Worldspace.FormKey);
            var worldspaceName = !region.Worldspace.IsNull && worldspacesByKey.TryGetValue(region.Worldspace.FormKey, out var worldContext)
                ? worldContext.Record.Name?.String
                : null;
            var source = EnsurePlace(
                region.FormKey,
                context.ModKey.FileName.ToString(),
                SoundtrackPlaceKind.Region,
                region.EditorID,
                null,
                worldspaceFormKey,
                worldspaceName);
            AddAssignment(source, source, region.Sounds.Music, "REGN.RDMO", true);

            if (!region.Worldspace.IsNull && worldspacesByKey.TryGetValue(region.Worldspace.FormKey, out worldContext))
            {
                var worldspace = worldContext.Record;
                var target = EnsurePlace(
                    worldspace.FormKey,
                    worldContext.ModKey.FileName.ToString(),
                    SoundtrackPlaceKind.Worldspace,
                    worldspace.EditorID,
                    worldspace.Name?.String,
                    FormatFormKey(worldspace.FormKey),
                    worldspace.Name?.String,
                    worldspace.Location.IsNull ? null : FormatFormKey(worldspace.Location.FormKey));
                AddAssignment(target, source, region.Sounds.Music, "REGN.RDMO", false);
            }
        }

        var areaCandidates = new List<AreaCellCandidate>();
        foreach (var context in cellContexts)
        {
            var cell = context.Record;
            var match = ExteriorCellName.Match(cell.EditorID ?? string.Empty);
            if (!match.Success) continue;
            var parentContext = context.Parent;
            while (parentContext is not null && parentContext.Record is not IWorldspaceGetter)
            {
                parentContext = parentContext.Parent;
            }
            var worldspace = parentContext?.Record as IWorldspaceGetter;
            areaCandidates.Add(new AreaCellCandidate(
                match.Groups["name"].Value,
                cell.FormKey,
                context.ModKey.FileName.ToString(),
                cell.EditorID ?? string.Empty,
                cell.Name?.String,
                cell.Location.IsNull ? null : cell.Location.FormKey,
                worldspace?.FormKey,
                worldspace?.Name?.String));
        }

        foreach (var group in areaCandidates
                     .GroupBy(candidate => candidate.GroupKey, StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Count() >= 2))
        {
            var members = group.OrderBy(candidate => candidate.EditorId, StringComparer.OrdinalIgnoreCase).ToArray();
            var first = members[0];
            var friendlyPrefix = FriendlyPlaceName(null, first.Prefix, SoundtrackPlaceKind.Area, first.FormKey);
            var id = StableAreaId(
                first.Prefix,
                first.WorldspaceFormKey,
                first.LocationFormKey,
                first.FormKey);
            var area = new PlaceBuilder(
                id,
                $"Outside {friendlyPrefix}",
                first.Prefix + "Exterior",
                string.Empty,
                first.FormKey.ModKey.FileName.ToString(),
                SoundtrackPlaceKind.Area,
                first.WorldspaceFormKey is null ? null : FormatFormKey(first.WorldspaceFormKey.Value),
                first.WorldspaceName,
                first.LocationFormKey is null ? null : FormatFormKey(first.LocationFormKey.Value));
            area.ConditionFormKeys.AddRange(members.Select(member => FormatFormKey(member.FormKey)));
            placeBuilders[id] = area;

            foreach (var member in members)
            {
                if (placeBuilders.TryGetValue(StablePlaceId(SoundtrackPlaceKind.Cell, member.FormKey), out var cellPlace))
                {
                    area.Assignments.AddRange(cellPlace.Assignments.Select(assignment => assignment with
                    {
                        Provenance = $"AREA member {member.EditorId} via {assignment.Provenance}",
                        IsDirect = false
                    }));
                }
            }
            foreach (var locationKey in members
                         .Where(member => member.LocationFormKey is not null)
                         .Select(member => member.LocationFormKey!.Value)
                         .Distinct())
            {
                if (placeBuilders.TryGetValue(StablePlaceId(SoundtrackPlaceKind.Location, locationKey), out var location))
                {
                    area.Assignments.AddRange(location.Assignments.Select(assignment => assignment with
                    {
                        Provenance = $"AREA location via {assignment.Provenance}",
                        IsDirect = false
                    }));
                }
            }
        }

        // Build map geometry from the same winning records used for routing.
        // Coordinates are presentation metadata only; the generated plugin
        // continues to route against stable FormKeys and concrete cells.
        var mapCells = new List<MapCellCandidate>();
        foreach (var context in cellContexts)
        {
            var cell = context.Record;
            var parentContext = context.Parent;
            while (parentContext is not null && parentContext.Record is not IWorldspaceGetter)
            {
                parentContext = parentContext.Parent;
            }
            var worldspace = parentContext?.Record as IWorldspaceGetter;
            var locationKey = cell.Location.IsNull ? (FormKey?)null : cell.Location.FormKey;
            var isInterior = cell.Flags.HasFlag(Cell.Flag.IsInteriorCell);
            if (!isInterior && worldspace is not null && cell.Grid is not null)
            {
                mapCells.Add(new MapCellCandidate(
                    cell.FormKey,
                    locationKey,
                    worldspace.FormKey,
                    cell.Grid.Point.X,
                    cell.Grid.Point.Y,
                    false));
            }
            else
            {
                mapCells.Add(new MapCellCandidate(cell.FormKey, locationKey, null, 0, 0, true));
            }
        }

        IEnumerable<FormKey> LocationAndAncestors(FormKey start)
        {
            var current = start;
            var visited = new HashSet<FormKey>();
            while (!current.IsNull && visited.Add(current) && locationsByKey.TryGetValue(current, out var context))
            {
                yield return current;
                if (context.Record.ParentLocation.IsNull) yield break;
                current = context.Record.ParentLocation.FormKey;
            }
        }

        var interiorCellsByLocation = new Dictionary<FormKey, List<MapCellCandidate>>();
        var exteriorCellsByLocation = new Dictionary<FormKey, List<MapCellCandidate>>();
        foreach (var cell in mapCells.Where(candidate => candidate.LocationFormKey is not null))
        {
            foreach (var locationKey in LocationAndAncestors(cell.LocationFormKey!.Value))
            {
                var target = cell.IsInterior ? interiorCellsByLocation : exteriorCellsByLocation;
                if (!target.TryGetValue(locationKey, out var members))
                {
                    members = [];
                    target.Add(locationKey, members);
                }
                members.Add(cell);
            }
        }

        foreach (var context in locationContexts)
        {
            var location = context.Record;
            var rootId = StablePlaceId(SoundtrackPlaceKind.Location, location.FormKey);
            if (!placeBuilders.TryGetValue(rootId, out var root)) continue;
            root.Scope = SoundtrackPlaceScope.WholeLocation;

            exteriorCellsByLocation.TryGetValue(location.FormKey, out var exteriorMembers);
            interiorCellsByLocation.TryGetValue(location.FormKey, out var interiorMembers);
            exteriorMembers ??= [];
            interiorMembers ??= [];

            if (exteriorMembers.Count > 0)
            {
                var worldGroup = exteriorMembers
                    .GroupBy(member => member.WorldspaceFormKey!.Value)
                    .OrderByDescending(group => group.Count())
                    .First();
                var exterior = worldGroup.DistinctBy(member => member.CellFormKey).ToArray();
                root.SetMapFromCells(worldGroup.Key, "location exterior cells", exterior, 0.72);
                root.WorldspaceFormKeyOverride ??= FormatFormKey(worldGroup.Key);
                root.WorldspaceNameOverride ??= worldspacesByKey.TryGetValue(worldGroup.Key, out var worldContext)
                    ? worldContext.Record.Name?.String
                    : null;

                var scoped = new PlaceBuilder(
                    StableScopedAreaId("exterior", location.FormKey),
                    $"{root.DisplayName} — exterior only",
                    location.EditorID + "ExteriorScope",
                    string.Empty,
                    context.ModKey.FileName.ToString(),
                    SoundtrackPlaceKind.Area,
                    FormatFormKey(worldGroup.Key),
                    root.WorldspaceNameOverride,
                    root.FormKey)
                {
                    Scope = SoundtrackPlaceScope.ExteriorOnly,
                    RootPlaceId = rootId,
                    GroupOverride = "exteriorArea"
                };
                scoped.ConditionFormKeys.AddRange(exterior.Select(member => FormatFormKey(member.CellFormKey)));
                scoped.Assignments.AddRange(root.Assignments);
                scoped.SetMapFromCells(worldGroup.Key, "location exterior cells", exterior, 0.76);
                placeBuilders[scoped.Id] = scoped;
            }

            if (interiorMembers.Count > 0)
            {
                var interior = interiorMembers.DistinctBy(member => member.CellFormKey).ToArray();
                var scoped = new PlaceBuilder(
                    StableScopedAreaId("interior", location.FormKey),
                    $"{root.DisplayName} — interior only",
                    location.EditorID + "InteriorScope",
                    string.Empty,
                    context.ModKey.FileName.ToString(),
                    SoundtrackPlaceKind.Area,
                    root.WorldspaceFormKeyOverride ?? root.WorldspaceFormKey,
                    root.WorldspaceNameOverride ?? root.WorldspaceName,
                    root.FormKey)
                {
                    Scope = SoundtrackPlaceScope.InteriorOnly,
                    RootPlaceId = rootId,
                    GroupOverride = "interior"
                };
                scoped.ConditionFormKeys.AddRange(interior.Select(member => FormatFormKey(member.CellFormKey)));
                scoped.Assignments.AddRange(root.Assignments);
                scoped.CopyMapFrom(root, "associated interior entrance");
                placeBuilders[scoped.Id] = scoped;
            }
        }

        // LCTN world-location markers are the most authoritative visual anchor
        // for a named place, including mod-added homes and DLC locations.
        foreach (var context in locationContexts)
        {
            var location = context.Record;
            if (location.WorldLocationMarkerRef.IsNull ||
                !environment.LinkCache.TryResolve<IPlacedObjectGetter>(location.WorldLocationMarkerRef.FormKey, out var marker) ||
                marker.Placement is null)
            {
                continue;
            }
            var rootId = StablePlaceId(SoundtrackPlaceKind.Location, location.FormKey);
            if (!placeBuilders.TryGetValue(rootId, out var root)) continue;
            var worldspaceKey = root.MapWorldspaceFormKey;
            if (worldspaceKey is null && exteriorCellsByLocation.TryGetValue(location.FormKey, out var exterior))
            {
                worldspaceKey = exterior.FirstOrDefault(candidate => candidate.WorldspaceFormKey is not null)?.WorldspaceFormKey;
            }
            if (worldspaceKey is null) continue;
            // Some locations reuse another location's marker (the three
            // Hearthfire homesteads do this). Keep the actual cell anchor
            // when that marker is nowhere near this location's exterior.
            if (exteriorCellsByLocation.TryGetValue(location.FormKey, out var markerCells) &&
                markerCells.Count > 0 && !markerCells.Any(cell =>
                    cell.WorldspaceFormKey == worldspaceKey &&
                    Math.Abs((cell.GridX + .5) * 4096 - marker.Placement.Position.X) < 12288 &&
                    Math.Abs((cell.GridY + .5) * 4096 - marker.Placement.Position.Y) < 12288)) continue;
            root.SetMapAnchor(
                worldspaceKey.Value,
                "location map marker",
                marker.Placement.Position.X,
                marker.Placement.Position.Y,
                1.0);
        }

        // Discover placed map markers and exterior-to-interior load doors.
        // This catches mod locations that omit LCTN.WorldLocationMarkerRef.
        var placedCandidates = new Dictionary<FormKey, PlacedMapCandidate>();
        foreach (var context in loadOrder.PriorityOrder.PlacedObject().WinningContextOverrides(environment.LinkCache))
        {
            var placed = context.Record;
            if (placed.MapMarker is null && placed.TeleportDestination is null) continue;
            var parentContext = context.Parent;
            while (parentContext is not null && parentContext.Record is not ICellGetter)
            {
                parentContext = parentContext.Parent;
            }
            if (parentContext?.Record is not ICellGetter cell || placed.Placement is null) continue;
            var worldContext = parentContext.Parent;
            while (worldContext is not null && worldContext.Record is not IWorldspaceGetter)
            {
                worldContext = worldContext.Parent;
            }
            var worldspace = worldContext?.Record as IWorldspaceGetter;
            var isInterior = cell.Flags.HasFlag(Cell.Flag.IsInteriorCell);
            var locationKey = !placed.LocationReference.IsNull
                ? placed.LocationReference.FormKey
                : !cell.Location.IsNull ? cell.Location.FormKey : (FormKey?)null;
            placedCandidates[placed.FormKey] = new PlacedMapCandidate(
                placed.FormKey,
                placed.MapMarker?.Name?.String,
                locationKey,
                isInterior ? null : worldspace?.FormKey,
                cell.FormKey,
                isInterior,
                placed.Placement.Position.X,
                placed.Placement.Position.Y,
                placed.TeleportDestination?.Door.FormKey,
                cell.Name?.String);
        }

        foreach (var marker in placedCandidates.Values.Where(candidate => !string.IsNullOrWhiteSpace(candidate.MarkerName)))
        {
            if (marker.LocationFormKey is not null &&
                placeBuilders.TryGetValue(StablePlaceId(SoundtrackPlaceKind.Location, marker.LocationFormKey.Value), out var location) &&
                marker.WorldspaceFormKey is not null)
            {
                location.SetMapAnchor(marker.WorldspaceFormKey.Value, "placed map marker", marker.X, marker.Y, 0.96);
                continue;
            }
            if (marker.WorldspaceFormKey is null) continue;
            var id = StableMarkerAreaId(marker.PlacedFormKey);
            var synthetic = new PlaceBuilder(
                id,
                marker.MarkerName!,
                $"MapMarker{marker.PlacedFormKey.ID:X6}",
                string.Empty,
                marker.PlacedFormKey.ModKey.FileName.ToString(),
                SoundtrackPlaceKind.Area,
                FormatFormKey(marker.WorldspaceFormKey.Value),
                worldspacesByKey.TryGetValue(marker.WorldspaceFormKey.Value, out var worldContext)
                    ? worldContext.Record.Name?.String
                    : null,
                null)
            {
                Scope = SoundtrackPlaceScope.MapMarker,
                GroupOverride = "location"
            };
            synthetic.ConditionFormKeys.Add(FormatFormKey(marker.CellFormKey));
            synthetic.SetMapAnchor(marker.WorldspaceFormKey.Value, "placed map marker", marker.X, marker.Y, 0.9);
            placeBuilders[id] = synthetic;
        }

        foreach (var exteriorDoor in placedCandidates.Values.Where(candidate =>
                     !candidate.IsInterior && candidate.DestinationDoorFormKey is not null && candidate.WorldspaceFormKey is not null))
        {
            if (!placedCandidates.TryGetValue(exteriorDoor.DestinationDoorFormKey!.Value, out var interiorDoor) ||
                !interiorDoor.IsInterior || interiorDoor.LocationFormKey is null)
            {
                continue;
            }
            var locationId = StablePlaceId(SoundtrackPlaceKind.Location, interiorDoor.LocationFormKey.Value);
            if (!placeBuilders.TryGetValue(locationId, out var location)) continue;
            location.SetMapAnchor(
                exteriorDoor.WorldspaceFormKey!.Value,
                "exterior load door",
                exteriorDoor.X,
                exteriorDoor.Y,
                string.Equals(interiorDoor.CellName, location.DisplayName, StringComparison.OrdinalIgnoreCase) ? 0.97 : 0.93);
        }

        // Some game records reuse one location for unrelated interiors. Follow
        // linked rooms from the building's own named cell instead of including
        // an unrelated barracks merely because it shares that location record.
        foreach (var root in placeBuilders.Values.Where(p => p.Kind == SoundtrackPlaceKind.Location &&
                     p.GroupOverride is not ("city" or "town" or "hold")))
        {
            var scoped = placeBuilders.Values.FirstOrDefault(p => p.RootPlaceId == root.Id && p.Scope == SoundtrackPlaceScope.InteriorOnly);
            if (scoped is null || scoped.ConditionFormKeys.Count < 2) continue;
            var allowed = scoped.ConditionFormKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var seeds = placedCandidates.Values.Where(p => p.IsInterior &&
                string.Equals(p.CellName, root.DisplayName, StringComparison.OrdinalIgnoreCase) &&
                allowed.Contains(FormatFormKey(p.CellFormKey))).Select(p => p.CellFormKey).ToHashSet();
            if (seeds.Count == 0) continue;
            var pending = new Queue<FormKey>(seeds);
            while (pending.TryDequeue(out var current))
            {
                foreach (var door in placedCandidates.Values.Where(p => p.IsInterior && p.CellFormKey == current && p.DestinationDoorFormKey is not null))
                    if (placedCandidates.TryGetValue(door.DestinationDoorFormKey!.Value, out var destination) && destination.IsInterior &&
                        allowed.Contains(FormatFormKey(destination.CellFormKey)) && seeds.Add(destination.CellFormKey))
                        pending.Enqueue(destination.CellFormKey);
            }
            scoped.ConditionFormKeys.RemoveAll(key => !seeds.Any(cell => FormatFormKey(cell).Equals(key, StringComparison.OrdinalIgnoreCase)));
        }

        // Scoped entries inherit a marker or load-door anchor discovered after
        // their cell groups were created.
        foreach (var context in locationContexts.Where(context => !context.Record.ParentLocation.IsNull))
        {
            if (placeBuilders.TryGetValue(StablePlaceId(SoundtrackPlaceKind.Location, context.Record.FormKey), out var child) &&
                placeBuilders.TryGetValue(StablePlaceId(SoundtrackPlaceKind.Location, context.Record.ParentLocation.FormKey), out var parent) &&
                child.DisplayName.Equals(parent.DisplayName, StringComparison.OrdinalIgnoreCase))
                parent.CopyMapFrom(child, "associated building entrance");
        }
        foreach (var scoped in placeBuilders.Values.Where(builder => builder.RootPlaceId is not null))
        {
            if (placeBuilders.TryGetValue(scoped.RootPlaceId!, out var root))
            {
                scoped.CopyMapFrom(root, scoped.Scope == SoundtrackPlaceScope.InteriorOnly
                    ? "associated interior entrance"
                    : "associated location");
            }
        }

        foreach (var context in regionContexts)
        {
            var region = context.Record;
            if (region.Worldspace.IsNull ||
                !placeBuilders.TryGetValue(StablePlaceId(SoundtrackPlaceKind.Region, region.FormKey), out var place))
            {
                continue;
            }
            var polygons = (region.RegionAreas ?? [])
                .Where(area => area.RegionPointListData is { Count: >= 3 })
                .Select(area => new SoundtrackMapPolygon(area.RegionPointListData!
                    .Select(point => new SoundtrackMapPoint(point.X, point.Y))
                    .ToArray()))
                .ToArray();
            place.SetMapAreas(region.Worldspace.FormKey, "region boundary", polygons, 0.88);
        }

        var places = placeBuilders.Values
            .Select(builder => builder.Build())
            .OrderBy(place => place.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(place => place.Kind)
            .ThenBy(place => place.FormKey, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var maps = worldspaceContexts
            .Select(context =>
            {
                var worldspace = context.Record;
                var cells = mapCells
                    .Where(cell => cell.WorldspaceFormKey == worldspace.FormKey)
                    .DistinctBy(cell => cell.CellFormKey)
                    .ToArray();
                if (cells.Length == 0) return null;
                var worldspaceKey = FormatFormKey(worldspace.FormKey);
                var mappedPoints = places
                    .Where(place => place.Map?.WorldspaceFormKey.Equals(worldspaceKey, StringComparison.OrdinalIgnoreCase) == true)
                    .SelectMany(place => place.Map!.Areas.Count > 0
                        ? place.Map.Areas.SelectMany(area => area.Points)
                        : [new SoundtrackMapPoint(place.Map.X, place.Map.Y)])
                    .ToArray();
                var minX = mappedPoints.Length >= 2
                    ? mappedPoints.Min(point => point.X)
                    : cells.Min(cell => cell.GridX * 4096d);
                var minY = mappedPoints.Length >= 2
                    ? mappedPoints.Min(point => point.Y)
                    : cells.Min(cell => cell.GridY * 4096d);
                var maxX = mappedPoints.Length >= 2
                    ? mappedPoints.Max(point => point.X)
                    : cells.Max(cell => (cell.GridX + 1) * 4096d);
                var maxY = mappedPoints.Length >= 2
                    ? mappedPoints.Max(point => point.Y)
                    : cells.Max(cell => (cell.GridY + 1) * 4096d);
                var span = Math.Max(maxX - minX, maxY - minY);
                var margin = Math.Max(4096d, span * 0.035);
                return new SoundtrackWorldMap(
                    $"map:{worldspace.FormKey.ModKey.FileName.ToString().ToLowerInvariant()}:{worldspace.FormKey.ID:x6}",
                    FriendlyPlaceName(worldspace.Name?.String, worldspace.EditorID, SoundtrackPlaceKind.Worldspace, worldspace.FormKey),
                    worldspace.EditorID ?? string.Empty,
                    FormatFormKey(worldspace.FormKey),
                    context.ModKey.FileName.ToString(),
                    InferMapStyle(worldspace.EditorID, worldspace.Name?.String),
                    minX - margin,
                    minY - margin,
                    maxX + margin,
                    maxY + margin,
                    cells.Length,
                    !IsOfficialPlugin(worldspace.FormKey.ModKey.FileName.ToString()))
                {
                    LocationFormKey = worldspace.Location.IsNull ? null : FormatFormKey(worldspace.Location.FormKey),
                    ParentWorldspaceFormKey = worldspace.Parent?.Worldspace.IsNull == false
                        ? FormatFormKey(worldspace.Parent.Worldspace.FormKey) : null,
                    TerrainParentEditorId = worldspace.Parent is { } parent &&
                        parent.Flags.HasFlag(WorldspaceParent.Flag.UseLandData) &&
                        worldspacesByKey.TryGetValue(parent.Worldspace.FormKey, out var parentWorld)
                        ? parentWorld.Record.EditorID : null
                };
            })
            .Where(map => map is not null)
            .Cast<SoundtrackWorldMap>()
            .OrderBy(map => MapSortOrder(map.Style, map.ExteriorCellCount))
            .ThenByDescending(map => map.ExteriorCellCount)
            .ThenBy(map => map.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new SoundtrackWorldIndex(places, musicIndex)
        {
            Bosses = BossEncounterIndexer.Build(environment),
            Maps = maps
        };
    }

    private static SoundtrackMusicType DescribeMusicType(IMusicTypeGetter type, string winnerPlugin)
    {
        var editorId = type.EditorID ?? FormatFormKey(type.FormKey);
        var category = SkyrimMusicPluginBuilder.InferCategoryFromText(editorId);
        var flags = Enum.GetValues<MusicType.Flag>()
            .Where(flag => flag != 0 && type.Flags.HasFlag(flag))
            .Select(flag => flag.ToString())
            .ToArray();
        var priority = type.Data?.Priority ?? 0;
        var (safety, reason) = ClassifySafety(editorId, category, priority, type.Flags, type.Tracks?.Count ?? 0);
        var formKey = FormatFormKey(type.FormKey);
        var searchText = string.Join(' ', new[]
        {
            editorId,
            formKey,
            winnerPlugin,
            category.ToString(),
            category.GetDisplayName(),
            safety.ToString(),
            reason,
            string.Join(' ', flags)
        }).ToLowerInvariant();

        return new SoundtrackMusicType(
            StableMusicTypeId(type.FormKey),
            formKey,
            editorId,
            winnerPlugin,
            category,
            safety,
            reason,
            priority,
            flags,
            type.FadeDuration ?? 0,
            searchText);
    }

    internal static (MusicTypeSafety Safety, string Reason) ClassifySafety(
        string editorId,
        TrackCategory category,
        ushort priority,
        MusicType.Flag flags,
        int trackCount)
    {
        if (trackCount == 0 || editorId.Equals("_NONE", StringComparison.OrdinalIgnoreCase) ||
            editorId.Contains("silence", StringComparison.OrdinalIgnoreCase))
        {
            return (MusicTypeSafety.Silence, "The palette is empty or explicitly provides silence.");
        }

        if (category == TrackCategory.MainMenu || category == TrackCategory.Special ||
            flags.HasFlag(MusicType.Flag.PlaysOneSelection))
        {
            return (MusicTypeSafety.Event, "The palette is one-shot or event-specific.");
        }

        if (category is TrackCategory.Combat or TrackCategory.BossCombat or TrackCategory.DragonCombat)
        {
            return (MusicTypeSafety.Contextual, "The palette is safe only while its combat context is active.");
        }

        if (flags.HasFlag(MusicType.Flag.CycleTracks) &&
            category is TrackCategory.ExplorationDay or TrackCategory.ExplorationNight or
                TrackCategory.Dungeon or TrackCategory.Town or TrackCategory.City or
                TrackCategory.Tavern or TrackCategory.Castle)
        {
            return (MusicTypeSafety.Ambient, "The palette is an ordinary ambient or location soundtrack target.");
        }

        return (MusicTypeSafety.Unknown, "The palette could not be classified safely from its winning record.");
    }

    internal static string StablePlaceId(SoundtrackPlaceKind kind, FormKey formKey) =>
        $"place:{kind.ToString().ToLowerInvariant()}:{formKey.ModKey.FileName.ToString().ToLowerInvariant()}:{formKey.ID:x6}";

    internal static string StableMusicTypeId(FormKey formKey) =>
        $"music:{formKey.ModKey.FileName.ToString().ToLowerInvariant()}:{formKey.ID:x6}";

    internal static string StableAreaId(
        string prefix,
        FormKey? worldspace,
        FormKey? location,
        FormKey fallback)
    {
        var slug = Regex.Replace(prefix.ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
        var anchor = worldspace ?? location ?? fallback;
        var locationSuffix = worldspace is not null && location is not null
            ? $":{location.Value.ModKey.FileName.ToString().ToLowerInvariant()}:{location.Value.ID:x6}"
            : string.Empty;
        return $"place:area:{anchor.ModKey.FileName.ToString().ToLowerInvariant()}:{anchor.ID:x6}{locationSuffix}:{slug}";
    }

    internal static string StableScopedAreaId(string scope, FormKey location) =>
        $"place:area:{scope}:{location.ModKey.FileName.ToString().ToLowerInvariant()}:{location.ID:x6}";

    internal static string StableMarkerAreaId(FormKey marker) =>
        $"place:area:marker:{marker.ModKey.FileName.ToString().ToLowerInvariant()}:{marker.ID:x6}";

    private static SoundtrackMapStyle InferMapStyle(string? editorId, string? name)
    {
        var text = $"{editorId} {name}".ToLowerInvariant();
        if (text.Contains("solstheim")) return SoundtrackMapStyle.Solstheim;
        if (text.Contains("apocrypha")) return SoundtrackMapStyle.Apocrypha;
        if (text.Contains("sovngarde")) return SoundtrackMapStyle.Sovngarde;
        if (text.Contains("soulcairn") || text.Contains("soul cairn")) return SoundtrackMapStyle.SoulCairn;
        if (text.Contains("falmer") || text.Contains("forgottenvale") || text.Contains("forgotten vale"))
        {
            return SoundtrackMapStyle.ForgottenVale;
        }
        if (text.Contains("tamriel") || text.Equals("skyrim skyrim", StringComparison.Ordinal))
        {
            return SoundtrackMapStyle.Skyrim;
        }
        return SoundtrackMapStyle.ArcaneRealm;
    }

    private static int MapSortOrder(SoundtrackMapStyle style, int cellCount) => style switch
    {
        SoundtrackMapStyle.Skyrim => 0,
        SoundtrackMapStyle.Solstheim => 1,
        SoundtrackMapStyle.Apocrypha => 2,
        SoundtrackMapStyle.Sovngarde => 3,
        SoundtrackMapStyle.SoulCairn => 4,
        SoundtrackMapStyle.ForgottenVale => 5,
        _ => cellCount >= 16 ? 6 : 7
    };

    private static bool IsOfficialPlugin(string plugin) =>
        plugin.Equals("Skyrim.esm", StringComparison.OrdinalIgnoreCase) ||
        plugin.Equals("Update.esm", StringComparison.OrdinalIgnoreCase) ||
        plugin.StartsWith("Dawnguard", StringComparison.OrdinalIgnoreCase) ||
        plugin.StartsWith("HearthFires", StringComparison.OrdinalIgnoreCase) ||
        plugin.StartsWith("Dragonborn", StringComparison.OrdinalIgnoreCase);

    private static string FriendlyPlaceName(
        string? fullName,
        string? editorId,
        SoundtrackPlaceKind kind,
        FormKey formKey)
    {
        if (!string.IsNullOrWhiteSpace(fullName)) return fullName.Trim();

        var source = editorId ?? string.Empty;
        if (kind == SoundtrackPlaceKind.Region)
        {
            source = Regex.Replace(source, @"^(?:Weather|AudioExt|Audio|Region)", string.Empty, RegexOptions.IgnoreCase);
        }
        source = Regex.Replace(source, @"(?:Location|Worldspace|World|Region|Cell)$", string.Empty, RegexOptions.IgnoreCase);
        source = WordBoundary.Replace(source, " ").Trim();
        source = Regex.Replace(source, @"\s+", " ");
        if (string.IsNullOrWhiteSpace(source)) source = FormatFormKey(formKey);
        var name = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(source.ToLowerInvariant());
        return kind == SoundtrackPlaceKind.Region ? $"{name} Region" : name;
    }

    internal static string FormatFormKey(FormKey formKey) =>
        $"{formKey.ModKey.FileName}|{formKey.ID:X6}";

    private static string RegionSearchAliases(string editorId)
    {
        var text = editorId.ToLowerInvariant();
        if (text.Contains("tundramarsh")) return "morthal hjaalmarch marsh";
        if (text.Contains("volcanictundra")) return "windhelm eastmarch volcanic tundra";
        if (text.Contains("tundra")) return "whiterun whiterun hold plains tundra";
        if (text.Contains("pineforest") || text.Contains("falkreath")) return "falkreath hold pine forest";
        if (text.Contains("fallforest")) return "riften the rift fall forest";
        if (text.Contains("reach")) return "markarth the reach";
        if (text.Contains("coast")) return "solitude haafingar dawnstar pale winterhold northern coast";
        if (text.Contains("snow")) return "winterhold dawnstar pale northern snow";
        if (text.Contains("mountain")) return "mountain mountains high hrothgar";
        return string.Empty;
    }

    internal static string InferPlaceGroup(SoundtrackPlaceKind kind, string editorId)
    {
        var text = editorId.ToLowerInvariant();
        if (kind == SoundtrackPlaceKind.Area) return "exteriorArea";
        if (kind == SoundtrackPlaceKind.Cell) return "cell";
        if (kind == SoundtrackPlaceKind.Region) return "regionBiome";
        if (kind == SoundtrackPlaceKind.Worldspace)
        {
            if (text.Contains("whiterun") || text.Contains("solitude") || text.Contains("markarth") ||
                text.Contains("riften") || text.Contains("windhelm")) return "city";
            return "worldspace";
        }
        if (text is "whiterunlocation" or "solitudelocation" or "markarthlocation" or
            "riftenlocation" or "windhelmlocation") return "city";
        // "WinterholdLocation" and records such as
        // "CWCampsiteWinterholdLocation" are ordinary locations whose names
        // happen to end in "holdLocation". A real hold either uses Skyrim's
        // doubled WinterholdHoldLocation name or another explicit HoldLocation
        // suffix.
        if (text.EndsWith("holdlocation", StringComparison.Ordinal) &&
            (!text.EndsWith("winterholdlocation", StringComparison.Ordinal) ||
             text.EndsWith("winterholdholdlocation", StringComparison.Ordinal))) return "hold";
        if (text.Contains("dungeon") || text.Contains("cave") || text.Contains("barrow") ||
            text.Contains("ruin") || text.Contains("fort") || text.Contains("mine")) return "dungeon";
        return "location";
    }

    private static string? InferLocationGroup(
        ILocationGetter location,
        IReadOnlyDictionary<FormKey, string> keywordEditorIds)
    {
        var keywords = (location.Keywords ?? [])
            .Select(keyword => keywordEditorIds.TryGetValue(keyword.FormKey, out var editorId) ? editorId : string.Empty)
            .Where(editorId => !string.IsNullOrWhiteSpace(editorId))
            .ToArray();
        // Hold membership keywords are also used by broad realm containers
        // such as Solstheim. The explicit *HoldLocation editor ID rule is the
        // reliable source for Skyrim's nine political holds.
        if (keywords.Any(editorId => editorId.Contains("LocTypeCity", StringComparison.OrdinalIgnoreCase))) return "city";

        var isTown = keywords.Any(editorId =>
            editorId.Contains("LocTypeTown", StringComparison.OrdinalIgnoreCase) ||
            editorId.Contains("LocTypeVillage", StringComparison.OrdinalIgnoreCase));
        var identity = $"{location.EditorID} {location.Name?.String}";
        var isSpecializedSite = keywords.Any(editorId =>
            editorId.Contains("Farm", StringComparison.OrdinalIgnoreCase) ||
            editorId.Contains("Mine", StringComparison.OrdinalIgnoreCase) ||
            editorId.Contains("Mill", StringComparison.OrdinalIgnoreCase) ||
            editorId.Contains("Guild", StringComparison.OrdinalIgnoreCase) ||
            editorId.Contains("Sanctuary", StringComparison.OrdinalIgnoreCase) ||
            editorId.Contains("Dungeon", StringComparison.OrdinalIgnoreCase) ||
            editorId.Contains("Cave", StringComparison.OrdinalIgnoreCase) ||
            editorId.Contains("Fort", StringComparison.OrdinalIgnoreCase) ||
            editorId.Contains("Camp", StringComparison.OrdinalIgnoreCase) ||
            editorId.Contains("PlayerHouse", StringComparison.OrdinalIgnoreCase)) ||
            new[] { "Farm", "Mine", "Mill", "Guild", "Sanctuary", "Dungeon", "Cave", "Fort", "Camp", "PlayerHouse" }
                .Any(word => identity.Contains(word, StringComparison.OrdinalIgnoreCase));
        if (isTown && !isSpecializedSite) return "town";
        return null;
    }

    private sealed class PlaceBuilder(
        string id,
        string displayName,
        string editorId,
        string formKey,
        string plugin,
        SoundtrackPlaceKind kind,
        string? worldspaceFormKey,
        string? worldspaceName,
        string? parentLocationFormKey)
    {
        public string Id { get; } = id;
        public string DisplayName { get; } = displayName;
        public string EditorId { get; } = editorId;
        public string FormKey { get; } = formKey;
        public string Plugin { get; } = plugin;
        public SoundtrackPlaceKind Kind { get; } = kind;
        public string? WorldspaceFormKey { get; } = worldspaceFormKey;
        public string? WorldspaceName { get; } = worldspaceName;
        public string? WorldspaceFormKeyOverride { get; set; }
        public string? WorldspaceNameOverride { get; set; }
        public string? ParentLocationFormKey { get; } = parentLocationFormKey;
        public List<string> ConditionFormKeys { get; } = string.IsNullOrWhiteSpace(formKey) ? [] : [formKey];
        public List<SoundtrackMusicAssignment> Assignments { get; } = [];
        public SoundtrackPlaceScope Scope { get; set; }
        public string? RootPlaceId { get; set; }
        public string? GroupOverride { get; set; }
        public FormKey? MapWorldspaceFormKey { get; private set; }
        private string? MapPlacementSource { get; set; }
        private double MapX { get; set; }
        private double MapY { get; set; }
        private double MapConfidence { get; set; }
        private List<SoundtrackMapPolygon> MapAreas { get; } = [];

        public void SetMapAnchor(
            FormKey worldspace,
            string source,
            double x,
            double y,
            double confidence)
        {
            if (MapWorldspaceFormKey is not null && confidence < MapConfidence) return;
            MapWorldspaceFormKey = worldspace;
            MapPlacementSource = source;
            MapX = x;
            MapY = y;
            MapConfidence = confidence;
        }

        public void SetMapFromCells(
            FormKey worldspace,
            string source,
            IReadOnlyList<MapCellCandidate> cells,
            double confidence)
        {
            if (cells.Count == 0) return;
            var polygons = CellBoundaryPolygons(cells);
            SetMapAreas(worldspace, source, polygons, confidence);
        }

        public void SetMapAreas(
            FormKey worldspace,
            string source,
            IReadOnlyList<SoundtrackMapPolygon> polygons,
            double confidence)
        {
            if (polygons.Count == 0) return;
            if (MapWorldspaceFormKey is null || confidence >= MapConfidence)
            {
                var points = polygons.SelectMany(polygon => polygon.Points).ToArray();
                SetMapAnchor(
                    worldspace,
                    source,
                    points.Average(point => point.X),
                    points.Average(point => point.Y),
                    confidence);
            }
            if (MapWorldspaceFormKey == worldspace)
            {
                MapAreas.Clear();
                MapAreas.AddRange(polygons);
            }
        }

        public void CopyMapFrom(PlaceBuilder source, string placementSource)
        {
            if (source.MapWorldspaceFormKey is null || source.MapConfidence < MapConfidence) return;
            SetMapAnchor(
                source.MapWorldspaceFormKey.Value,
                placementSource,
                source.MapX,
                source.MapY,
                source.MapConfidence);
            if (MapAreas.Count == 0 && source.MapAreas.Count > 0) MapAreas.AddRange(source.MapAreas);
        }

        private static IReadOnlyList<SoundtrackMapPolygon> CellBoundaryPolygons(
            IReadOnlyList<MapCellCandidate> cells)
        {
            var occupied = cells
                .Select(cell => new GridVertex(cell.GridX, cell.GridY))
                .ToHashSet();
            var remaining = new HashSet<GridEdge>();
            foreach (var cell in occupied)
            {
                var x = cell.X;
                var y = cell.Y;
                if (!occupied.Contains(new GridVertex(x, y - 1)))
                    remaining.Add(new GridEdge(new GridVertex(x, y), new GridVertex(x + 1, y)));
                if (!occupied.Contains(new GridVertex(x + 1, y)))
                    remaining.Add(new GridEdge(new GridVertex(x + 1, y), new GridVertex(x + 1, y + 1)));
                if (!occupied.Contains(new GridVertex(x, y + 1)))
                    remaining.Add(new GridEdge(new GridVertex(x + 1, y + 1), new GridVertex(x, y + 1)));
                if (!occupied.Contains(new GridVertex(x - 1, y)))
                    remaining.Add(new GridEdge(new GridVertex(x, y + 1), new GridVertex(x, y)));
            }

            var polygons = new List<SoundtrackMapPolygon>();
            while (remaining.Count > 0)
            {
                var first = remaining.First();
                var edge = first;
                var vertices = new List<GridVertex>();
                var guard = remaining.Count + 1;
                while (guard-- > 0)
                {
                    vertices.Add(edge.Start);
                    remaining.Remove(edge);
                    if (edge.End == first.Start) break;

                    var incomingDirection = Direction(edge);
                    var candidates = remaining.Where(candidate => candidate.Start == edge.End).ToArray();
                    if (candidates.Length == 0) break;
                    edge = candidates
                        .OrderBy(candidate => TurnRank(incomingDirection, Direction(candidate)))
                        .First();
                }

                vertices = RemoveCollinearVertices(vertices);
                if (vertices.Count < 3) continue;
                polygons.Add(new SoundtrackMapPolygon(vertices
                    .Select(vertex => new SoundtrackMapPoint(vertex.X * 4096d, vertex.Y * 4096d))
                    .ToArray()));
            }
            return polygons;
        }

        private static int Direction(GridEdge edge) => (edge.End.X - edge.Start.X, edge.End.Y - edge.Start.Y) switch
        {
            (1, 0) => 0,
            (0, 1) => 1,
            (-1, 0) => 2,
            (0, -1) => 3,
            _ => 0
        };

        private static int TurnRank(int incoming, int outgoing) => ((outgoing - incoming + 4) % 4) switch
        {
            1 => 0, // Keep the occupied cell on the left at shared vertices.
            0 => 1,
            3 => 2,
            _ => 3
        };

        private static List<GridVertex> RemoveCollinearVertices(IReadOnlyList<GridVertex> vertices)
        {
            if (vertices.Count < 4) return vertices.ToList();
            var result = new List<GridVertex>(vertices.Count);
            for (var i = 0; i < vertices.Count; i++)
            {
                var previous = vertices[(i - 1 + vertices.Count) % vertices.Count];
                var current = vertices[i];
                var next = vertices[(i + 1) % vertices.Count];
                var firstDx = current.X - previous.X;
                var firstDy = current.Y - previous.Y;
                var secondDx = next.X - current.X;
                var secondDy = next.Y - current.Y;
                if (firstDx * secondDy != firstDy * secondDx) result.Add(current);
            }
            return result;
        }

        private readonly record struct GridVertex(int X, int Y);
        private readonly record struct GridEdge(GridVertex Start, GridVertex End);

        public SoundtrackPlace Build()
        {
            var assignments = Assignments
                .Distinct()
                .OrderBy(assignment => assignment.MusicTypeEditorId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(assignment => assignment.Provenance, StringComparer.OrdinalIgnoreCase)
                .ThenBy(assignment => assignment.SourcePlaceFormKey, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var searchText = string.Join(' ', new[]
            {
                DisplayName,
                EditorId,
                Kind.ToString(),
                GroupOverride ?? InferPlaceGroup(Kind, EditorId),
                FormKey,
                Plugin,
                WorldspaceNameOverride ?? WorldspaceName ?? string.Empty,
                WorldspaceFormKeyOverride ?? WorldspaceFormKey ?? string.Empty,
                ParentLocationFormKey ?? string.Empty,
                string.Join(' ', ConditionFormKeys),
                string.Join(' ', assignments.SelectMany(assignment => new[]
                {
                    assignment.MusicTypeEditorId,
                    assignment.MusicTypeFormKey,
                    assignment.MusicTypePlugin,
                    assignment.Category.ToString(),
                    assignment.Category.GetDisplayName(),
                    assignment.Provenance,
                    assignment.SourcePlaceName
                })),
                Kind == SoundtrackPlaceKind.Region ? RegionSearchAliases(EditorId) : string.Empty
            }).ToLowerInvariant();

            return new SoundtrackPlace(
                Id,
                DisplayName,
                EditorId,
                FormKey,
                Plugin,
                Kind,
                GroupOverride ?? InferPlaceGroup(Kind, EditorId),
                WorldspaceFormKeyOverride ?? WorldspaceFormKey,
                WorldspaceNameOverride ?? WorldspaceName,
                ParentLocationFormKey,
                Kind == SoundtrackPlaceKind.Cell,
                ConditionFormKeys.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                assignments,
                searchText)
            {
                Scope = Scope,
                RootPlaceId = RootPlaceId,
                Map = MapWorldspaceFormKey is null
                    ? null
                    : new SoundtrackPlaceMapData(
                        FormatFormKey(MapWorldspaceFormKey.Value),
                        MapPlacementSource ?? "game records",
                        MapX,
                        MapY,
                        MapAreas.ToArray(),
                        MapConfidence)
            };
        }
    }

    private sealed record AreaCellCandidate(
        string Prefix,
        FormKey FormKey,
        string Plugin,
        string EditorId,
        string? Name,
        FormKey? LocationFormKey,
        FormKey? WorldspaceFormKey,
        string? WorldspaceName)
    {
        public string GroupKey =>
            $"{Prefix.ToLowerInvariant()}|{WorldspaceFormKey?.ToString() ?? string.Empty}|{LocationFormKey?.ToString() ?? string.Empty}";
    }

    private sealed record MapCellCandidate(
        FormKey CellFormKey,
        FormKey? LocationFormKey,
        FormKey? WorldspaceFormKey,
        int GridX,
        int GridY,
        bool IsInterior);

    private sealed record PlacedMapCandidate(
        FormKey PlacedFormKey,
        string? MarkerName,
        FormKey? LocationFormKey,
        FormKey? WorldspaceFormKey,
        FormKey CellFormKey,
        bool IsInterior,
        float X,
        float Y,
        FormKey? DestinationDoorFormKey,
        string? CellName);
}
