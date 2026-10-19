#pragma once

namespace PrismaBridge
{
    bool Initialize();
    void Open();
    void Close();
    void Toggle();
    [[nodiscard]] bool IsOpen();
    void ReloadLibrary();
    void ReportRuntimeStatus(std::string_view message);
    void Shutdown();
}
