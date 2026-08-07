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

    // ADL's functions are __cdecl but the allocation callback is __stdcall
    // (ADL_MAIN_MALLOC_CALLBACK in adl_sdk.h). Winapi resolves to StdCall on Windows,
    // which is what the default would give — stated explicitly so it survives a move.
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
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

    /// <summary>One display's desired state, already matched to attached hardware.</summary>
    /// <param name="GdiDeviceName">"\\.\DISPLAY1" — how the display is located in ADL.</param>
    public readonly record struct DisplayTarget(
        string GdiDeviceName,
        int PositionX,
        int PositionY,
        int Width,
        int Height,
        double RefreshHz,
        DisplayRotation Rotation);

    /// <summary>
    /// Applies position, size, refresh rate and rotation for every target in one pass,
    /// driver-side. Returns false if any target could not be set, so the caller can fall
    /// back to the CCD path.
    /// </summary>
    /// <remarks>
    /// The Radeon counterpart to NvidiaRotationService.ApplyFullProfile. Going through
    /// the driver avoids the cursor-coordinate bug that Windows' own rotation path has.
    ///
    /// This is all-or-nothing on purpose: a partial apply would leave the desktop in a
    /// state that is neither the old layout nor the requested one. Any failure returns
    /// false with nothing further attempted, and DisplayService then runs the CCD path
    /// over the whole profile.
    /// </remarks>
    public bool ApplyFullProfile(IReadOnlyList<DisplayTarget> targets)
    {
        if (!IsAvailable || targets.Count == 0) return false;

        lock (_gate)
        {
            if (_disposed || _context == 0) return false;

            try
            {
                // Resolve every target before writing anything. If even one display
                // cannot be located, the whole apply is abandoned untouched.
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
        // Read the current mode first and edit it, rather than building one from
        // scratch: ModeFlag/ModeMask/ModeValue and colour depth carry driver state this
        // code has no business inventing.
        if (ADL2_Display_Modes_Get(_context, adapterIndex, displayIndex, out int numModes, out nint modesPtr) != ADL_OK
            || numModes == 0 || modesPtr == 0)
            return false;

        ADLMode mode;
        try { mode = Marshal.PtrToStructure<ADLMode>(modesPtr); }
        finally { Marshal.FreeHGlobal(modesPtr); }

        mode.XPos = target.PositionX;
        mode.YPos = target.PositionY;

        // Mo stores Width/Height already swapped for portrait rotations, but ADL wants
        // the panel's own resolution with Orientation applied separately. Swap back so
        // a 1440x2560 portrait entry is sent as a 2560x1440 panel rotated 90 degrees.
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
