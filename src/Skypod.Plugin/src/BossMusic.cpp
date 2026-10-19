#include "PCH.h"
#include "BossMusic.h"
#include "BossEncounterSession.h"
#include "RuntimeMusic.h"
#include "Settings.h"

#include <atomic>
#include <charconv>
#include <chrono>
#include <condition_variable>
#include <fstream>
#include <mutex>
#include <set>
#include <sstream>
#include <thread>
#include <unordered_map>
#include <unordered_set>

namespace BossMusic
{
    namespace
    {
        using Clock = std::chrono::steady_clock;
        struct Encounter {
            std::string id;
            std::string name;
            RE::BGSMusicType* music{ nullptr };
            std::unordered_set<RE::FormID> references;
            std::unordered_set<RE::FormID> bases;
        };
        std::vector<Encounter> encounters;
        std::unordered_map<RE::FormID, std::vector<std::size_t>> byReference;
        std::unordered_map<RE::FormID, std::vector<std::size_t>> byBase;
        BossEncounterSession session;
        std::unordered_set<RE::FormID> observedActors;
        std::unordered_set<RE::FormID> activeActors;
        bool loading{ true };
        std::atomic<bool> queued{ false };
        std::atomic<std::uint64_t> generation{ 0 };

        std::vector<RE::ActorHandle> Targets(RE::Actor* actor)
        {
            std::vector<RE::ActorHandle> result;
            auto* group = actor ? actor->GetCombatGroup() : nullptr;
            if (!group) return result;
            RE::BSReadLockGuard lock(group->lock);
            for (const auto& target : group->targets) result.push_back(target.targetHandle);
            return result;
        }

        bool OpposesPlayer(RE::Actor* actor, RE::PlayerCharacter* player,
            const std::unordered_set<RE::FormID>& playerTargets)
        {
            if (!actor || actor == player || actor->IsDead() || !actor->Get3D() ||
                actor->IsPlayerTeammate() || !actor->IsInCombat() || !actor->IsHostileToActor(player)) return false;
            if (actor->GetWorldspace() != player->GetWorldspace()) return false;
            if (!actor->GetWorldspace() && actor->GetParentCell() != player->GetParentCell()) return false;
            const auto delta = actor->GetPosition() - player->GetPosition();
            if (delta.SqrLength() > 10000.0f * 10000.0f) return false;
            if (playerTargets.contains(actor->GetFormID())) return true;
            for (const auto& handle : Targets(actor)) {
                const auto target = handle.get();
                if (target && (target.get() == player || target->IsPlayerTeammate())) return true;
            }
            return false;
        }

        void Tick()
        {
            if (loading || encounters.empty()) return;
            auto* player = RE::PlayerCharacter::GetSingleton();
            if (!player || !player->GetParentCell()) return;
            const auto now = Clock::now();
            if (player->IsDead()) {
                session.Clear();
                activeActors.clear();
                RuntimeMusic::EndBossMusic();
                return;
            }
            if (auto* ui = RE::UI::GetSingleton(); ui && ui->GameIsPaused()) {
                session.Pause(now);
                return;
            }
            std::unordered_set<RE::FormID> playerTargets;
            for (const auto& handle : Targets(player)) {
                if (const auto target = handle.get()) playerTargets.insert(target->GetFormID());
            }
            auto candidates = playerTargets;
            candidates.insert(observedActors.begin(), observedActors.end());
            for (const auto& [id, indices] : byReference) { static_cast<void>(indices); candidates.insert(id); }
            std::unordered_map<std::string, std::unordered_set<RE::FormID>> matches;
            for (const auto id : candidates) {
                auto* actor = RE::TESForm::LookupByID<RE::Actor>(id);
                if (!OpposesPlayer(actor, player, playerTargets)) continue;
                const auto* base = actor->GetActorBase();
                if (const auto refs = byReference.find(id); refs != byReference.end())
                    for (auto index : refs->second) matches[encounters[index].id].insert(id);
                if (base) {
                    if (const auto bases = byBase.find(base->GetFormID()); bases != byBase.end())
                        for (auto index : bases->second) matches[encounters[index].id].insert(id);
                }
            }
            const Encounter* selected = nullptr;
            // Stay with the current encounter when several bosses are present.
            for (const auto& encounter : encounters) {
                if (encounter.id == session.active && matches.contains(encounter.id)) { selected = &encounter; break; }
            }
            if (!selected) {
                for (const auto& encounter : encounters) {
                    if (matches.contains(encounter.id)) { selected = &encounter; break; }
                }
            }
            const bool previousDied = !activeActors.empty() && std::ranges::all_of(activeActors, [](auto id) {
                const auto* actor = RE::TESForm::LookupByID<RE::Actor>(id);
                return actor && actor->IsDead();
            });
            const auto previous = session.active;
            session.Observe(selected ? selected->id : "", !selected && previousDied, now);
            if (selected) activeActors = matches[selected->id];
            if (previous != session.active) {
                RuntimeMusic::EndBossMusic();
                if (session.active.empty()) {
                    activeActors.clear();
                    logger::info("Boss encounter ended; Skyrim music control restored");
                } else logger::info("Boss encounter started: {}", selected->name);
            }
            if (session.active.empty()) { RuntimeMusic::EndBossMusic(); return; }
            if (!selected) return; // Keep the current song through short phase/search gaps.
            if (RuntimeMusic::HasManualMusic()) session.suppressed = true;
            if (!session.suppressed) RuntimeMusic::StartBossMusic(selected->music);
            else RuntimeMusic::EndBossMusic();
        }

