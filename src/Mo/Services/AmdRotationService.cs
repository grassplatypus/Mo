using System.Runtime.InteropServices;
using Mo.Models;

namespace Mo.Services;

public sealed class AmdRotationService
{
    public bool IsAvailable { get; }

    private const string AdlLib = "atiadlxx.dll";

    [DllImport(AdlLib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ADL2_Main_Control_Create(ADL_Main_Memory_Alloc callback, int enumConnectedAdapters, out IntPtr context);

    [DllImport(AdlLib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ADL2_Main_Control_Destroy(IntPtr context);

    [DllImport(AdlLib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ADL2_Adapter_NumberOfAdapters_Get(IntPtr context, out int numAdapters);

    [DllImport(AdlLib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ADL2_Display_Modes_Get(IntPtr context, int adapterIndex, int displayIndex, out int numModes, out IntPtr modes);

    [DllImport(AdlLib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ADL2_Display_Modes_Set(IntPtr context, int adapterIndex, int displayIndex, int numModes, ref ADLMode modes);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr ADL_Main_Memory_Alloc(int size);

    private static IntPtr ADL_Alloc(int size) => Marshal.AllocHGlobal(size);

    [StructLayout(LayoutKind.Sequential)]
    private struct ADLMode
    {
        public int AdapterIndex;
        public ADLDisplayID DisplayID;
        public int XPos;
        public int YPos;
        public int XRes;
        public int YRes;
        public int ColourDepth;
        public float RefreshRate;
        public int Orientation;       // 0=landscape, 90, 180, 270
        public int ModeFlag;
        public int ModeMask;
        public int ModeValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ADLDisplayID
    {
        public int DisplayLogicalIndex;
        public int DisplayPhysicalIndex;
        public int DisplayLogicalAdapterIndex;
        public int DisplayPhysicalAdapterIndex;
    }

    public AmdRotationService()
    {
        try
        {
            var ctx = IntPtr.Zero;
            int result = ADL2_Main_Control_Create(ADL_Alloc, 1, out ctx);
            if (result == 0 && ctx != IntPtr.Zero)
            {
                ADL2_Adapter_NumberOfAdapters_Get(ctx, out int numAdapters);
                IsAvailable = numAdapters > 0;
                ADL2_Main_Control_Destroy(ctx);
            }
        }
        catch (DllNotFoundException) { IsAvailable = false; }
        catch { IsAvailable = false; }
    }

    public bool ApplyRotation(MonitorInfo monitor, DisplayRotation rotation)
    {
        if (!IsAvailable) return false;

        try
        {
            var ctx = IntPtr.Zero;
            int result = ADL2_Main_Control_Create(ADL_Alloc, 1, out ctx);
            if (result != 0 || ctx == IntPtr.Zero) return false;

            try
            {
                ADL2_Adapter_NumberOfAdapters_Get(ctx, out int numAdapters);

                for (int adapter = 0; adapter < numAdapters; adapter++)
                {
                    result = ADL2_Display_Modes_Get(ctx, adapter, 0, out int numModes, out IntPtr modesPtr);
                    if (result != 0 || numModes == 0) continue;

                    var mode = Marshal.PtrToStructure<ADLMode>(modesPtr);
                    Marshal.FreeHGlobal(modesPtr);

                    mode.Orientation = rotation switch
                    {
                        DisplayRotation.Rotate90 => 90,
                        DisplayRotation.Rotate180 => 180,
                        DisplayRotation.Rotate270 => 270,
                        _ => 0,
                    };

                    result = ADL2_Display_Modes_Set(ctx, adapter, 0, 1, ref mode);
                    if (result == 0) return true;
                }
            }
            finally
            {
                ADL2_Main_Control_Destroy(ctx);
            }
        }
        catch { }

        return false;
    }
}
