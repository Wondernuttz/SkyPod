#include "PCH.h"
#include "PrismaBridge.h"
#include "PrismaUI_API.h"
#include "RuntimeMusic.h"
#include "Settings.h"

#include <iomanip>

namespace PrismaBridge
{
    namespace
    {
        constexpr auto viewPath = "Skypod/index.html";
        PRISMA_UI_API::IVPrismaUI1* api{ nullptr };
        std::atomic<PrismaView> view{ 0 };
        std::atomic_bool domReady{ false };
        std::atomic_bool openIntent{ false };
        std::unordered_set<std::string> excludedTrackIds;
        bool exclusionsLoaded{ false };

        std::string NormalizeTrackId(std::string_view value)
        {
            std::string result(value);
            std::ranges::transform(result, result.begin(), [](unsigned char character) {
                return static_cast<char>(std::tolower(character));
            });
            return result;
        }

        std::filesystem::path ExclusionsPath()
        {
            return Settings::GameRoot() / "Data" / "SKSE" / "Plugins" / "Skypod.exclusions.json";
        }

        void AppendJsonString(std::string& json, std::string_view value)
        {
            json.push_back('"');
            for (const unsigned char character : value) {
                switch (character) {
                case '"':
                case '\\':
                    json.push_back('\\');
                    json.push_back(static_cast<char>(character));
                    break;
                case '\b':
                    json += "\\b";
                    break;
                case '\f':
                    json += "\\f";
                    break;
                case '\n':
                    json += "\\n";
                    break;
                case '\r':
                    json += "\\r";
                    break;
                case '\t':
                    json += "\\t";
                    break;
                default:
                    if (character < 0x20) {
                        constexpr char digits[] = "0123456789ABCDEF";
                        json += "\\u00";
                        json.push_back(digits[character >> 4]);
                        json.push_back(digits[character & 0x0F]);
                    } else {
                        json.push_back(static_cast<char>(character));
                    }
                    break;
                }
            }
            json.push_back('"');
        }

        std::string HexFormId(std::uint32_t value)
        {
            std::ostringstream stream;
            stream << std::uppercase << std::hex << std::setw(8) << std::setfill('0') << value;
            return stream.str();
        }

        void AppendFormReference(std::string& json, const std::optional<RuntimeMusic::FormReference>& form)
        {
            if (!form) {
                json += "null";
                return;
            }

            json += R"({"plugin":)";
            AppendJsonString(json, form->plugin);
            json += R"(,"formId":)";
            AppendJsonString(json, HexFormId(form->localFormId));
            json += R"(,"editorId":)";
            AppendJsonString(json, form->editorId);
            json.push_back('}');
        }

        void AppendStringArray(std::string& json, const std::vector<std::string>& values)
        {
            json.push_back('[');
            bool first = true;
            for (const auto& value : values) {
                if (!first) {
                    json.push_back(',');
                }
                first = false;
                AppendJsonString(json, value);
            }
            json.push_back(']');
        }

        std::string SerializePlaybackState(const RuntimeMusic::PlaybackState& state)
        {
            std::string json = R"({"schemaVersion":1,"currentTrackId":)";
            if (state.currentTrackId) {
                AppendJsonString(json, *state.currentTrackId);
            } else {
                json += "null";
            }
            json += R"(,"activeTrackIds":)";
            AppendStringArray(json, state.activeTrackIds);
            json += R"(,"ownedTrackIds":)";
            AppendStringArray(json, state.ownedTrackIds);
            json += R"(,"transitioning":)";
            json += state.transitioning ? "true" : "false";
            json += R"(,"currentMusicType":)";
            AppendFormReference(json, state.currentMusicType);
            json += R"(,"currentMusicTrack":)";
            AppendFormReference(json, state.currentMusicTrack);
            json.push_back('}');
            return json;
        }

        std::string ReadLibrary()
        {
            const auto path = Settings::GameRoot() / "Data" / "SKSE" / "Plugins" / "Skypod.library.json";
            std::ifstream stream(path, std::ios::binary);
            if (!stream) {
                logger::warn("Library manifest not found at {}", path.string());
                return R"({"schemaVersion":1,"name":"Skypod","tracks":[]})";
            }

            std::ostringstream contents;
            contents << stream.rdbuf();
            return contents.str();
        }

