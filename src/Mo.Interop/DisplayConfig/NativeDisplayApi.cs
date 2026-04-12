using System.Runtime.InteropServices;

namespace Mo.Interop.DisplayConfig;

public static class NativeDisplayApi
{
    [DllImport("user32.dll")]
    public static extern int GetDisplayConfigBufferSizes(
        QDC_FLAGS flags,
        out uint numPathArrayElements,
        out uint numModeInfoArrayElements);

    [DllImport("user32.dll")]
    public static extern int QueryDisplayConfig(
        QDC_FLAGS flags,
        ref uint numPathArrayElements,
        [Out] DISPLAYCONFIG_PATH_INFO[] pathArray,
        ref uint numModeInfoArrayElements,
        [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray,
        IntPtr currentTopologyId);

    [DllImport("user32.dll")]
    public static extern int SetDisplayConfig(
        uint numPathArrayElements,
        [In] DISPLAYCONFIG_PATH_INFO[]? pathArray,
        uint numModeInfoArrayElements,
        [In] DISPLAYCONFIG_MODE_INFO[]? modeInfoArray,
        SDC_FLAGS flags);

    [DllImport("user32.dll")]
    public static extern int DisplayConfigGetDeviceInfo(
        ref DISPLAYCONFIG_TARGET_DEVICE_NAME requestPacket);

    [DllImport("user32.dll")]
    public static extern int DisplayConfigGetDeviceInfo(
        ref DISPLAYCONFIG_SOURCE_DEVICE_NAME requestPacket);

    [DllImport("user32.dll")]
    public static extern bool SystemParametersInfo(
        uint uiAction, uint uiParam, IntPtr pvParam, uint fWinIni);

    [DllImport("user32.dll")]
    public static extern bool PostMessage(
        IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    public const int ERROR_SUCCESS = 0;
    public static readonly IntPtr HWND_BROADCAST = new(0xffff);
    public const uint WM_DISPLAYCHANGE = 0x007E;
    public const uint SPI_SETWORKAREA = 0x002F;
    public const uint SPIF_SENDCHANGE = 0x02;
}
