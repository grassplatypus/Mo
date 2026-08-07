using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace Mo.Helpers;

public static class WindowHelper
{
    public static AppWindow GetAppWindow(Window window)
    {
        var hwnd = WindowNative.GetWindowHandle(window);
        var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
        return AppWindow.GetFromWindowId(windowId);
    }

    public static nint GetHwnd(Window window)
    {
        return WindowNative.GetWindowHandle(window);
    }

    // ── Work-area enumeration ──
    //
    // Win32 rather than DisplayArea.FindAll(): that projection throws
    // InvalidCastException in this app's self-contained/unpackaged configuration, and
    // it was doing so inside a catch, silently yielding an empty monitor list. Any
    // caller then concluded "no monitors" and gave up on restoring window placement.

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEXW
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    private delegate bool MonitorEnumProc(nint hMonitor, nint hdc, ref RECT rect, nint data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(nint hdc, nint clip, MonitorEnumProc callback, nint data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfoW(nint hMonitor, ref MONITORINFOEXW info);

    /// <summary>
    /// Work areas (screen minus taskbar) of every attached monitor, in virtual-desktop
    /// pixels. Empty only if the enumeration genuinely fails.
    /// </summary>
    public static List<Core.WindowPlacementValidator.Rect> GetWorkAreas()
    {
        var areas = new List<Core.WindowPlacementValidator.Rect>();

        EnumDisplayMonitors(0, 0, (nint hMonitor, nint _, ref RECT _, nint _) =>
        {
            var info = new MONITORINFOEXW { cbSize = Marshal.SizeOf<MONITORINFOEXW>() };
            if (GetMonitorInfoW(hMonitor, ref info))
            {
                var w = info.rcWork;
                areas.Add(new Core.WindowPlacementValidator.Rect(
                    w.Left, w.Top, w.Right - w.Left, w.Bottom - w.Top));
            }
            return true; // keep enumerating
        }, 0);

        return areas;
    }
}
