#include "PCH.h"
#include "Settings.h"

namespace Settings
{
    namespace
    {
        constexpr const char* kPluginName = "Skypod.esp";
        constexpr RE::FormID kToggleHotkeyGlobalId = 0x800;

        std::atomic<std::uint32_t> toggleScancode{ 0x42 };
        std::uint32_t lastLoggedGlobalHotkey = 0;
        bool loggedMissingGlobal = false;

        std::uint32_t ReadHotkeyFromGlobal(std::uint32_t fallback)
        {
            auto* dataHandler = RE::TESDataHandler::GetSingleton();
            if (!dataHandler) {
                return fallback;
            }

            auto* form = dataHandler->LookupForm(kToggleHotkeyGlobalId, kPluginName);
            auto* global = form ? form->As<RE::TESGlobal>() : nullptr;
            if (!global) {
                if (!loggedMissingGlobal) {
                    logger::warn("MCM hotkey global {}|{:X} is not loaded; using Skypod.ini fallback",
                        kPluginName, kToggleHotkeyGlobalId);
                    loggedMissingGlobal = true;
                }
                return fallback;
            }

            const auto scancode = static_cast<int>(global->value + 0.5F);
            if (scancode <= 0) {
                logger::warn("MCM hotkey global {}|{:X} has invalid value {}; keeping current hotkey",
                    kPluginName, kToggleHotkeyGlobalId, global->value);
                return fallback;
            }

            const auto newValue = static_cast<std::uint32_t>(scancode);
            if (toggleScancode.load(std::memory_order_relaxed) != newValue || lastLoggedGlobalHotkey != newValue) {
                logger::info("MCM hotkey {}|{:X} = {} (0x{:X})",
                    kPluginName, kToggleHotkeyGlobalId, newValue, newValue);
                lastLoggedGlobalHotkey = newValue;
            }
            loggedMissingGlobal = false;
            return newValue;
        }
    }

    std::filesystem::path GameRoot()
    {
        return std::filesystem::path(REL::Module::get().filePath()).parent_path();
    }

    void Load()
    {
        const auto path = GameRoot() / "Data" / "SKSE" / "Plugins" / "Skypod.ini";
        const auto scancode = GetPrivateProfileIntW(L"General", L"ToggleKey", 0x42, path.c_str());

        const auto fallback = scancode > 0 ? static_cast<std::uint32_t>(scancode) : 0x42;
        const auto loaded = ReadHotkeyFromGlobal(fallback);
        toggleScancode.store(loaded, std::memory_order_relaxed);
        logger::info("Settings loaded: toggle scancode {} (0x{:X})", loaded, loaded);
    }

    void RefreshHotkey()
    {
        const auto current = toggleScancode.load(std::memory_order_relaxed);
        toggleScancode.store(ReadHotkeyFromGlobal(current), std::memory_order_relaxed);
    }

    Values Get()
    {
        return Values{ toggleScancode.load(std::memory_order_relaxed) };
    }
}
