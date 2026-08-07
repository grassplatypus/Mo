using System.Runtime.InteropServices;
using Mo.Models;

namespace Mo.Services;

/// <summary>
/// Driver-level rotation for Radeon GPUs via AMD's ADL2 API.
/// </summary>
/// <remarks>
/// Rotating through Windows' own CCD path triggers a long-standing cursor-coordinate
/// bug, so when the user opts into <see cref="RotationMethod.AmdDriver"/> the rotation
/// is handed to the driver instead.
///
/// The display has to be resolved before it can be rotated. ADL indexes displays per
/// adapter, and those indices have nothing to do with the order Windows reports
/// monitors in — so a monitor is matched by its GDI device name ("\\.\DISPLAY1"),
/// which ADL surfaces as ADLDisplayInfo.strDisplayName. This is the same bridge the
/// NVIDIA path uses. Earlier versions skipped this entirely and always wrote to
/// display index 0 of each adapter, which rotated whichever monitor happened to be
/// first rather than the one the user asked for.
/// </remarks>
public sealed class AmdRotationService : IDisposable
{
    private const string AdlLib = "atiadlxx.dll";
    private const int ADL_OK = 0;

    private readonly object _gate = new();
    private nint _context;
    private bool _disposed;

    public bool IsAvailable { get; }

    [DllImport(AdlLib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ADL2_Main_Control_Create(ADL_Main_Memory_Alloc callback, int enumConnectedAdapters, out nint context);

    [DllImport(AdlLib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ADL2_Main_Control_Destroy(nint context);

    [DllImport(AdlLib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ADL2_Adapter_NumberOfAdapters_Get(nint context, out int numAdapters);

    [DllImport(AdlLib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ADL2_Display_Modes_Get(nint context, int adapterIndex, int displayIndex, out int numModes, out nint modes);

    [DllImport(AdlLib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ADL2_Display_Modes_Set(nint context, int adapterIndex, int displayIndex, int numModes, ref ADLMode modes);

    private delegate nint ADL_Main_Memory_Alloc(int size);

    // ADL allocates its out-buffers through this callback and hands ownership to us,
    // so every pointer it returns has to come back through Marshal.FreeHGlobal.
    private static readonly ADL_Main_Memory_Alloc AllocCallback = Marshal.AllocHGlobal;

    [StructLayout(LayoutKind.Sequential)]
    private struct ADLMode
    {
        public int AdapterIndex;
        public AdlDisplays.ADLDisplayID DisplayID;
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

    public AmdRotationService()
    {
        try
        {
            // The context is created once and kept. ADL2_Main_Control_Create is slow,
            // and the previous code paid that cost on every single rotation.
            if (ADL2_Main_Control_Create(AllocCallback, 1, out var ctx) == ADL_OK && ctx != 0)
            {
                _context = ctx;
                IsAvailable = ADL2_Adapter_NumberOfAdapters_Get(ctx, out int n) == ADL_OK && n > 0;

                if (!IsAvailable)
                {
                    ADL2_Main_Control_Destroy(ctx);
                    _context = 0;
                }
            }
        }
        catch (DllNotFoundException) { IsAvailable = false; }   // No Radeon driver installed.
        catch (EntryPointNotFoundException) { IsAvailable = false; } // Driver too old for ADL2.
        catch { IsAvailable = false; }
    }

    public bool ApplyRotation(MonitorInfo monitor, DisplayRotation rotation)
    {
        if (!IsAvailable) return false;

        lock (_gate)
        {
            if (_disposed || _context == 0) return false;

            try
            {
                var target = AdlDisplays.Resolve(_context, monitor.GdiDeviceName);
                if (target == null)
                {
                    Helpers.BootLog.Write("amd.rotate.unresolved",
                        $"{monitor.FriendlyName} ({monitor.GdiDeviceName})");
                    return false;
                }

                var (adapterIndex, displayIndex) = target.Value;

                if (ADL2_Display_Modes_Get(_context, adapterIndex, displayIndex, out int numModes, out nint modesPtr) != ADL_OK
                    || numModes == 0 || modesPtr == 0)
                    return false;

                ADLMode mode;
                try { mode = Marshal.PtrToStructure<ADLMode>(modesPtr); }
                finally { Marshal.FreeHGlobal(modesPtr); }

                mode.Orientation = rotation switch
                {
                    DisplayRotation.Rotate90 => 90,
                    DisplayRotation.Rotate180 => 180,
                    DisplayRotation.Rotate270 => 270,
                    _ => 0,
                };

                bool ok = ADL2_Display_Modes_Set(_context, adapterIndex, displayIndex, 1, ref mode) == ADL_OK;
                if (!ok)
                    Helpers.BootLog.Write("amd.rotate.failed",
                        $"adapter={adapterIndex} display={displayIndex} rotation={rotation}");
                return ok;
            }
            catch (Exception ex)
            {
                Helpers.BootLog.WriteError("amd.rotate", ex);
                return false;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;

            if (_context != 0)
            {
                try { ADL2_Main_Control_Destroy(_context); } catch { }
                _context = 0;
            }
        }
    }
}
