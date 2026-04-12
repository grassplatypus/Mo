using System.Runtime.InteropServices;
using Mo.Models;

namespace Mo.Services;

public sealed class IntelRotationService
{
    public bool IsAvailable { get; }

    private const string IgclLib = "ControlLib.dll";

    [DllImport(IgclLib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ctlInit(ref CtlInitArgs args, out IntPtr apiHandle);

    [DllImport(IgclLib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ctlClose(IntPtr apiHandle);

    [DllImport(IgclLib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ctlEnumerateDevices(IntPtr apiHandle, out uint count, IntPtr devices);

    [StructLayout(LayoutKind.Sequential)]
    private struct CtlInitArgs
    {
        public uint Size;
        public byte Version;
        public uint AppVersion;
        public uint Flags;
    }

    public IntelRotationService()
    {
        // Intel IGCL is only available on systems with Intel GPUs and recent drivers
        try
        {
            var args = new CtlInitArgs
            {
                Size = (uint)Marshal.SizeOf<CtlInitArgs>(),
                Version = 0,
                AppVersion = 0x01000000,
                Flags = 0,
            };
            int result = ctlInit(ref args, out IntPtr handle);
            if (result == 0 && handle != IntPtr.Zero)
            {
                IsAvailable = true;
                ctlClose(handle);
            }
        }
        catch (DllNotFoundException) { IsAvailable = false; }
        catch { IsAvailable = false; }
    }

    public bool ApplyRotation(MonitorInfo monitor, DisplayRotation rotation)
    {
        // Intel IGCL rotation requires complex display property manipulation
        // with ctlGetDisplayProperties / ctlSetDisplayProperties.
        // The rotation field is in the display properties structure.
        // Full implementation requires significant struct definitions.
        // For now, return false to fall back to CCD API.
        if (!IsAvailable) return false;

        // TODO: Implement when Intel IGCL .NET bindings mature
        return false;
    }
}
