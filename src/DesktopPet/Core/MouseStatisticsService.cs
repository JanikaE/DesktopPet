using DesktopPet.Data;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace DesktopPet.Core;

public sealed class MouseStatisticsService : IDisposable
{
    private const int LowLevelMouseHook = 14;
    private const int MouseMove = 0x0200;
    private const int LeftButtonDown = 0x0201;
    private const int RightButtonDown = 0x0204;
    private const int MiddleButtonDown = 0x0207;
    private const int MouseWheel = 0x020A;
    private const uint InjectedFlag = 0x01;
    private const int DoubleClickWidthMetric = 36;
    private const int DoubleClickHeightMetric = 37;

    private static readonly TimeSpan MoveSampleInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan LiveSampleInterval = TimeSpan.FromMilliseconds(30);

    private readonly DesktopPetRepository _repository;
    private readonly HookProcedure _hookProcedure;
    private readonly DispatcherTimer _flushTimer;
    private readonly Dictionary<DateOnly, Dictionary<(int X, int Y), long>> _pendingMoves = [];
    private readonly Dictionary<DateOnly, Dictionary<(int X, int Y, int Button), long>> _pendingClicks = [];
    private readonly Dictionary<DateOnly, DailyAccumulator> _pendingDaily = [];
    private readonly int _doubleClickTime = GetDoubleClickTime();
    private readonly int _doubleClickWidth = GetSystemMetrics(DoubleClickWidthMetric);
    private readonly int _doubleClickHeight = GetSystemMetrics(DoubleClickHeightMetric);
    private IntPtr _hook;
    private int _lastX;
    private int _lastY;
    private bool _hasLastPoint;
    private DateTime _lastMoveSample;
    private DateTime _lastLiveSample;
    private DateTime _lastClickTime = DateTime.MinValue;
    private int _lastClickX;
    private int _lastClickY;
    private MouseButtonKind _lastClickButton;

    public event EventHandler<MousePointEventArgs>? MouseMoved;
    public event EventHandler<MouseClickEventArgs>? MouseClicked;

    public MouseStatisticsService(DesktopPetRepository repository)
    {
        _repository = repository;
        _hookProcedure = HookCallback;
        using var process = Process.GetCurrentProcess();
        using var module = process.MainModule!;
        _hook = SetWindowsHookEx(LowLevelMouseHook, _hookProcedure, GetModuleHandle(module.ModuleName), 0);
        if (_hook == IntPtr.Zero) throw new InvalidOperationException("无法启用全局鼠标统计。");
        _flushTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _flushTimer.Tick += (_, _) => Flush();
        _flushTimer.Start();
    }

    public void Flush()
    {
        var dates = new HashSet<DateOnly>(_pendingMoves.Keys);
        dates.UnionWith(_pendingClicks.Keys);
        dates.UnionWith(_pendingDaily.Keys);
        foreach (var date in dates)
        {
            _pendingMoves.Remove(date, out var moves);
            _pendingClicks.Remove(date, out var clicks);
            _pendingDaily.Remove(date, out var daily);
            _repository.AddMouseStatistics(
                date,
                moves ?? EmptyMoves,
                clicks ?? EmptyClicks,
                daily?.ToTotals() ?? MouseDailyTotals.Empty);
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
            try
            {
                var info = Marshal.PtrToStructure<LowLevelMouseInput>(data);
                if ((info.Flags & InjectedFlag) == 0)
                {
                    switch (message.ToInt32())
                    {
                        case MouseMove: HandleMove(info); break;
                        case LeftButtonDown: HandleClick(info, MouseButtonKind.Left); break;
                        case RightButtonDown: HandleClick(info, MouseButtonKind.Right); break;
                        case MiddleButtonDown: HandleClick(info, MouseButtonKind.Middle); break;
                        case MouseWheel: HandleWheel(info); break;
                    }
                }
            }
            catch (Exception)
            {
            }
        }
        return CallNextHookEx(_hook, code, message, data);
    }

    private void HandleMove(LowLevelMouseInput info)
    {
        var now = DateTime.UtcNow;
        if (_hasLastPoint)
        {
            var deltaX = (double)info.X - _lastX;
            var deltaY = (double)info.Y - _lastY;
            if (deltaX != 0 || deltaY != 0) GetDaily(DateOnly.FromDateTime(DateTime.Now)).MoveDistance += Math.Sqrt(deltaX * deltaX + deltaY * deltaY);
        }
        _hasLastPoint = true;
        _lastX = info.X;
        _lastY = info.Y;

        if (now - _lastMoveSample >= MoveSampleInterval)
        {
            _lastMoveSample = now;
            AccumulateMove(info.X, info.Y);
        }

        if (now - _lastLiveSample >= LiveSampleInterval)
        {
            _lastLiveSample = now;
            MouseMoved?.Invoke(this, new MousePointEventArgs(info.X, info.Y));
        }
    }

