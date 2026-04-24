using System.Runtime.InteropServices;

namespace Mo.Services;

// Brightness / contrast / saturation / hue / color-temperature via ADL2 (AMD Display
// Library). Runs alongside DDC/CI — for AMD GPUs these adjust the display pipeline
// on the GPU side, so they work even for monitors that don't expose DDC/CI.
//
// Reference: AMD Display Library (ADL) SDK, specifically ADL2_Display_Color_Get/Set.
//   type = 0 brightness, 1 contrast, 2 saturation, 3 hue, 4 temperature.
// Sliders accept the range the adapter reports via ADL_Display_Color_Get(... min/max).
public sealed class AmdColorService
{
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

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr ADL_Main_Memory_Alloc(int size);

    private static IntPtr ADL_Alloc(int size) => Marshal.AllocHGlobal(size);

    public bool IsAvailable { get; }

    public AmdColorService()
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

    public readonly record struct ColorRange(int Current, int Default, int Min, int Max, int Step);

    public ColorRange? GetColor(int adapterIndex, int displayIndex, ColorKind kind)
    {
        if (!IsAvailable) return null;
        var ctx = IntPtr.Zero;
        try
        {
            if (ADL2_Main_Control_Create(ADL_Alloc, 1, out ctx) != 0 || ctx == IntPtr.Zero)
                return null;

            if (ADL2_Display_Color_Get(ctx, adapterIndex, displayIndex, (int)kind,
                out int current, out int def, out int min, out int max, out int step) != 0)
                return null;

            return new ColorRange(current, def, min, max, step);
        }
        catch { return null; }
        finally
        {
            if (ctx != IntPtr.Zero) ADL2_Main_Control_Destroy(ctx);
        }
    }

    public bool SetColor(int adapterIndex, int displayIndex, ColorKind kind, int value)
    {
        if (!IsAvailable) return false;
        var ctx = IntPtr.Zero;
        try
        {
            if (ADL2_Main_Control_Create(ADL_Alloc, 1, out ctx) != 0 || ctx == IntPtr.Zero)
                return false;

            return ADL2_Display_Color_Set(ctx, adapterIndex, displayIndex, (int)kind, value) == 0;
        }
        catch { return false; }
        finally
        {
            if (ctx != IntPtr.Zero) ADL2_Main_Control_Destroy(ctx);
        }
    }

    // Best-effort helper for UIs that don't know the adapter/display index layout —
    // walks every adapter/display 0 pair and applies the first that accepts the value.
    public bool SetColorFirstAvailable(ColorKind kind, int value)
    {
        if (!IsAvailable) return false;
        var ctx = IntPtr.Zero;
        try
        {
            if (ADL2_Main_Control_Create(ADL_Alloc, 1, out ctx) != 0 || ctx == IntPtr.Zero)
                return false;

            ADL2_Adapter_NumberOfAdapters_Get(ctx, out int numAdapters);
            for (int adapter = 0; adapter < numAdapters; adapter++)
            {
                if (ADL2_Display_Color_Set(ctx, adapter, 0, (int)kind, value) == 0)
                    return true;
            }
            return false;
        }
        catch { return false; }
        finally
        {
            if (ctx != IntPtr.Zero) ADL2_Main_Control_Destroy(ctx);
        }
    }
}
