using System.Drawing;
using System.Runtime.InteropServices;

namespace BybitTaskbar.Core;

internal enum TaskbarEdge
{
    Bottom,
    Top,
    Left,
    Right,
    Unknown
}

internal static class TaskbarGeometry
{
    public static Rectangle GetVirtualScreen() => new(
        Interop.GetSystemMetrics(Interop.SM_XVIRTUALSCREEN),
        Interop.GetSystemMetrics(Interop.SM_YVIRTUALSCREEN),
        Math.Max(1, Interop.GetSystemMetrics(Interop.SM_CXVIRTUALSCREEN)),
        Math.Max(1, Interop.GetSystemMetrics(Interop.SM_CYVIRTUALSCREEN)));

    /// <summary>Прямоугольник панели задач (Shell_TrayWnd) в физических пикселях.</summary>
    public static Rectangle? GetTaskbarRect()
    {
        var hwnd = Interop.FindWindow("Shell_TrayWnd", null);
        if (hwnd == IntPtr.Zero) return null;
        if (!Interop.GetWindowRect(hwnd, out var r) || r.Width <= 0 || r.Height <= 0) return null;
        return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
    }

    /// <summary>Прямоугольник области уведомлений (часы + трей) — чтобы встать рядом с ней.</summary>
    public static Rectangle? GetTrayRect()
    {
        var taskbar = Interop.FindWindow("Shell_TrayWnd", null);
        if (taskbar == IntPtr.Zero) return null;
        var tray = Interop.FindWindowEx(taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
        if (tray == IntPtr.Zero) tray = Interop.FindWindowEx(taskbar, IntPtr.Zero, "TrayDummySearchControl", null);
        if (tray == IntPtr.Zero) return null;
        if (!Interop.GetWindowRect(tray, out var r) || r.Width <= 0 || r.Height <= 0) return null;
        return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
    }

    public static TaskbarEdge GetEdge()
    {
        var tb = GetTaskbarRect();
        if (tb is null) return TaskbarEdge.Unknown;
        var screen = GetVirtualScreen();
        var r = tb.Value;

        if (r.Height <= r.Width)
            return Math.Abs(r.Top - screen.Top) < 4 ? TaskbarEdge.Top : TaskbarEdge.Bottom;
        return Math.Abs(r.Left - screen.Left) < 4 ? TaskbarEdge.Left : TaskbarEdge.Right;
    }

    /// <summary>true, если в точке лежит чужое окно (наш чип перекрыт, например панелью задач).</summary>
    public static bool IsCoveredAt(IntPtr ourHwnd, int x, int y)
    {
        var hit = Interop.WindowFromPoint(new Interop.POINT { X = x, Y = y });
        if (hit == IntPtr.Zero) return false;
        if (hit == ourHwnd) return false;

        var root = Interop.GetAncestor(hit, Interop.GA_ROOT);
        return root != ourHwnd;
    }

    /// <summary>true, если активно окно самой панели задач или рабочий стол.</summary>
    public static bool IsTaskbarOrDesktopForeground()
    {
        var fg = Interop.GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;

        var cls = Interop.ClassOf(fg);
        return cls is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Progman" or "WorkerW";
    }

    /// <summary>true, если активное окно занимает весь монитор (игра/видео на весь экран).</summary>
    public static bool ForegroundIsFullscreen(IntPtr ignoreHwnd)
    {
        var fg = Interop.GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == ignoreHwnd) return false;
        if (!Interop.GetWindowRect(fg, out var wr) || wr.Width < 32 || wr.Height < 32) return false;

        var mon = Interop.MonitorFromWindow(fg, 2 /* MONITOR_DEFAULTTONEAREST */);
        if (mon == IntPtr.Zero) return false;

        var mi = new Interop.MONITORINFO { cbSize = Marshal.SizeOf<Interop.MONITORINFO>() };
        if (!Interop.GetMonitorInfo(mon, ref mi)) return false;

        var m = mi.rcMonitor;
        return wr.Left <= m.Left + 1 && wr.Top <= m.Top + 1 &&
               wr.Right >= m.Right - 1 && wr.Bottom >= m.Bottom - 1;
    }
}
