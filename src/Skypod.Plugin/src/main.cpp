#include "PCH.h"
#include "HotkeyManager.h"
#include "PrismaBridge.h"
#include "RuntimeMusic.h"
#include "BossMusic.h"
#include "Settings.h"

#include <spdlog/sinks/basic_file_sink.h>

namespace
{
    void OnSKSEMessage(SKSE::MessagingInterface::Message* message)
    {
        if (!message) {
            return;
        }

        switch (message->type) {
        case SKSE::MessagingInterface::kPostLoad:
            Settings::Load();
            PrismaBridge::Initialize();
            break;
        case SKSE::MessagingInterface::kDataLoaded:
            Settings::Load();
            RuntimeMusic::SetStatusCallback(&PrismaBridge::ReportRuntimeStatus);
            RuntimeMusic::ReloadMap();
            HotkeyManager::Install();
            BossMusic::Install();
            BossMusic::Reload();
            logger::info("Skypod ready. Use the configured hotkey to open the Prisma player.");
            break;
        case SKSE::MessagingInterface::kPreLoadGame:
            BossMusic::BeforeLoad();
            break;
        case SKSE::MessagingInterface::kNewGame:
        case SKSE::MessagingInterface::kPostLoadGame:
            Settings::Load();
            RuntimeMusic::ReloadMap();
            PrismaBridge::ReloadLibrary();
            BossMusic::Reload();
            break;
        default:
            break;
        }
    }
}

extern "C" __declspec(dllexport) bool SKSEPlugin_Load(const SKSE::LoadInterface* skse)
{
    SKSE::Init(skse, false);

    auto path = logger::log_directory();
    if (!path) {
        return false;
    }
    *path /= "Skypod.log";

    auto sink = std::make_shared<spdlog::sinks::basic_file_sink_mt>(path->string(), true);
    auto log = std::make_shared<spdlog::logger>("global", std::move(sink));
    log->set_level(spdlog::level::info);
    log->flush_on(spdlog::level::info);
    spdlog::set_default_logger(std::move(log));
    spdlog::set_pattern("[%H:%M:%S.%e] [%^%l%$] %v");

    logger::info("Skypod 1.0.0 loading (build {} {})", __DATE__, __TIME__);
    logger::info("Skyrim runtime {}", REL::Module::get().version().string());

    const auto messaging = SKSE::GetMessagingInterface();
    if (!messaging || !messaging->RegisterListener(OnSKSEMessage)) {
        logger::critical("Could not register the SKSE message listener");
        return false;
    }

    return true;
}
