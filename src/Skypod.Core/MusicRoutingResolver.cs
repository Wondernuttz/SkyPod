namespace Skypod.Core;

/// <summary>
/// Resolves a persisted playback rule against the winning music and place
/// index. This class is deliberately record-agnostic: the Mutagen builder owns
/// cloning tracks and writing the resolved links into its patch.
/// </summary>
public static class MusicRoutingResolver
{
    public static PlaybackRoutingResolution Resolve(
        PlaybackRoutingRule? rule,
        TrackCategory sourceCategory,
        SoundtrackWorldIndex worldIndex)
    {
        ArgumentNullException.ThrowIfNull(worldIndex);
        rule ??= new PlaybackRoutingRule();

        var warnings = new List<string>();
        var targets = new List<ResolvedPlaybackTarget>();
        var seenTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var musicByFormKey = worldIndex.MusicTypes
            .GroupBy(type => type.FormKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        void AddTarget(SoundtrackMusicType musicType, PlaybackPlaceCondition? place = null)
        {
            // A grouped exterior area and its advanced raw cell can resolve to
            // the same engine condition. Deduplicate by the actual condition,
            // not by the UI selection ID, or that cell receives double weight.
            var placeKey = place is null
                ? string.Empty
                : $"{place.Kind}:{place.FormKey}";
            var key = musicType.FormKey + "\n" + placeKey;
            if (seenTargets.Add(key)) targets.Add(new ResolvedPlaybackTarget(musicType, place));
        }

        switch (rule.Mode)
        {
            case AutomaticPlaybackMode.BossEncounters:
            case AutomaticPlaybackMode.ManualOnly:
                break;

            case AutomaticPlaybackMode.CategoryDefault:
                foreach (var musicType in worldIndex.MusicTypes)
                {
                    if (IsCategoryDefaultTarget(sourceCategory, musicType)) AddTarget(musicType);
                }
                break;

            case AutomaticPlaybackMode.AnywhereAmbient:
                if (IsCombatCategory(sourceCategory))
                {
                    foreach (var musicType in worldIndex.MusicTypes.Where(type =>
                                 type.Safety == MusicTypeSafety.Contextual &&
                                 CategoriesAreCompatible(sourceCategory, type.Category)))
                    {
                        AddTarget(musicType);
                    }
                }
                else if (sourceCategory is TrackCategory.Special or TrackCategory.MainMenu)
                {
                    warnings.Add(
                        $"{sourceCategory.GetDisplayName()} has no safe Anywhere automatic context and remains manual-only.");
                }
                else
                {
                    foreach (var musicType in worldIndex.MusicTypes.Where(type =>
                                 type.Safety == MusicTypeSafety.Ambient))
                    {
                        AddTarget(musicType);
                    }
                }
                break;

            case AutomaticPlaybackMode.SpecificPlaces:
                ResolvePlaces();
                break;

            case AutomaticPlaybackMode.ExactPlaylistAdvanced:
                ResolveExactMusicTypes();
                break;

            default:
                warnings.Add($"Unsupported automatic playback mode {rule.Mode}.");
                break;
        }

        return new PlaybackRoutingResolution(targets, warnings);

        void ResolvePlaces()
        {
            var placesById = worldIndex.Places
                .GroupBy(place => place.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            foreach (var selectedId in rule.SelectedPlaceIds
                         .Where(id => !string.IsNullOrWhiteSpace(id))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!placesById.TryGetValue(selectedId, out var selectedPlace))
                {
                    warnings.Add($"Selected place {selectedId} is not present in the active load order.");
                    continue;
                }

                var placeTargets = new List<SoundtrackMusicType>();
                var seenPlaceTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (IsCombatCategory(sourceCategory))
                {
                    foreach (var musicType in worldIndex.MusicTypes.Where(type =>
                                 type.Safety == MusicTypeSafety.Contextual &&
                                 CategoriesAreCompatible(sourceCategory, type.Category)))
                    {
                        if (seenPlaceTargets.Add(musicType.FormKey)) placeTargets.Add(musicType);
                    }
                }
                else if (sourceCategory is not (TrackCategory.Special or TrackCategory.MainMenu))
                {
                    var safeAssignments = selectedPlace.Assignments
                        .Where(assignment => assignment.Safety == MusicTypeSafety.Ambient)
                        .Where(assignment => musicByFormKey.TryGetValue(assignment.MusicTypeFormKey, out var type) &&
                                             type.Safety == MusicTypeSafety.Ambient &&
                                             CategoriesAreCompatible(sourceCategory, type.Category))
                        .ToArray();
                    foreach (var assignment in safeAssignments)
                    {
                        if (musicByFormKey.TryGetValue(assignment.MusicTypeFormKey, out var musicType) &&
                            seenPlaceTargets.Add(musicType.FormKey))
                        {
                            placeTargets.Add(musicType);
                        }
                    }
                    foreach (var musicType in worldIndex.MusicTypes.Where(type =>
                                 type.Safety == MusicTypeSafety.Ambient &&
                                 CategoriesAreCompatible(sourceCategory, type.Category)))
                    {
                        if (seenPlaceTargets.Add(musicType.FormKey)) placeTargets.Add(musicType);
                    }
                }

                if (placeTargets.Count == 0)
                {
                    warnings.Add(
                        $"Selected place {selectedPlace.DisplayName} has no compatible ambient music palette for {sourceCategory.GetDisplayName()}.");
                    continue;
                }

                var conditionKind = selectedPlace.Kind == SoundtrackPlaceKind.Area
                    ? SoundtrackPlaceKind.Cell
                    : selectedPlace.Kind;
                var conditionFormKeys = selectedPlace.ConditionFormKeys
                    .Where(formKey => !string.IsNullOrWhiteSpace(formKey))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (conditionFormKeys.Length == 0)
                {
                    warnings.Add($"Selected place {selectedPlace.DisplayName} has no concrete place condition.");
                    continue;
                }

                for (var conditionIndex = 0; conditionIndex < conditionFormKeys.Length; conditionIndex++)
                {
                    var conditionFormKey = conditionFormKeys[conditionIndex];
                    var conditionId = conditionFormKeys.Length == 1
                        ? selectedPlace.Id
                        : $"{selectedPlace.Id}:member:{conditionFormKey.ToLowerInvariant()}";
                    var conditionName = conditionFormKeys.Length == 1
                        ? selectedPlace.DisplayName
                        : $"{selectedPlace.DisplayName} {conditionIndex + 1}";
                    var condition = new PlaybackPlaceCondition(
                        conditionId,
                        conditionKind,
                        conditionFormKey,
                        conditionName);
                    foreach (var musicType in placeTargets) AddTarget(musicType, condition);
                }
            }
        }

        void ResolveExactMusicTypes()
        {
            foreach (var selectedFormKey in rule.SelectedMusicTypeFormKeys
                         .Where(id => !string.IsNullOrWhiteSpace(id))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!musicByFormKey.TryGetValue(selectedFormKey, out var musicType))
                {
                    warnings.Add($"Selected music palette {selectedFormKey} is not present in the active load order.");
                    continue;
                }
                if (musicType.Safety == MusicTypeSafety.Silence)
                {
                    warnings.Add($"Selected music palette {selectedFormKey} provides silence and was rejected.");
                    continue;
                }
                if (musicType.Safety == MusicTypeSafety.Unknown)
                {
                    warnings.Add(
                        $"Selected music palette {selectedFormKey} has unknown safety and was accepted only because Exact Playlist Advanced was selected.");
                }
                AddTarget(musicType);
            }
        }
    }