        std::optional<std::string> ExtractJsonString(std::string_view json, std::string_view key)
        {
            const auto marker = std::string("\"") + std::string(key) + "\"";
            auto cursor = json.find(marker);
            if (cursor == std::string_view::npos) {
                return std::nullopt;
            }
            cursor = json.find(':', cursor + marker.size());
            if (cursor == std::string_view::npos) {
                return std::nullopt;
            }
            cursor = json.find('"', cursor + 1);
            if (cursor == std::string_view::npos) {
                return std::nullopt;
            }

            std::string value;
            bool escaped = false;
            for (++cursor; cursor < json.size(); ++cursor) {
                const auto character = json[cursor];
                if (escaped) {
                    value.push_back(character);
                    escaped = false;
                } else if (character == '\\') {
                    escaped = true;
                } else if (character == '"') {
                    return value;
                } else {
                    value.push_back(character);
                }
            }
            return std::nullopt;
        }

        std::optional<bool> ExtractJsonBool(std::string_view json, std::string_view key)
        {
            const auto marker = std::string("\"") + std::string(key) + "\"";
            auto cursor = json.find(marker);
            if (cursor == std::string_view::npos) {
                return std::nullopt;
            }
            cursor = json.find(':', cursor + marker.size());
            if (cursor == std::string_view::npos) {
                return std::nullopt;
            }
            ++cursor;
            while (cursor < json.size() && std::isspace(static_cast<unsigned char>(json[cursor]))) {
                ++cursor;
            }
            if (json.substr(cursor).starts_with("true")) {
                return true;
            }
            if (json.substr(cursor).starts_with("false")) {
                return false;
            }
            return std::nullopt;
        }

        std::optional<std::unordered_set<std::string>> ParseExclusions(std::string_view json)
        {
            constexpr std::string_view marker = "\"excludedTrackIds\"";
            auto cursor = json.find(marker);
            if (cursor == std::string_view::npos) {
                return std::nullopt;
            }
            cursor = json.find('[', cursor + marker.size());
            if (cursor == std::string_view::npos) {
                return std::nullopt;
            }

            std::unordered_set<std::string> result;
            for (++cursor; cursor < json.size();) {
                while (cursor < json.size() &&
                    (std::isspace(static_cast<unsigned char>(json[cursor])) || json[cursor] == ',')) {
                    ++cursor;
                }
                if (cursor >= json.size()) {
                    return std::nullopt;
                }
                if (json[cursor] == ']') {
                    return result;
                }
                if (json[cursor] != '"') {
                    return std::nullopt;
                }

                std::string value;
                bool escaped = false;
                bool closed = false;
                for (++cursor; cursor < json.size(); ++cursor) {
                    const auto character = json[cursor];
                    if (escaped) {
                        value.push_back(character);
                        escaped = false;
                    } else if (character == '\\') {
                        escaped = true;
                    } else if (character == '"') {
                        ++cursor;
                        closed = true;
                        break;
                    } else {
                        value.push_back(character);
                    }
                }
                if (!closed) {
                    return std::nullopt;
                }
                if (!value.empty()) {
                    result.insert(NormalizeTrackId(value));
                }
            }
            return std::nullopt;
        }

        void LoadExclusions()
        {
            const auto path = ExclusionsPath();
            std::ifstream stream(path, std::ios::binary);
            if (!stream) {
                excludedTrackIds.clear();
                exclusionsLoaded = true;
                logger::info("No persisted exclusion file at {}; starting with an empty set", path.string());
                return;
            }

            std::ostringstream contents;
            contents << stream.rdbuf();
            const auto parsed = ParseExclusions(contents.str());
            if (!parsed) {
                logger::error("Could not parse {}; retaining the previous in-memory exclusions", path.string());
                exclusionsLoaded = true;
                return;
            }

            excludedTrackIds = *parsed;
            exclusionsLoaded = true;
            logger::info("Loaded {} persisted soundtrack exclusions", excludedTrackIds.size());
        }

