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

    private static void Log(string msg)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mo", "logs");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "nvapi_debug.log"),
                $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n");
        }
        catch { }
    }

    // Cache of NVAPI PathInfo per display (saved when all monitors are active)
    private static readonly Dictionary<uint, PathInfo> _pathCache = new();

    public bool ApplyFullProfile(DisplayProfile profile)
    {
        if (!IsAvailable) { Log("NVAPI not available"); return false; }

        try
        {
            var gpu = PhysicalGPU.GetPhysicalGPUs().FirstOrDefault();
            if (gpu == null) { Log("No GPU found"); return false; }

            var allConnected = gpu.GetConnectedDisplayDevices(ConnectedIdsFlag.UnCached);
            Log($"Connected devices: {allConnected.Length}");
            foreach (var d in allConnected)
                Log($"  DisplayId={d.DisplayId} IsActive={d.IsActive} IsAvailable={d.IsAvailable}");

            var currentPaths = PathInfo.GetDisplaysConfig();
            Log($"Current NVAPI paths: {currentPaths.Length}");

            // Cache all active paths for later re-activation
            foreach (var path in currentPaths)
                foreach (var target in path.TargetsInfo)
                    _pathCache[target.DisplayDevice.DisplayId] = path;

            var nvapiToCcdEdid = BuildNvapiToEdidMap(allConnected, currentPaths);
            Log($"NVAPI→CCD map entries: {nvapiToCcdEdid.Count}");
            foreach (var (id, edid) in nvapiToCcdEdid)
                Log($"  NvapiId={id} → name={edid.Item5} mfr=0x{edid.Item2:X4} prod=0x{edid.Item3:X4}");

            // If profile needs MORE monitors than currently active, try to restore from cache
            int enabledInProfile = profile.Monitors.Count(m => m.IsEnabled);
            if (enabledInProfile > currentPaths.Length)
            {
                Log($"Need {enabledInProfile} monitors, have {currentPaths.Length} active.");

                // Try restoring inactive monitors from cached PathInfo
                var restoredPaths = new List<PathInfo>(currentPaths);
                foreach (var device in allConnected)
                {
                    if (device.IsActive) continue;
                    if (_pathCache.TryGetValue(device.DisplayId, out var cachedPath))
                    {
                        Log($"  Restoring cached path for DisplayId={device.DisplayId}");
                        restoredPaths.Add(cachedPath);
                    }
                }

                if (restoredPaths.Count > currentPaths.Length)
                {
                    try
                    {
                        PathInfo.SetDisplaysConfig(restoredPaths.ToArray(), DisplayConfigFlags.DriverReloadAllowed);
                        Thread.Sleep(1000);
                        currentPaths = PathInfo.GetDisplaysConfig();
                        Log($"After restore: {currentPaths.Length} paths");
                    }
                    catch (Exception ex)
                    {
                        Log($"Restore failed: {ex.Message}");
                    }
                }

                // Fallback: CCD topology extend
                if (currentPaths.Length < enabledInProfile)
                {
                    Log("Trying CCD topology extend...");
                    NativeDisplayApi.SetDisplayConfig(0, null, 0, null,
                        Interop.DisplayConfig.SDC_FLAGS.SDC_TOPOLOGY_EXTEND |
                        Interop.DisplayConfig.SDC_FLAGS.SDC_APPLY |
                        Interop.DisplayConfig.SDC_FLAGS.SDC_ALLOW_CHANGES |
                        Interop.DisplayConfig.SDC_FLAGS.SDC_SAVE_TO_DATABASE);
                    Thread.Sleep(1500);
                    currentPaths = PathInfo.GetDisplaysConfig();
                    Log($"After CCD extend: {currentPaths.Length} paths");
                }
            }

            // Modify existing paths in-place
            bool modified = false;
            var usedDisplayIds = new HashSet<uint>();

            foreach (var pm in profile.Monitors)
            {
                if (!pm.IsEnabled) continue;

                // Find NVAPI display for this profile monitor
                uint? matchedNvapiId = null;
                foreach (var device in allConnected)
                {
                    if (usedDisplayIds.Contains(device.DisplayId)) continue;
                    if (!nvapiToCcdEdid.TryGetValue(device.DisplayId, out var edid)) continue;

                    bool match = (!string.IsNullOrEmpty(pm.DevicePath) && pm.DevicePath == edid.devicePath) ||
                                 (pm.EdidManufacturerId != 0 && edid.mfrId == pm.EdidManufacturerId && edid.prodId == pm.EdidProductCodeId) ||
                                 (!string.IsNullOrEmpty(pm.FriendlyName) && !string.IsNullOrEmpty(edid.name) && edid.name.Contains(pm.FriendlyName, StringComparison.OrdinalIgnoreCase));

                    if (match) { matchedNvapiId = device.DisplayId; break; }
                }

                if (matchedNvapiId == null)
                {
                    Log($"  NO MATCH for: {pm.FriendlyName}");
                    continue;
                }
                Log($"  MATCHED: {pm.FriendlyName} → NvapiId={matchedNvapiId}");
                usedDisplayIds.Add(matchedNvapiId.Value);

                // Find and modify the existing path for this display
                foreach (var path in currentPaths)
                {
                    foreach (var target in path.TargetsInfo)
                    {
                        if (target.DisplayDevice.DisplayId != matchedNvapiId) continue;
                        var newRot = pm.Rotation switch
                        {
                            DisplayRotation.Rotate90 => Rotate.Degree90,
                            DisplayRotation.Rotate180 => Rotate.Degree180,
                            DisplayRotation.Rotate270 => Rotate.Degree270,
                            _ => Rotate.Degree0,
                        };
                        Log($"    Rotation: {target.Rotation} → {newRot}");
                        target.Rotation = newRot;
                        modified = true;
                    }
                }
            }

            // Remove paths for monitors that should be disabled
            // Auto-disable if profile has fewer enabled monitors than currently active
            bool shouldDisableUnmatched = profile.UnmatchedAction == UnmatchedMonitorAction.Disable ||
                profile.Monitors.Count(m => m.IsEnabled) < currentPaths.Length;

            var finalPaths = currentPaths.AsEnumerable();
            if (shouldDisableUnmatched)
            {
                finalPaths = currentPaths.Where(path =>
                    path.TargetsInfo.Any(t => usedDisplayIds.Contains(t.DisplayDevice.DisplayId)));
                Log($"Disable unmatched: keeping {finalPaths.Count()} of {currentPaths.Length} paths");
            }

            // Also remove paths for profile monitors with IsEnabled=false
            var disabledProfileDisplayIds = new HashSet<uint>();
            foreach (var pm in profile.Monitors.Where(m => !m.IsEnabled))
            {
                foreach (var device in allConnected)
                {
                    if (!nvapiToCcdEdid.TryGetValue(device.DisplayId, out var edid)) continue;
                    if ((pm.EdidManufacturerId != 0 && edid.mfrId == pm.EdidManufacturerId && edid.prodId == pm.EdidProductCodeId) ||
                        (!string.IsNullOrEmpty(pm.FriendlyName) && edid.name.Contains(pm.FriendlyName, StringComparison.OrdinalIgnoreCase)))
                    {
                        disabledProfileDisplayIds.Add(device.DisplayId);
                        Log($"  Disabling: {pm.FriendlyName} NvapiId={device.DisplayId}");
                    }
                }
            }
            if (disabledProfileDisplayIds.Count > 0)
            {
                finalPaths = finalPaths.Where(path =>
                    !path.TargetsInfo.Any(t => disabledProfileDisplayIds.Contains(t.DisplayDevice.DisplayId)));
            }

            var pathArray = finalPaths.ToArray();
            bool pathsRemoved = pathArray.Length < currentPaths.Length;
            Log($"Final path count: {pathArray.Length} (modified={modified}, removed={pathsRemoved})");
            if (pathArray.Length == 0) { Log("No paths to apply"); return false; }
            if (!modified && !pathsRemoved) { Log("Nothing changed"); return false; }

            try
            {
                PathInfo.SetDisplaysConfig(pathArray, DisplayConfigFlags.None);
                Log("SetDisplaysConfig SUCCESS");
            }
            catch (Exception ex)
            {
                Log($"SetDisplaysConfig FAILED: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
            return true;
        }
        catch (Exception ex) { Log($"ApplyFullProfile exception: {ex.GetType().Name}: {ex.Message}"); return false; }
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
