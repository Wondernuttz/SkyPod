#include "PCH.h"
#include "RuntimeMusic.h"
#include "BossMusic.h"
#include "Settings.h"

#include <chrono>
#include <condition_variable>
#include <functional>
#include <thread>

namespace RuntimeMusic
{
    namespace
    {
        using namespace std::chrono_literals;
        using Clock = std::chrono::steady_clock;
        using Message = RE::BSMusicEvent::MUSIC_MESSAGE_TYPE;

        struct MusicReference
        {
            std::string previewPlugin;
            std::uint32_t previewLocalFormId{ 0 };
            RE::BGSMusicType* preview{ nullptr };
        };

        enum class TransitionPhase
        {
            kIdle,
            kWaitingForRemoval,
            kVerifyingStart,
            kWaitingForCleanup,
            kWaitingForForcedCleanup
        };

        class DelayedScheduler
        {
        public:
            DelayedScheduler() :
                worker([this](std::stop_token token) { Run(token); })
            {}

            ~DelayedScheduler()
            {
                worker.request_stop();
                condition.notify_all();
            }

            void Schedule(std::chrono::milliseconds delay, std::function<void()> callback)
            {
                std::scoped_lock lock(mutex);
                ++revision;
                job = Job{ Clock::now() + delay, std::move(callback) };
                condition.notify_all();
            }

            void Cancel()
            {
                std::scoped_lock lock(mutex);
                ++revision;
                job.reset();
                condition.notify_all();
            }

        private:
            struct Job
            {
                Clock::time_point due;
                std::function<void()> callback;
            };

            void Run(std::stop_token token)
            {
                std::unique_lock lock(mutex);
                while (!token.stop_requested()) {
                    condition.wait(lock, [&] { return token.stop_requested() || job.has_value(); });
                    if (token.stop_requested()) {
                        return;
                    }

                    const auto expectedRevision = revision;
                    const auto due = job->due;
                    if (condition.wait_until(lock, due, [&] {
                            return token.stop_requested() || revision != expectedRevision;
                        })) {
                        continue;
                    }

                    auto callback = std::move(job->callback);
                    job.reset();
                    lock.unlock();
                    if (const auto* tasks = SKSE::GetTaskInterface()) {
                        tasks->AddTask(std::move(callback));
                    }
                    lock.lock();
                }
            }

            std::mutex mutex;
            std::condition_variable condition;
            std::optional<Job> job;
            std::uint64_t revision{ 0 };
            std::jthread worker;
        };

        std::unordered_map<std::string, MusicReference> musicByTrack;
        std::unordered_map<RE::BGSMusicType*, std::vector<std::string>> trackIdsByPreview;
        std::unordered_map<std::string, std::string> catalogIdBySourceForm;
        std::unordered_set<RE::BGSMusicType*> ownedTypes;
        std::unordered_map<RE::BGSMusicType*, std::string> ownedTrackByType;

        RE::BGSMusicType* pendingPreview{ nullptr };
        std::unordered_map<RE::BGSMusicType*, Clock::time_point> bossTypes;
        RE::BGSMusicType* verifyingPreview{ nullptr };
        std::string pendingTrackId;
        std::string verifyingTrackId;
        std::string cleanupSuccessMessage;
        TransitionPhase transitionPhase{ TransitionPhase::kIdle };
        Clock::time_point transitionDeadline{};
        std::uint64_t timerToken{ 0 };
        StatusCallback reportStatus{ nullptr };

        DelayedScheduler& Scheduler()
        {
            static DelayedScheduler scheduler;
            return scheduler;
        }

        DelayedScheduler& SkipConfirmationScheduler()
        {
            static DelayedScheduler scheduler;
            return scheduler;
        }

        RE::BSTEventSource<RE::BSMusicEvent>& EventSource()
        {
            static auto* source = new RE::BSTEventSource<RE::BSMusicEvent>();
            return *source;
        }

        void Report(std::string_view message)
        {
            logger::info("Runtime music: {}", message);
            if (reportStatus) {
                reportStatus(message);
            }
        }

        bool Send(RE::BGSMusicType* musicType, Message message)
        {
            auto* manager = RE::BSMusicManager::GetSingleton();
            if (!manager || !musicType) {
                return false;
            }

            static RE::BSMusicManager* registeredManager{ nullptr };
            if (registeredManager != manager) {
                if (registeredManager) {
                    EventSource().RemoveEventSink(registeredManager);
                }
                EventSource().AddEventSink(manager);
                registeredManager = manager;
            }

            RE::BSMusicEvent event{
                static_cast<RE::BSIMusicType*>(musicType),
                message,
                0
            };
            EventSource().SendEvent(std::addressof(event));
            return true;
        }

        bool IsPlayingOrPaused(const RE::BSIMusicType* musicType)
        {
            if (!musicType) {
                return false;
            }

            using Status = RE::BSIMusicType::MUSIC_STATUS;
            const auto status = musicType->typeStatus.get();
            return status == Status::kPlaying || status == Status::kPaused;
        }