        void EnsureExclusionsLoaded()
        {
            if (!exclusionsLoaded) {
                LoadExclusions();
            }
        }

        bool SaveExclusions()
        {
            const auto path = ExclusionsPath();
            std::error_code error;
            std::filesystem::create_directories(path.parent_path(), error);
            if (error) {
                logger::error("Could not create exclusion directory {}: {}", path.parent_path().string(), error.message());
                return false;
            }

            std::vector<std::string> sorted(excludedTrackIds.begin(), excludedTrackIds.end());
            std::ranges::sort(sorted);
            std::string json = R"({"schemaVersion":1,"excludedTrackIds":)";
            AppendStringArray(json, sorted);
            json += "}\n";

            auto temporaryPath = path;
            temporaryPath += ".tmp";
            {
                std::ofstream stream(temporaryPath, std::ios::binary | std::ios::trunc);
                if (!stream) {
                    logger::error("Could not open temporary exclusion file {}", temporaryPath.string());
                    return false;
                }
                stream.write(json.data(), static_cast<std::streamsize>(json.size()));
                stream.flush();
                if (!stream) {
                    logger::error("Could not write temporary exclusion file {}", temporaryPath.string());
                    return false;
                }
            }

            if (!MoveFileExW(temporaryPath.c_str(), path.c_str(),
                    MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH)) {
                const auto win32Error = GetLastError();
                logger::error("Could not commit exclusion file {} (Win32 error {})", path.string(), win32Error);
                std::filesystem::remove(temporaryPath, error);
                return false;
            }

            logger::info("Saved {} soundtrack exclusions to {}", excludedTrackIds.size(), path.string());
            return true;
        }

        bool SetExcluded(std::string_view trackId, bool excluded)
        {
            EnsureExclusionsLoaded();
            const auto normalized = NormalizeTrackId(trackId);
            const auto wasExcluded = excludedTrackIds.contains(normalized);
            if (wasExcluded == excluded) {
                return true;
            }

            if (excluded) {
                excludedTrackIds.insert(normalized);
            } else {
                excludedTrackIds.erase(normalized);
            }
            if (SaveExclusions()) {
                return true;
            }

            if (wasExcluded) {
                excludedTrackIds.insert(normalized);
            } else {
                excludedTrackIds.erase(normalized);
            }
            return false;
        }

        bool IsExcluded(std::string_view trackId)
        {
            EnsureExclusionsLoaded();
            return excludedTrackIds.contains(NormalizeTrackId(trackId));
        }

