using System.Runtime.InteropServices;
using Mo.Models;

namespace Mo.Services;

public sealed class IntelRotationService
{
    public bool IsAvailable { get; }

    private const string IgclLib = "ControlLib.dll";

    // ── IGCL P/Invoke ──

    [DllImport(IgclLib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ctlInit(ref CtlInitArgs args, out IntPtr apiHandle);

    [DllImport(IgclLib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ctlClose(IntPtr apiHandle);

    [DllImport(IgclLib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ctlEnumerateDevices(IntPtr apiHandle, ref uint count, IntPtr[] devices);

    [DllImport(IgclLib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ctlEnumerateDisplayOutputs(IntPtr deviceHandle, ref uint count, IntPtr[] displays);

    [DllImport(IgclLib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ctlGetDisplayProperties(IntPtr displayHandle, ref CtlDisplayProperties properties);

    [DllImport(IgclLib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ctlSetDisplayProperties(IntPtr displayHandle, ref CtlDisplayProperties properties);

    // ── Structs ──

    [StructLayout(LayoutKind.Sequential)]
    private struct CtlInitArgs
    {
        public uint Size;
        public byte Version;
        public uint AppVersion;
        public uint Flags;
        public ulong SupportedVersion;
    }

    private enum CtlDisplayOrientation : uint
    {
        Degree0 = 0,
        Degree90 = 1,
        Degree180 = 2,
        Degree270 = 3,
        Max = 4,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CtlDisplayProperties
    {
        public uint Size;
        public byte Version;
        public uint OSDisplayEncoderHandle;
        public uint Type;
        public uint AttachedFlag;
        public CtlDisplayOrientation Orientation;
        // Remaining fields are not needed for rotation
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
        public byte[] Reserved;
    }

    private const int CTL_RESULT_SUCCESS = 0;

    public IntelRotationService()
    {
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
            if (result == CTL_RESULT_SUCCESS && handle != IntPtr.Zero)
            {
                // Verify at least one device exists
                uint count = 0;
                ctlEnumerateDevices(handle, ref count, null!);
                IsAvailable = count > 0;
                ctlClose(handle);
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
            var args = new CtlInitArgs
            {
                Size = (uint)Marshal.SizeOf<CtlInitArgs>(),
                Version = 0,
                AppVersion = 0x01000000,
            };
            int result = ctlInit(ref args, out IntPtr apiHandle);
            if (result != CTL_RESULT_SUCCESS) return false;

            try
            {
                // Enumerate devices
                uint deviceCount = 0;
                ctlEnumerateDevices(apiHandle, ref deviceCount, null!);
                if (deviceCount == 0) return false;

                var devices = new IntPtr[deviceCount];
                result = ctlEnumerateDevices(apiHandle, ref deviceCount, devices);
                if (result != CTL_RESULT_SUCCESS) return false;

                foreach (var device in devices)
                {
                    // Enumerate displays on this device
                    uint displayCount = 0;
                    ctlEnumerateDisplayOutputs(device, ref displayCount, null!);
                    if (displayCount == 0) continue;

                    var displays = new IntPtr[displayCount];
                    result = ctlEnumerateDisplayOutputs(device, ref displayCount, displays);
                    if (result != CTL_RESULT_SUCCESS) continue;

                    foreach (var display in displays)
                    {
                        var props = new CtlDisplayProperties
                        {
                            Size = (uint)Marshal.SizeOf<CtlDisplayProperties>(),
                            Version = 0,
                            Reserved = new byte[256],
                        };

                        result = ctlGetDisplayProperties(display, ref props);
                        if (result != CTL_RESULT_SUCCESS) continue;
                        if (props.AttachedFlag == 0) continue;

                        props.Orientation = rotation switch
                        {
                            DisplayRotation.Rotate90 => CtlDisplayOrientation.Degree90,
                            DisplayRotation.Rotate180 => CtlDisplayOrientation.Degree180,
                            DisplayRotation.Rotate270 => CtlDisplayOrientation.Degree270,
                            _ => CtlDisplayOrientation.Degree0,
                        };

                        result = ctlSetDisplayProperties(display, ref props);
                        if (result == CTL_RESULT_SUCCESS)
                            return true;
                    }
                }
            }
            finally
            {
                ctlClose(apiHandle);
            }
        }
        catch { }

        return false;
    }
}
