using System.Runtime.InteropServices;

namespace DesktopPet.Core;

public static class DpiAwareness
{
    private static readonly IntPtr PerMonitorAwareV2 = new(-4);

    public static void EnablePerMonitorV2()
    {
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 15063))
            SetProcessDpiAwarenessContext(PerMonitorAwareV2);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);
}
