using System.Runtime.InteropServices;

namespace Skypod.Core;

public static class ModOrganizerDetector
{
    public static bool IsRunningUnderUsvfs()
    {
        if (!OperatingSystem.IsWindows()) return false;
        return GetModuleHandle("usvfs_x64.dll") != IntPtr.Zero ||
               GetModuleHandle("usvfs_proxy_x64.exe") != IntPtr.Zero;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string moduleName);
}
