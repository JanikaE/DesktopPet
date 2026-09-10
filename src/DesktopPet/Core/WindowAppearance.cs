using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace DesktopPet.Core;

public static class WindowAppearance
{
    private const int CornerPreferenceAttribute = 33;
    private const int RoundCornerPreference = 2;

    public static void EnableRoundedCorners(Window window)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return;
        var handle = new WindowInteropHelper(window).Handle;
        var preference = RoundCornerPreference;
        DwmSetWindowAttribute(handle, CornerPreferenceAttribute, ref preference, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr windowHandle, int attribute, ref int value, int valueSize);
}
