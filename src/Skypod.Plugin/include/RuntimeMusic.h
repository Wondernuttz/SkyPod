#pragma once

#include <cstdint>
#include <optional>
#include <string>
#include <string_view>
#include <vector>

namespace RE { class BGSMusicType; }

namespace RuntimeMusic
{
    using StatusCallback = void (*)(std::string_view);

    struct FormReference
    {
        std::string plugin;
        std::uint32_t localFormId{ 0 };
        std::string editorId;
    };

    struct PlaybackState
    {
        std::optional<std::string> currentTrackId;
        std::vector<std::string> activeTrackIds;
        std::vector<std::string> ownedTrackIds;
        std::optional<FormReference> currentMusicType;
        std::optional<FormReference> currentMusicTrack;
        bool transitioning{ false };
    };

    enum class ExclusionPlaybackAction
    {
        kNone,
        kStoppedOwnedPreview,
        kAdvancedSkyrimTrack,
        kAdvancedSkyrimPalette,
        kNaturalContextNotSafe,
        kNaturalPlaylistHasNoAlternative
    };

    void SetStatusCallback(StatusCallback callback);
    void ReloadMap();
    [[nodiscard]] bool IsReady();
    [[nodiscard]] PlaybackState GetPlaybackState();
    [[nodiscard]] std::string Play(std::string_view trackId);
    [[nodiscard]] std::string Toggle(std::string_view trackId);
    [[nodiscard]] std::string Stop();
    [[nodiscard]] std::string AdvanceNaturalPlaylist();
    [[nodiscard]] ExclusionPlaybackAction ApplyExclusionToPlayback(std::string_view trackId);
    void Reset();
    bool HasManualMusic();
    void StartBossMusic(RE::BGSMusicType* music);
    void EndBossMusic(bool immediate = false);
}