    private static bool IsCategoryDefaultTarget(
        TrackCategory sourceCategory,
        SoundtrackMusicType musicType)
    {
        if (!CategoriesAreCompatible(sourceCategory, musicType.Category)) return false;
        if (musicType.Safety == MusicTypeSafety.Ambient) return true;
        return IsCombatCategory(sourceCategory) &&
               IsCombatCategory(musicType.Category) &&
               musicType.Safety == MusicTypeSafety.Contextual;
    }

    internal static bool CategoriesAreCompatible(TrackCategory source, TrackCategory target)
    {
        if (source == target) return true;
        if (source == TrackCategory.ExplorationAnyTime &&
            target is TrackCategory.ExplorationDay or TrackCategory.ExplorationNight or TrackCategory.ExplorationAnyTime)
        {
            return true;
        }
        if (target == TrackCategory.ExplorationAnyTime &&
            source is TrackCategory.ExplorationDay or TrackCategory.ExplorationNight)
        {
            return true;
        }
        return source is TrackCategory.Town or TrackCategory.City &&
               target is TrackCategory.Town or TrackCategory.City;
    }

    private static bool IsCombatCategory(TrackCategory category) =>
        category is TrackCategory.Combat or TrackCategory.BossCombat or TrackCategory.DragonCombat;

}

public sealed record PlaybackRoutingResolution(
    IReadOnlyList<ResolvedPlaybackTarget> Targets,
    IReadOnlyList<string> Warnings);

public sealed record ResolvedPlaybackTarget(
    SoundtrackMusicType MusicType,
    PlaybackPlaceCondition? PlaceCondition);

public sealed record PlaybackPlaceCondition(
    string Id,
    SoundtrackPlaceKind Kind,
    string FormKey,
    string DisplayName);