        bool HasActiveStatus(const RE::BSIMusicType* musicType)
        {
            if (!musicType) {
                return false;
            }

            using Status = RE::BSIMusicType::MUSIC_STATUS;
            const auto status = musicType->typeStatus.get();
            return status == Status::kPlaying || status == Status::kPaused || status == Status::kFinishing;
        }

        bool IsManagedBySkyrim(const RE::BSIMusicType* musicType)
        {
            if (!musicType) {
                return false;
            }

            auto* manager = RE::BSMusicManager::GetSingleton();
            if (!manager) {
                // The manager is expected after DataLoaded. Status is only a fallback for
                // the brief interval where Skyrim has not exposed its singleton yet.
                return HasActiveStatus(musicType);
            }
            if (manager->current == musicType) {
                return true;
            }
            return std::ranges::find(manager->musicQueue, musicType) != manager->musicQueue.end();
        }

        std::string NormalizeId(std::string_view value)
        {
            std::string result(value);
            std::ranges::transform(result, result.begin(), [](unsigned char character) {
                return static_cast<char>(std::tolower(character));
            });
            return result;
        }

        std::optional<std::uint32_t> ParseHex(std::string_view value)
        {
            if (value.starts_with("0x") || value.starts_with("0X")) {
                value.remove_prefix(2);
            }
            if (value.empty()) {
                return std::nullopt;
            }

            std::uint32_t result{ 0 };
            const auto parsed = std::from_chars(value.data(), value.data() + value.size(), result, 16);
            if (parsed.ec != std::errc{} || parsed.ptr != value.data() + value.size()) {
                return std::nullopt;
            }
            return result;
        }

        std::string FormKey(std::string_view plugin, std::uint32_t localFormId)
        {
            return NormalizeId(plugin) + '|' + std::to_string(localFormId);
        }

