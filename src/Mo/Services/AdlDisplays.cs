using System.Runtime.InteropServices;

namespace Mo.Services;

/// <summary>
/// Shared ADL2 display lookup for the Radeon services.
/// </summary>
/// <remarks>
/// ADL addresses a display by an (adapter index, logical display index) pair, and
/// neither number relates to the order Windows reports monitors in. Every ADL call has
/// to resolve its target first; the rotation and colour services used to skip that and
/// hardcode index 0, so on any multi-monitor Radeon setup they acted on whichever
/// monitor happened to be first rather than the selected one.
///
/// The bridge is the GDI device name ("\\.\DISPLAY1"), which Mo carries on
/// <c>MonitorInfo.GdiDeviceName</c>. Getting the right ADL field matters:
///   • <c>AdapterInfo.strDisplayName</c>   — "Display name. For example, \\Display0 for
///                                           Windows."  ← the GDI name, what we match on
///   • <c>ADLDisplayInfo.strDisplayName</c> — "The display's EDID name."  ← the monitor
///                                           model, which never equals a GDI name
/// So the lookup is two steps: find the adapter whose GDI name matches, then take that
/// adapter's connected+mapped display for its logical index.
///
/// Field layouts and constants below are transcribed from AMD's published headers
/// (adl_structures.h / adl_defines.h in GPUOpen-LibrariesAndSDKs/display-library).
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

    // Caller-allocated buffer: iInputSize is numAdapters * sizeof(AdapterInfo).
    [DllImport(AdlLib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ADL2_Adapter_AdapterInfo_Get(nint context, nint lpInfo, int iInputSize);

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
    private struct AdapterInfo
    {
        public int Size;
        public int AdapterIndex;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = ADL_MAX_PATH)] public string UDID;
        public int BusNumber;
        public int DeviceNumber;
        public int FunctionNumber;
        public int VendorID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = ADL_MAX_PATH)] public string AdapterName;
        /// <summary>GDI display name, e.g. "\\.\DISPLAY1".</summary>
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = ADL_MAX_PATH)] public string DisplayName;
        public int Present;
        // The remaining fields are Windows-only, which is the only platform Mo targets.
        public int Exist;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = ADL_MAX_PATH)] public string DriverPath;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = ADL_MAX_PATH)] public string DriverPathExt;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = ADL_MAX_PATH)] public string PNPString;
        public int OSDisplayIndex;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct ADLDisplayInfo
    {
        public ADLDisplayID DisplayID;
        public int DisplayControllerIndex;
        /// <summary>The display's EDID name — a monitor model, NOT a GDI device name.</summary>
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = ADL_MAX_PATH)] public string EdidName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = ADL_MAX_PATH)] public string ManufacturerName;
        public int DisplayType;
        public int DisplayOutputType;
        public int DisplayConnector;
        public int DisplayInfoMask;
        public int DisplayInfoValue;
    }

    /// <summary>
    /// Finds the ADL (adapter, display) pair for a GDI device name, or null when the
    /// monitor is not driven by a Radeon adapter.
    /// </summary>
    public static (int adapterIndex, int displayIndex)? Resolve(nint context, string? gdiDeviceName)
    {
        if (context == 0 || string.IsNullOrEmpty(gdiDeviceName)) return null;

        try
        {
            foreach (var adapter in EnumerateAdapters(context))
            {
                // iExist distinguishes an adapter that is actually present from a stale
                // entry the driver still lists.
                if (adapter.Exist == 0) continue;
                if (!string.Equals(adapter.DisplayName?.Trim(), gdiDeviceName, StringComparison.OrdinalIgnoreCase))
                    continue;

                foreach (var display in EnumerateDisplays(context, adapter.AdapterIndex))
                {
                    if ((display.DisplayInfoValue & DISPLAYINFO_CONNECTED) == 0) continue;
                    if ((display.DisplayInfoValue & DISPLAYINFO_MAPPED) == 0) continue;

                    // ADL indexes by the logical display index, not by this entry's
                    // position in the array.
                    return (adapter.AdapterIndex, display.DisplayID.DisplayLogicalIndex);
                }
            }
        }
        catch (Exception ex) { Helpers.BootLog.WriteError("adl.resolve", ex); }

        return null;
    }

    private static List<AdapterInfo> EnumerateAdapters(nint context)
    {
        var result = new List<AdapterInfo>();

        if (ADL2_Adapter_NumberOfAdapters_Get(context, out int count) != ADL_OK || count <= 0)
            return result;

        // Unlike DisplayInfo_Get, this buffer is ours to allocate and free — ADL only
        // fills it in.
        int stride = Marshal.SizeOf<AdapterInfo>();
        nint buffer = Marshal.AllocHGlobal(stride * count);
        try
        {
            // Zeroed first: ADL reads iSize back out of each entry on some driver
            // versions, and uninitialised memory here produces garbage adapters.
            for (int i = 0; i < stride * count; i++) Marshal.WriteByte(buffer, i, 0);

            if (ADL2_Adapter_AdapterInfo_Get(context, buffer, stride * count) != ADL_OK)
                return result;

            for (int i = 0; i < count; i++)
                result.Add(Marshal.PtrToStructure<AdapterInfo>(buffer + i * stride));
        }
        catch (Exception ex) { Helpers.BootLog.WriteError("adl.adapters", ex); }
        finally { Marshal.FreeHGlobal(buffer); }

        return result;
    }

    private static List<ADLDisplayInfo> EnumerateDisplays(nint context, int adapterIndex)
    {
        var result = new List<ADLDisplayInfo>();

        // forceDetect: 0 — a detect pass re-probes every connector and can blank the
        // screen briefly, which is unacceptable while a profile is being applied.
        if (ADL2_Display_DisplayInfo_Get(context, adapterIndex, out int count, out nint ptr, 0) != ADL_OK
            || count <= 0 || ptr == 0)
            return result;

        try
        {
            // ADL allocated this through the caller's ADL_MAIN_MALLOC_CALLBACK and
            // handed us ownership, so it must be freed here.
            int stride = Marshal.SizeOf<ADLDisplayInfo>();
            for (int i = 0; i < count; i++)
                result.Add(Marshal.PtrToStructure<ADLDisplayInfo>(ptr + i * stride));
        }
        catch (Exception ex) { Helpers.BootLog.WriteError("adl.displays", ex); }
        finally { Marshal.FreeHGlobal(ptr); }

        return result;
    }
}
