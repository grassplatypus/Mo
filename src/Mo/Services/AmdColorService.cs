using System.Runtime.InteropServices;

namespace Mo.Services;

// Colour via ADL2_Display_Color_Get/Set (type 0 brightness, 1 contrast, 2 saturation,
// 3 hue, 4 temperature), adjusting the GPU-side pipeline — so it works on monitors with
// no DDC/CI. Ranges come from the adapter's own reported min/max.
public sealed class AmdColorService : IDisposable
{
    // ADL2 context creation is ~50 ms — caching it makes slider drags responsive.
    private IntPtr _ctx;
    private readonly object _ctxLock = new();
    private bool _disposed;

    public enum ColorKind
    {
        Brightness = 0,
        Contrast = 1,
        Saturation = 2,
        Hue = 3,
        Temperature = 4,
    }

    private const string AdlLib = "atiadlxx.dll";

    [DllImport(AdlLib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ADL2_Main_Control_Create(ADL_Main_Memory_Alloc callback, int enumConnectedAdapters, out IntPtr context);

    [DllImport(AdlLib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ADL2_Main_Control_Destroy(IntPtr context);

    [DllImport(AdlLib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ADL2_Adapter_NumberOfAdapters_Get(IntPtr context, out int numAdapters);

    [DllImport(AdlLib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ADL2_Display_Color_Get(
        IntPtr context, int adapterIndex, int displayIndex, int type,
        out int current, out int @default, out int min, out int max, out int step);

    [DllImport(AdlLib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ADL2_Display_Color_Set(
        IntPtr context, int adapterIndex, int displayIndex, int type, int current);

    // StdCall, not Cdecl: ADL's exports are __cdecl but ADL_MAIN_MALLOC_CALLBACK is
    // __stdcall [adl_sdk.h]. Cdecl here corrupts the stack on every ADL allocation.
    // See .claude/rules/30-display-apis.md.
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate IntPtr ADL_Main_Memory_Alloc(int size);

    private static IntPtr ADL_Alloc(int size) => Marshal.AllocHGlobal(size);

    public bool IsAvailable { get; }

    public AmdColorService()
    {
        try
        {
            if (ADL2_Main_Control_Create(ADL_Alloc, 1, out _ctx) == 0 && _ctx != IntPtr.Zero)
            {
                // Requires a real AMD adapter, not just any adapter ADL can see — it
                // enumerates NVIDIA ones too, so a count would enable this on a GeForce
                // machine and put dead saturation/hue sliders in the tuning page.
                IsAvailable = AdlDisplays.HasAmdAdapter(_ctx);
            }
        }
        catch (DllNotFoundException) { IsAvailable = false; }
        catch (EntryPointNotFoundException) { IsAvailable = false; }
        catch { IsAvailable = false; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_ctxLock)
        {
            if (_ctx != IntPtr.Zero)
            {
                try { ADL2_Main_Control_Destroy(_ctx); } catch { }
                _ctx = IntPtr.Zero;
            }
        }
    }

    public readonly record struct ColorRange(int Current, int Default, int Min, int Max, int Step);

    public ColorRange? GetColor(int adapterIndex, int displayIndex, ColorKind kind)
    {
        if (!IsAvailable || _disposed) return null;
        lock (_ctxLock) return GetColorCore(adapterIndex, displayIndex, kind);
    }

    /// <summary>Caller must hold <see cref="_ctxLock"/>.</summary>
    private ColorRange? GetColorCore(int adapterIndex, int displayIndex, ColorKind kind)
    {
        try
        {
            if (ADL2_Display_Color_Get(_ctx, adapterIndex, displayIndex, (int)kind,
                out int current, out int def, out int min, out int max, out int step) != 0)
                return null;
            return new ColorRange(current, def, min, max, step);
        }
        catch { return null; }
    }

    public bool SetColor(int adapterIndex, int displayIndex, ColorKind kind, int value)
    {
        if (!IsAvailable || _disposed) return false;
        try
        {
            lock (_ctxLock)
                return ADL2_Display_Color_Set(_ctx, adapterIndex, displayIndex, (int)kind, value) == 0;
        }
        catch { return false; }
    }

    // ── Device-name targeting (preferred) ──
    // ADL's (adapter, display) indices are unrelated to Windows' monitor order, so the
    // caller must name the monitor. Hardcoding (0, 0) drove the first monitor always.

    /// <summary>Reads a colour control for a specific monitor, by GDI device name.</summary>
    public ColorRange? GetColorByDeviceName(string gdiDeviceName, ColorKind kind)
    {
        if (!IsAvailable || _disposed) return null;

        lock (_ctxLock)
        {
            var target = AdlDisplays.Resolve(_ctx, gdiDeviceName);
            if (target == null) return null;
            return GetColorCore(target.Value.adapterIndex, target.Value.displayIndex, kind);
        }
    }

    /// <summary>Writes a colour control for a specific monitor, by GDI device name.</summary>
    public bool SetColorByDeviceName(string gdiDeviceName, ColorKind kind, int value)
    {
        if (!IsAvailable || _disposed) return false;

        lock (_ctxLock)
        {
            var target = AdlDisplays.Resolve(_ctx, gdiDeviceName);
            if (target == null) return false;

            try { return ADL2_Display_Color_Set(_ctx, target.Value.adapterIndex, target.Value.displayIndex, (int)kind, value) == 0; }
            catch { return false; }
        }
    }
}
