using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Glance.Gif;

namespace Glance;

/// <summary>Win32 helpers for overlay windows that must sit at exact device-pixel positions.</summary>
internal static class NativeWindows
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x20;
    private const int WS_EX_TOOLWINDOW = 0x80;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;
    private const uint SWP_NOACTIVATE = 0x10;
    private const uint SWP_NOSIZE = 0x1;
    private static readonly IntPtr HWND_TOPMOST = new(-1);

    public static IntPtr Handle(Window window) => new WindowInteropHelper(window).EnsureHandle();

    /// <summary>Keeps the window out of screenshots and recordings (Windows 10 2004+; silently ignored before).</summary>
    public static void ExcludeFromCapture(Window window) =>
        SetWindowDisplayAffinity(Handle(window), WDA_EXCLUDEFROMCAPTURE);

    /// <summary>No rounded corners: a full-screen overlay must cover the screen edge to edge (Windows 11; ignored before).</summary>
    public static void SquareCorners(Window window)
    {
        var doNotRound = 1; // DWMWCP_DONOTROUND
        DwmSetWindowAttribute(Handle(window), 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref doNotRound, sizeof(int));
    }

    /// <summary>The process name of the window in front (for the log), or "?".</summary>
    public static string ForegroundProcessName()
    {
        try
        {
            GetWindowThreadProcessId(GetForegroundWindow(), out var pid);
            using var process = System.Diagnostics.Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return "?";
        }
    }

    /// <summary>
    /// Brings the window to the front with keyboard focus, even when Windows' focus rules would refuse (a key seen
    /// by the keyboard hook, not a hotkey, doesn't give Glance permission). Briefly joins the input of the window in front.
    /// Returns whether it ended up in front.
    /// </summary>
    public static bool ForceForeground(Window window)
    {
        var hwnd = Handle(window);
        if (GetForegroundWindow() == hwnd)
        {
            return true;
        }

        var front = GetForegroundWindow();
        var frontThread = GetWindowThreadProcessId(front, out _);
        var ourThread = GetCurrentThreadId();
        var attached = frontThread != ourThread && AttachThreadInput(ourThread, frontThread, true);
        try
        {
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached)
            {
                AttachThreadInput(ourThread, frontThread, false);
            }
        }

        return GetForegroundWindow() == hwnd;
    }

    /// <summary>Mouse clicks pass through to whatever is underneath.</summary>
    public static void MakeClickThrough(Window window)
    {
        var hwnd = Handle(window);
        var style = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, style | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
    }

    public static void SetBounds(Window window, PixelRect bounds) =>
        SetWindowPos(Handle(window), HWND_TOPMOST, bounds.X, bounds.Y, bounds.Width, bounds.Height, SWP_NOACTIVATE);

    public static void SetPosition(Window window, int x, int y) =>
        SetWindowPos(Handle(window), HWND_TOPMOST, x, y, 0, 0, SWP_NOACTIVATE | SWP_NOSIZE);

    public static (double X, double Y) DpiScale(Visual visual)
    {
        var dpi = VisualTreeHelper.GetDpi(visual);
        return (dpi.DpiScaleX, dpi.DpiScaleY);
    }

    /// <summary>The work area (screen minus taskbar) of the monitor that contains most of <paramref name="rect"/>.</summary>
    public static PixelRect WorkAreaFor(PixelRect rect)
    {
        var r = new RECT { Left = rect.X, Top = rect.Y, Right = rect.Right, Bottom = rect.Bottom };
        var monitor = MonitorFromRect(ref r, 2 /* MONITOR_DEFAULTTONEAREST */);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info))
        {
            var w = info.rcWork;
            return new PixelRect(w.Left, w.Top, w.Right - w.Left, w.Bottom - w.Top);
        }

        return Capture.ScreenCapture.VirtualScreen;
    }

    public static IEnumerable<PixelRect> Monitors()
    {
        var result = new List<PixelRect>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr monitor, IntPtr hdc, ref RECT bounds, IntPtr data) =>
        {
            result.Add(new PixelRect(bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top));
            return true;
        }, IntPtr.Zero);
        return result;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, ref RECT bounds, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint attach, uint attachTo, bool join);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hwnd);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromRect(ref RECT rect, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);
}
