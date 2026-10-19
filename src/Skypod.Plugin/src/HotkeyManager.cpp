#include "PCH.h"
#include "HotkeyManager.h"
#include "PrismaBridge.h"
#include "Settings.h"

#include <chrono>

namespace HotkeyManager
{
    namespace
    {
        class InputSink final : public RE::BSTEventSink<RE::InputEvent*>
        {
        public:
            static InputSink* GetSingleton()
            {
                static InputSink sink;
                return std::addressof(sink);
            }

            RE::BSEventNotifyControl ProcessEvent(
                RE::InputEvent* const* events,
                RE::BSTEventSource<RE::InputEvent*>*) override
            {
                if (!events || !*events) {
                    return RE::BSEventNotifyControl::kContinue;
                }

                // MCM writes the global immediately, but resolving the form for every input
                // batch caused needless main-thread work. Refresh rarely; every comparison
                // below is against the atomic cache.
                const auto now = std::chrono::steady_clock::now();
                if (now >= nextSettingsRefresh) {
                    Settings::RefreshHotkey();
                    nextSettingsRefresh = now + std::chrono::seconds(2);
                }
                const auto settings = Settings::Get();
                for (auto* event = *events; event; event = event->next) {
                    if (event->GetEventType() != RE::INPUT_EVENT_TYPE::kButton) {
                        continue;
                    }

                    const auto button = event->AsButtonEvent();
                    if (!button || button->GetDevice() != RE::INPUT_DEVICE::kKeyboard || !button->IsDown()) {
                        continue;
                    }

                    if (button->GetIDCode() != settings.toggleScancode) {
                        continue;
                    }

                    if (!PrismaBridge::IsOpen()) {
                        if (const auto ui = RE::UI::GetSingleton(); ui &&
                            (ui->GameIsPaused() || ui->IsMenuOpen(RE::Console::MENU_NAME))) {
                            continue;
                        }
                    }

                    PrismaBridge::Toggle();
                }

                return RE::BSEventNotifyControl::kContinue;
            }

        private:
            std::chrono::steady_clock::time_point nextSettingsRefresh{};
        };
    }

    void Install()
    {
        if (const auto input = RE::BSInputDeviceManager::GetSingleton()) {
            input->AddEventSink(InputSink::GetSingleton());
            logger::info("Skypod input sink installed");
        } else {
            logger::error("BSInputDeviceManager was not available");
        }
    }

    void Uninstall()
    {
        if (const auto input = RE::BSInputDeviceManager::GetSingleton()) {
            input->RemoveEventSink(InputSink::GetSingleton());
        }
    }
}
