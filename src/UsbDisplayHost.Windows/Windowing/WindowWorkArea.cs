using System.ComponentModel;
using System.Runtime.InteropServices;

namespace UsbDisplayHost.Windows.Windowing;

public readonly record struct WindowWorkArea(int Left, int Top, int Width, int Height,
    uint Dpi, int NonClientWidth, int NonClientHeight)
{
    public double WidthDip => Width * 96d / Dpi;
    public double HeightDip => Height * 96d / Dpi;
    public double MinimumWidthDip => Math.Min(WidthDip, 640 + NonClientWidth * 96d / Dpi);
    public double MinimumHeightDip => Math.Min(HeightDip, 400 + NonClientHeight * 96d / Dpi);
}

public static class WindowWorkAreaAdapter
{
    public static WindowWorkArea Read(nint window)
    {
        ArgumentOutOfRangeException.ThrowIfZero(window);
        var monitor = MonitorFromWindow(window, 2);
        if (monitor == 0) { throw new InvalidOperationException("Window monitor is unavailable."); }
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) { throw new Win32Exception(Marshal.GetLastPInvokeError()); }
        if (!GetWindowRect(window, out var bounds) || !GetClientRect(window, out var client))
        { throw new Win32Exception(Marshal.GetLastPInvokeError()); }
        uint dpi = GetDpiForWindow(window);
        int width = checked(info.Work.Right - info.Work.Left);
        int height = checked(info.Work.Bottom - info.Work.Top);
        if (dpi == 0 || width <= 0 || height <= 0)
        { throw new InvalidOperationException("Window work area or DPI is invalid."); }
        return new(info.Work.Left, info.Work.Top, width, height, dpi,
            Math.Max(0, checked(bounds.Right - bounds.Left - (client.Right - client.Left))),
            Math.Max(0, checked(bounds.Bottom - bounds.Top - (client.Bottom - client.Top))));
    }

    public static void Constrain(nint window, WindowWorkArea area)
    {
        ArgumentOutOfRangeException.ThrowIfZero(window);
        if (area.Width <= 0 || area.Height <= 0)
        { throw new ArgumentOutOfRangeException(nameof(area)); }
        if (!GetWindowRect(window, out var bounds))
        { throw new Win32Exception(Marshal.GetLastPInvokeError()); }
        int width = Math.Clamp(checked(bounds.Right - bounds.Left), 1, area.Width);
        int height = Math.Clamp(checked(bounds.Bottom - bounds.Top), 1, area.Height);
        int left = Math.Clamp(bounds.Left, area.Left, checked(area.Left + area.Width - width));
        int top = Math.Clamp(bounds.Top, area.Top, checked(area.Top + area.Height - height));
        if (left == bounds.Left && top == bounds.Top
            && width == bounds.Right - bounds.Left && height == bounds.Bottom - bounds.Top) { return; }
        // 不改变激活窗口和 Z 序，仅收敛窗口在当前显示器工作区内。
        if (!SetWindowPos(window, 0, left, top, width, height, 0x0014))
        { throw new Win32Exception(Marshal.GetLastPInvokeError()); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public uint Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out NativeRect bounds);
    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(nint window, out NativeRect bounds);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
}