        void SendRuntimeStatusToView(std::string_view message)
        {
            const auto currentView = view.load(std::memory_order_acquire);
            if (!api || !currentView || !domReady.load(std::memory_order_acquire)) {
                return;
            }

            std::string json = RuntimeMusic::IsReady()
                ? R"({"phase":"runtime","engineControl":true,"message":")"
                : R"({"phase":"library","engineControl":false,"message":")";
            for (const char character : message) {
                if (character == '"' || character == '\\') {
                    json.push_back('\\');
                }
                json.push_back(character);
            }
            json += R"("})";
            api->InteropCall(currentView, "skypodOnRuntime", json.c_str());
        }

        void SendPlaybackStateToView()
        {
            const auto currentView = view.load(std::memory_order_acquire);
            if (!api || !currentView || !domReady.load(std::memory_order_acquire)) {
                return;
            }

            const auto json = SerializePlaybackState(RuntimeMusic::GetPlaybackState());
            api->InteropCall(currentView, "skypodOnPlayback", json.c_str());
        }

        void SendExclusionsToView()
        {
            const auto currentView = view.load(std::memory_order_acquire);
            if (!api || !currentView || !domReady.load(std::memory_order_acquire)) {
                return;
            }

            EnsureExclusionsLoaded();
            std::vector<std::string> sorted(excludedTrackIds.begin(), excludedTrackIds.end());
            std::ranges::sort(sorted);
            std::string json = R"({"schemaVersion":1,"excludedTrackIds":)";
            AppendStringArray(json, sorted);
            json.push_back('}');
            api->InteropCall(currentView, "skypodOnExclusions", json.c_str());
        }

        void SendLibrary()
        {
            const auto currentView = view.load(std::memory_order_acquire);
            if (!api || !currentView || !domReady.load(std::memory_order_acquire)) {
                return;
            }

            const auto library = ReadLibrary();
            api->InteropCall(currentView, "skypodOnLibrary", library.c_str());
            SendExclusionsToView();
            SendRuntimeStatusToView(RuntimeMusic::IsReady()
                ? "Library loaded. Live Skyrim music control is ready."
                : "Library loaded, but the runtime map is missing. Build Skypod.esp through MO2.");
            SendPlaybackStateToView();
        }

        void OnJsCommand(const char* argument)
        {
            const std::string command = argument ? argument : "";
            const auto commandName = ExtractJsonString(command, "command");
            if (!commandName) {
                logger::warn("Invalid Prisma command: {}", command);
                SendRuntimeStatusToView("Skypod received an invalid player command.");
                return;
            }
            if (*commandName != "state") {
                logger::info("Prisma command: {}", command);
            }

            if (*commandName == "close") {
                Close();
                return;
            }
            if (*commandName == "reload") {
                LoadExclusions();
                RuntimeMusic::ReloadMap();
                SendLibrary();
                return;
            }
            if (*commandName == "state") {
                SendPlaybackStateToView();
                return;
            }
            if (*commandName == "stop") {
                SendRuntimeStatusToView(RuntimeMusic::Stop());
                SendPlaybackStateToView();
                return;
            }
            if (*commandName == "next") {
                SendRuntimeStatusToView(RuntimeMusic::AdvanceNaturalPlaylist());
                SendPlaybackStateToView();
                return;
            }

            const auto trackId = ExtractJsonString(command, "id");
            if (*commandName == "setExcluded") {
                const auto excluded = ExtractJsonBool(command, "excluded");
                if (!trackId || trackId->empty() || !excluded) {
                    SendRuntimeStatusToView("Skypod received an invalid exclusion command.");
                    SendExclusionsToView();
                    return;
                }

                if (!SetExcluded(*trackId, *excluded)) {
                    SendRuntimeStatusToView("Skypod could not save that exclusion. The previous setting was restored.");
                    SendExclusionsToView();
                    return;
                }

                auto playbackAction = RuntimeMusic::ExclusionPlaybackAction::kNone;
                if (*excluded) {
                    playbackAction = RuntimeMusic::ApplyExclusionToPlayback(*trackId);
                    SendPlaybackStateToView();
                }
                SendExclusionsToView();
                switch (playbackAction) {
                case RuntimeMusic::ExclusionPlaybackAction::kStoppedOwnedPreview:
                    SendRuntimeStatusToView(
                        "Excluded the track and stopped its Skypod preview. Rebuild Skypod Output to remove it from Skyrim's rotation.");
                    break;
                case RuntimeMusic::ExclusionPlaybackAction::kAdvancedSkyrimPalette:
                    SendRuntimeStatusToView(
                        "Excluded the active palette and finished its playing layers. Skyrim is advancing the current playlist. Rebuild Skypod Output to keep it out of rotation.");
                    break;
                case RuntimeMusic::ExclusionPlaybackAction::kAdvancedSkyrimTrack:
                    SendRuntimeStatusToView(
                        "Excluded and skipped the active Skyrim track. Skyrim is advancing the current playlist. Rebuild Skypod Output to keep it out of rotation.");
                    break;
                case RuntimeMusic::ExclusionPlaybackAction::kNaturalPlaylistHasNoAlternative:
                    SendRuntimeStatusToView(
                        "Exclusion saved, but this ambient playlist has no other top-level track to advance to. Rebuild Skypod Output to keep it out of rotation.");
                    break;
                case RuntimeMusic::ExclusionPlaybackAction::kNaturalContextNotSafe:
                    SendRuntimeStatusToView(
                        "Exclusion saved, but Skypod left this event or non-ambient music context untouched. Rebuild Skypod Output to keep it out of rotation.");
                    break;
                default:
                    SendRuntimeStatusToView(*excluded
                        ? "Track excluded from manual preview. Rebuild Skypod Output to remove it from Skyrim's rotation."
                        : "Track included again. Rebuild Skypod Output to restore it to Skyrim's rotation.");
                    break;
                }
                return;
            }
            if ((*commandName == "play" || *commandName == "toggle") && (!trackId || trackId->empty())) {
                SendRuntimeStatusToView("Select a track first.");
                return;
            }
            if (*commandName == "play") {
                if (IsExcluded(*trackId)) {
                    SendRuntimeStatusToView("That track is excluded. Uncheck Exclude before manually previewing it.");
                    return;
                }
                SendRuntimeStatusToView(RuntimeMusic::Play(*trackId));
                SendPlaybackStateToView();
                return;
            }
            if (*commandName == "toggle") {
                if (IsExcluded(*trackId)) {
                    SendRuntimeStatusToView("That track is excluded. Uncheck Exclude before manually previewing it.");
                    return;
                }
                SendRuntimeStatusToView(RuntimeMusic::Toggle(*trackId));
                SendPlaybackStateToView();
                return;
            }
            if (*commandName == "select") {
                return;
            }

            SendRuntimeStatusToView("That control is not persistent yet. Playback controls are live.");
        }

        void OnDomReady(PrismaView readyView)
        {
            const auto currentView = view.load(std::memory_order_acquire);
            if (!currentView || readyView != currentView) {
                logger::info("Ignoring DOM-ready callback for stale Skypod view {}", readyView);
                return;
            }

            domReady.store(true, std::memory_order_release);
            logger::info("Skypod Prisma DOM ready for view {}", readyView);
            if (!openIntent.load(std::memory_order_acquire) || !api) {
                return;
            }

            api->Show(currentView);
            api->Focus(currentView, false, false);
            SendLibrary();
        }

        bool EnsureView()
        {
            if (view.load(std::memory_order_acquire)) {
                return true;
            }
            if (!api && !Initialize()) {
                return false;
            }

            const auto createdView = api->CreateView(viewPath, &OnDomReady);
            if (!createdView) {
                logger::error("PrismaUI could not create {}", viewPath);
                return false;
            }

            domReady.store(false, std::memory_order_release);
            view.store(createdView, std::memory_order_release);
            api->Hide(createdView);
            api->SetScrollingPixelSize(createdView, 90);
            api->RegisterJSListener(createdView, "skypodCommand", &OnJsCommand);
            logger::info("Skypod Prisma view {} created", createdView);
            return true;
        }
    }

    bool Initialize()
    {
        if (api) {
            return true;
        }

        api = PRISMA_UI_API::RequestV1();
        if (!api) {
            logger::error("PrismaUI V1 API is unavailable. Is PrismaUI installed and loaded?");
            return false;
        }

        logger::info("PrismaUI V1 API acquired; view creation is deferred until first open");
        return true;
    }

    void Open()
    {
        openIntent = true;
        if (!EnsureView()) {
            openIntent = false;
            return;
        }

        const auto currentView = view.load(std::memory_order_acquire);
        if (domReady.load(std::memory_order_acquire) && currentView) {
            api->Show(currentView);
            api->Focus(currentView, false, false);
            SendLibrary();
        }
        logger::info("Skypod open requested (DOM ready: {})", domReady.load());
    }

    void Close()
    {
        openIntent = false;
        domReady.store(false, std::memory_order_release);
        const auto closingView = view.exchange(0, std::memory_order_acq_rel);
        if (api && closingView) {
            api->Unfocus(closingView);
            api->Destroy(closingView);
            logger::info("Skypod Prisma view {} destroyed", closingView);
        }
        logger::info("Skypod closed");
    }

    void Toggle()
    {
        IsOpen() ? Close() : Open();
    }

    bool IsOpen()
    {
        return openIntent.load();
    }

    void ReloadLibrary()
    {
        LoadExclusions();
        SendLibrary();
    }

    void ReportRuntimeStatus(std::string_view message)
    {
        SendRuntimeStatusToView(message);
        SendPlaybackStateToView();
    }

    void Shutdown()
    {
        openIntent = false;
        domReady = false;
        const auto closingView = view.exchange(0, std::memory_order_acq_rel);
        if (api && closingView) {
            api->Destroy(closingView);
        }
        api = nullptr;
    }
}
