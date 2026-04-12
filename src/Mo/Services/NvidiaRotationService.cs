using Mo.Core.DisplayConfiguration;
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

    public bool ApplyFullProfile(DisplayProfile profile)
    {
        if (!IsAvailable) return false;

        try
        {
            // Get current NVAPI display config
            var currentPaths = PathInfo.GetDisplaysConfig();

            // Get all connected devices (active + inactive)
            var gpu = PhysicalGPU.GetPhysicalGPUs().FirstOrDefault();
            if (gpu == null) return false;

            var allConnected = gpu.GetConnectedDisplayDevices(ConnectedIdsFlag.None);

            // Build lookup: active displays by NVAPI DisplayId
            var activeTargets = new Dictionary<uint, PathTargetInfo>();
            var activePathsByTarget = new Dictionary<uint, PathInfo>();
            foreach (var path in currentPaths)
            {
                foreach (var target in path.TargetsInfo)
                {
                    activeTargets.TryAdd(target.DisplayDevice.DisplayId, target);
                    activePathsByTarget.TryAdd(target.DisplayDevice.DisplayId, path);
                }
            }

            // Match profile monitors to NVAPI displays using CCD identity bridge
            // CCD ALL_PATHS gives us (CCD TargetId → EDID/name), and we match EDID/name to profile monitors
            // Then we find which NVAPI DisplayDevice corresponds by checking active paths
            var ccdToNvapi = BuildCcdToNvapiMap(allConnected, activeTargets);

            var newPaths = new List<PathInfo>();
            var usedDisplayIds = new HashSet<uint>();

            foreach (var pm in profile.Monitors)
            {
                if (!pm.IsEnabled) continue;

                // Find NVAPI display for this profile monitor
                DisplayDevice? device = null;

                // Try matching via CCD bridge
                foreach (var (ccdTargetId, nvapiDisplayId) in ccdToNvapi)
                {
                    if (usedDisplayIds.Contains(nvapiDisplayId)) continue;

                    // Check if this CCD target matches the profile monitor
                    var ccdInfo = GetCcdTargetInfo(ccdTargetId);
                    if (ccdInfo == null) continue;

                    bool nameMatch = !string.IsNullOrEmpty(pm.FriendlyName) &&
                                     !string.IsNullOrEmpty(ccdInfo.Value.name) &&
                                     ccdInfo.Value.name.Contains(pm.FriendlyName, StringComparison.OrdinalIgnoreCase);
                    bool edidMatch = pm.EdidManufacturerId != 0 &&
                                     ccdInfo.Value.mfrId == pm.EdidManufacturerId &&
                                     ccdInfo.Value.prodId == pm.EdidProductCodeId;
                    bool pathMatch = !string.IsNullOrEmpty(pm.DevicePath) &&
                                     pm.DevicePath == ccdInfo.Value.devicePath;

                    if (pathMatch || edidMatch || nameMatch)
                    {
                        // Find the DisplayDevice with this NVAPI ID
                        foreach (var d in allConnected)
                        {
                            if (d.DisplayId == nvapiDisplayId)
                            { device = d; break; }
                        }
                        if (device != null)
                        {
                            usedDisplayIds.Add(nvapiDisplayId);
                            break;
                        }
                    }
                }

                if (device == null) continue;

                var targetInfo = new PathTargetInfo(device);
                targetInfo.Rotation = pm.Rotation switch
                {
                    DisplayRotation.Rotate90 => Rotate.Degree90,
                    DisplayRotation.Rotate180 => Rotate.Degree180,
                    DisplayRotation.Rotate270 => Rotate.Degree270,
                    _ => Rotate.Degree0,
                };

                // Use native (unrotated) resolution for NVAPI
                var w = pm.Width;
                var h = pm.Height;
                if (pm.Rotation is DisplayRotation.Rotate90 or DisplayRotation.Rotate270)
                {
                    if (w < h) (w, h) = (h, w);
                }

                newPaths.Add(new PathInfo(
                    new NvAPIWrapper.Native.Display.Structures.Resolution(w, h, 32),
                    ColorFormat.A8R8G8B8,
                    [targetInfo]));
            }

            // Keep unmatched active monitors if UnmatchedAction == Keep
            if (profile.UnmatchedAction == UnmatchedMonitorAction.Keep)
            {
                foreach (var path in currentPaths)
                {
                    foreach (var target in path.TargetsInfo)
                    {
                        if (!usedDisplayIds.Contains(target.DisplayDevice.DisplayId))
                        {
                            newPaths.Add(path);
                            usedDisplayIds.Add(target.DisplayDevice.DisplayId);
                        }
                    }
                }
            }

            if (newPaths.Count == 0) return false;

            PathInfo.SetDisplaysConfig(newPaths.ToArray(), DisplayConfigFlags.DriverReloadAllowed);
            return true;
        }
        catch { return false; }
    }

    public bool ApplyRotation(MonitorInfo monitor, DisplayRotation rotation)
    {
        if (!IsAvailable) return false;

        try
        {
            var currentPaths = PathInfo.GetDisplaysConfig();
            var ccdToNvapi = BuildCcdToNvapiMap(
                PhysicalGPU.GetPhysicalGPUs().First().GetConnectedDisplayDevices(ConnectedIdsFlag.None),
                currentPaths.SelectMany(p => p.TargetsInfo).ToDictionary(t => t.DisplayDevice.DisplayId, t => t));

            uint? targetNvapiId = null;
            foreach (var (ccdTargetId, nvapiId) in ccdToNvapi)
            {
                var info = GetCcdTargetInfo(ccdTargetId);
                if (info == null) continue;
                if (info.Value.devicePath == monitor.DevicePath ||
                    (monitor.EdidManufacturerId != 0 && info.Value.mfrId == monitor.EdidManufacturerId && info.Value.prodId == monitor.EdidProductCodeId))
                {
                    targetNvapiId = nvapiId;
                    break;
                }
            }

            if (targetNvapiId == null) return false;

            bool modified = false;
            foreach (var path in currentPaths)
            {
                foreach (var target in path.TargetsInfo)
                {
                    if (target.DisplayDevice.DisplayId != targetNvapiId) continue;
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

    // Bridge between CCD target IDs and NVAPI DisplayIds
    // Uses the adapter LUID + source ID overlap between CCD and NVAPI
    private static Dictionary<uint, uint> BuildCcdToNvapiMap(
        DisplayDevice[] allConnected, Dictionary<uint, PathTargetInfo> activeTargets)
    {
        var map = new Dictionary<uint, uint>();
        try
        {
            int r = NativeDisplayApi.GetDisplayConfigBufferSizes(
                QDC_FLAGS.QDC_ALL_PATHS, out uint pc, out uint mc);
            if (r != NativeDisplayApi.ERROR_SUCCESS) return map;

            var paths = new DISPLAYCONFIG_PATH_INFO[pc];
            var modes = new DISPLAYCONFIG_MODE_INFO[mc];
            r = NativeDisplayApi.QueryDisplayConfig(
                QDC_FLAGS.QDC_ALL_PATHS, ref pc, paths, ref mc, modes, IntPtr.Zero);
            if (r != NativeDisplayApi.ERROR_SUCCESS) return map;

            // For each CCD target, try to find the NVAPI display
            // Strategy: match by iterating connected devices in order
            // (CCD and NVAPI typically enumerate in the same order)
            var ccdTargets = new List<uint>();
            var seen = new HashSet<uint>();
            for (int i = 0; i < pc; i++)
            {
                var tid = paths[i].targetInfo.id;
                if (seen.Add(tid))
                    ccdTargets.Add(tid);
            }

            // Simple order-based mapping: CCD targets and NVAPI connected devices
            // are typically enumerated in the same order by the driver
            for (int i = 0; i < Math.Min(ccdTargets.Count, allConnected.Length); i++)
            {
                map[ccdTargets[i]] = allConnected[i].DisplayId;
            }
        }
        catch { }
        return map;
    }

    private static (string devicePath, ushort mfrId, ushort prodId, uint connector, string name)? GetCcdTargetInfo(uint ccdTargetId)
    {
        try
        {
            int r = NativeDisplayApi.GetDisplayConfigBufferSizes(
                QDC_FLAGS.QDC_ALL_PATHS, out uint pc, out uint mc);
            if (r != NativeDisplayApi.ERROR_SUCCESS) return null;

            var paths = new DISPLAYCONFIG_PATH_INFO[pc];
            var modes = new DISPLAYCONFIG_MODE_INFO[mc];
            r = NativeDisplayApi.QueryDisplayConfig(
                QDC_FLAGS.QDC_ALL_PATHS, ref pc, paths, ref mc, modes, IntPtr.Zero);
            if (r != NativeDisplayApi.ERROR_SUCCESS) return null;

            for (int i = 0; i < pc; i++)
            {
                if (paths[i].targetInfo.id != ccdTargetId) continue;

                var dn = new DISPLAYCONFIG_TARGET_DEVICE_NAME();
                dn.header.type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME;
                dn.header.size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>();
                dn.header.adapterId = paths[i].targetInfo.adapterId;
                dn.header.id = ccdTargetId;
                if (NativeDisplayApi.DisplayConfigGetDeviceInfo(ref dn) == NativeDisplayApi.ERROR_SUCCESS)
                    return (dn.monitorDevicePath ?? "", dn.edidManufactureId, dn.edidProductCodeId, dn.connectorInstance, dn.monitorFriendlyDeviceName ?? "");
                break;
            }
        }
        catch { }
        return null;
    }
}