        void QueueTick()
        {
            if (queued.exchange(true)) return;
            const auto revision = generation.load();
            if (auto* tasks = SKSE::GetTaskInterface()) {
                tasks->AddTask([revision] {
                    queued.store(false);
                    if (generation.load() == revision) Tick();
                });
            } else queued.store(false);
        }

        class Worker
        {
            std::mutex mutex;
            std::condition_variable_any wake;
            std::jthread thread;
        public:
            Worker() : thread([this](std::stop_token stop) {
                std::unique_lock lock(mutex);
                while (!stop.stop_requested()) {
                    wake.wait_for(lock, stop, std::chrono::milliseconds(500), [] { return false; });
                    if (!stop.stop_requested()) QueueTick();
                }
            }) {}
            ~Worker() { thread.request_stop(); wake.notify_all(); thread.join(); }
        };

        class CombatSink final : public RE::BSTEventSink<RE::TESCombatEvent>, public RE::BSTEventSink<RE::TESDeathEvent>
        {
        public:
            RE::BSEventNotifyControl ProcessEvent(const RE::TESCombatEvent* event, RE::BSTEventSource<RE::TESCombatEvent>*) override
            {
                if (event && event->actor) {
                    const auto id = event->actor->GetFormID();
                    const auto target = event->targetActor ? event->targetActor->GetFormID() : 0;
                    const auto revision = generation.load();
                    if (auto* tasks = SKSE::GetTaskInterface()) tasks->AddTask([id, target, revision] {
                        if (generation.load() != revision || loading || encounters.empty()) return;
                        if (observedActors.size() >= 512) observedActors.clear();
                        observedActors.insert(id);
                        if (target) observedActors.insert(target);
                        Tick();
                    });
                }
                return RE::BSEventNotifyControl::kContinue;
            }
            RE::BSEventNotifyControl ProcessEvent(const RE::TESDeathEvent*, RE::BSTEventSource<RE::TESDeathEvent>*) override
            {
                QueueTick();
                return RE::BSEventNotifyControl::kContinue;
            }
        };

        RE::TESForm* Resolve(const std::string& plugin, const std::string& id)
        {
            std::uint32_t local{ 0 };
            const auto parsed = std::from_chars(id.data(), id.data() + id.size(), local, 16);
            if (parsed.ec != std::errc{} || parsed.ptr != id.data() + id.size() || local > 0xFFFFFF) return nullptr;
            auto* data = RE::TESDataHandler::GetSingleton();
            return data ? data->LookupForm(local, plugin) : nullptr;
        }
    }

    void Install()
    {
        static CombatSink sink;
        static bool installed{ false };
        if (installed) return;
        if (auto* events = RE::ScriptEventSourceHolder::GetSingleton()) {
            events->AddEventSink<RE::TESCombatEvent>(&sink);
            events->AddEventSink<RE::TESDeathEvent>(&sink);
            installed = true;
            static Worker worker;
        }
    }

    void BeforeLoad()
    {
        generation.fetch_add(1);
        loading = true;
        RuntimeMusic::EndBossMusic(true);
        session.Clear();
        encounters.clear();
        byReference.clear();
        byBase.clear();
        observedActors.clear();
        activeActors.clear();
    }

    void Reload()
    {
        generation.fetch_add(1);
        session.Clear();
        encounters.clear();
        byReference.clear();
        byBase.clear();
        observedActors.clear();
        activeActors.clear();
        std::ifstream file(Settings::GameRoot() / "Data" / "SKSE" / "Plugins" / "Skypod.bosses.tsv");
        std::string line;
        std::size_t rejected{ 0 };
        while (std::getline(file, line)) {
            if (!line.empty() && line.back() == '\r') line.pop_back();
            if (line.empty() || line.front() == '#') continue;
            std::vector<std::string> columns;
            std::istringstream input(line);
            std::string value;
            while (std::getline(input, value, '\t')) columns.push_back(value);
            if (columns.size() != 7 || encounters.size() > 4096) { ++rejected; continue; }
            auto* musicForm = Resolve(columns[1], columns[2]);
            auto* target = Resolve(columns[4], columns[5]);
            auto* music = musicForm ? musicForm->As<RE::BGSMusicType>() : nullptr;
            if (!music || !target || (columns[3] != "ref" && columns[3] != "base") ||
                (columns[3] == "ref" && !target->As<RE::Actor>()) ||
                (columns[3] == "base" && !target->As<RE::TESNPC>())) { ++rejected; continue; }
            auto found = std::ranges::find_if(encounters, [&](const auto& encounter) { return encounter.id == columns[0]; });
            if (found == encounters.end()) {
                encounters.push_back({ columns[0], columns[6], music, {}, {} });
                found = std::prev(encounters.end());
            }
            if (found->music != music) { ++rejected; continue; }
            (columns[3] == "ref" ? found->references : found->bases).insert(target->GetFormID());
        }
        for (std::size_t index = 0; index < encounters.size(); ++index) {
            for (auto id : encounters[index].references) byReference[id].push_back(index);
            for (auto id : encounters[index].bases) byBase[id].push_back(index);
        }
        loading = false;
        logger::info("Loaded {} assigned boss encounters; {} invalid or unavailable rows skipped", encounters.size(), rejected);
        QueueTick();
    }

    bool SuppressForManualPlayback()
    {
        if (session.active.empty()) return false;
        session.suppressed = true;
        RuntimeMusic::EndBossMusic();
        return true;
    }
}
