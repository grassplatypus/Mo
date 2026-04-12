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
            foreach (var gpu in PhysicalGPU.GetPhysicalGPUs())
            {
                var allConnected = gpu.GetConnectedDisplayDevices(ConnectedIdsFlag.None);
                if (allConnected.Length <= 1) continue;

                var currentPaths = PathInfo.GetDisplaysConfig();
                var activeIds = new HashSet<uint>(
                    currentPaths.SelectMany(p => p.TargetsInfo).Select(t => t.DisplayDevice.DisplayId));

                // Find inactive but connected displays
                var inactiveDevices = allConnected.Where(d => !activeIds.Contains(d.DisplayId)).ToList();
                if (inactiveDevices.Count == 0) continue;

                // Build new path list: keep existing paths + add paths for inactive displays
                var newPaths = new List<PathInfo>(currentPaths);
                foreach (var device in inactiveDevices)
                {
                    try
                    {
                        var target = new PathTargetInfo(device);
                        var path = new PathInfo(
                            new NvAPIWrapper.Native.Display.Structures.Resolution(0, 0, 32),
                            NvAPIWrapper.Native.Display.ColorFormat.A8R8G8B8,
                            [target]);
                        newPaths.Add(path);
                    }
                    catch { continue; }
                }

                PathInfo.SetDisplaysConfig(
                    newPaths.ToArray(),
                    DisplayConfigFlags.ForceCommitVideoPresentNetwork | DisplayConfigFlags.DriverReloadAllowed);
                return true;
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
