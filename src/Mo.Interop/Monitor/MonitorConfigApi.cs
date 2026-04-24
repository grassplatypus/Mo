using System.Runtime.InteropServices;

namespace Mo.Interop.Monitor;

public static class MonitorConfigApi
{
    // ── Physical Monitor Handle ──

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct PHYSICAL_MONITOR
    {
        public nint hPhysicalMonitor;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szPhysicalMonitorDescription;
    }

    [DllImport("dxva2.dll", SetLastError = true)]
    public static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(
        nint hMonitor, out uint pdwNumberOfPhysicalMonitors);

    [DllImport("dxva2.dll", SetLastError = true)]
    public static extern bool GetPhysicalMonitorsFromHMONITOR(
        nint hMonitor, uint dwPhysicalMonitorArraySize,
        [Out] PHYSICAL_MONITOR[] pPhysicalMonitorArray);

    [DllImport("dxva2.dll", SetLastError = true)]
    public static extern bool DestroyPhysicalMonitors(
        uint dwPhysicalMonitorArraySize,
        [In] PHYSICAL_MONITOR[] pPhysicalMonitorArray);

    // ── Brightness / Contrast ──

    [DllImport("dxva2.dll", SetLastError = true)]
    public static extern bool GetMonitorBrightness(
        nint hMonitor, out uint pdwMinimumBrightness,
        out uint pdwCurrentBrightness, out uint pdwMaximumBrightness);

    [DllImport("dxva2.dll", SetLastError = true)]
    public static extern bool SetMonitorBrightness(nint hMonitor, uint dwNewBrightness);

    [DllImport("dxva2.dll", SetLastError = true)]
    public static extern bool GetMonitorContrast(
        nint hMonitor, out uint pdwMinimumContrast,
        out uint pdwCurrentContrast, out uint pdwMaximumContrast);

    [DllImport("dxva2.dll", SetLastError = true)]
    public static extern bool SetMonitorContrast(nint hMonitor, uint dwNewContrast);

    // ── Color (RGB Gain) ──

    public enum MC_GAIN_TYPE
    {
        MC_RED_GAIN = 0,
        MC_GREEN_GAIN = 1,
        MC_BLUE_GAIN = 2,
    }

    [DllImport("dxva2.dll", SetLastError = true)]
    public static extern bool GetMonitorRedGreenOrBlueGain(
        nint hMonitor, MC_GAIN_TYPE gtGainType,
        out uint pdwMinimumGain, out uint pdwCurrentGain, out uint pdwMaximumGain);

    [DllImport("dxva2.dll", SetLastError = true)]
    public static extern bool SetMonitorRedGreenOrBlueGain(
        nint hMonitor, MC_GAIN_TYPE gtGainType, uint dwNewGain);

    // ── Generic VCP access (color temp presets, input switch, etc.) ──

    public const byte VCP_SELECT_COLOR_PRESET = 0x14;  // 5000K/6500K/sRGB/User
    public const byte VCP_INPUT_SOURCE = 0x60;

    [DllImport("dxva2.dll", SetLastError = true)]
    public static extern bool GetVCPFeatureAndVCPFeatureReply(
        nint hMonitor, byte bVCPCode, nint pvct,
        out uint pdwCurrentValue, out uint pdwMaximumValue);

    [DllImport("dxva2.dll", SetLastError = true)]
    public static extern bool SetVCPFeature(nint hMonitor, byte bVCPCode, uint dwNewValue);

    // ── Monitor Enumeration ──

    public delegate bool MonitorEnumProc(nint hMonitor, nint hdcMonitor, ref RECT lprcMonitor, nint dwData);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int left, top, right, bottom;
    }

    [DllImport("user32.dll")]
    public static extern bool EnumDisplayMonitors(
        nint hdc, nint lprcClip, MonitorEnumProc lpfnEnum, nint dwData);
}