        std::optional<FormReference> IdentifyForm(const RE::TESForm* form, bool includeEditorId = false)
        {
            if (!form) {
                return std::nullopt;
            }

            const auto* file = form->GetFile(0);
            if (!file) {
                return std::nullopt;
            }

            FormReference result{
                std::string(file->GetFilename()),
                form->GetLocalFormID(),
                {}
            };
            if (includeEditorId) {
                if (const auto* editorId = form->GetFormEditorID(); editorId) {
                    result.editorId = editorId;
                }
            }
            return result;
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
            ++cursor;
            while (cursor < json.size() && std::isspace(static_cast<unsigned char>(json[cursor]))) {
                ++cursor;
            }
            if (cursor >= json.size() || json[cursor] != '"') {
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

        void ReadCatalogMappings()
        {
            const auto path = Settings::GameRoot() / "Data" / "SKSE" / "Plugins" / "Skypod.library.json";
            std::ifstream stream(path, std::ios::binary);
            if (!stream) {
                return;
            }

            std::ostringstream contents;
            contents << stream.rdbuf();
            const auto json = contents.str();
            const auto tracksMarker = json.find("\"tracks\"");
            const auto arrayStart = tracksMarker == std::string::npos ? std::string::npos : json.find('[', tracksMarker);
            if (arrayStart == std::string::npos) {
                logger::warn("Skypod library has no tracks array; ambient music matching is unavailable");
                return;
            }

            std::size_t objectStart = std::string::npos;
            std::uint32_t objectDepth = 0;
            bool inString = false;
            bool escaped = false;
            std::size_t mappedSources = 0;
            std::size_t recoveredPreviews = 0;

            const auto consumeTrack = [&](std::string_view object) {
                const auto idValue = ExtractJsonString(object, "id");
                if (!idValue || idValue->empty()) {
                    return;
                }
                const auto id = NormalizeId(*idValue);

                if (const auto sourceForm = ExtractJsonString(object, "sourceFormKey")) {
                    const auto separator = sourceForm->rfind('|');
                    if (separator != std::string::npos) {
                        const auto localId = ParseHex(std::string_view(*sourceForm).substr(separator + 1));
                        if (localId) {
                            catalogIdBySourceForm.insert_or_assign(
                                FormKey(std::string_view(*sourceForm).substr(0, separator), *localId), id);
                            ++mappedSources;
                        }
                    }
                }

                const auto previewPlugin = ExtractJsonString(object, "previewPlugin");
                const auto previewFormText = ExtractJsonString(object, "previewLocalFormId");
                const auto previewForm = previewFormText ? ParseHex(*previewFormText) : std::nullopt;
                if (previewPlugin && !previewPlugin->empty() && previewForm) {
                    auto [entry, inserted] = musicByTrack.try_emplace(id);
                    if (inserted || entry->second.previewPlugin.empty()) {
                        entry->second.previewPlugin = *previewPlugin;
                        entry->second.previewLocalFormId = *previewForm;
                        ++recoveredPreviews;
                    }
                }
            };

            for (auto cursor = arrayStart + 1; cursor < json.size(); ++cursor) {
                const auto character = json[cursor];
                if (inString) {
                    if (escaped) {
                        escaped = false;
                    } else if (character == '\\') {
                        escaped = true;
                    } else if (character == '"') {
                        inString = false;
                    }
                    continue;
                }
                if (character == '"') {
                    inString = true;
                } else if (character == '{') {
                    if (objectDepth++ == 0) {
                        objectStart = cursor;
                    }
                } else if (character == '}' && objectDepth > 0) {
                    if (--objectDepth == 0 && objectStart != std::string::npos) {
                        consumeTrack(std::string_view(json).substr(objectStart, cursor - objectStart + 1));
                        objectStart = std::string::npos;
                    }
                } else if (character == ']' && objectDepth == 0) {
                    break;
                }
            }

            logger::info("Loaded {} ambient track identities and {} preview references from the runtime catalog",
                mappedSources, recoveredPreviews);
        }

        RE::BGSMusicType* Resolve(std::string_view trackId)
        {
            const auto found = musicByTrack.find(NormalizeId(trackId));
            if (found == musicByTrack.end()) {
                return nullptr;
            }
            return found->second.preview;
        }

        void IndexReferences()
        {
            trackIdsByPreview.clear();
            auto* dataHandler = RE::TESDataHandler::GetSingleton();
            if (!dataHandler) {
                return;
            }

            for (auto& [trackId, reference] : musicByTrack) {
                reference.preview = reference.previewPlugin.empty() ? nullptr :
                    dataHandler->LookupForm<RE::BGSMusicType>(reference.previewLocalFormId, reference.previewPlugin);
                if (reference.preview) {
                    trackIdsByPreview[reference.preview].push_back(trackId);
                } else {
                    logger::warn("Runtime preview {} could not resolve {}|{:08X}",
                        trackId, reference.previewPlugin, reference.previewLocalFormId);
                }
            }
        }

        void CancelTick()
        {
            ++timerToken;
            Scheduler().Cancel();
        }

        void Tick(std::uint64_t token);

        void ScheduleTick(std::chrono::milliseconds delay)
        {
            const auto token = ++timerToken;
            Scheduler().Schedule(delay, [token] { Tick(token); });
        }

        void EraseOwned(std::unordered_set<RE::BGSMusicType*>::iterator& iterator)
        {
            ownedTrackByType.erase(*iterator);
            iterator = ownedTypes.erase(iterator);
        }

        bool PruneReleasedOwned()
        {
            bool hasManaged = false;
            for (auto iterator = ownedTypes.begin(); iterator != ownedTypes.end();) {
                if (IsManagedBySkyrim(*iterator)) {
                    hasManaged = true;
                    ++iterator;
                } else {
                    EraseOwned(iterator);
                }
            }
            return hasManaged;
        }

        void StartPending()
        {
            auto* target = pendingPreview;
            auto trackId = std::move(pendingTrackId);
            pendingPreview = nullptr;
            pendingTrackId.clear();
            if (!target) {
                transitionPhase = TransitionPhase::kIdle;
                return;
            }

            if (!Send(target, Message::kAdd)) {
                transitionPhase = TransitionPhase::kIdle;
                Report("Skyrim's music manager was unavailable.");
                return;
            }

            ownedTypes.insert(target);
            ownedTrackByType.insert_or_assign(target, trackId);
            verifyingPreview = target;
            verifyingTrackId = std::move(trackId);
            transitionPhase = TransitionPhase::kVerifyingStart;
            transitionDeadline = Clock::now() + 2500ms;
            ScheduleTick(50ms);
        }

        void Tick(std::uint64_t token)
        {
            if (token != timerToken) {
                return;
            }

            const auto now = Clock::now();
            if (transitionPhase == TransitionPhase::kWaitingForRemoval) {
                if (!PruneReleasedOwned()) {
                    StartPending();
                    return;
                }
                if (now < transitionDeadline) {
                    ScheduleTick(50ms);
                    return;
                }

                pendingPreview = nullptr;
                pendingTrackId.clear();
                transitionPhase = TransitionPhase::kIdle;
                Report("Skyrim still owns the previous preview after a forced stop, so Skypod cancelled the switch to prevent overlapping music.");
                return;
            }

            if (transitionPhase == TransitionPhase::kVerifyingStart) {
                if (IsManagedBySkyrim(verifyingPreview) && IsPlayingOrPaused(verifyingPreview)) {
                    const auto trackId = verifyingTrackId;
                    verifyingPreview = nullptr;
                    verifyingTrackId.clear();
                    transitionPhase = TransitionPhase::kIdle;
                    Report(std::string("Playing track ") + trackId);
                    return;
                }
                if (now < transitionDeadline) {
                    ScheduleTick(50ms);
                    return;
                }

                if (verifyingPreview) {
                    Send(verifyingPreview, Message::kRemoveImmediate);
                }
                verifyingPreview = nullptr;
                verifyingTrackId.clear();
                cleanupSuccessMessage = "Blocked preview removed from Skyrim's music queue.";
                transitionPhase = TransitionPhase::kWaitingForForcedCleanup;
                transitionDeadline = now + 6000ms;
                Report("Skyrim did not start that preview. It was discarded instead of being left in the music queue.");
                ScheduleTick(50ms);
                return;
            }

            if (transitionPhase == TransitionPhase::kWaitingForCleanup) {
                if (!PruneReleasedOwned()) {
                    transitionPhase = TransitionPhase::kIdle;
                    if (!cleanupSuccessMessage.empty()) {
                        auto message = std::move(cleanupSuccessMessage);
                        cleanupSuccessMessage.clear();
                        Report(message);
                    }
                    return;
                }
                if (now < transitionDeadline) {
                    ScheduleTick(50ms);
                    return;
                }

                for (auto* type : ownedTypes) {
                    if (IsManagedBySkyrim(type)) {
                        Send(type, Message::kRemoveImmediate);
                    }
                }
                transitionPhase = TransitionPhase::kWaitingForForcedCleanup;
                transitionDeadline = now + 6000ms;
                ScheduleTick(50ms);
                return;
            }

            if (transitionPhase == TransitionPhase::kWaitingForForcedCleanup) {
                if (!PruneReleasedOwned()) {
                    transitionPhase = TransitionPhase::kIdle;
                    if (!cleanupSuccessMessage.empty()) {
                        auto message = std::move(cleanupSuccessMessage);
                        cleanupSuccessMessage.clear();
                        Report(message);
                    }
                    return;
                }
                if (now < transitionDeadline) {
                    ScheduleTick(50ms);
                    return;
                }

                cleanupSuccessMessage.clear();
                transitionPhase = TransitionPhase::kIdle;
                Report("Skyrim still owns a Skypod preview after a forced stop; cleanup ownership was retained.");
            }
        }

        bool BeginPlay(RE::BGSMusicType* target, std::string_view trackId)
        {
            const auto normalizedTrackId = NormalizeId(trackId);
            if ((pendingPreview == target && pendingTrackId == normalizedTrackId) ||
                (verifyingPreview == target && verifyingTrackId == normalizedTrackId)) {
                return transitionPhase == TransitionPhase::kWaitingForRemoval;
            }

            if (transitionPhase == TransitionPhase::kWaitingForRemoval && pendingPreview) {
                pendingPreview = target;
                pendingTrackId = normalizedTrackId;
                return true;
            }

            if (transitionPhase == TransitionPhase::kWaitingForForcedCleanup && !pendingPreview) {
                CancelTick();
                pendingPreview = target;
                pendingTrackId = normalizedTrackId;
                transitionPhase = TransitionPhase::kWaitingForRemoval;
                transitionDeadline = Clock::now() + 6000ms;
                ScheduleTick(50ms);
                return true;
            }

            if (transitionPhase == TransitionPhase::kIdle && ownedTypes.contains(target) &&
                IsManagedBySkyrim(target) && IsPlayingOrPaused(target) &&
                ownedTrackByType[target] == normalizedTrackId) {
                return false;
            }

            CancelTick();
            verifyingPreview = nullptr;
            verifyingTrackId.clear();
            cleanupSuccessMessage.clear();
            pendingPreview = target;
            pendingTrackId = normalizedTrackId;

            for (auto* type : ownedTypes) {
                if (IsManagedBySkyrim(type)) {
                    Send(type, Message::kRemoveImmediate);
                }
            }

            const auto waitingForOldTrack = PruneReleasedOwned();

            if (waitingForOldTrack) {
                transitionPhase = TransitionPhase::kWaitingForRemoval;
                transitionDeadline = Clock::now() + 6000ms;
                ScheduleTick(50ms);
                return true;
            }

            StartPending();
            return false;
        }

        RE::BSIMusicTrack* GetSelectedTrack(RE::BSIMusicType* musicType)
        {
            if (!musicType || musicType->tracks.empty()) {
                return nullptr;
            }

            using Status = RE::BSIMusicTrack::MUSIC_STATUS;
            for (const auto wanted : { Status::kPlaying, Status::kPaused, Status::kFinishing }) {
                for (auto* track : musicType->tracks) {
                    if (track && track->GetMusicStatus() == wanted) {
                        return track;
                    }
                }
            }
            return musicType->currentTrackIndex < musicType->tracks.size() ?
                musicType->tracks[musicType->currentTrackIndex] : nullptr;
        }

        std::optional<std::string> CatalogIdForTrack(RE::BSIMusicTrack* track)
        {
            auto* wrapper = track ? skyrim_cast<RE::BGSMusicTrackFormWrapper*>(track) : nullptr;
            const auto form = IdentifyForm(wrapper);
            if (!form) {
                return std::nullopt;
            }

            const auto found = catalogIdBySourceForm.find(FormKey(form->plugin, form->localFormId));
            return found == catalogIdBySourceForm.end() ? std::nullopt :
                std::optional<std::string>(found->second);
        }

        void ConfirmNaturalAdvance(
            RE::BSIMusicType* expectedType,
            RE::BSIMusicTrack* previousTrack,
            std::string operation,
            std::uint8_t checksRemaining)
        {
            auto* manager = RE::BSMusicManager::GetSingleton();
            if (!manager || manager->current != expectedType) {
                logger::info("{} handed off with its music context", operation);
                return;
            }

            auto* selectedTrack = expectedType->currentTrackIndex < expectedType->tracks.size() ?
                expectedType->tracks[expectedType->currentTrackIndex] : nullptr;
            if (selectedTrack != previousTrack) {
                const auto nextId = CatalogIdForTrack(selectedTrack);
                logger::info("{} advanced to {}",
                    operation, nextId.value_or("an uncataloged track"));
                return;
            }

            if (checksRemaining > 1) {
                SkipConfirmationScheduler().Schedule(250ms, [
                    expectedType,
                    previousTrack,
                    operation = std::move(operation),
                    checksRemaining
                ]() mutable {
                    ConfirmNaturalAdvance(
                        expectedType, previousTrack, std::move(operation), checksRemaining - 1);
                });
                return;
            }

            logger::warn(
                "{} finished its track, but the ambient MUST did not select another top-level track within the confirmation window",
                operation);
        }

        void AddPreviewIds(std::vector<std::string>& output, RE::BGSMusicType* musicType)
        {
            const auto found = trackIdsByPreview.find(musicType);
            if (found != trackIdsByPreview.end()) {
                output.insert(output.end(), found->second.begin(), found->second.end());
            }
        }

        void SortUnique(std::vector<std::string>& values)
        {
            std::ranges::sort(values);
            values.erase(std::unique(values.begin(), values.end()), values.end());
        }
    }

    void SetStatusCallback(StatusCallback callback)
    {
        reportStatus = callback;
    }

    void ReloadMap()
    {
        Reset();
        musicByTrack.clear();
        trackIdsByPreview.clear();
        catalogIdBySourceForm.clear();

        const auto path = Settings::GameRoot() / "Data" / "SKSE" / "Plugins" / "Skypod.runtime.tsv";
        std::ifstream stream(path);
        if (!stream) {
            logger::warn("Runtime music map not found at {}", path.string());
        } else {
            std::string line;
            while (std::getline(stream, line)) {
                if (!line.empty() && line.back() == '\r') {
                    line.pop_back();
                }
                if (line.empty() || line.front() == '#') {
                    continue;
                }

                std::vector<std::string_view> columns;
                std::size_t start = 0;
                while (start <= line.size()) {
                    const auto tab = line.find('\t', start);
                    columns.emplace_back(line.data() + start,
                        (tab == std::string::npos ? line.size() : tab) - start);
                    if (tab == std::string::npos) {
                        break;
                    }
                    start = tab + 1;
                }
                if (columns.size() < 3) {
                    logger::warn("Ignoring malformed runtime map line: {}", line);
                    continue;
                }

                auto trackId = NormalizeId(columns[0]);
                auto formId = ParseHex(columns[2]);
                if (trackId.empty() || columns[1].empty() || !formId) {
                    logger::warn("Ignoring invalid runtime map line: {}", line);
                    continue;
                }

                musicByTrack.insert_or_assign(std::move(trackId), MusicReference{
                    std::string(columns[1]), *formId, nullptr
                });

                // Optional v2 columns directly map an ambient MUST identity. The schema-v2
                // library provides the same mapping, so legacy three-column files remain valid.
                if (columns.size() >= 5 && !columns[3].empty()) {
                    if (const auto sourceId = ParseHex(columns[4])) {
                        catalogIdBySourceForm.insert_or_assign(
                            FormKey(columns[3], *sourceId), NormalizeId(columns[0]));
                    }
                }
            }
        }

        ReadCatalogMappings();
        IndexReferences();
        logger::info("Loaded {} runtime music references ({} resolved)",
            musicByTrack.size(), trackIdsByPreview.size());
    }

    bool IsReady()
    {
        return !trackIdsByPreview.empty();
    }

    PlaybackState GetPlaybackState()
    {
        PlaybackState state;
        state.transitioning = transitionPhase != TransitionPhase::kIdle || pendingPreview != nullptr;

        if (transitionPhase == TransitionPhase::kIdle) {
            PruneReleasedOwned();
        }
        for (const auto& [type, trackId] : ownedTrackByType) {
            static_cast<void>(type);
            state.ownedTrackIds.push_back(trackId);
        }

        auto* manager = RE::BSMusicManager::GetSingleton();
        auto* currentBase = manager ? manager->current : nullptr;
        auto* currentType = currentBase ? skyrim_cast<RE::BGSMusicType*>(currentBase) : nullptr;
        auto* selectedBase = GetSelectedTrack(currentBase);
        auto* selectedTrack = selectedBase ? skyrim_cast<RE::BGSMusicTrackFormWrapper*>(selectedBase) : nullptr;

        state.currentMusicType = IdentifyForm(currentType, true);
        state.currentMusicTrack = IdentifyForm(selectedTrack);

        if (state.currentMusicTrack) {
            const auto found = catalogIdBySourceForm.find(FormKey(
                state.currentMusicTrack->plugin, state.currentMusicTrack->localFormId));
            if (found != catalogIdBySourceForm.end()) {
                state.currentTrackId = found->second;
                state.activeTrackIds.push_back(found->second);
            }
        }
        if (!state.currentTrackId && currentType) {
            const auto found = trackIdsByPreview.find(currentType);
            if (found != trackIdsByPreview.end() && !found->second.empty()) {
                state.currentTrackId = found->second.front();
            }
        }
        if (currentType) {
            AddPreviewIds(state.activeTrackIds, currentType);
        }

        if (manager) {
            for (auto* queued : manager->musicQueue) {
                if (queued && IsPlayingOrPaused(queued)) {
                    AddPreviewIds(state.activeTrackIds, skyrim_cast<RE::BGSMusicType*>(queued));
                }
            }
        }
        for (auto* type : ownedTypes) {
            if (IsManagedBySkyrim(type) && IsPlayingOrPaused(type)) {
                AddPreviewIds(state.activeTrackIds, type);
            }
        }

        SortUnique(state.activeTrackIds);
        SortUnique(state.ownedTrackIds);
        return state;
    }

    std::string Play(std::string_view trackId)
    {
        auto* target = Resolve(trackId);
        if (!target) {
            return "That track has no loaded preview music record. Rebuild the output through MO2 and enable its plugin.";
        }

        BossMusic::SuppressForManualPlayback();
        const auto switching = BeginPlay(target, trackId);
        return switching ? "Switching tracks safely." : "Playback requested.";
    }

    std::string Toggle(std::string_view trackId)
    {
        auto* target = Resolve(trackId);
        if (!target) {
            return "That track has no loaded preview music record. Rebuild the output through MO2 and enable its plugin.";
        }

        const auto normalizedId = NormalizeId(trackId);
        const auto owned = ownedTrackByType.find(target);
        if ((owned != ownedTrackByType.end() && owned->second == normalizedId &&
                IsManagedBySkyrim(target) && IsPlayingOrPaused(target)) ||
            (verifyingPreview == target && verifyingTrackId == normalizedId)) {
            return Stop();
        }

        BossMusic::SuppressForManualPlayback();
        const auto switching = BeginPlay(target, normalizedId);
        return switching ? "Switching tracks safely." : "Playback requested.";
    }

    std::string Stop()
    {
        const bool stoppedBoss = BossMusic::SuppressForManualPlayback();
        if (transitionPhase == TransitionPhase::kWaitingForCleanup ||
            transitionPhase == TransitionPhase::kWaitingForForcedCleanup) {
            return "Stopping the Skypod preview and restoring Skyrim's music.";
        }
        if (transitionPhase == TransitionPhase::kWaitingForRemoval) {
            pendingPreview = nullptr;
            pendingTrackId.clear();
            cleanupSuccessMessage = "Skypod preview stopped; Skyrim music control is restored.";
            transitionPhase = TransitionPhase::kWaitingForForcedCleanup;
            return "Stopping the Skypod preview and restoring Skyrim's music.";
        }

        CancelTick();
        pendingPreview = nullptr;
        pendingTrackId.clear();
        verifyingPreview = nullptr;
        verifyingTrackId.clear();
        cleanupSuccessMessage.clear();

        if (ownedTypes.empty()) {
            transitionPhase = TransitionPhase::kIdle;
            return stoppedBoss ? "Boss music stopped for this encounter." : "No Skypod preview is playing.";
        }

        for (auto* type : ownedTypes) {
            Send(type, Message::kRemove);
        }
        if (!PruneReleasedOwned()) {
            transitionPhase = TransitionPhase::kIdle;
            return "Stopped the Skypod preview and restored Skyrim's music.";
        }

        cleanupSuccessMessage = "Skypod preview stopped; Skyrim music control is restored.";
        transitionPhase = TransitionPhase::kWaitingForCleanup;
        transitionDeadline = Clock::now() + 6000ms;
        ScheduleTick(50ms);
        return "Stopping the Skypod preview and restoring Skyrim's music.";
    }

    std::string AdvanceNaturalPlaylist()
    {
        auto* manager = RE::BSMusicManager::GetSingleton();
        auto* currentType = manager ? manager->current : nullptr;
        auto* currentForm = currentType ? skyrim_cast<RE::BGSMusicType*>(currentType) : nullptr;
        if (!currentType || !currentForm) {
            return "Skyrim is not playing a soundtrack playlist right now.";
        }
        if (ownedTypes.contains(currentForm)) {
            return "This is a Skypod preview. Use the catalog arrows to move between preview tracks.";
        }
        if (currentType->currentTrackIndex >= currentType->tracks.size()) {
            return "Skyrim's active playlist has not selected a track yet.";
        }

        auto* selectedTrack = currentType->tracks[currentType->currentTrackIndex];
        using TypeStatus = RE::BSIMusicType::MUSIC_STATUS;
        using TrackStatus = RE::BSIMusicTrack::MUSIC_STATUS;
        if (!selectedTrack || currentType->typeStatus.get() != TypeStatus::kPlaying ||
            selectedTrack->GetMusicStatus() != TrackStatus::kPlaying) {
            return "Wait for the current soundtrack transition to finish before skipping.";
        }

        using Flag = RE::BSIMusicType::MST;
        if (!currentType->flags.all(Flag::kCycleTracks) ||
            currentType->flags.any(
                Flag::kPlaysOnce,
                Flag::kRemovalQueued,
                Flag::kPlaysOver,
                Flag::kDoesntQueue)) {
            return "This music is an event or non-cycling context, so Skypod left it untouched.";
        }

        const auto hasQualifyingAlternative = std::ranges::any_of(
            currentType->tracks,
            [selectedTrack](const auto* track) {
                return track && track != selectedTrack && track->TestCanPlay();
            });
        if (!hasQualifyingAlternative) {
            return "No other track in the current playlist qualifies right now.";
        }

        const auto currentId = CatalogIdForTrack(selectedTrack).value_or("an uncataloged track");
        const auto trackType = selectedTrack->GetType();
        const auto typeForm = IdentifyForm(currentForm, true);
        const auto trackForm = IdentifyForm(skyrim_cast<RE::BGSMusicTrackFormWrapper*>(selectedTrack));

        // Finish only the active root. BGSMusicType keeps ownership of the
        // playlist and performs its normal TestCanPlay/history selection on the
        // following game tick, preserving time, place and combat conditions.
        selectedTrack->DoFinish(true, 0.0f);
        SkipConfirmationScheduler().Schedule(100ms, [
            currentType,
            selectedTrack,
            operation = std::string("Natural next from ") + currentId
        ]() mutable {
            ConfirmNaturalAdvance(currentType, selectedTrack, std::move(operation), 5);
        });

        logger::info(
            "Natural next finished Skyrim-owned {} {}|{:08X} inside MUST {}|{:08X} ({})",
            trackType == RE::BSIMusicTrack::TrackType::kPalette ? "palette" : "track",
            trackForm ? trackForm->plugin : "<unknown>",
            trackForm ? trackForm->localFormId : 0,
            typeForm ? typeForm->plugin : "<unknown>",
            typeForm ? typeForm->localFormId : 0,
            typeForm ? typeForm->editorId : "<unknown>");

        return "Skipping the current track. Skyrim is selecting another track that qualifies for this playlist now.";
    }

    ExclusionPlaybackAction ApplyExclusionToPlayback(std::string_view trackId)
    {
        const auto normalizedId = NormalizeId(trackId);
        if (normalizedId.empty()) {
            return ExclusionPlaybackAction::kNone;
        }

        // Skypod owns preview records that it adds to the music manager. Those
        // must use the existing remove-and-verify path so a preview cannot be
        // left queued behind Skyrim's natural soundtrack.
        if ((pendingPreview && pendingTrackId == normalizedId) ||
            (verifyingPreview && verifyingTrackId == normalizedId)) {
            logger::info("Exclusion cancelled pending or verifying Skypod preview {}", normalizedId);
            static_cast<void>(Stop());
            return ExclusionPlaybackAction::kStoppedOwnedPreview;
        }
        for (const auto& [type, ownedTrackId] : ownedTrackByType) {
            if (ownedTrackId == normalizedId && IsManagedBySkyrim(type) && IsPlayingOrPaused(type)) {
                logger::info("Exclusion stopped Skypod-owned preview {}", normalizedId);
                static_cast<void>(Stop());
                return ExclusionPlaybackAction::kStoppedOwnedPreview;
            }
        }

        auto* manager = RE::BSMusicManager::GetSingleton();
        auto* currentType = manager ? manager->current : nullptr;
        auto* currentForm = currentType ? skyrim_cast<RE::BGSMusicType*>(currentType) : nullptr;
        if (!currentType || !currentForm || ownedTypes.contains(currentForm)) {
            return ExclusionPlaybackAction::kNone;
        }

        // Only act on the exact top-level track selected by the current MUST.
        // The status scan used by diagnostics is intentionally too permissive
        // for a write path because an older finishing track may still be found.
        if (currentType->currentTrackIndex >= currentType->tracks.size()) {
            return ExclusionPlaybackAction::kNone;
        }
        auto* selectedTrack = currentType->tracks[currentType->currentTrackIndex];
        const auto currentCatalogId = CatalogIdForTrack(selectedTrack);
        if (!selectedTrack || !currentCatalogId || *currentCatalogId != normalizedId) {
            return ExclusionPlaybackAction::kNone;
        }

        using TypeStatus = RE::BSIMusicType::MUSIC_STATUS;
        using TrackStatus = RE::BSIMusicTrack::MUSIC_STATUS;
        if (currentType->typeStatus.get() != TypeStatus::kPlaying ||
            selectedTrack->GetMusicStatus() != TrackStatus::kPlaying) {
            return ExclusionPlaybackAction::kNaturalContextNotSafe;
        }

        using Flag = RE::BSIMusicType::MST;
        if (!currentType->flags.all(Flag::kCycleTracks) ||
            currentType->flags.any(
                Flag::kPlaysOnce,
                Flag::kRemovalQueued,
                Flag::kPlaysOver,
                Flag::kDoesntQueue)) {
            logger::info(
                "Exclusion left non-ambient music context {}|{:08X} untouched (flags {:08X})",
                currentForm->GetFile(0) ? currentForm->GetFile(0)->GetFilename() : "<unknown>",
                currentForm->GetLocalFormID(),
                currentType->flags.underlying());
            return ExclusionPlaybackAction::kNaturalContextNotSafe;
        }

        const auto playableTopLevelTracks = std::ranges::count_if(
            currentType->tracks, [](const auto* track) { return track != nullptr; });
        if (playableTopLevelTracks <= 1) {
            logger::info(
                "Exclusion did not advance ambient MUST {}|{:08X}; it has no alternative top-level track",
                currentForm->GetFile(0) ? currentForm->GetFile(0)->GetFilename() : "<unknown>",
                currentForm->GetLocalFormID());
            return ExclusionPlaybackAction::kNaturalPlaylistHasNoAlternative;
        }

        // Finish only the selected root track. Removing manager->current would
        // discard the entire ambient MUST and can expose or interrupt unrelated
        // music stacks. A palette root owns its currently playing layers, so its
        // virtual DoFinish finishes those layers together. BGSMusicType::DoUpdate
        // can then advance its own history normally on the following game tick.
        const auto trackType = selectedTrack->GetType();
        const auto typeForm = IdentifyForm(currentForm, true);
        const auto trackForm = IdentifyForm(skyrim_cast<RE::BGSMusicTrackFormWrapper*>(selectedTrack));
        // This calls BSIMusicTrack::DoFinish, not BSIMusicType::DoFinish. It
        // therefore does not enter Engine Fixes' MusicOverlap type-level hook.
        selectedTrack->DoFinish(true, 0.0f);
        SkipConfirmationScheduler().Schedule(100ms, [
            currentType,
            selectedTrack,
            operation = std::string("Excluded natural track ") + normalizedId
        ]() mutable {
            ConfirmNaturalAdvance(currentType, selectedTrack, std::move(operation), 5);
        });

        logger::info(
            "Exclusion advanced Skyrim-owned {} {}|{:08X} inside MUST {}|{:08X} ({})",
            trackType == RE::BSIMusicTrack::TrackType::kPalette ? "palette" : "track",
            trackForm ? trackForm->plugin : "<unknown>",
            trackForm ? trackForm->localFormId : 0,
            typeForm ? typeForm->plugin : "<unknown>",
            typeForm ? typeForm->localFormId : 0,
            typeForm ? typeForm->editorId : "<unknown>");

        return trackType == RE::BSIMusicTrack::TrackType::kPalette ?
            ExclusionPlaybackAction::kAdvancedSkyrimPalette :
            ExclusionPlaybackAction::kAdvancedSkyrimTrack;
    }

    bool HasManualMusic()
    {
        PruneReleasedOwned();
        return !ownedTypes.empty() || pendingPreview || verifyingPreview;
    }

    void EndBossMusic(bool immediate)
    {
        const auto now = Clock::now();
        for (auto it = bossTypes.begin(); it != bossTypes.end();) {
            auto* type = it->first;
            if (!IsManagedBySkyrim(type)) { it = bossTypes.erase(it); continue; }
            if (immediate || (it->second != Clock::time_point{} && now - it->second > 6s)) {
                Send(type, Message::kRemoveImmediate);
                it = bossTypes.erase(it);
                continue;
            }
            if (it->second == Clock::time_point{}) {
                Send(type, Message::kRemove);
                it->second = now;
            }
            ++it;
        }
    }

    void StartBossMusic(RE::BGSMusicType* music)
    {
        if (!music || HasManualMusic()) return;
        auto* manager = RE::BSMusicManager::GetSingleton();
        if (!manager) return;
        // One-shot quest/death cues keep control even if combat is still flagged.
        const auto protectedCue = [music](const RE::BSIMusicType* type) {
            return type && type != static_cast<RE::BSIMusicType*>(music) &&
                type->flags.any(RE::BSIMusicType::MST::kPlaysOnce) && type->priority <= 2 &&
                type->typeStatus != RE::BSIMusicType::MUSIC_STATUS::kFinished;
        };
        if (protectedCue(manager->current) || std::ranges::any_of(manager->musicQueue, protectedCue)) {
            EndBossMusic();
            return;
        }
        for (auto it = bossTypes.begin(); it != bossTypes.end();) {
            if (!IsManagedBySkyrim(it->first)) it = bossTypes.erase(it);
            else ++it;
        }
        if (const auto found = bossTypes.find(music); found != bossTypes.end() && found->second == Clock::time_point{}) return;
        if (!bossTypes.empty()) { EndBossMusic(); return; }
        if (Send(music, Message::kAdd)) bossTypes.emplace(music, Clock::time_point{});
    }

    void Reset()
    {
        EndBossMusic(true);
        CancelTick();
        SkipConfirmationScheduler().Cancel();
        pendingPreview = nullptr;
        pendingTrackId.clear();
        verifyingPreview = nullptr;
        verifyingTrackId.clear();
        cleanupSuccessMessage.clear();

        for (auto* type : ownedTypes) {
            Send(type, Message::kRemoveImmediate);
        }
        if (PruneReleasedOwned()) {
            cleanupSuccessMessage = "Skypod preview state reset.";
            transitionPhase = TransitionPhase::kWaitingForForcedCleanup;
            transitionDeadline = Clock::now() + 6000ms;
            ScheduleTick(50ms);
        } else {
            transitionPhase = TransitionPhase::kIdle;
        }
    }
}
