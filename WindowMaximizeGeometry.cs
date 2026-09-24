using System.Drawing;
using System.Runtime.InteropServices;

namespace z3nDash;

internal static class WindowMaximizeGeometry
{
    private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    internal static Rectangle GetMaximizedBounds(Rectangle monitorBounds, Rectangle workArea) =>
        new(
            workArea.Left - monitorBounds.Left,
            workArea.Top - monitorBounds.Top,
            workArea.Width,
            workArea.Height);

    internal static void ApplyWorkingArea(IntPtr windowHandle, IntPtr lParam)
    {
        var monitor = MonitorFromWindow(windowHandle, MONITOR_DEFAULTTONEAREST);
        var monitorInfo = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };

        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref monitorInfo))
            return;

        var maximizedBounds = GetMaximizedBounds(
            monitorInfo.rcMonitor.ToRectangle(),
            monitorInfo.rcWork.ToRectangle());
        var minMaxInfo = Marshal.PtrToStructure<MINMAXINFO>(lParam);

        minMaxInfo.ptMaxPosition.X = maximizedBounds.X;
        minMaxInfo.ptMaxPosition.Y = maximizedBounds.Y;
        minMaxInfo.ptMaxSize.X = maximizedBounds.Width;
        minMaxInfo.ptMaxSize.Y = maximizedBounds.Height;

        Marshal.StructureToPtr(minMaxInfo, lParam, false);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;

        public readonly Rectangle ToRectangle() =>
            Rectangle.FromLTRB(Left, Top, Right, Bottom);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }
}
