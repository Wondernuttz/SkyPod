/*
 * PrismaUI consumer API. This is an ABI-compatible V1 subset of the public
 * PrismaUI header. Skypod requests V1 so it works with PrismaUI 1.3 and 1.5.
 */
#pragma once

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif

#include <Windows.h>
#include <cstdint>

using PrismaView = std::uint64_t;

namespace PRISMA_UI_API
{
    enum class InterfaceVersion : std::uint8_t
    {
        V1,
        V2
    };

    using OnDomReadyCallback = void (*)(PrismaView view);
    using JSCallback = void (*)(const char* result);
    using JSListenerCallback = void (*)(const char* argument);

    class IVPrismaUI1
    {
    protected:
        ~IVPrismaUI1() = default;

    public:
        virtual PrismaView CreateView(const char* htmlPath, OnDomReadyCallback onDomReadyCallback = nullptr) noexcept = 0;
        virtual void Invoke(PrismaView view, const char* script, JSCallback callback = nullptr) noexcept = 0;
        virtual void InteropCall(PrismaView view, const char* functionName, const char* argument) noexcept = 0;
        virtual void RegisterJSListener(PrismaView view, const char* functionName, JSListenerCallback callback) noexcept = 0;
        virtual bool HasFocus(PrismaView view) noexcept = 0;
        virtual bool Focus(PrismaView view, bool pauseGame = false, bool disableFocusMenu = false) noexcept = 0;
        virtual void Unfocus(PrismaView view) noexcept = 0;
        virtual void Show(PrismaView view) noexcept = 0;
        virtual void Hide(PrismaView view) noexcept = 0;
        virtual bool IsHidden(PrismaView view) noexcept = 0;
        virtual int GetScrollingPixelSize(PrismaView view) noexcept = 0;
        virtual void SetScrollingPixelSize(PrismaView view, int pixelSize) noexcept = 0;
        virtual bool IsValid(PrismaView view) noexcept = 0;
        virtual void Destroy(PrismaView view) noexcept = 0;
        virtual void SetOrder(PrismaView view, int order) noexcept = 0;
        virtual int GetOrder(PrismaView view) noexcept = 0;
        virtual void CreateInspectorView(PrismaView view) noexcept = 0;
        virtual void SetInspectorVisibility(PrismaView view, bool visible) noexcept = 0;
        virtual bool IsInspectorVisible(PrismaView view) noexcept = 0;
        virtual void SetInspectorBounds(PrismaView view, float topLeftX, float topLeftY, unsigned int width, unsigned int height) noexcept = 0;
        virtual bool HasAnyActiveFocus() noexcept = 0;
    };

    using RequestPluginAPIFunc = void* (*)(InterfaceVersion interfaceVersion);

    [[nodiscard]] inline IVPrismaUI1* RequestV1()
    {
        const auto module = GetModuleHandleW(L"PrismaUI.dll");
        if (!module) {
            return nullptr;
        }

        const auto request = reinterpret_cast<RequestPluginAPIFunc>(GetProcAddress(module, "RequestPluginAPI"));
        return request ? static_cast<IVPrismaUI1*>(request(InterfaceVersion::V1)) : nullptr;
    }
}