    private void HandleClick(LowLevelMouseInput info, MouseButtonKind button)
    {
        AccumulateClick(info.X, info.Y, button, 1);
        var daily = GetDaily(DateOnly.FromDateTime(DateTime.Now));
        var isDoubleClick = IsDoubleClick(info, button);
        switch (button)
        {
            case MouseButtonKind.Left:
                daily.LeftClicks++;
                if (isDoubleClick) daily.DoubleClicks++;
                break;
            case MouseButtonKind.Right: daily.RightClicks++; break;
            case MouseButtonKind.Middle: daily.MiddleClicks++; break;
        }
        MouseClicked?.Invoke(this, new MouseClickEventArgs(info.X, info.Y, button, isDoubleClick));
    }

    private void HandleWheel(LowLevelMouseInput info)
    {
        var notches = (short)((info.MouseData >> 16) & 0xFFFF);
        if (notches == 0) return;
        var units = Math.Max(1, Math.Abs(notches) / 120);
        AccumulateClick(info.X, info.Y, MouseButtonKind.Wheel, units);
        GetDaily(DateOnly.FromDateTime(DateTime.Now)).WheelUnits += units;
        MouseClicked?.Invoke(this, new MouseClickEventArgs(info.X, info.Y, MouseButtonKind.Wheel, false));
    }

    private bool IsDoubleClick(LowLevelMouseInput info, MouseButtonKind button)
    {
        var now = DateTime.UtcNow;
        var isDoubleClick = button == _lastClickButton
            && _doubleClickTime > 0
            && (now - _lastClickTime).TotalMilliseconds <= _doubleClickTime
            && Math.Abs(info.X - _lastClickX) <= _doubleClickWidth
            && Math.Abs(info.Y - _lastClickY) <= _doubleClickHeight;
        _lastClickTime = now;
        _lastClickX = info.X;
        _lastClickY = info.Y;
        _lastClickButton = button;
        return isDoubleClick;
    }

    private void AccumulateMove(int x, int y)
    {
        var date = DateOnly.FromDateTime(DateTime.Now);
        if (!_pendingMoves.TryGetValue(date, out var cells)) _pendingMoves[date] = cells = [];
        var cell = ToCell(x, y);
        cells[cell] = cells.GetValueOrDefault(cell) + 1;
    }

    private void AccumulateClick(int x, int y, MouseButtonKind button, long count)
    {
        var date = DateOnly.FromDateTime(DateTime.Now);
        if (!_pendingClicks.TryGetValue(date, out var cells)) _pendingClicks[date] = cells = [];
        var cell = ToCell(x, y);
        var key = (cell.X, cell.Y, (int)button);
        cells[key] = cells.GetValueOrDefault(key) + count;
    }

    private DailyAccumulator GetDaily(DateOnly date)
    {
        if (!_pendingDaily.TryGetValue(date, out var accumulator)) _pendingDaily[date] = accumulator = new DailyAccumulator();
        return accumulator;
    }

    private static (int X, int Y) ToCell(int x, int y) => MouseGridGeometry.ToCell(x, y);

    private static readonly IReadOnlyDictionary<(int X, int Y), long> EmptyMoves = new Dictionary<(int X, int Y), long>();
    private static readonly IReadOnlyDictionary<(int X, int Y, int Button), long> EmptyClicks = new Dictionary<(int X, int Y, int Button), long>();

    private sealed class DailyAccumulator
    {
        public double MoveDistance;
        public long LeftClicks;
        public long RightClicks;
        public long MiddleClicks;
        public long WheelUnits;
        public long DoubleClicks;

        public MouseDailyTotals ToTotals() => new(MoveDistance, LeftClicks, RightClicks, MiddleClicks, WheelUnits, DoubleClicks);
    }

    private delegate IntPtr HookProcedure(int code, IntPtr message, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct LowLevelMouseInput
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int hookId, HookProcedure procedure, IntPtr module, uint threadId);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll")] private static extern int GetDoubleClickTime();
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)] private static extern IntPtr GetModuleHandle(string moduleName);
}

public enum MouseButtonKind
{
    Left = 0,
    Right = 1,
    Middle = 2,
    Wheel = 3
}

public sealed class MousePointEventArgs(int x, int y) : EventArgs
{
    public int X { get; } = x;
    public int Y { get; } = y;
}

public sealed class MouseClickEventArgs(int x, int y, MouseButtonKind button, bool isDoubleClick) : EventArgs
{
    public int X { get; } = x;
    public int Y { get; } = y;
    public MouseButtonKind Button { get; } = button;
    public bool IsDoubleClick { get; } = isDoubleClick;
}
