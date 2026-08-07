using System.Runtime.InteropServices;

namespace Mo.Services;

// Shared ADL2 display lookup for the Radeon services. See CLAUDE.md "Radeon (ADL)
// rules" for the constraints these calls depend on — they were verified against real
// hardware and are easy to break by inspection.
internal static class AdlDisplays
{
    private const string AdlLib = "atiadlxx.dll";
    private const int ADL_OK = 0;
    private const int ADL_MAX_PATH = 256;

    private const int DISPLAYINFO_CONNECTED = 0x00000001;
    private const int DISPLAYINFO_MAPPED = 0x00000002;

    // Decimal 1002, not the PCI value 0x1002 — that is what ADL reports.
    private const int AMD_VENDOR_ID = 1002;

    [DllImport(AdlLib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ADL2_Adapter_NumberOfAdapters_Get(nint context, out int numAdapters);

    // Caller-allocated buffer; iInputSize is numAdapters * sizeof(AdapterInfo).
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
        // Windows-only tail, which is the only platform Mo targets.
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
        /// <summary>EDID model name — not a GDI device name.</summary>
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = ADL_MAX_PATH)] public string EdidName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = ADL_MAX_PATH)] public string ManufacturerName;
        public int DisplayType;
        public int DisplayOutputType;
        public int DisplayConnector;
        public int DisplayInfoMask;
        public int DisplayInfoValue;
    }

    /// <summary>
    /// ADL (adapter, display) pair for a GDI device name, or null when the monitor is
    /// not driven by a Radeon adapter.
    /// </summary>
    public static (int adapterIndex, int displayIndex)? Resolve(nint context, string? gdiDeviceName)
    {
        if (context == 0 || string.IsNullOrEmpty(gdiDeviceName)) return null;

        try
        {
            foreach (var adapter in EnumerateAdapters(context))
            {
                if (adapter.Exist == 0) continue;
                if (adapter.VendorID != AMD_VENDOR_ID) continue;
                if (!string.Equals(adapter.DisplayName?.Trim(), gdiDeviceName, StringComparison.OrdinalIgnoreCase))
                    continue;

                foreach (var display in EnumerateDisplays(context, adapter.AdapterIndex))
                {
                    if ((display.DisplayInfoValue & DISPLAYINFO_CONNECTED) == 0) continue;
                    if ((display.DisplayInfoValue & DISPLAYINFO_MAPPED) == 0) continue;

                    // ADL indexes by logical index, not array position.
                    return (adapter.AdapterIndex, display.DisplayID.DisplayLogicalIndex);
                }
            }
        }
        catch (Exception ex) { Helpers.BootLog.WriteError("adl.resolve", ex); }

        return null;
    }

    /// <summary>True when a present adapter is actually AMD. An adapter count is not.</summary>
    public static bool HasAmdAdapter(nint context)
    {
        try { return EnumerateAdapters(context).Any(a => a.Exist != 0 && a.VendorID == AMD_VENDOR_ID); }
        catch { return false; }
    }

    private static List<AdapterInfo> EnumerateAdapters(nint context)
    {
        var result = new List<AdapterInfo>();

        if (ADL2_Adapter_NumberOfAdapters_Get(context, out int count) != ADL_OK || count <= 0)
            return result;

        int stride = Marshal.SizeOf<AdapterInfo>();
        nint buffer = Marshal.AllocHGlobal(stride * count);
        try
        {
            // Some driver versions read iSize back out; uninitialised memory yields
            // garbage adapters.
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

        // forceDetect: 0 — a detect pass re-probes connectors and can blank the screen.
        if (ADL2_Display_DisplayInfo_Get(context, adapterIndex, out int count, out nint ptr, 0) != ADL_OK
            || count <= 0 || ptr == 0)
            return result;

        try
        {
            // ADL allocated this through our callback and handed us ownership.
            int stride = Marshal.SizeOf<ADLDisplayInfo>();
            for (int i = 0; i < count; i++)
                result.Add(Marshal.PtrToStructure<ADLDisplayInfo>(ptr + i * stride));
        }
        catch (Exception ex) { Helpers.BootLog.WriteError("adl.displays", ex); }
        finally { Marshal.FreeHGlobal(ptr); }

        return result;
    }
}
