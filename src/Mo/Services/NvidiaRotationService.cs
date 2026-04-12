using System.Runtime.InteropServices;
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
            var gpu = PhysicalGPU.GetPhysicalGPUs().FirstOrDefault();
            if (gpu == null) return false;

            var allConnected = gpu.GetConnectedDisplayDevices(ConnectedIdsFlag.UnCached);
            var currentPaths = PathInfo.GetDisplaysConfig();

            // Build NVAPI DisplayId → CCD EDID map via GDI device name bridge
            var nvapiToCcdEdid = BuildNvapiToEdidMap(allConnected, currentPaths);

            var newPaths = new List<PathInfo>();
            var usedDisplayIds = new HashSet<uint>();

            foreach (var pm in profile.Monitors)
            {
                if (!pm.IsEnabled) continue;

                DisplayDevice? matchedDevice = null;

                foreach (var device in allConnected)
                {
                    if (usedDisplayIds.Contains(device.DisplayId)) continue;

                    if (nvapiToCcdEdid.TryGetValue(device.DisplayId, out var edid))
                    {
                        bool pathMatch = !string.IsNullOrEmpty(pm.DevicePath) &&
                                         pm.DevicePath == edid.devicePath;
                        bool edidMatch = pm.EdidManufacturerId != 0 &&
                                         edid.mfrId == pm.EdidManufacturerId &&
                                         edid.prodId == pm.EdidProductCodeId;
                        bool nameMatch = !string.IsNullOrEmpty(pm.FriendlyName) &&
                                         !string.IsNullOrEmpty(edid.name) &&
                                         edid.name.Contains(pm.FriendlyName, StringComparison.OrdinalIgnoreCase);

                        if (pathMatch || edidMatch || nameMatch)
                        {
                            matchedDevice = device;
                            break;
                        }
                    }
                }

                if (matchedDevice == null) continue;
                usedDisplayIds.Add(matchedDevice.DisplayId);

                var targetInfo = new PathTargetInfo(matchedDevice);
                targetInfo.Rotation = pm.Rotation switch
                {
                    DisplayRotation.Rotate90 => Rotate.Degree90,
                    DisplayRotation.Rotate180 => Rotate.Degree180,
                    DisplayRotation.Rotate270 => Rotate.Degree270,
                    _ => Rotate.Degree0,
                };

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
            var gpu = PhysicalGPU.GetPhysicalGPUs().FirstOrDefault();
            if (gpu == null) return false;

            var allConnected = gpu.GetConnectedDisplayDevices(ConnectedIdsFlag.UnCached);
            var currentPaths = PathInfo.GetDisplaysConfig();
            var nvapiToCcdEdid = BuildNvapiToEdidMap(allConnected, currentPaths);

            uint? targetNvapiId = null;
            foreach (var (nvapiId, edid) in nvapiToCcdEdid)
            {
                if (edid.devicePath == monitor.DevicePath ||
                    (monitor.EdidManufacturerId != 0 && edid.mfrId == monitor.EdidManufacturerId && edid.prodId == monitor.EdidProductCodeId))
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

    /// <summary>
    /// Maps NVAPI DisplayId → CCD EDID info using GDI device name as bridge.
    /// Active displays: CCD source → GDI name (\\.\DISPLAY1) → NVAPI Display.Name
    /// Inactive displays: matched by exclusion after active matching
    /// </summary>
    private static Dictionary<uint, (string devicePath, ushort mfrId, ushort prodId, uint connector, string name)>
        BuildNvapiToEdidMap(DisplayDevice[] allConnected, PathInfo[] currentPaths)
    {
        var map = new Dictionary<uint, (string, ushort, ushort, uint, string)>();
        try
        {
            // Step 1: Get all CCD target info (EDID)
            int r = NativeDisplayApi.GetDisplayConfigBufferSizes(
                QDC_FLAGS.QDC_ALL_PATHS, out uint pc, out uint mc);
            if (r != NativeDisplayApi.ERROR_SUCCESS) return map;

            var paths = new DISPLAYCONFIG_PATH_INFO[pc];
            var modes = new DISPLAYCONFIG_MODE_INFO[mc];
            r = NativeDisplayApi.QueryDisplayConfig(QDC_FLAGS.QDC_ALL_PATHS, ref pc, paths, ref mc, modes, IntPtr.Zero);
            if (r != NativeDisplayApi.ERROR_SUCCESS) return map;

            // Step 2: Build CCD source GDI name → target EDID map (for active paths)
            var gdiToEdid = new Dictionary<string, (uint targetId, string devicePath, ushort mfrId, ushort prodId, uint connector, string name)>();

            // Get active paths for GDI name lookup
            NativeDisplayApi.GetDisplayConfigBufferSizes(QDC_FLAGS.QDC_ONLY_ACTIVE_PATHS, out uint apc, out uint amc);
            var aPaths = new DISPLAYCONFIG_PATH_INFO[apc];
            var aModes = new DISPLAYCONFIG_MODE_INFO[amc];
            NativeDisplayApi.QueryDisplayConfig(QDC_FLAGS.QDC_ONLY_ACTIVE_PATHS, ref apc, aPaths, ref amc, aModes, IntPtr.Zero);

            for (int i = 0; i < apc; i++)
            {
                // Get GDI device name for this source
                var srcName = new DISPLAYCONFIG_SOURCE_DEVICE_NAME();
                srcName.header.type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;
                srcName.header.size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>();
                srcName.header.adapterId = aPaths[i].sourceInfo.adapterId;
                srcName.header.id = aPaths[i].sourceInfo.id;

                if (NativeDisplayApi.DisplayConfigGetDeviceInfo(ref srcName) != NativeDisplayApi.ERROR_SUCCESS)
                    continue;

                // Get target EDID for this path
                var tgtName = new DISPLAYCONFIG_TARGET_DEVICE_NAME();
                tgtName.header.type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME;
                tgtName.header.size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>();
                tgtName.header.adapterId = aPaths[i].targetInfo.adapterId;
                tgtName.header.id = aPaths[i].targetInfo.id;

                if (NativeDisplayApi.DisplayConfigGetDeviceInfo(ref tgtName) != NativeDisplayApi.ERROR_SUCCESS)
                    continue;

                var gdiName = srcName.viewGdiDeviceName?.TrimEnd('\0') ?? "";
                if (!string.IsNullOrEmpty(gdiName))
                {
                    gdiToEdid[gdiName] = (aPaths[i].targetInfo.id,
                        tgtName.monitorDevicePath ?? "", tgtName.edidManufactureId,
                        tgtName.edidProductCodeId, tgtName.connectorInstance,
                        tgtName.monitorFriendlyDeviceName ?? "");
                }
            }

            // Step 3: Match NVAPI active displays by GDI name
            var matchedCcdTargetIds = new HashSet<uint>();
            var nvapiDisplays = NvAPIWrapper.Display.Display.GetDisplays();
            foreach (var display in nvapiDisplays)
            {
                var nvapiName = display.Name?.TrimEnd('\0') ?? "";
                if (gdiToEdid.TryGetValue(nvapiName, out var edid))
                {
                    map[display.DisplayDevice.DisplayId] = (edid.devicePath, edid.mfrId, edid.prodId, edid.connector, edid.name);
                    matchedCcdTargetIds.Add(edid.targetId);
                }
            }

            // Step 4: Match remaining (inactive) by exclusion
            // Get all CCD targets with EDID
            var allCcdEdids = new List<(uint targetId, string devicePath, ushort mfrId, ushort prodId, uint connector, string name)>();
            var seenTargets = new HashSet<uint>();
            for (int i = 0; i < pc; i++)
            {
                var tid = paths[i].targetInfo.id;
                if (!seenTargets.Add(tid)) continue;

                var dn = new DISPLAYCONFIG_TARGET_DEVICE_NAME();
                dn.header.type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME;
                dn.header.size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>();
                dn.header.adapterId = paths[i].targetInfo.adapterId;
                dn.header.id = tid;

                if (NativeDisplayApi.DisplayConfigGetDeviceInfo(ref dn) == NativeDisplayApi.ERROR_SUCCESS &&
                    !string.IsNullOrEmpty(dn.monitorDevicePath))
                {
                    allCcdEdids.Add((tid, dn.monitorDevicePath, dn.edidManufactureId,
                        dn.edidProductCodeId, dn.connectorInstance, dn.monitorFriendlyDeviceName ?? ""));
                }
            }

            // Filter to only real monitors (non-zero EDID manufacturer)
            var unmatchedCcdEdids = allCcdEdids
                .Where(e => !matchedCcdTargetIds.Contains(e.targetId) && e.mfrId != 0)
                .ToList();
            var unmatchedNvapiDevices = allConnected.Where(d => !map.ContainsKey(d.DisplayId)).ToList();

            // Match remaining NVAPI devices to CCD targets by EDID
            foreach (var nvapiDevice in unmatchedNvapiDevices)
            {
                if (unmatchedCcdEdids.Count == 0) break;
                // With only real monitors left, match 1:1
                if (unmatchedCcdEdids.Count == 1 && unmatchedNvapiDevices.Count == 1)
                {
                    var e = unmatchedCcdEdids[0];
                    map[nvapiDevice.DisplayId] = (e.devicePath, e.mfrId, e.prodId, e.connector, e.name);
                    break;
                }
                // Multiple remaining: try matching by unique EDID values
                foreach (var ccd in unmatchedCcdEdids)
                {
                    // Each unique EDID should correspond to one physical monitor
                    if (!map.Values.Any(v => v.Item2 == ccd.mfrId && v.Item3 == ccd.prodId))
                    {
                        map[nvapiDevice.DisplayId] = (ccd.devicePath, ccd.mfrId, ccd.prodId, ccd.connector, ccd.name);
                        unmatchedCcdEdids.Remove(ccd);
                        break;
                    }
                }
            }
        }
        catch { }
        return map;
    }
}
