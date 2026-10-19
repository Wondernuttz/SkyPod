(() => {
    "use strict";

    const PAGE_SIZE = 12;
    const catalogModel = window.SkypodCatalogModel;
    if (!catalogModel) throw new Error("Skypod catalog model did not load");
    const {
        audioTypeLabel,
        displayCategory,
        matchesTrack,
        normalizeId,
        normalizeTrack,
        playlistDetail,
        playlistLabel,
        providerIdentity,
        recordSourceLabel,
        sourceGroupLabel,
        sourceLabel
    } = catalogModel;

    const state = {
        library: { name: "Skypod", tracks: [] },
        selectedId: null,
        currentTrackId: null,
        activeTrackIds: new Set(),
        ownedTrackIds: new Set(),
        excludedTrackIds: new Set(),
        currentMusicType: null,
        currentMusicTrack: null,
        transitioning: false,
        search: "",
        category: "all",
        provider: "all",
        scope: "playing",
        page: 0,
        toastTimer: null,
        playbackSignature: "",
        exclusionsSignature: ""
    };

    const elements = {};

    function byId(id) {
        return document.getElementById(id);
    }

    function send(command, payload = {}) {
        const message = JSON.stringify({ command, ...payload });
        if (typeof window.skypodCommand === "function") {
            window.skypodCommand(message);
        } else {
            toast(`Native bridge unavailable: ${command}`);
        }
    }

    function parsePayload(payload, fallback) {
        if (typeof payload === "object" && payload !== null) return payload;
        try {
            return JSON.parse(payload);
        } catch (error) {
            console.error("Skypod payload parse failed", error, payload);
            return fallback;
        }
    }

    function scopeTitle() {
        if (state.search.trim()) return "Global Search Results";
        if (state.scope === "bethesda") return "Bethesda Soundtrack";
        if (state.scope === "mods") return "Enabled Music Mods";
        if (state.scope === "skypod") return state.library.name || "My Skypod";
        if (state.scope === "playing") return "Now Playing";
        return "Complete Active Soundtrack";
    }

    function isTrackPlaying(track) {
        return Boolean(track && (track.id === state.currentTrackId || state.activeTrackIds.has(track.id)));
    }

    function isTrackExcluded(track) {
        return Boolean(track && state.excludedTrackIds.has(track.id));
    }

    function selectedTrack() {
        return state.library.tracks.find(track => track.id === state.selectedId) || null;
    }

    function currentTrack() {
        return state.library.tracks.find(track => track.id === state.currentTrackId) || null;
    }

    function filteredTracks() {
        return state.library.tracks.filter(track => matchesTrack(track, {
            scope: state.scope,
            query: state.search,
            category: state.category,
            provider: state.provider,
            isPlaying: isTrackPlaying
        }));
    }

    function updatePlayer(track) {
        if (!track) {
            elements.nowLabel.textContent = "SELECTED TRACK";
            elements.trackTitle.textContent = "The Sky Is Waiting";
            elements.trackArtist.textContent = "No soundtrack track selected";
            elements.trackCategory.textContent = "NO LIBRARY";
            elements.trackSource.textContent = "SKYPOD";
            elements.detailProvider.textContent = "Not loaded";
            elements.detailRecordSource.textContent = "Not loaded";
            elements.detailPlaylists.textContent = "None";
            elements.detailAudioType.textContent = "None";
            elements.playButton.textContent = "\u25B6";
            elements.playButton.disabled = true;
            elements.playNowButton.disabled = true;
            elements.timelineFill.style.width = "0%";
            return;
        }

        const playing = isTrackPlaying(track);
        const excluded = isTrackExcluded(track);
        const owned = state.ownedTrackIds.has(track.id);
        elements.nowLabel.textContent = playing ? "NOW PLAYING" : (excluded ? "EXCLUDED • REBUILD TO APPLY" : "SELECTED TRACK");
        elements.trackTitle.textContent = track.title;
        elements.trackArtist.textContent = track.artist || (track.recordKind === "palette" ? "Layered music palette" : track.musicTrackEditorId) || "Skyrim soundtrack";
        elements.trackCategory.textContent = displayCategory(track.category).toUpperCase();
        elements.trackSource.textContent = sourceGroupLabel(track);
        elements.detailProvider.textContent = sourceLabel(track);
        elements.detailProvider.title = sourceLabel(track);
        elements.detailRecordSource.textContent = recordSourceLabel(track);
        elements.detailRecordSource.title = [recordSourceLabel(track), track.sourceFormKey].filter(Boolean).join(" | ");
        elements.detailPlaylists.textContent = playlistLabel(track);
        elements.detailPlaylists.title = playlistDetail(track);
        elements.detailAudioType.textContent = audioTypeLabel(track);
        elements.detailAudioType.title = track.audioPaths.join("; ");
        elements.playButton.textContent = playing && owned ? "\u25A0" : "\u25B6";
        elements.playButton.disabled = excluded && !(playing && owned);
        elements.playButton.title = excluded && !(playing && owned)
            ? "Excluded tracks cannot be manually previewed"
            : "Play or stop";
        elements.playNowButton.disabled = excluded;
        elements.playNowButton.title = excluded
            ? "Uncheck Exclude to manually preview this track"
            : "Play selected track now";
        elements.timelineFill.style.width = playing ? "100%" : "0%";
    }

    function selectTrack(track, notifyNative = true) {
        if (!track) return;
        state.selectedId = track.id;
        updatePlayer(track);
        renderTracks();
        if (notifyNative) send("select", { id: track.id });
    }

    function requestPlay(track) {
        if (!track) {
            toast("Select a track first");
            return;
        }
        if (isTrackExcluded(track)) {
            toast("This track is excluded. Uncheck Exclude to preview it; rebuild Skypod Output to update Skyrim's rotation.");
            return;
        }
        if (!track.previewAvailable) {
            toast("That soundtrack record could not be made available for manual playback");
            return;
        }
        state.selectedId = track.id;
        state.transitioning = true;
        updatePlayer(track);
        send("play", { id: track.id });
        toast(`Requesting ${track.title}`);
    }

    function hasNaturalSkyrimPlayback() {
        return Boolean(state.currentMusicType && state.ownedTrackIds.size === 0);
    }

    function playRelativeTrack(offset) {
        if (hasNaturalSkyrimPlayback()) {
            if (state.transitioning) {
                toast("Wait for the current soundtrack transition to finish");
                return;
            }
            if (offset > 0) {
                send("next");
                toast("Asking Skyrim for the next track that qualifies right now");
            } else {
                toast("Skyrim playlists do not expose a safe Previous operation; use Next to skip this track");
            }
            return;
        }

        const tracks = filteredTracks().filter(track => track.previewAvailable && !isTrackExcluded(track));
        if (!tracks.length) {
            toast("No playable tracks are available in this view");
            return;
        }
        const selectedIndex = tracks.findIndex(track => track.id === state.selectedId);
        const nextIndex = selectedIndex < 0 ? 0 : (selectedIndex + offset + tracks.length) % tracks.length;
        const track = tracks[nextIndex];
        state.page = Math.floor(filteredTracks().findIndex(candidate => candidate.id === track.id) / PAGE_SIZE);
        selectTrack(track, false);
        requestPlay(track);
    }

    function setTrackExcluded(track, excluded) {
        if (!track) return;
        if (excluded) state.excludedTrackIds.add(track.id);
        else state.excludedTrackIds.delete(track.id);
        state.exclusionsSignature = "";
        renderTracks();
        updatePlayer(selectedTrack());
        send("setExcluded", { id: track.id, excluded });
        toast(excluded
            ? "Excluding track. If it is playing now, Skypod will advance it. Rebuild Skypod Output to keep it out of rotation."
            : "Included again. Rebuild Skypod Output to restore it to Skyrim's rotation.");
    }

    function renderCategoryFilter() {
        const categories = [...new Set(state.library.tracks.map(track => track.category))];
        const previous = state.category;
        elements.categoryFilter.innerHTML = '<option value="all">All categories</option>';
        categories.sort((a, b) => displayCategory(a).localeCompare(displayCategory(b))).forEach(category => {
            const option = document.createElement("option");
            option.value = category;
            option.textContent = displayCategory(category);
            elements.categoryFilter.appendChild(option);
        });
        state.category = categories.includes(previous) ? previous : "all";
        elements.categoryFilter.value = state.category;
    }

    function renderProviderFilter() {
        const providers = new Map();
        state.library.tracks.forEach(track => {
            const current = providers.get(track.providerKey);
            if (current) {
                current.count += 1;
                return;
            }
            providers.set(track.providerKey, {
                key: track.providerKey,
                label: providerIdentity(track),
                group: track.group,
                count: 1
            });
        });

        const previous = state.provider;
        elements.providerFilter.replaceChildren();
        const allOption = document.createElement("option");
        allOption.value = "all";
        allOption.textContent = `All providers (${state.library.tracks.length})`;
        elements.providerFilter.appendChild(allOption);

        [
            ["bethesda", "Bethesda"],
            ["mods", "Music Mods"],
            ["skypod", "My Skypod"]
        ].forEach(([group, label]) => {
            const entries = [...providers.values()]
                .filter(provider => provider.group === group)
                .sort((left, right) => left.label.localeCompare(right.label));
            if (!entries.length) return;
            const optionGroup = document.createElement("optgroup");
            optionGroup.label = `${label} (${entries.reduce((sum, provider) => sum + provider.count, 0)})`;
            entries.forEach(provider => {
                const option = document.createElement("option");
                option.value = provider.key;
                option.textContent = `${provider.label} (${provider.count})`;
                optionGroup.appendChild(option);
            });
            elements.providerFilter.appendChild(optionGroup);
        });

        state.provider = providers.has(previous) ? previous : "all";
        elements.providerFilter.value = state.provider;
    }

    function updateTabCounts() {
        const counts = {
            playing: state.library.tracks.filter(isTrackPlaying).length,
            all: state.library.tracks.length,
            bethesda: state.library.tracks.filter(track => track.group === "bethesda").length,
            mods: state.library.tracks.filter(track => track.group === "mods").length,
            skypod: state.library.tracks.filter(track => track.group === "skypod").length
        };
        document.querySelectorAll(".tab").forEach(tab => {
            const scope = tab.dataset.scope || "all";
            const label = tab.dataset.label || tab.textContent.replace(/\s*\(\d+\)\s*$/, "");
            tab.dataset.label = label;
            tab.textContent = `${label} (${counts[scope] || 0})`;
        });
    }

    function renderTracks() {
        updateTabCounts();
        const tracks = filteredTracks();
        const totalPages = Math.max(1, Math.ceil(tracks.length / PAGE_SIZE));
        state.page = Math.min(Math.max(0, state.page), totalPages - 1);
        const first = state.page * PAGE_SIZE;
        const visibleTracks = tracks.slice(first, first + PAGE_SIZE);

        const excludedCount = state.library.tracks.filter(isTrackExcluded).length;
        const searchPrefix = state.search.trim() ? "GLOBAL SEARCH \u2022 " : "";
        elements.trackCount.textContent = `${searchPrefix}${tracks.length} OF ${state.library.tracks.length} TRACKS${excludedCount ? ` \u2022 ${excludedCount} EXCLUDED` : ""}`;
        elements.searchHint.textContent = `SEARCHES ALL ${state.library.tracks.length} TRACKS`;
        elements.pageLabel.textContent = `PAGE ${state.page + 1} / ${totalPages}`;
        elements.firstPage.disabled = state.page === 0;
        elements.previousPage.disabled = state.page === 0;
        elements.nextPage.disabled = state.page >= totalPages - 1;
        elements.lastPage.disabled = state.page >= totalPages - 1;

        if (!visibleTracks.length) {
            elements.trackList.innerHTML = `
                <div class="empty-state">
                    <div class="empty-glyph">&#x266B;</div>
                    <h3>${state.library.tracks.length ? "No matches" : "No soundtrack catalog loaded"}</h3>
                    <p>${state.library.tracks.length
                        ? "Clear the search or choose another soundtrack source."
                        : "Build <strong>Skypod Output</strong> through MO2, enable it, and press Reload."}</p>
                </div>`;
            return;
        }

        const fragment = document.createDocumentFragment();
        visibleTracks.forEach((track, index) => {
            const playing = isTrackPlaying(track);
            const excluded = isTrackExcluded(track);
            const row = document.createElement("div");
            row.className = `track-row${track.id === state.selectedId ? " selected" : ""}${playing ? " playing" : ""}${excluded ? " excluded" : ""}`;
            row.setAttribute("role", "option");
            row.setAttribute("aria-selected", track.id === state.selectedId ? "true" : "false");
            row.innerHTML = `
                <div class="track-index"></div>
                <div class="track-main"><strong></strong><small></small></div>
                <div class="track-artist"><span></span></div>
                <div class="track-routing"><strong></strong><small></small></div>
                <div class="track-state"><strong></strong><small></small></div>
                <label class="exclude-toggle" title="Save this exclusion; rebuild Skypod Output to change Skyrim's rotation">
                    <input class="exclude-checkbox" type="checkbox" aria-label="Exclude this track">
                    <span class="exclude-box" aria-hidden="true"></span>
                    <span>EXCLUDE</span>
                </label>
                <button class="row-play" title="Play this track">&#x25B6;</button>`;

            row.querySelector(".track-index").textContent = String(first + index + 1).padStart(2, "0");
            row.querySelector(".track-main strong").textContent = track.title;
            row.querySelector(".track-main small").textContent = `${sourceGroupLabel(track)} \u2022 ${sourceLabel(track)}`;
            row.querySelector(".track-main small").title = [sourceLabel(track), recordSourceLabel(track), track.sourceFormKey].filter(Boolean).join(" | ");
            row.querySelector(".track-artist span").textContent = track.artist || (track.recordKind === "palette" ? "Layered music palette" : track.musicTrackEditorId) || "Skyrim soundtrack";
            const routing = row.querySelector(".track-routing");
            routing.querySelector("strong").textContent = displayCategory(track.category);
            routing.querySelector("small").textContent = playlistLabel(track, 1);
            routing.title = playlistDetail(track);
            const trackState = row.querySelector(".track-state");
            trackState.querySelector("strong").textContent = playing
                ? "NOW PLAYING"
                : (excluded ? "EXCLUDED" : (track.previewAvailable ? (track.recordKind === "palette" ? "PALETTE" : "READY") : "CATALOG ONLY"));
            trackState.querySelector("small").textContent = excluded ? "REBUILD TO APPLY" : "";
            const excludeCheckbox = row.querySelector(".exclude-checkbox");
            excludeCheckbox.checked = excluded;
            excludeCheckbox.setAttribute("aria-checked", excluded ? "true" : "false");
            const playButton = row.querySelector(".row-play");
            playButton.disabled = !track.previewAvailable || excluded;

            row.addEventListener("click", event => {
                if (event.target.closest(".row-play") || event.target.closest(".exclude-toggle")) return;
                selectTrack(track);
            });
            row.addEventListener("dblclick", event => {
                if (!event.target.closest(".exclude-toggle") && !event.target.closest(".row-play")) requestPlay(track);
            });
            row.querySelector(".exclude-toggle").addEventListener("click", event => event.stopPropagation());
            excludeCheckbox.addEventListener("change", event => {
                event.stopPropagation();
                setTrackExcluded(track, event.target.checked);
            });
            playButton.addEventListener("click", event => {
                event.stopPropagation();
                selectTrack(track, false);
                requestPlay(track);
            });
            fragment.appendChild(row);
        });
        elements.trackList.replaceChildren(fragment);
    }

    function renderLibrary() {
        elements.libraryName.textContent = scopeTitle();
        renderCategoryFilter();
        renderProviderFilter();
        updateTabCounts();
        renderTracks();
        const tracks = state.library.tracks;
        if (!selectedTrack() && tracks.length) {
            state.selectedId = state.currentTrackId || tracks[0].id;
        }
        updatePlayer(selectedTrack());
    }

    function updateCurrentMusicText() {
        const track = currentTrack();
        if (track) {
            elements.currentMusicText.textContent = `SKYRIM TRACK: ${track.title}`;
            return;
        }
        const raw = state.currentMusicTrack;
        if (raw?.plugin && raw?.formId) {
            elements.currentMusicText.textContent = `SKYRIM TRACK: ${raw.plugin} ${raw.formId}`;
            return;
        }
        elements.currentMusicText.textContent = "SKYRIM TRACK: NONE DETECTED";
    }

    function toast(message) {
        elements.toast.textContent = message;
        elements.toast.classList.add("show");
        clearTimeout(state.toastTimer);
        state.toastTimer = setTimeout(() => elements.toast.classList.remove("show"), 3200);
    }

    function bindEvents() {
        elements.closeButton.addEventListener("click", () => send("close"));
        elements.reloadButton.addEventListener("click", () => {
            state.page = 0;
            send("reload");
        });
        elements.searchInput.addEventListener("input", event => {
            state.search = event.target.value;
            state.page = 0;
            elements.libraryName.textContent = scopeTitle();
            renderTracks();
            elements.trackList.scrollTop = 0;
        });
        elements.categoryFilter.addEventListener("change", event => {
            state.category = event.target.value;
            state.page = 0;
            renderTracks();
            elements.trackList.scrollTop = 0;
        });
        elements.providerFilter.addEventListener("change", event => {
            state.provider = event.target.value;
            state.page = 0;
            renderTracks();
            elements.trackList.scrollTop = 0;
        });
        const changePage = page => {
            state.page = page;
            renderTracks();
            elements.trackList.scrollTop = 0;
        };
        elements.firstPage.addEventListener("click", () => changePage(0));
        elements.previousPage.addEventListener("click", () => changePage(state.page - 1));
        elements.nextPage.addEventListener("click", () => changePage(state.page + 1));
        elements.lastPage.addEventListener("click", () => changePage(Number.MAX_SAFE_INTEGER));
        elements.playNowButton.addEventListener("click", () => requestPlay(selectedTrack()));
        elements.stopButton.addEventListener("click", () => send("stop"));

        document.querySelectorAll("[data-command]").forEach(button => {
            button.addEventListener("click", () => {
                const command = button.dataset.command;
                if (command === "previous") {
                    playRelativeTrack(-1);
                    return;
                }
                if (command === "next") {
                    playRelativeTrack(1);
                    return;
                }
                const track = selectedTrack();
                if (command === "toggle" && track && isTrackPlaying(track) && state.ownedTrackIds.has(track.id)) {
                    send("stop");
                    return;
                }
                if (command === "toggle") requestPlay(track);
            });
        });

        document.querySelectorAll(".tab").forEach(tab => {
            tab.addEventListener("click", () => {
                document.querySelectorAll(".tab").forEach(candidate => candidate.classList.remove("active"));
                tab.classList.add("active");
                state.scope = tab.dataset.scope || "all";
                state.search = "";
                state.provider = "all";
                elements.searchInput.value = "";
                elements.providerFilter.value = "all";
                state.page = 0;
                if (state.scope === "playing" && currentTrack()) {
                    state.selectedId = state.currentTrackId;
                    updatePlayer(currentTrack());
                }
                elements.libraryName.textContent = scopeTitle();
                renderTracks();
                elements.trackList.scrollTop = 0;
            });
        });

        document.addEventListener("keydown", event => {
            if (event.key === "Escape") {
                event.preventDefault();
                send("close");
            }
        });
    }

    window.skypodOnLibrary = payload => {
        const library = parsePayload(payload, { name: "Skypod", tracks: [] });
        state.library = {
            schemaVersion: Number(library.schemaVersion || 1),
            name: library.name || "Skyrim Load Order",
            tracks: Array.isArray(library.tracks) ? library.tracks.map(normalizeTrack).filter(track => track.id) : []
        };
        if (!state.library.tracks.some(track => track.id === state.selectedId)) {
            state.selectedId = state.currentTrackId || state.library.tracks[0]?.id || null;
        }
        renderLibrary();
        updateCurrentMusicText();
        elements.runtimeDot.classList.add("connected");
        elements.runtimeText.textContent = "SKYPOD.DLL CONNECTED";
    };

    window.skypodOnRuntime = payload => {
        const runtime = parsePayload(payload, {});
        elements.bridgeState.textContent = runtime.engineControl
            ? "SKYRIM MUSIC CONTROL ONLINE"
            : "BUILD SKYPOD.ESP THROUGH MO2";
        if (runtime.message) toast(runtime.message);
    };

    window.skypodOnExclusions = payload => {
        const exclusions = parsePayload(payload, {});
        const ids = (Array.isArray(exclusions.excludedTrackIds) ? exclusions.excludedTrackIds : [])
            .map(normalizeId)
            .filter(Boolean);
        const signature = JSON.stringify([...ids].sort());
        if (signature === state.exclusionsSignature) return;
        state.exclusionsSignature = signature;
        state.excludedTrackIds = new Set(ids);
        renderTracks();
        updatePlayer(selectedTrack());
    };

    window.skypodOnPlayback = payload => {
        const playback = parsePayload(payload, {});
        const currentTrackId = normalizeId(playback.currentTrackId);
        const activeTrackIds = (Array.isArray(playback.activeTrackIds) ? playback.activeTrackIds : []).map(normalizeId).filter(Boolean);
        const ownedTrackIds = (Array.isArray(playback.ownedTrackIds) ? playback.ownedTrackIds : []).map(normalizeId).filter(Boolean);
        const signature = JSON.stringify([
            currentTrackId,
            activeTrackIds,
            ownedTrackIds,
            Boolean(playback.transitioning),
            playback.currentMusicType || null,
            playback.currentMusicTrack || null
        ]);
        if (signature === state.playbackSignature) return;

        const priorCurrent = state.currentTrackId;
        state.playbackSignature = signature;
        state.currentTrackId = currentTrackId;
        state.activeTrackIds = new Set(activeTrackIds);
        state.ownedTrackIds = new Set(ownedTrackIds);
        state.transitioning = Boolean(playback.transitioning);
        state.currentMusicType = playback.currentMusicType || null;
        state.currentMusicTrack = playback.currentMusicTrack || null;

        if ((!state.selectedId || state.scope === "playing") && currentTrackId) {
            state.selectedId = currentTrackId;
        }
        if (priorCurrent !== currentTrackId && state.scope === "playing") state.page = 0;
        renderTracks();
        updatePlayer(selectedTrack());
        updateCurrentMusicText();
    };

    document.addEventListener("DOMContentLoaded", () => {
        [
            "runtimeDot", "runtimeText", "closeButton", "reloadButton", "searchInput", "searchHint",
            "categoryFilter", "providerFilter", "libraryName", "trackCount", "trackList", "trackTitle",
            "trackArtist", "trackCategory", "trackSource", "nowLabel", "detailProvider",
            "detailRecordSource", "detailPlaylists", "detailAudioType", "playButton", "playNowButton", "stopButton", "timelineFill",
            "firstPage", "previousPage", "nextPage", "lastPage", "pageLabel", "currentMusicText", "bridgeState", "toast"
        ].forEach(id => { elements[id] = byId(id); });
        bindEvents();
        renderLibrary();
        send("state");
        window.setInterval(() => send("state"), 1000);
    });
})();
