using System.Runtime.InteropServices;

namespace Mo.Services;

/// <summary>
/// Shared ADL2 display enumeration for the Radeon services.
/// </summary>
/// <remarks>
/// ADL addresses a display by an (adapter index, logical display index) pair, and
/// neither number has any relationship to the order Windows reports monitors in. Every
/// ADL call therefore has to resolve its target first; both the rotation and the colour
/// service used to skip that and hardcode index 0, so on any multi-monitor Radeon setup
/// they acted on whichever monitor happened to be first rather than the selected one.
///
/// The bridge is the GDI device name ("\\.\DISPLAY1"), which ADL reports as
/// ADLDisplayInfo.strDisplayName and Mo carries on <c>MonitorInfo.GdiDeviceName</c> —
/// the same bridge the NVIDIA path uses.
/// </remarks>
internal static class AdlDisplays
{
    private const string AdlLib = "atiadlxx.dll";
    private const int ADL_OK = 0;
    private const int ADL_MAX_PATH = 256;

    // ADLDisplayInfo.iDisplayInfoValue bits.
    private const int DISPLAYINFO_CONNECTED = 0x00000001;
    private const int DISPLAYINFO_MAPPED = 0x00000002;

    [DllImport(AdlLib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ADL2_Adapter_NumberOfAdapters_Get(nint context, out int numAdapters);

    [DllImport(AdlLib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ADL2_Display_DisplayInfo_Get(nint context, int adapterIndex, out int numDisplays, out nint displays, int forceDetect);

    [StructLayout(LayoutKind.Sequential)]
    internal struct ADLDisplayID
    {
        public int DisplayLogicalIndex;
        public int DisplayPhysicalIndex;
        public int DisplayLogicalAdapterIndex;
        public int DisplayPhysicalAdapterIndex;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct ADLDisplayInfo
    {
        public ADLDisplayID DisplayID;
        public int DisplayControllerIndex;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = ADL_MAX_PATH)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = ADL_MAX_PATH)] public string DisplayManufacturerName;
        public int DisplayType;
        public int DisplayOutputType;
        public int DisplayConnector;
        public int DisplayInfoMask;
        public int DisplayInfoValue;
    }

    /// <summary>
    /// Finds the ADL (adapter, display) pair for a GDI device name, or null when the
    /// monitor is not attached to a Radeon adapter.
    /// </summary>
    public static (int adapterIndex, int displayIndex)? Resolve(nint context, string? gdiDeviceName)
    {
        if (context == 0 || string.IsNullOrEmpty(gdiDeviceName)) return null;

        try
        {
            if (ADL2_Adapter_NumberOfAdapters_Get(context, out int adapterCount) != ADL_OK) return null;

            for (int adapter = 0; adapter < adapterCount; adapter++)
            {
                foreach (var info in Enumerate(context, adapter))
                {
                    // Only a display the OS has actually mapped can be driven.
                    if ((info.DisplayInfoValue & DISPLAYINFO_CONNECTED) == 0) continue;
                    if ((info.DisplayInfoValue & DISPLAYINFO_MAPPED) == 0) continue;

                    if (string.Equals(info.DisplayName?.Trim(), gdiDeviceName, StringComparison.OrdinalIgnoreCase))
                    {
                        // ADL indexes by the logical display index, not by this entry's
                        // position in the array.
                        return (adapter, info.DisplayID.DisplayLogicalIndex);
                    }
                }
            }
        }
        catch (Exception ex) { Helpers.BootLog.WriteError("adl.resolve", ex); }

        return null;
    }

    private static List<ADLDisplayInfo> Enumerate(nint context, int adapterIndex)
    {
        var result = new List<ADLDisplayInfo>();

        // forceDetect: 0 — a detect pass re-probes every connector and can blank the
        // screen briefly, which is unacceptable while a profile is being applied.
        if (ADL2_Display_DisplayInfo_Get(context, adapterIndex, out int count, out nint ptr, 0) != ADL_OK
            || count <= 0 || ptr == 0)
            return result;

        try
        {
            // ADL allocated this through the caller's ADL_Main_Memory_Alloc callback and
            // handed us ownership, so it must be freed here.
            int stride = Marshal.SizeOf<ADLDisplayInfo>();
            for (int i = 0; i < count; i++)
                result.Add(Marshal.PtrToStructure<ADLDisplayInfo>(ptr + i * stride));
        }
        catch (Exception ex) { Helpers.BootLog.WriteError("adl.enumerate", ex); }
        finally { Marshal.FreeHGlobal(ptr); }

        return result;
    }
}
