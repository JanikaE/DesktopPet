using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace DesktopPet.Core;

[Flags]
public enum HotkeyModifiers : uint
{
    None = 0,
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Windows = 0x0008
}

public sealed record HotkeyGesture(HotkeyModifiers Modifiers, int VirtualKey)
{
    public string DisplayText
    {
        get
        {
            var parts = new List<string>();
            if (Modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
            if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
            if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
            if (Modifiers.HasFlag(HotkeyModifiers.Windows)) parts.Add("Win");
            parts.Add(System.Windows.Input.KeyInterop.KeyFromVirtualKey(VirtualKey).ToString());
            return string.Join(" + ", parts);
        }
    }
}

public sealed class GlobalHotkey : IDisposable
{
    private const int HotkeyId = 0x4450;
    private const int WindowMessageHotkey = 0x0312;
    private const uint NoRepeat = 0x4000;
    private readonly IntPtr _windowHandle;
    private readonly HwndSource _source;
    private readonly Action _pressed;
    private bool _registered;
    private HotkeyGesture? _current;

    public GlobalHotkey(IntPtr windowHandle, Action pressed)
    {
        _windowHandle = windowHandle;
        _pressed = pressed;
        _source = HwndSource.FromHwnd(windowHandle) ?? throw new InvalidOperationException("无法获取桌宠窗口句柄。");
        _source.AddHook(WindowMessageHook);
    }

    public bool TrySet(HotkeyGesture? gesture)
    {
        var previous = _current;
        if (_registered)
        {
            UnregisterHotKey(_windowHandle, HotkeyId);
            _registered = false;
        }

        if (gesture is null)
        {
            _current = null;
            return true;
        }
        _registered = RegisterHotKey(_windowHandle, HotkeyId, (uint)gesture.Modifiers | NoRepeat, (uint)gesture.VirtualKey);
        if (_registered)
        {
            _current = gesture;
            return true;
        }

        if (previous is not null)
            _registered = RegisterHotKey(_windowHandle, HotkeyId, (uint)previous.Modifiers | NoRepeat, (uint)previous.VirtualKey);
        _current = _registered ? previous : null;
        return false;
    }

    public void Dispose()
    {
        if (_registered) UnregisterHotKey(_windowHandle, HotkeyId);
        _source.RemoveHook(WindowMessageHook);
    }

    private IntPtr WindowMessageHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WindowMessageHotkey && wParam.ToInt32() == HotkeyId)
        {
            _pressed();
            handled = true;
        }
        return IntPtr.Zero;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr windowHandle, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr windowHandle, int id);
}
