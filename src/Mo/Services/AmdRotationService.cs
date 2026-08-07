using System.Runtime.InteropServices;
using Mo.Models;

namespace Mo.Services;

// Driver-level rotation for Radeon GPUs via ADL2, used when the user picks
// RotationMethod.AmdDriver — Windows' own CCD rotation has a cursor-coordinate bug.
// Displays must be resolved by GDI name; see CLAUDE.md "Radeon (ADL) rules".
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

    // Functions are __cdecl, but ADL_MAIN_MALLOC_CALLBACK is __stdcall.
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate nint ADL_Main_Memory_Alloc(int size);

    // ADL allocates out-buffers through this and hands us ownership.
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
            // Created once and kept — ADL2_Main_Control_Create is slow.
            if (ADL2_Main_Control_Create(AllocCallback, 1, out var ctx) == ADL_OK && ctx != 0)
            {
                _context = ctx;

                // Not an adapter count: ADL enumerates NVIDIA adapters too.
                IsAvailable = AdlDisplays.HasAmdAdapter(ctx);

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

    /// <summary>One display's desired state, already matched to attached hardware.</summary>
    public readonly record struct DisplayTarget(
        string GdiDeviceName,
        int PositionX,
        int PositionY,
        int Width,
        int Height,
        double RefreshHz,
        DisplayRotation Rotation);

    /// <summary>
    /// Applies position, size, refresh and rotation for every target in one pass.
    /// All-or-nothing: any failure returns false and DisplayService runs CCD over the
    /// whole profile, rather than leaving a half-applied desktop.
    /// </summary>
    public bool ApplyFullProfile(IReadOnlyList<DisplayTarget> targets)
    {
        if (!IsAvailable || targets.Count == 0) return false;

        lock (_gate)
        {
            if (_disposed || _context == 0) return false;

            try
            {
                // Resolve everything before writing anything.
                var resolved = new List<(DisplayTarget target, int adapter, int display)>(targets.Count);
                foreach (var target in targets)
                {
                    var located = AdlDisplays.Resolve(_context, target.GdiDeviceName);
                    if (located == null)
                    {
                        Helpers.BootLog.Write("amd.fullprofile.unresolved", target.GdiDeviceName);
                        return false;
                    }
                    resolved.Add((target, located.Value.adapterIndex, located.Value.displayIndex));
                }

                foreach (var (target, adapter, display) in resolved)
                {
                    if (!ApplyOne(target, adapter, display)) return false;
                }

                Helpers.BootLog.Write("amd.fullprofile.applied", $"{resolved.Count} displays");
                return true;
            }
            catch (Exception ex)
            {
                Helpers.BootLog.WriteError("amd.fullprofile", ex);
                return false;
            }
        }
    }

    private bool ApplyOne(DisplayTarget target, int adapterIndex, int displayIndex)
    {
        // Edit the current mode rather than build one: ModeFlag/Mask/Value and colour
        // depth carry driver state we have no business inventing.
        if (ADL2_Display_Modes_Get(_context, adapterIndex, displayIndex, out int numModes, out nint modesPtr) != ADL_OK
            || numModes == 0 || modesPtr == 0)
            return false;

        ADLMode mode;
        try { mode = Marshal.PtrToStructure<ADLMode>(modesPtr); }
        finally { Marshal.FreeHGlobal(modesPtr); }

        mode.XPos = target.PositionX;
        mode.YPos = target.PositionY;

        // Mo stores Width/Height pre-swapped for portrait; ADL wants the panel's own
        // resolution with Orientation separate. Verified only by the read-back check in
        // DisplayService — ADL does not document which it expects.
        bool portrait = target.Rotation is DisplayRotation.Rotate90 or DisplayRotation.Rotate270;
        mode.XRes = portrait ? target.Height : target.Width;
        mode.YRes = portrait ? target.Width : target.Height;

        if (target.RefreshHz > 0) mode.RefreshRate = (float)target.RefreshHz;

        mode.Orientation = target.Rotation switch
        {
            DisplayRotation.Rotate90 => 90,
            DisplayRotation.Rotate180 => 180,
            DisplayRotation.Rotate270 => 270,
            _ => 0,
        };

        if (ADL2_Display_Modes_Set(_context, adapterIndex, displayIndex, 1, ref mode) == ADL_OK)
            return true;

        Helpers.BootLog.Write("amd.fullprofile.setfailed",
            $"adapter={adapterIndex} display={displayIndex} {mode.XRes}x{mode.YRes}@{mode.RefreshRate} rot={mode.Orientation}");
        return false;
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
