using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Environments;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Allocators;
using Mutagen.Bethesda.Plugins.Binary.Translations;
using Mutagen.Bethesda.Plugins.Order;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using SkyrimMusicTrack = Mutagen.Bethesda.Skyrim.MusicTrack;

namespace Skypod.Core;

public sealed class SkyrimMusicPluginBuilder
{
    private static readonly Regex WordBoundary = new(
        @"(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])|[_\-]+",
        RegexOptions.Compiled);

    public PluginBuildResult Build(
        string outputDirectory,
        IReadOnlyCollection<PluginTrack> tracks,
        BuildSettings settings,
        IReadOnlyCollection<CatalogTrackRoutingOverride>? catalogRoutingOverrides = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentNullException.ThrowIfNull(tracks);
        ArgumentNullException.ThrowIfNull(settings);
        ModManagerBuildValidator.Validate(settings, "plugin generation");

        Directory.CreateDirectory(outputDirectory);
        var patch = new SkyrimMod(ModKey.FromFileName(settings.PluginName), SkyrimRelease.SkyrimVR);
        SkypodMcmPlugin.AddRecords(patch);
        var allocatorPath = settings.FormIdAllocatorPath;
        if (string.IsNullOrWhiteSpace(allocatorPath))
        {
            allocatorPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Skypod",
                "Skypod.formids.txt");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(allocatorPath))!);

        using var allocator = new TextFileFormKeyAllocator(patch, allocatorPath, preload: true);
        patch.SetAllocator(allocator);

        var environmentBuilder = GameEnvironment.Typical
            .Builder<ISkyrimMod, ISkyrimModGetter>(GameRelease.SkyrimVR)
            .WithOutputMod(patch, OutputModTrimming.Self);
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
        if (settings.RequiredPlugins is { Count: > 0 })
        {
            var loadedPlugins = environment.LoadOrder.ListedOrder
                .Where(entry => entry.Enabled && entry.Mod is not null)
                .Select(entry => entry.ModKey.FileName.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var missing = settings.RequiredPlugins.Where(name => !loadedPlugins.Contains(name)).ToArray();
            if (missing.Length > 0)
                throw new InvalidOperationException("This soundtrack requires active plugins: " + string.Join(", ", missing));
        }
        var worldIndex = new SoundtrackWorldIndexer().Build(environment);

        var winningTypeContexts = environment.LoadOrder.PriorityOrder
            .MusicType()
            .WinningContextOverrides()
            .ToArray();
        var winningTrackContexts = environment.LoadOrder.PriorityOrder
            .MusicTrack()
            .WinningContextOverrides()
            .ToArray();
        var winningTypes = winningTypeContexts.ToDictionary(context => context.Record.FormKey, context => context.Record);
        var winningTypePlugins = winningTypeContexts.ToDictionary(
            context => context.Record.FormKey,
            context => context.ModKey.FileName.ToString());
        var winningTracks = winningTrackContexts.ToDictionary(context => context.Record.FormKey, context => context.Record);
        var winningTrackPlugins = winningTrackContexts.ToDictionary(
            context => context.Record.FormKey,
            context => context.ModKey.FileName.ToString());

        var overriddenTypes = new HashSet<FormKey>();
        var generated = 0;
        var skipped = new List<string>();
        var runtimeMusicTypes = new List<RuntimeMusicType>();
        var catalog = new List<SoundtrackCatalogTrack>();
        var excludedTrackIds = SkypodExclusionStore.Normalize(settings.ExcludedTrackIds ?? []);
        var catalogRoutingById = (catalogRoutingOverrides ?? [])
            .Where(entry => !string.IsNullOrWhiteSpace(entry.TrackId))
            .GroupBy(entry => entry.TrackId.Trim().ToLowerInvariant(), StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Last().AutomaticPlayback ?? new PlaybackRoutingRule(),
                StringComparer.Ordinal);
        var removedTrackIds = excludedTrackIds
            .Concat(catalogRoutingById
                .Where(entry => entry.Value.Mode != AutomaticPlaybackMode.CategoryDefault)
                .Select(entry => entry.Key))
            .ToHashSet(StringComparer.Ordinal);

        ApplyDiscoveredExclusions(
            patch,
            winningTypes,
            winningTracks,
            removedTrackIds,
            overriddenTypes);

        bool AppendToWinningMusicType(
            ResolvedPlaybackTarget target,
            FormKey trackFormKey,
            int weight,
            string trackLabel,
            ICollection<SoundtrackPlaylist> playlists)
        {
            if (!MutagenMusicRouting.TryParseFormKey(target.MusicType.FormKey, out var targetKey) ||
                !winningTypes.TryGetValue(targetKey, out var winner))
            {
                skipped.Add(
                    $"{trackLabel}: target {target.MusicType.FormKey} is not a winning music palette in the active load order");
                return false;
            }

            var outputType = patch.MusicTypes.GetOrAddAsOverride(winner);
            outputType.Tracks ??= new ExtendedList<IFormLinkGetter<IMusicTrackGetter>>();
            for (var occurrence = 0; occurrence < Math.Max(1, weight); occurrence++)
            {
                outputType.Tracks.Add(trackFormKey);
            }
            overriddenTypes.Add(targetKey);
            playlists.Add(new SoundtrackPlaylist(
                FormatFormKey(targetKey),
                winner.EditorID ?? FormatFormKey(targetKey),
                winningTypePlugins[targetKey],
                target.MusicType.Category));
            return true;
        }

        foreach (var source in tracks)
        {
            var previewTrack = patch.MusicTracks.AddNew($"SKYPOD_PREVIEW_TRACK_{source.Id:N}");
            previewTrack.Type = SkyrimMusicTrack.TypeEnum.SingleTrack;
            previewTrack.TrackFilename = source.PluginPath;
            previewTrack.Duration = source.DurationSeconds is null ? null : (float)source.DurationSeconds.Value;
            previewTrack.FadeOut = 1.0f;
            generated++;

            var preview = AddPreviewMusicType(patch, $"SKYPOD_PREVIEW_{source.Id:N}", previewTrack.FormKey);
            var catalogId = source.Id.ToString("D");

            var playlists = new List<SoundtrackPlaylist>();
            SkyrimMusicTrack? categoryTrack = null;
            var automaticTracks = new List<SkyrimMusicTrack>();
            if (excludedTrackIds.Contains(catalogId))
            {
                // Exclusion only affects Skyrim's automatic rotation. The dedicated
                // condition-free preview and catalog row remain available in Prisma.
                if ((source.AutomaticPlayback?.Mode ?? AutomaticPlaybackMode.CategoryDefault) !=
                    AutomaticPlaybackMode.CategoryDefault)
                {
                    skipped.Add(
                        $"{source.Title}: the persistent exclusion overrides its automatic playback rule; uncheck Exclude and rebuild before routing it automatically");
                }
            }
            else
            {
                var routing = MusicRoutingResolver.Resolve(
                    source.AutomaticPlayback,
                    source.Category,
                    worldIndex);
                foreach (var warning in routing.Warnings) skipped.Add($"{source.Title}: {warning}");
                if (routing.Targets.Count == 0)
                {
                    if ((source.AutomaticPlayback?.Mode ?? AutomaticPlaybackMode.CategoryDefault) !=
                        AutomaticPlaybackMode.ManualOnly && source.AutomaticPlayback?.Mode != AutomaticPlaybackMode.BossEncounters && routing.Warnings.Count == 0)
                    {
                        skipped.Add(
                            $"{source.Title}: no safe winning music palette matched this automatic playback rule; manual playback remains available");
                    }
                }
                else
                {
                    categoryTrack = patch.MusicTracks.AddNew($"SKYPOD_CATEGORY_TRACK_{source.Id:N}");
                    categoryTrack.Type = SkyrimMusicTrack.TypeEnum.SingleTrack;
                    categoryTrack.TrackFilename = source.PluginPath;
                    categoryTrack.Duration = source.DurationSeconds is null ? null : (float)source.DurationSeconds.Value;
                    categoryTrack.FadeOut = 1.0f;
                    CopyTemplateConditions(source.Category, winningTracks.Values, categoryTrack);

                    var tracksByPlace = new Dictionary<string, SkyrimMusicTrack>(StringComparer.OrdinalIgnoreCase);
                    foreach (var target in routing.Targets)
                    {
                        var routedTrack = categoryTrack;
                        if (target.PlaceCondition is not null)
                        {
                            if (!tracksByPlace.TryGetValue(target.PlaceCondition.Id, out routedTrack))
                            {
                                var editorId =
                                    $"SKYPOD_PLACE_TRACK_{source.Id:N}_{StableHash(target.PlaceCondition.Id)}";
                                routedTrack = patch.MusicTracks.DuplicateInAsNewRecord(categoryTrack, editorId);
                                routedTrack.Conditions ??= new ExtendedList<Condition>();
                                routedTrack.Conditions.Add(
                                    MutagenMusicRouting.CreatePlaceCondition(target.PlaceCondition));
                                tracksByPlace.Add(target.PlaceCondition.Id, routedTrack);
                            }
                        }

                        if (AppendToWinningMusicType(
                                target,
                                routedTrack.FormKey,
                                Math.Clamp(source.Weight, 1, 10),
                                source.Title,
                                playlists) &&
                            automaticTracks.All(track => track.FormKey != routedTrack.FormKey))
                        {
                            automaticTracks.Add(routedTrack);
                        }
                    }
                }
            }

            var selectedRuntimeTrack = automaticTracks.FirstOrDefault() ?? categoryTrack ?? previewTrack;
            foreach (var mappedTrack in automaticTracks.Count == 0 ? [previewTrack] : automaticTracks)
            {
                runtimeMusicTypes.Add(new RuntimeMusicType(
                    catalogId,
                    preview.FormKey.ID,
                    settings.PluginName,
                    mappedTrack.FormKey.ID));
            }

            if (automaticTracks.Count > 0 && (source.AutomaticPlayback?.SelectedBossIds.Count ?? 0) > 0)
                runtimeMusicTypes.Add(new RuntimeMusicType(catalogId, preview.FormKey.ID, settings.PluginName, previewTrack.FormKey.ID));
            var audioPath = NormalizeAudioPath(source.PluginPath);
            catalog.Add(new SoundtrackCatalogTrack(
                catalogId,
                source.Title,
                source.Artist,
                source.Category,
                source.RelativePath ?? audioPath,
                Math.Clamp(source.Weight, 1, 100),
                Math.Clamp(source.GainDb, -24, 12),
                source.Favorite,
                "skypod",
                settings.PluginName,
                settings.PluginName,
                FormatFormKey(selectedRuntimeTrack.FormKey),
                selectedRuntimeTrack.EditorID,
                "single",
                [],
                [audioPath],
                playlists.Distinct().ToArray(),
                automaticTracks.Any(track => (track.Conditions?.Count ?? 0) > 0),
                true,
                settings.PluginName,
                preview.FormKey.ID.ToString("X8"),
                "Skypod",
                "loose"));
        }

        var memberships = BuildPlaylistMemberships(
            winningTypes,
            winningTypePlugins,
            winningTracks);
        var looseResolver = ModOrganizerAudioProviderResolver.TryCreate(
            outputDirectory,
            settings.ModOrganizerInstanceDirectory, settings.ModOrganizerModsDirectory, settings.ModOrganizerProfileDirectory);
        var audioPathsByTrack = new Dictionary<FormKey, IReadOnlyList<string>>();
        var childTracksByTrack = new Dictionary<FormKey, IReadOnlyList<FormKey>>();
        var conditionFreeClones = new Dictionary<FormKey, SkyrimMusicTrack>();
        var automaticGraphClones = new Dictionary<(FormKey Root, FormKey Track), SkyrimMusicTrack>();
        var normalizedRouteClones = new Dictionary<(FormKey Track, TrackCategory Category), SkyrimMusicTrack>();
        var cloning = new HashSet<FormKey>();
        var automaticCloning = new HashSet<(FormKey Root, FormKey Track)>();

        IReadOnlyList<string> ResolveAudioPaths(FormKey trackKey, HashSet<FormKey>? visiting = null)
        {
            if (audioPathsByTrack.TryGetValue(trackKey, out var cached)) return cached;
            if (!winningTracks.TryGetValue(trackKey, out var source)) return [];

            visiting ??= [];
            if (!visiting.Add(trackKey)) return [];
            IReadOnlyList<string> result;
            if (source.Type == SkyrimMusicTrack.TypeEnum.SingleTrack &&
                source.TrackFilename is { IsNull: false } filename)
            {
                var paths = new List<string> { NormalizeAudioPath(filename.GivenPath) };
                if (source.FinaleFilename is { IsNull: false } finale)
                {
                    paths.Add(NormalizeAudioPath(finale.GivenPath));
                }
                result = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            }
            else if (source.Type == SkyrimMusicTrack.TypeEnum.Palette && source.Tracks is not null)
            {
                result = source.Tracks
                    .SelectMany(link => ResolveAudioPaths(link.FormKey, visiting))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            else
            {
                result = [];
            }
            visiting.Remove(trackKey);
            audioPathsByTrack[trackKey] = result;
            return result;
        }

        IReadOnlyList<FormKey> ResolveLeafTracks(FormKey trackKey, HashSet<FormKey>? visiting = null)
        {
            if (childTracksByTrack.TryGetValue(trackKey, out var cached)) return cached;
            if (!winningTracks.TryGetValue(trackKey, out var source)) return [];
            if (source.Type == SkyrimMusicTrack.TypeEnum.SingleTrack)
            {
                return ResolveAudioPaths(trackKey).Count == 0 ? [] : [trackKey];
            }
            if (source.Type != SkyrimMusicTrack.TypeEnum.Palette || source.Tracks is null) return [];

            visiting ??= [];
            if (!visiting.Add(trackKey)) return [];
            var result = source.Tracks
                .SelectMany(link => ResolveLeafTracks(link.FormKey, visiting))
                .Distinct()
                .ToArray();
            visiting.Remove(trackKey);
            childTracksByTrack[trackKey] = result;
            return result;
        }

        SkyrimMusicTrack? CloneWithoutConditions(FormKey trackKey)
        {
            if (conditionFreeClones.TryGetValue(trackKey, out var existing)) return existing;
            if (!winningTracks.TryGetValue(trackKey, out var source) || !cloning.Add(trackKey)) return null;
            try
            {
                var hash = StableHash(StableRecordId(trackKey));
                if (source.Type == SkyrimMusicTrack.TypeEnum.SingleTrack &&
                    source.TrackFilename is { IsNull: false } filename)
                {
                    var clone = patch.MusicTracks.AddNew($"SKYPOD_CATALOG_TRACK_{hash}");
                    clone.Type = SkyrimMusicTrack.TypeEnum.SingleTrack;
                    clone.Duration = source.Duration;
                    clone.FadeOut = source.FadeOut;
                    clone.TrackFilename = filename.GivenPath;
                    if (source.FinaleFilename is { IsNull: false } finale)
                    {
                        clone.FinaleFilename = finale.GivenPath;
                    }
                    if (source.LoopData is not null)
                    {
                        clone.LoopData = new MusicTrackLoopData
                        {
                            Begins = source.LoopData.Begins,
                            Ends = source.LoopData.Ends,
                            Count = source.LoopData.Count
                        };
                    }
                    if (source.CuePoints is { Count: > 0 })
                    {
                        clone.CuePoints = new ExtendedList<float>();
                        foreach (var cuePoint in source.CuePoints) clone.CuePoints.Add(cuePoint);
                    }
                    conditionFreeClones[trackKey] = clone;
                    return clone;
                }

                if (source.Type == SkyrimMusicTrack.TypeEnum.SilentTrack)
                {
                    var clone = patch.MusicTracks.AddNew($"SKYPOD_CATALOG_TRACK_{hash}");
                    clone.Type = SkyrimMusicTrack.TypeEnum.SilentTrack;
                    clone.Duration = source.Duration;
                    clone.FadeOut = source.FadeOut;
                    if (source.LoopData is not null)
                    {
                        clone.LoopData = new MusicTrackLoopData
                        {
                            Begins = source.LoopData.Begins,
                            Ends = source.LoopData.Ends,
                            Count = source.LoopData.Count
                        };
                    }
                    if (source.CuePoints is { Count: > 0 })
                    {
                        clone.CuePoints = new ExtendedList<float>();
                        foreach (var cuePoint in source.CuePoints) clone.CuePoints.Add(cuePoint);
                    }
                    conditionFreeClones[trackKey] = clone;
                    return clone;
                }

                if (source.Type == SkyrimMusicTrack.TypeEnum.Palette && source.Tracks is not null)
                {
                    var clonedLinks = new List<FormKey>(source.Tracks.Count);
                    var playableChildren = 0;
                    foreach (var link in source.Tracks)
                    {
                        if (link.IsNull)
                        {
                            // Palette SNAM nulls separate the engine's three
                            // simultaneous layers. Their positions are data,
                            // not missing references.
                            clonedLinks.Add(FormKey.Null);
                            continue;
                        }

                        var child = CloneWithoutConditions(link.FormKey);
                        if (child is null) return null;
                        clonedLinks.Add(child.FormKey);
                        playableChildren++;
                    }
                    if (playableChildren == 0) return null;

                    var clone = patch.MusicTracks.AddNew($"SKYPOD_CATALOG_TRACK_{hash}");
                    clone.Type = SkyrimMusicTrack.TypeEnum.Palette;
                    clone.Duration = source.Duration;
                    clone.FadeOut = source.FadeOut;
                    clone.Tracks = new ExtendedList<IFormLinkGetter<IMusicTrackGetter>>();
                    foreach (var link in clonedLinks) clone.Tracks.Add(link);
                    conditionFreeClones[trackKey] = clone;
                    return clone;
                }

                return null;
            }
            finally
            {
                cloning.Remove(trackKey);
            }
        }

        SkyrimMusicTrack? CloneAutomaticGraph(FormKey routeRootKey, FormKey trackKey)
        {
            // A routed root is deliberately removed from its old memberships and then
            // reintroduced at the user's new targets. Descendants with their own
            // exclusion/manual/reroute rule must stay removed from this new graph too.
            if (trackKey != routeRootKey && removedTrackIds.Contains(StableRecordId(trackKey)))
            {
                return null;
            }

            var cacheKey = (routeRootKey, trackKey);
            if (automaticGraphClones.TryGetValue(cacheKey, out var cached)) return cached;
            if (!winningTracks.TryGetValue(trackKey, out var source) || !automaticCloning.Add(cacheKey))
            {
                return null;
            }

            try
            {
                if (source.Type != SkyrimMusicTrack.TypeEnum.Palette || source.Tracks is null)
                {
                    return CloneWithoutConditions(trackKey);
                }

                var clonedLinks = new List<FormKey>(source.Tracks.Count);
                var playableChildren = 0;
                foreach (var link in source.Tracks)
                {
                    if (link.IsNull)
                    {
                        // These nulls are layer separators. Keep their exact positions
                        // even when a child on either side is filtered out.
                        clonedLinks.Add(FormKey.Null);
                        continue;
                    }

                    var child = CloneAutomaticGraph(routeRootKey, link.FormKey);
                    if (child is null) continue;
                    clonedLinks.Add(child.FormKey);
                    playableChildren++;
                }
                if (playableChildren == 0) return null;

                var clone = patch.MusicTracks.AddNew(
                    $"SKYPOD_ROUTE_GRAPH_{StableHash(StableRecordId(routeRootKey))}_{StableHash(StableRecordId(trackKey))}");
                clone.Type = SkyrimMusicTrack.TypeEnum.Palette;
                clone.Duration = source.Duration;
                clone.FadeOut = source.FadeOut;
                clone.Tracks = new ExtendedList<IFormLinkGetter<IMusicTrackGetter>>();
                foreach (var link in clonedLinks) clone.Tracks.Add(link);
                automaticGraphClones.Add(cacheKey, clone);
                return clone;
            }
            finally
            {
                automaticCloning.Remove(cacheKey);
            }
        }

        SkyrimMusicTrack? CloneForAutomaticRoute(FormKey trackKey, TrackCategory category)
        {
            var cacheKey = (trackKey, category);
            if (normalizedRouteClones.TryGetValue(cacheKey, out var existing)) return existing;
            var filteredGraph = CloneAutomaticGraph(trackKey, trackKey);
            if (filteredGraph is null) return null;

            var editorId = $"SKYPOD_ROUTE_TRACK_{StableHash(StableRecordId(trackKey))}_{category}";
            var clone = patch.MusicTracks.DuplicateInAsNewRecord(filteredGraph, editorId);
            clone.Conditions = null;
            CopyTemplateConditions(category, winningTracks.Values, clone);
            normalizedRouteClones.Add(cacheKey, clone);
            return clone;
        }

        int OriginalRoutingWeight(FormKey trackKey)
        {
            var weight = 0;
            foreach (var musicType in winningTypes.Values)
            {
                if (musicType.Tracks is null) continue;
                weight = Math.Max(weight, musicType.Tracks.Count(link => link.FormKey == trackKey));
            }
            foreach (var musicTrack in winningTracks.Values)
            {
                if (musicTrack.Type != SkyrimMusicTrack.TypeEnum.Palette || musicTrack.Tracks is null) continue;
                weight = Math.Max(weight, musicTrack.Tracks.Count(link => link.FormKey == trackKey));
            }
            return Math.Max(1, weight);
        }

        var catalogSources = winningTracks.Values
            .Where(source => ResolveAudioPaths(source.FormKey).Count > 0)
            .OrderBy(source => source.FormKey.ModKey.FileName.ToString(), StringComparer.OrdinalIgnoreCase)
            .ThenBy(source => source.FormKey.ID)
            .ToArray();
        var discoveredCatalogIds = catalogSources
            .Select(source => StableRecordId(source.FormKey))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var missingOverrideId in catalogRoutingById.Keys.Where(id =>
                     !discoveredCatalogIds.Contains(id)))
        {
            skipped.Add(
                $"{missingOverrideId}: catalog routing override does not match a playable winning music track in the active load order");
        }
        var skippedCatalogPreviews = 0;
        foreach (var source in catalogSources)
        {
            var conditionFreeTrack = CloneWithoutConditions(source.FormKey);
            if (conditionFreeTrack is null)
            {
                skippedCatalogPreviews++;
                skipped.Add($"{FormatFormKey(source.FormKey)} could not be cloned into a safe preview record");
                continue;
            }

            var catalogId = StableRecordId(source.FormKey);
            var preview = AddPreviewMusicType(
                patch,
                $"SKYPOD_CATALOG_PREVIEW_{StableHash(catalogId)}",
                conditionFreeTrack.FormKey);
            runtimeMusicTypes.Add(new RuntimeMusicType(
                catalogId,
                preview.FormKey.ID,
                source.FormKey.ModKey.FileName.ToString(),
                source.FormKey.ID));

            if (catalogRoutingById.TryGetValue(catalogId, out var bossPreviewRule) && bossPreviewRule.SelectedBossIds.Count > 0)
                runtimeMusicTypes.Add(new RuntimeMusicType(catalogId, preview.FormKey.ID, settings.PluginName, conditionFreeTrack.FormKey.ID));
            var audioPaths = ResolveAudioPaths(source.FormKey);
            var trackMemberships = memberships.GetValueOrDefault(source.FormKey) ?? [];
            var originalPlaylists = trackMemberships
                .Select(entry => new SoundtrackPlaylist(
                    FormatFormKey(entry.MusicTypeFormKey),
                    entry.EditorId,
                    entry.Plugin,
                    entry.Category))
                .Distinct()
                .OrderBy(entry => entry.EditorId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.Category)
                .ToArray();
            var category = InferPrimaryCategory(source, audioPaths, trackMemberships);
            IReadOnlyList<SoundtrackPlaylist> effectivePlaylists = originalPlaylists;
            var originalHadConditions =
                (source.Conditions?.Count ?? 0) > 0 || trackMemberships.Any(entry => entry.HadConditions);
            var effectiveHadConditions = originalHadConditions;

            if (excludedTrackIds.Contains(catalogId))
            {
                effectivePlaylists = [];
                effectiveHadConditions = false;
                if (catalogRoutingById.ContainsKey(catalogId))
                {
                    skipped.Add(
                        $"{catalogId}: the persistent exclusion overrides its catalog routing rule; uncheck Exclude and rebuild before routing it automatically");
                }
            }
            else if (catalogRoutingById.TryGetValue(catalogId, out var catalogRule) &&
                     catalogRule.Mode != AutomaticPlaybackMode.CategoryDefault)
            {
                var routedPlaylists = new List<SoundtrackPlaylist>();
                var generatedAliases = new List<SkyrimMusicTrack>();
                effectiveHadConditions = false;
                if (catalogRule.Mode != AutomaticPlaybackMode.ManualOnly)
                {
                    var routing = MusicRoutingResolver.Resolve(catalogRule, category, worldIndex);
                    foreach (var warning in routing.Warnings) skipped.Add($"{catalogId}: {warning}");
                    if (routing.Targets.Count == 0 && routing.Warnings.Count == 0 && catalogRule.Mode != AutomaticPlaybackMode.BossEncounters)
                    {
                        skipped.Add(
                            $"{catalogId}: no safe winning music palette matched this catalog routing rule; the track remains manual-only");
                    }
                    else if (routing.Targets.Count > 0)
                    {
                        SkyrimMusicTrack? normalizedTrack = null;
                        if (catalogRule.Mode != AutomaticPlaybackMode.ExactPlaylistAdvanced)
                        {
                            normalizedTrack = CloneForAutomaticRoute(source.FormKey, category);
                            if (normalizedTrack is null)
                            {
                                skipped.Add(
                                    $"{catalogId}: the source track graph could not be cloned for automatic rerouting; the track remains manual-only");
                            }
                        }

                        if (catalogRule.Mode == AutomaticPlaybackMode.ExactPlaylistAdvanced ||
                            normalizedTrack is not null)
                        {
                            var tracksByPlace = new Dictionary<string, SkyrimMusicTrack>(
                                StringComparer.OrdinalIgnoreCase);
                            foreach (var target in routing.Targets)
                            {
                                var routedTrackFormKey = source.FormKey;
                                SkyrimMusicTrack? generatedAlias = null;
                                if (catalogRule.Mode != AutomaticPlaybackMode.ExactPlaylistAdvanced)
                                {
                                    generatedAlias = normalizedTrack!;
                                    if (target.PlaceCondition is not null)
                                    {
                                        if (!tracksByPlace.TryGetValue(
                                                target.PlaceCondition.Id,
                                                out generatedAlias))
                                        {
                                            var editorId =
                                                $"SKYPOD_ROUTE_PLACE_{StableHash(catalogId + ":" + target.PlaceCondition.Id)}";
                                            generatedAlias = patch.MusicTracks.DuplicateInAsNewRecord(
                                                normalizedTrack!,
                                                editorId);
                                            generatedAlias.Conditions ??= new ExtendedList<Condition>();
                                            generatedAlias.Conditions.Add(
                                                MutagenMusicRouting.CreatePlaceCondition(target.PlaceCondition));
                                            tracksByPlace.Add(target.PlaceCondition.Id, generatedAlias);
                                        }
                                    }
                                    routedTrackFormKey = generatedAlias.FormKey;
                                }

                                if (!AppendToWinningMusicType(
                                        target,
                                        routedTrackFormKey,
                                        OriginalRoutingWeight(source.FormKey),
                                        catalogId,
                                        routedPlaylists))
                                {
                                    continue;
                                }

                                if (generatedAlias is not null &&
                                    generatedAliases.All(track => track.FormKey != generatedAlias.FormKey))
                                {
                                    generatedAliases.Add(generatedAlias);
                                }
                            }

                            foreach (var alias in generatedAliases)
                            {
                                runtimeMusicTypes.Add(new RuntimeMusicType(
                                    catalogId,
                                    preview.FormKey.ID,
                                    settings.PluginName,
                                    alias.FormKey.ID));
                            }
                            effectiveHadConditions = catalogRule.Mode ==
                                                       AutomaticPlaybackMode.ExactPlaylistAdvanced
                                ? originalHadConditions
                                : generatedAliases.Any(track => (track.Conditions?.Count ?? 0) > 0);
                        }
                    }
                }
                effectivePlaylists = routedPlaylists
                    .Distinct()
                    .OrderBy(entry => entry.EditorId, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(entry => entry.Category)
                    .ToArray();
            }
            var sourcePlugin = source.FormKey.ModKey.FileName.ToString();
            var winnerPlugin = winningTrackPlugins[source.FormKey];
            var looseProvider = looseResolver is null
                ? null
                : audioPaths.Select(looseResolver.ResolveLooseProvider).FirstOrDefault(provider => provider is not null);
            var audioProvider = looseProvider ?? winnerPlugin;
            var childTrackIds = source.Type == SkyrimMusicTrack.TypeEnum.Palette
                ? ResolveLeafTracks(source.FormKey).Select(StableRecordId).ToArray()
                : [];

            catalog.Add(new SoundtrackCatalogTrack(
                catalogId,
                DeriveTitle(source, audioPaths),
                DeriveArtist(audioProvider),
                category,
                audioPaths[0],
                1,
                0,
                false,
                "game",
                sourcePlugin,
                winnerPlugin,
                FormatFormKey(source.FormKey),
                source.EditorID,
                source.Type == SkyrimMusicTrack.TypeEnum.Palette ? "palette" : "single",
                childTrackIds,
                audioPaths,
                effectivePlaylists,
                effectiveHadConditions,
                true,
                settings.PluginName,
                preview.FormKey.ID.ToString("X8"),
                audioProvider,
                looseProvider is null ? "pluginOrArchive" : "loose"));
        }

        var bossRules = new Dictionary<string, PlaybackRoutingRule>(catalogRoutingById, StringComparer.OrdinalIgnoreCase);
        foreach (var track in tracks) bossRules[track.Id.ToString("D")] = track.AutomaticPlayback ?? new();
        var bossAssignments = BossMusicBuilder.Build(patch, worldIndex, catalog, bossRules, excludedTrackIds, skipped);
        BossMusicBuilder.Write(outputDirectory, bossAssignments);

        var lightPluginCompatible = patch.ModHeader.Stats.NextFormID <= 0x1000;
        if (!lightPluginCompatible)
        {
            skipped.Add("The generated records exceeded Skyrim's compact light-plugin FormID window; Skypod.esp remains a safe normal ESP.");
        }

        var pluginPath = Path.Combine(outputDirectory, settings.PluginName);
        patch.BeginWrite
            .ToPath(pluginPath)
            .WithLoadOrder(environment.LoadOrder)
            .Write();

        return new PluginBuildResult(
            pluginPath,
            generated,
            overriddenTypes.Count,
            skipped,
            runtimeMusicTypes,
            catalog,
            worldIndex,
            lightPluginCompatible,
            skippedCatalogPreviews);
    }

    private static MusicType AddPreviewMusicType(SkyrimMod patch, string editorId, FormKey trackFormKey)
    {
        var preview = patch.MusicTypes.AddNew(editorId);
        preview.Flags = MusicType.Flag.PlaysOneSelection |
                        MusicType.Flag.AbruptTransition |
                        MusicType.Flag.DucksCurrentTrack |
                        MusicType.Flag.DoesNotQueue;
        preview.Data = new MusicTypeData
        {
            Priority = 1,
            DuckingDecibel = 100.0f
        };
        preview.FadeDuration = 1.0f;
        preview.Tracks = new ExtendedList<IFormLinkGetter<IMusicTrackGetter>> { trackFormKey };
        return preview;
    }

    private static void ApplyDiscoveredExclusions(
        SkyrimMod patch,
        IReadOnlyDictionary<FormKey, IMusicTypeGetter> winningTypes,
        IReadOnlyDictionary<FormKey, IMusicTrackGetter> winningTracks,
        IReadOnlySet<string> excludedTrackIds,
        ISet<FormKey> overriddenTypes)
    {
        if (!excludedTrackIds.Any(id => id.StartsWith("record:", StringComparison.Ordinal))) return;

        var playableByTrack = new Dictionary<FormKey, bool>();
        var visiting = new HashSet<FormKey>();

        bool KeepInRotation(FormKey trackKey)
        {
            if (excludedTrackIds.Contains(StableRecordId(trackKey))) return false;
            if (playableByTrack.TryGetValue(trackKey, out var cached)) return cached;
            if (!winningTracks.TryGetValue(trackKey, out var source) ||
                source.Type != SkyrimMusicTrack.TypeEnum.Palette ||
                source.Tracks is null)
            {
                playableByTrack[trackKey] = true;
                return true;
            }
            if (!visiting.Add(trackKey))
            {
                throw new InvalidDataException(
                    $"Cyclic music palette detected while applying exclusions at {FormatFormKey(trackKey)}.");
            }

            try
            {
                var filteredLinks = new List<FormKey>(source.Tracks.Count);
                var changed = false;
                var playableChildren = 0;
                foreach (var child in source.Tracks)
                {
                    if (child.IsNull)
                    {
                        // Null SNAM entries split a palette into its three simultaneous
                        // layers. Preserve every separator at its exact position.
                        filteredLinks.Add(FormKey.Null);
                        continue;
                    }

                    if (KeepInRotation(child.FormKey))
                    {
                        filteredLinks.Add(child.FormKey);
                        playableChildren++;
                    }
                    else
                    {
                        // Remove every occurrence of an excluded or now-empty child while
                        // leaving sibling order and duplicate weighting untouched.
                        changed = true;
                    }
                }

                if (playableChildren == 0)
                {
                    playableByTrack[trackKey] = false;
                    return false;
                }

                if (changed)
                {
                    var outputTrack = patch.MusicTracks.GetOrAddAsOverride(source);
                    outputTrack.Tracks = new ExtendedList<IFormLinkGetter<IMusicTrackGetter>>();
                    foreach (var link in filteredLinks) outputTrack.Tracks.Add(link);
                }

                playableByTrack[trackKey] = true;
                return true;
            }
            finally
            {
                visiting.Remove(trackKey);
            }
        }

        foreach (var (musicTypeKey, musicType) in winningTypes)
        {
            if (musicType.Tracks is null) continue;

            var filteredLinks = new List<FormKey>(musicType.Tracks.Count);
            var changed = false;
            foreach (var root in musicType.Tracks)
            {
                if (root.IsNull)
                {
                    filteredLinks.Add(FormKey.Null);
                    continue;
                }
                if (KeepInRotation(root.FormKey))
                {
                    filteredLinks.Add(root.FormKey);
                }
                else
                {
                    changed = true;
                }
            }
            if (!changed) continue;

            var outputType = patch.MusicTypes.GetOrAddAsOverride(musicType);
            outputType.Tracks = new ExtendedList<IFormLinkGetter<IMusicTrackGetter>>();
            foreach (var link in filteredLinks) outputType.Tracks.Add(link);
            overriddenTypes.Add(musicTypeKey);
        }
    }

    private static Dictionary<FormKey, IReadOnlyList<PlaylistMembership>> BuildPlaylistMemberships(
        IReadOnlyDictionary<FormKey, IMusicTypeGetter> winningTypes,
        IReadOnlyDictionary<FormKey, string> winningTypePlugins,
        IReadOnlyDictionary<FormKey, IMusicTrackGetter> winningTracks)
    {
        var memberships = new Dictionary<FormKey, List<PlaylistMembership>>();
        foreach (var (musicTypeKey, musicType) in winningTypes)
        {
            if (musicType.Tracks is null) continue;
            foreach (var rootLink in musicType.Tracks)
            {
                Visit(rootLink.FormKey, musicType.EditorID ?? FormatFormKey(musicTypeKey), false, []);
            }

            void Visit(FormKey trackKey, string hierarchy, bool inheritedConditions, HashSet<FormKey> visiting)
            {
                if (!winningTracks.TryGetValue(trackKey, out var track) || !visiting.Add(trackKey)) return;
                var hadConditions = inheritedConditions || (track.Conditions?.Count ?? 0) > 0;
                var hint = hierarchy + " " + track.EditorID;
                if (!memberships.TryGetValue(trackKey, out var entries))
                {
                    entries = [];
                    memberships.Add(trackKey, entries);
                }
                entries.Add(new PlaylistMembership(
                    musicTypeKey,
                    musicType.EditorID ?? FormatFormKey(musicTypeKey),
                    winningTypePlugins[musicTypeKey],
                    InferCategoryFromText(hint),
                    hadConditions));

                if (track.Type == SkyrimMusicTrack.TypeEnum.Palette && track.Tracks is not null)
                {
                    foreach (var child in track.Tracks)
                    {
                        Visit(child.FormKey, hint, hadConditions, visiting);
                    }
                }
                visiting.Remove(trackKey);
            }
        }

        return memberships.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<PlaylistMembership>)pair.Value.Distinct().ToArray());
    }

    private static TrackCategory InferPrimaryCategory(
        IMusicTrackGetter track,
        IReadOnlyList<string> audioPaths,
        IReadOnlyList<PlaylistMembership> memberships)
    {
        var ownCategory = InferCategoryFromText(track.EditorID + " " + string.Join(' ', audioPaths));
        if (ownCategory != TrackCategory.Special) return ownCategory;

        return memberships
            .Where(entry => entry.Category != TrackCategory.Special)
            .GroupBy(entry => entry.Category)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key)
            .Select(group => group.Key)
            .FirstOrDefault(TrackCategory.Special);
    }

    internal static TrackCategory InferCategoryFromText(string? value)
    {
        var text = value?.ToLowerInvariant() ?? string.Empty;
        if (text.Contains("boss")) return TrackCategory.BossCombat;
        if (text.Contains("dragon") && (text.Contains("combat") || text.Contains("battle"))) return TrackCategory.DragonCombat;
        if (text.Contains("combat") || text.Contains("battle")) return TrackCategory.Combat;
        if (text.Contains("tavern") || text.Contains(" inn") || text.StartsWith("inn")) return TrackCategory.Tavern;
        if (text.Contains("castle")) return TrackCategory.Castle;
        if (text.Contains("dungeon") || text.Contains("cave") || text.Contains("barrow") ||
            text.Contains("ruin") || text.Contains("fort")) return TrackCategory.Dungeon;
        if (text.Contains("mainmenu") || text.Contains("main menu") || text.Contains("title")) return TrackCategory.MainMenu;
        if (text.Contains("town")) return TrackCategory.Town;
        if (text.Contains("city")) return TrackCategory.City;
        if (text.Contains("night") || text.Contains("evening")) return TrackCategory.ExplorationNight;
        if (text.Contains("explore") || text.Contains("day") || text.Contains("wilderness")) return TrackCategory.ExplorationDay;
        return TrackCategory.Special;
    }

    internal static string StableRecordId(FormKey formKey) =>
        $"record:{formKey.ModKey.FileName.ToString().ToLowerInvariant()}:{formKey.ID:x6}";

    private static string StableHash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes.AsSpan(0, 8));
    }

    private static string DeriveTitle(IMusicTrackGetter track, IReadOnlyList<string> audioPaths)
    {
        var source = track.Type == SkyrimMusicTrack.TypeEnum.Palette
            ? track.EditorID
            : Path.GetFileNameWithoutExtension(audioPaths.FirstOrDefault()) ?? track.EditorID;
        if (string.IsNullOrWhiteSpace(source)) return FormatFormKey(track.FormKey);

        source = Regex.Replace(
            source,
            @"^(?:DLC[\s_\-]*\d*[\s_\-]*)?MUS(?:IC)?[\s_\-]*(?:DLC[\s_\-]*\d*[\s_\-]*)?",
            string.Empty,
            RegexOptions.IgnoreCase);
        var words = WordBoundary.Replace(source, " ").Trim();
        words = Regex.Replace(words, @"\s+", " ");
        var title = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(words.ToLowerInvariant());
        return Regex.Replace(title, @"\bIi\b", "II");
    }

    private static string DeriveArtist(string provider)
    {
        if (provider.Equals("Skyrim.esm", StringComparison.OrdinalIgnoreCase) ||
            provider.Equals("Update.esm", StringComparison.OrdinalIgnoreCase) ||
            provider.Equals("Dawnguard.esm", StringComparison.OrdinalIgnoreCase) ||
            provider.Equals("HearthFires.esm", StringComparison.OrdinalIgnoreCase) ||
            provider.Equals("Dragonborn.esm", StringComparison.OrdinalIgnoreCase) ||
            provider.Equals("SkyrimVR.esm", StringComparison.OrdinalIgnoreCase))
        {
            return "Skyrim";
        }
        return Path.GetFileNameWithoutExtension(provider);
    }

    private static string NormalizeAudioPath(string path) =>
        ModOrganizerAudioProviderResolver.NormalizeDataRelativePath(path)
            .Replace(Path.DirectorySeparatorChar, '/');

    private static string FormatFormKey(FormKey formKey) =>
        $"{formKey.ModKey.FileName}|{formKey.ID:X6}";

    private static void CopyTemplateConditions(
        TrackCategory category,
        IEnumerable<IMusicTrackGetter> winningTracks,
        SkyrimMusicTrack destination)
    {
        var templateEditorId = category switch
        {
            TrackCategory.ExplorationDay => "MUSExploreDay01",
            TrackCategory.ExplorationNight => "MUSExploreNight01",
            TrackCategory.Combat or TrackCategory.BossCombat or TrackCategory.DragonCombat => "MUSCombat01",
            _ => null
        };
        if (templateEditorId is null) return;

        var template = winningTracks.FirstOrDefault(track =>
            string.Equals(track.EditorID, templateEditorId, StringComparison.OrdinalIgnoreCase));
        if (template?.Conditions is null) return;

        destination.Conditions ??= new ExtendedList<Condition>();
        foreach (var condition in template.Conditions)
        {
            destination.Conditions.Add(condition.DeepCopy());
        }
    }

    private sealed record PlaylistMembership(
        FormKey MusicTypeFormKey,
        string EditorId,
        string Plugin,
        TrackCategory Category,
        bool HadConditions);
}

public sealed record PluginTrack(
    Guid Id,
    string Title,
    TrackCategory Category,
    string PluginPath,
    int Weight,
    double? DurationSeconds,
    string Artist = "",
    string? RelativePath = null,
    double GainDb = 0,
    bool Favorite = false,
    PlaybackRoutingRule? AutomaticPlayback = null);

public sealed record PluginBuildResult(
    string PluginPath,
    int GeneratedTracks,
    int OverriddenMusicTypes,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<RuntimeMusicType> RuntimeMusicTypes,
    IReadOnlyList<SoundtrackCatalogTrack> CatalogTracks,
    SoundtrackWorldIndex WorldIndex,
    bool LightPluginCompatible,
    int SkippedCatalogPreviews);

public sealed record RuntimeMusicType(
    string TrackId,
    uint LocalFormId,
    string OriginalTrackPlugin,
    uint OriginalTrackLocalFormId);
