#pragma once

#include <cstdint>
#include <filesystem>

namespace Settings
{
    struct Values
    {
        std::uint32_t toggleScancode{ 0x42 };  // F8
    };

    void Load();
    void RefreshHotkey();
    [[nodiscard]] Values Get();
    [[nodiscard]] std::filesystem::path GameRoot();
}
