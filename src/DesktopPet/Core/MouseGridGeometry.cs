using System.Runtime.InteropServices;

namespace DesktopPet.Core;

public static class MouseGridGeometry
{
    public const int Columns = 32;
    public const int Rows = 18;

    private const uint MonitorDefaultToNearest = 0x00000002;
    private const int SmCxScreen = 0;
    private const int SmCyScreen = 1;

    private static readonly object Sync = new();
    private static IntPtr _cachedMonitor;
    private static NativeRect _cachedBounds;

    public static (int X, int Y) ToCell(int x, int y)
    {
        var bounds = GetMonitorBounds(x, y);
        var column = (int)Math.Floor((x - (double)bounds.Left) / bounds.Width * Columns);
        var row = (int)Math.Floor((y - (double)bounds.Top) / bounds.Height * Rows);
        return (Math.Clamp(column, 0, Columns - 1), Math.Clamp(row, 0, Rows - 1));
    }

    public static (int Left, int Top, int Width, int Height) GetMonitorBounds(int x, int y)
    {
        lock (Sync)
        {
            if (_cachedMonitor != IntPtr.Zero && Contains(_cachedBounds, x, y))
                return (_cachedBounds.Left, _cachedBounds.Top, _cachedBounds.Width, _cachedBounds.Height);

            var monitor = MonitorFromPoint(new NativePoint(x, y), MonitorDefaultToNearest);
            if (monitor == IntPtr.Zero)
                return (_cachedBounds.Left, _cachedBounds.Top, _cachedBounds.Width, _cachedBounds.Height);

            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(monitor, ref info))
                return (_cachedBounds.Left, _cachedBounds.Top, _cachedBounds.Width, _cachedBounds.Height);

            _cachedMonitor = monitor;
            _cachedBounds = info.Monitor;
            return (_cachedBounds.Left, _cachedBounds.Top, _cachedBounds.Width, _cachedBounds.Height);
        }
    }

    public static (int Width, int Height) GetPrimaryScreenSize() =>
        (Math.Max(1, GetSystemMetrics(SmCxScreen)), Math.Max(1, GetSystemMetrics(SmCyScreen)));

    private static bool Contains(NativeRect bounds, int x, int y) =>
        bounds.Width > 0 && bounds.Height > 0 && x >= bounds.Left && y >= bounds.Top && x < bounds.Right && y < bounds.Bottom;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint(int x, int y)
    {
        public int X = x;
        public int Y = y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect WorkArea;
        public uint Flags;
    }

    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
}
