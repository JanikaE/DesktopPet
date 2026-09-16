using DesktopPet.Data;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace DesktopPet.Core;

public sealed class KeyboardStatisticsService : IDisposable
{
    private const int LowLevelKeyboardHook = 13;
    private const int KeyDown = 0x0100;
    private const int KeyUp = 0x0101;
    private const int SystemKeyDown = 0x0104;
    private const int SystemKeyUp = 0x0105;
    private const uint InjectedFlag = 0x10;
    private const uint ExtendedFlag = 0x01;
    private readonly DesktopPetRepository _repository;
    private readonly HookProcedure _hookProcedure;
    private readonly DispatcherTimer _flushTimer;
    private readonly HashSet<int> _pressedKeys = [];
    private readonly Dictionary<DateOnly, Dictionary<int, long>> _pending = [];
    private IntPtr _hook;

    public event EventHandler<int>? KeyPressed;

    public KeyboardStatisticsService(DesktopPetRepository repository)
    {
        _repository = repository;
        _hookProcedure = HookCallback;
        using var process = Process.GetCurrentProcess();
        using var module = process.MainModule!;
        _hook = SetWindowsHookEx(LowLevelKeyboardHook, _hookProcedure, GetModuleHandle(module.ModuleName), 0);
        if (_hook == IntPtr.Zero) throw new InvalidOperationException("无法启用全局键盘统计。");
        _flushTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _flushTimer.Tick += (_, _) => Flush();
        _flushTimer.Start();
    }

    public void Flush()
    {
        foreach (var (date, counts) in _pending.ToArray())
        {
            _repository.AddKeyboardStatistics(date, counts);
            _pending.Remove(date);
        }
    }

    public void Dispose()
    {
        _flushTimer.Stop();
        Flush();
        if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private IntPtr HookCallback(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0)
        {
            var info = Marshal.PtrToStructure<LowLevelKeyboardInput>(data);
            var keyCode = NormalizeKeyCode(info);
            var messageCode = message.ToInt32();
            if (messageCode is KeyUp or SystemKeyUp) _pressedKeys.Remove(keyCode);
            else if ((messageCode is KeyDown or SystemKeyDown) && (info.Flags & InjectedFlag) == 0 && _pressedKeys.Add(keyCode))
            {
                var date = DateOnly.FromDateTime(DateTime.Now);
                if (!_pending.TryGetValue(date, out var counts)) _pending[date] = counts = [];
                counts[keyCode] = counts.GetValueOrDefault(keyCode) + 1;
                KeyPressed?.Invoke(this, keyCode);
            }
        }
        return CallNextHookEx(_hook, code, message, data);
    }

    private static int NormalizeKeyCode(LowLevelKeyboardInput info)
    {
        var extended = (info.Flags & ExtendedFlag) != 0;
        if (info.VirtualKey == 13 && extended) return 0x1000D;
        if (!extended)
        {
            return info.ScanCode switch
            {
                0x47 => 103, 0x48 => 104, 0x49 => 105,
                0x4B => 100, 0x4C => 101, 0x4D => 102,
                0x4F => 97, 0x50 => 98, 0x51 => 99,
                0x52 => 96, 0x53 => 110,
                _ => (int)info.VirtualKey
            };
        }
        return (int)info.VirtualKey;
    }

    private delegate IntPtr HookProcedure(int code, IntPtr message, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct LowLevelKeyboardInput
    {
        public uint VirtualKey;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int hookId, HookProcedure procedure, IntPtr module, uint threadId);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)] private static extern IntPtr GetModuleHandle(string moduleName);
}
