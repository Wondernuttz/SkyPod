(() => {
    "use strict";

    const categoryNames = Object.freeze({
        explorationDay: "Exploration: Day",
        explorationNight: "Exploration: Night",
        dungeon: "Dungeon",
        town: "Town",
        city: "City",
        tavern: "Tavern",
        castle: "Castle",
        combat: "Combat: General",
        bossCombat: "Combat: Boss",
        dragonCombat: "Combat: Dragon",
        special: "Special",
        mainMenu: "Main Menu"
    });

    const bethesdaProviders = new Set([
        "skyrim.esm",
        "update.esm",
        "dawnguard.esm",
        "hearthfires.esm",
        "dragonborn.esm",
        "skyrimvr.esm"
    ]);

    function normalizeId(value) {
        return value === null || value === undefined ? null : String(value).trim().toLowerCase();
    }

    function displayCategory(value) {
        return categoryNames[value] || String(value || "Uncategorized");
    }

    function fileStem(path) {
        const value = String(path || "").replace(/\\/g, "/").split("/").pop() || "";
        return value.replace(/\.(?:xwm|wav|fuz|lip)$/i, "").replace(/_/g, " ").replace(/-/g, " ").trim();
    }

    function humanizeEditorId(value) {
        return String(value || "")
            .replace(/^MUS(?:IC)?/i, "")
            .replace(/([a-z0-9])([A-Z])/g, "$1 $2")
            .replace(/[_-]+/g, " ")
            .replace(/\s+/g, " ")
            .trim();
    }

    function isBethesdaProvider(value) {
        return bethesdaProviders.has(String(value || "").trim().toLowerCase());
    }

    function sourceGroup(track) {
        if (track?.sourceKind === "skypod") return "skypod";

        const provider = String(track?.audioProvider || "").trim();
        if (provider) return isBethesdaProvider(provider) ? "bethesda" : "mods";

        const recordProvider = track?.winnerPlugin || track?.sourcePlugin || "";
        return isBethesdaProvider(recordProvider) ? "bethesda" : "mods";
    }

    function sourceGroupLabel(track) {
        const group = sourceGroup(track);
        if (group === "bethesda") return "BETHESDA";
        if (group === "mods") return "MUSIC MOD";
        return "MY SKYPOD";
    }

    function sourceLabel(track) {
        if (!track) return "Not loaded";
        return track.audioProvider || track.winnerPlugin || track.sourcePlugin ||
            (track.sourceKind === "skypod" ? "Skypod" : "Unknown provider");
    }

    function recordSourceLabel(track) {
        if (!track) return "Not loaded";
        const winner = track.winnerPlugin || track.sourcePlugin || "Unknown plugin";
        if (track.sourcePlugin && track.sourcePlugin !== winner) {
            return `${track.sourcePlugin} -> ${winner}`;
        }
        return winner;
    }

    function playlistNames(track) {
        if (!Array.isArray(track?.playlists)) return [];
        return track.playlists.map(item =>
            humanizeEditorId(item?.editorId) || String(item?.editorId || item?.category || item?.formKey || "In-game playlist"));
    }

    function playlistLabel(track, limit = 2) {
        const names = playlistNames(track);
        if (!names.length) return "Manual playback only";
        const visible = names.slice(0, Math.max(1, limit));
        return names.length > visible.length
            ? `${visible.join(", ")} +${names.length - visible.length}`
            : visible.join(", ");
    }

    function playlistDetail(track) {
        if (!Array.isArray(track?.playlists) || !track.playlists.length) return "Manual playback only";
        return track.playlists.map(item => {
            const name = humanizeEditorId(item?.editorId) || String(item?.editorId || item?.category || "In-game playlist");
            const identity = [item?.editorId, item?.plugin, item?.formKey].filter(Boolean).join(" | ");
            return identity ? `${name} (${identity})` : name;
        }).join("; ");
    }

    function audioTypeLabel(track) {
        const kind = String(track?.audioProviderKind || "").trim().toLowerCase();
        if (kind === "loose") return "Loose audio files";
        if (kind === "pluginorarchive") return "Plugin or archive audio";
        if (kind) return humanizeEditorId(kind);
        return Array.isArray(track?.audioPaths) && track.audioPaths.length > 1
            ? `${track.audioPaths.length} audio files`
            : "Audio file";
    }

    function providerIdentity(track) {
        return sourceLabel(track);
    }

    function providerKey(track) {
        return `provider:${providerIdentity(track).trim().toLowerCase()}`;
    }

    function normalizeTrack(source) {
        const audioPaths = Array.isArray(source?.audioPaths)
            ? source.audioPaths.map(String)
            : (source?.relativePath ? [String(source.relativePath)] : []);
        const playlists = Array.isArray(source?.playlists) ? source.playlists : [];
        const sourceKind = source?.sourceKind === "game" ? "game" : "skypod";
        const audioProvider = String(
            source?.audioProvider || source?.sourceMod || source?.audioWinnerMod ||
            source?.winnerPlugin || source?.sourcePlugin || (sourceKind === "skypod" ? "Skypod" : ""));
        const track = {
            ...source,
            id: normalizeId(source?.id),
            title: String(source?.title || fileStem(audioPaths[0]) || source?.musicTrackEditorId || "Untitled"),
            artist: String(source?.artist || ""),
            category: String(source?.category || "special"),
            sourceKind,
            audioProvider,
            audioProviderKind: String(source?.audioProviderKind || ""),
            sourceMod: audioProvider,
            sourcePlugin: String(source?.sourcePlugin || ""),
            winnerPlugin: String(source?.winnerPlugin || ""),
            sourceFormKey: String(source?.sourceFormKey || ""),
            musicTrackEditorId: String(source?.musicTrackEditorId || ""),
            recordKind: String(source?.recordKind || "single"),
            childTrackIds: Array.isArray(source?.childTrackIds) ? source.childTrackIds.map(normalizeId).filter(Boolean) : [],
            audioPaths,
            playlists,
            previewAvailable: source?.previewAvailable !== false,
            favorite: Boolean(source?.favorite)
        };
        const playlistText = playlists
            .map(item => `${item?.editorId || ""} ${humanizeEditorId(item?.editorId)} ${item?.plugin || ""} ${item?.category || ""} ${item?.formKey || ""}`)
            .join(" ");
        track.group = sourceGroup(track);
        track.providerKey = providerKey(track);
        track.searchText = [
            track.title,
            track.artist,
            track.category,
            displayCategory(track.category),
            track.sourceKind,
            sourceGroupLabel(track),
            track.audioProvider,
            track.audioProviderKind,
            track.sourcePlugin,
            track.winnerPlugin,
            track.sourceFormKey,
            track.musicTrackEditorId,
            track.audioPaths.join(" "),
            playlistText
        ].join(" ").toLowerCase();
        return track;
    }

    function matchesTrack(track, filters = {}) {
        const query = String(filters.query || "").trim().toLowerCase();
        const scope = String(filters.scope || "all");
        const category = String(filters.category || "all");
        const provider = String(filters.provider || "all");

        // Text search is deliberately global. A search entered while Now Playing,
        // Bethesda, or a mod group is selected still searches the complete catalog.
        if (!query) {
            if (scope === "playing" && !filters.isPlaying?.(track)) return false;
            if (scope === "bethesda" && track.group !== "bethesda") return false;
            if (scope === "mods" && track.group !== "mods") return false;
            if (scope === "skypod" && track.group !== "skypod") return false;
        }
        if (category !== "all" && track.category !== category) return false;
        if (provider !== "all" && track.providerKey !== provider) return false;
        return !query || track.searchText.includes(query);
    }

    window.SkypodCatalogModel = Object.freeze({
        audioTypeLabel,
        displayCategory,
        humanizeEditorId,
        matchesTrack,
        normalizeId,
        normalizeTrack,
        playlistDetail,
        playlistLabel,
        providerIdentity,
        providerKey,
        recordSourceLabel,
        sourceGroup,
        sourceGroupLabel,
        sourceLabel
    });
})();
