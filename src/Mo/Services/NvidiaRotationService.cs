using Mo.Interop.DisplayConfig;
using Mo.Models;
using NvAPIWrapper;
using NvAPIWrapper.Display;
using NvAPIWrapper.GPU;
using NvAPIWrapper.Native.Display;
using NvAPIWrapper.Native.GPU;

namespace Mo.Services;

public sealed class NvidiaRotationService
{
    public bool IsAvailable { get; }

    public NvidiaRotationService()
    {
        try
        {
            NVIDIA.Initialize();
            IsAvailable = PhysicalGPU.GetPhysicalGPUs().Length > 0;
        }
        catch
        {
            IsAvailable = false;
        }
    }

    public bool ApplyRotation(MonitorInfo monitor, DisplayRotation rotation)
    {
        if (!IsAvailable) return false;

        try
        {
            // Get current display config and modify only the rotation for the target monitor
            var currentPaths = PathInfo.GetDisplaysConfig();
            bool modified = false;

            foreach (var path in currentPaths)
            {
                foreach (var target in path.TargetsInfo)
                {
                    if (!MatchesMonitor(target.DisplayDevice, monitor)) continue;

                    target.Rotation = rotation switch
                    {
                        DisplayRotation.Rotate90 => Rotate.Degree90,
                        DisplayRotation.Rotate180 => Rotate.Degree180,
                        DisplayRotation.Rotate270 => Rotate.Degree270,
                        _ => Rotate.Degree0,
                    };
                    modified = true;
                }
            }

            if (!modified) return false;

            PathInfo.SetDisplaysConfig(currentPaths, DisplayConfigFlags.None);
            return true;
        }
        catch { return false; }
    }

    public bool EnableAllDisplays()
    {
        if (!IsAvailable) return false;
        try
        {
            // Get all physical GPUs and enable all connected displays
            foreach (var gpu in PhysicalGPU.GetPhysicalGPUs())
            {
                var allDisplayIds = gpu.GetConnectedDisplayDevices(ConnectedIdsFlag.None);
                if (allDisplayIds.Length <= 1) continue;

                // Build paths with all connected displays enabled
                var currentPaths = PathInfo.GetDisplaysConfig();
                var activeDeviceIds = new HashSet<uint>(
                    currentPaths.SelectMany(p => p.TargetsInfo).Select(t => t.DisplayDevice.DisplayId));

                bool anyNew = false;
                foreach (var displayId in allDisplayIds)
                {
                    if (!activeDeviceIds.Contains(displayId.DisplayId))
                    {
                        anyNew = true;
                        break;
                    }
                }

                if (anyNew)
                {
                    // Use CCD topology extend as NVAPI path manipulation for enabling is complex
                    NativeDisplayApi.SetDisplayConfig(0, null, 0, null,
                        Interop.DisplayConfig.SDC_FLAGS.SDC_TOPOLOGY_EXTEND | Interop.DisplayConfig.SDC_FLAGS.SDC_APPLY |
                        Interop.DisplayConfig.SDC_FLAGS.SDC_ALLOW_CHANGES | Interop.DisplayConfig.SDC_FLAGS.SDC_SAVE_TO_DATABASE);
                    return true;
                }
            }
        }
        catch { }
        return false;
    }

    private static bool MatchesMonitor(DisplayDevice device, MonitorInfo monitor)
    {
        try
        {
            // Match by target ID (most reliable in NVAPI context)
            if (device.DisplayId != 0 && monitor.TargetId != 0 &&
                device.DisplayId == monitor.TargetId)
                return true;
        }
        catch { }

        return false;
    }
}
