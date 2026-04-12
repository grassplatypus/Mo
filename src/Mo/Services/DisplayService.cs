using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Mo.Core.DisplayConfiguration;
using Mo.Interop.DisplayConfig;
using Mo.Models;

namespace Mo.Services;

public sealed class DisplayService : IDisplayService
{
    private bool UseDriverRotation
    {
        get
        {
            try
            {
                var settings = App.Services.GetRequiredService<ISettingsService>();
                return settings.Settings.RotationMethod != RotationMethod.Windows;
            }
            catch { return false; }
        }
    }
    public List<MonitorInfo> GetCurrentConfiguration()
    {
        var monitors = new List<MonitorInfo>();

        int result = NativeDisplayApi.GetDisplayConfigBufferSizes(
            QDC_FLAGS.QDC_ONLY_ACTIVE_PATHS,
            out uint pathCount,
            out uint modeCount);

        if (result != NativeDisplayApi.ERROR_SUCCESS)
            return monitors;

        var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
        var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];

        result = NativeDisplayApi.QueryDisplayConfig(
            QDC_FLAGS.QDC_ONLY_ACTIVE_PATHS,
            ref pathCount, paths,
            ref modeCount, modes,
            IntPtr.Zero);

        if (result != NativeDisplayApi.ERROR_SUCCESS)
            return monitors;

        for (int i = 0; i < pathCount; i++)
        {
            ref var path = ref paths[i];

            var monitor = new MonitorInfo
            {
                AdapterId = path.targetInfo.adapterId.ToInt64(),
                SourceId = path.sourceInfo.id,
                TargetId = path.targetInfo.id,
                Rotation = MapRotation(path.targetInfo.rotation),
                RefreshRateNumerator = path.targetInfo.refreshRate.Numerator,
                RefreshRateDenominator = path.targetInfo.refreshRate.Denominator,
            };

            // Get source mode (resolution + position)
            if (path.sourceInfo.modeInfoIdx < modeCount)
            {
                ref var mode = ref modes[path.sourceInfo.modeInfoIdx];
                if (mode.infoType == DISPLAYCONFIG_MODE_INFO_TYPE.DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE)
                {
                    monitor.Width = (int)mode.sourceMode.width;
                    monitor.Height = (int)mode.sourceMode.height;
                    monitor.PositionX = mode.sourceMode.position.x;
                    monitor.PositionY = mode.sourceMode.position.y;
                    monitor.IsPrimary = mode.sourceMode.position.x == 0 && mode.sourceMode.position.y == 0;

                    // Some drivers report native (unrotated) dimensions in source mode.
                    // Ensure Width/Height reflect the logical (rotated) orientation.
                    if (monitor.Rotation is Models.DisplayRotation.Rotate90 or Models.DisplayRotation.Rotate270)
                    {
                        if (monitor.Width > monitor.Height)
                            (monitor.Width, monitor.Height) = (monitor.Height, monitor.Width);
                    }
                }
            }

            // Get device name info
            var deviceName = new DISPLAYCONFIG_TARGET_DEVICE_NAME();
            deviceName.header.type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME;
            deviceName.header.size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>();
            deviceName.header.adapterId = path.targetInfo.adapterId;
            deviceName.header.id = path.targetInfo.id;

            if (NativeDisplayApi.DisplayConfigGetDeviceInfo(ref deviceName) == NativeDisplayApi.ERROR_SUCCESS)
            {
                monitor.FriendlyName = deviceName.monitorFriendlyDeviceName ?? string.Empty;
                monitor.DevicePath = deviceName.monitorDevicePath ?? string.Empty;
                monitor.EdidManufacturerId = deviceName.edidManufactureId;
                monitor.EdidProductCodeId = deviceName.edidProductCodeId;
                monitor.ConnectorInstance = deviceName.connectorInstance;
            }

            monitors.Add(monitor);
        }

        return monitors;
    }

    public DisplayApplyResult ApplyProfile(DisplayProfile profile)
    {
        // Phase 1: Match profile monitors against ALL connected monitors (including inactive)
        int result = NativeDisplayApi.GetDisplayConfigBufferSizes(
            QDC_FLAGS.QDC_ALL_PATHS, out uint allPathCount, out uint allModeCount);
        if (result != NativeDisplayApi.ERROR_SUCCESS) return DisplayApplyResult.Failed;

        var allPaths = new DISPLAYCONFIG_PATH_INFO[allPathCount];
        var allModes = new DISPLAYCONFIG_MODE_INFO[allModeCount];
        result = NativeDisplayApi.QueryDisplayConfig(
            QDC_FLAGS.QDC_ALL_PATHS, ref allPathCount, allPaths, ref allModeCount, allModes, IntPtr.Zero);
        if (result != NativeDisplayApi.ERROR_SUCCESS) return DisplayApplyResult.Failed;

        // Build identity map for all connected targets
        var allTargetIdentities = new Dictionary<uint, (string devicePath, ushort mfrId, ushort prodId, uint connector, string name)>();
        for (int p = 0; p < allPathCount; p++)
        {
            var tid = allPaths[p].targetInfo.id;
            if (allTargetIdentities.ContainsKey(tid)) continue;
            var dn = new DISPLAYCONFIG_TARGET_DEVICE_NAME();
            dn.header.type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME;
            dn.header.size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>();
            dn.header.adapterId = allPaths[p].targetInfo.adapterId;
            dn.header.id = tid;
            if (NativeDisplayApi.DisplayConfigGetDeviceInfo(ref dn) == NativeDisplayApi.ERROR_SUCCESS)
                allTargetIdentities[tid] = (dn.monitorDevicePath ?? "", dn.edidManufactureId, dn.edidProductCodeId, dn.connectorInstance, dn.monitorFriendlyDeviceName ?? "");
        }

        var currentConfig = GetCurrentConfiguration();
        var profileIdentities = profile.Monitors.Select(m =>
            new MonitorMatcher.MonitorIdentity(m.DevicePath, m.EdidManufacturerId, m.EdidProductCodeId, m.ConnectorInstance, m.FriendlyName)).ToList();
        var currentIdentities = currentConfig.Select(m =>
            new MonitorMatcher.MonitorIdentity(m.DevicePath, m.EdidManufacturerId, m.EdidProductCodeId, m.ConnectorInstance, m.FriendlyName)).ToList();

        var matchResult = MonitorMatcher.Match(profileIdentities, currentIdentities);
        if (matchResult.Matches.Count == 0 && profile.Monitors.Count > 0)
            return DisplayApplyResult.Failed;

        // Phase 2: Determine if topology extend is needed
        int enabledProfileMonitors = profile.Monitors.Count(m => m.IsEnabled);
        bool needsTopologyExtend = enabledProfileMonitors > currentConfig.Count ||
            matchResult.UnmatchedProfile.Any(i => profile.Monitors[i].IsEnabled);

        // Phase 3: If inactive monitors need activation, extend topology first
        if (needsTopologyExtend)
        {
            // Try NVAPI-based enable first (more reliable for NVIDIA-managed displays)
            try
            {
                var nvService = App.Services.GetRequiredService<NvidiaRotationService>();
                if (nvService.IsAvailable)
                    nvService.EnableAllDisplays();
            }
            catch { }

            // Also try CCD topology extend as fallback
            NativeDisplayApi.SetDisplayConfig(0, null, 0, null,
                SDC_FLAGS.SDC_TOPOLOGY_EXTEND | SDC_FLAGS.SDC_APPLY | SDC_FLAGS.SDC_ALLOW_CHANGES | SDC_FLAGS.SDC_SAVE_TO_DATABASE);

            // Wait and retry matching until all monitors appear or timeout
            for (int attempt = 0; attempt < 3; attempt++)
            {
                Thread.Sleep(1000);
                currentConfig = GetCurrentConfiguration();
                currentIdentities = currentConfig.Select(m =>
                    new MonitorMatcher.MonitorIdentity(m.DevicePath, m.EdidManufacturerId, m.EdidProductCodeId, m.ConnectorInstance, m.FriendlyName)).ToList();
                matchResult = MonitorMatcher.Match(profileIdentities, currentIdentities);
                if (matchResult.UnmatchedProfile.Count(i => profile.Monitors[i].IsEnabled) == 0)
                    break;
            }
        }

        // Phase 4: Determine which monitors to disable
        var disabledCurrentIndices = new HashSet<int>();
        foreach (var (profileIdx, currentIdx) in matchResult.Matches)
        {
            if (!profile.Monitors[profileIdx].IsEnabled)
                disabledCurrentIndices.Add(currentIdx);
        }
        // Handle unmatched monitors based on profile's UnmatchedAction
        if (profile.UnmatchedAction == Models.UnmatchedMonitorAction.Disable)
        {
            foreach (var unmatchedCurrentIdx in matchResult.UnmatchedCurrent)
                disabledCurrentIndices.Add(unmatchedCurrentIdx);
        }

        // Phase 5: Modify active paths in-place (no index remapping)
        result = NativeDisplayApi.GetDisplayConfigBufferSizes(
            QDC_FLAGS.QDC_ONLY_ACTIVE_PATHS, out uint activePathCount, out uint activeModeCount);
        if (result != NativeDisplayApi.ERROR_SUCCESS) return DisplayApplyResult.Failed;

        var activePaths = new DISPLAYCONFIG_PATH_INFO[activePathCount];
        var activeModes = new DISPLAYCONFIG_MODE_INFO[activeModeCount];
        result = NativeDisplayApi.QueryDisplayConfig(
            QDC_FLAGS.QDC_ONLY_ACTIVE_PATHS, ref activePathCount, activePaths, ref activeModeCount, activeModes, IntPtr.Zero);
        if (result != NativeDisplayApi.ERROR_SUCCESS) return DisplayApplyResult.Failed;

        bool hasRotationChange = false;
        bool useDriverRotation = UseDriverRotation;
        var driverRotationTasks = new List<(MonitorInfo monitor, DisplayRotation rotation)>();
        var pathsToRemove = new HashSet<int>();

        for (int p = 0; p < activePathCount; p++)
        {
            int? matchedCurrentIdx = null;
            int? matchedProfileIdx = null;
            for (int c = 0; c < currentConfig.Count; c++)
            {
                if (activePaths[p].sourceInfo.id == currentConfig[c].SourceId &&
                    activePaths[p].targetInfo.id == currentConfig[c].TargetId)
                {
                    matchedCurrentIdx = c;
                    foreach (var (pi, ci) in matchResult.Matches)
                    {
                        if (ci == c) { matchedProfileIdx = pi; break; }
                    }
                    break;
                }
            }

            if (matchedCurrentIdx.HasValue && disabledCurrentIndices.Contains(matchedCurrentIdx.Value))
            {
                pathsToRemove.Add(p);
                continue;
            }

            if (matchedProfileIdx.HasValue)
            {
                var profileMonitor = profile.Monitors[matchedProfileIdx.Value];
                var newRotation = MapRotationBack(profileMonitor.Rotation);
                if (activePaths[p].targetInfo.rotation != newRotation) hasRotationChange = true;

                if (useDriverRotation && profileMonitor.Rotation != DisplayRotation.None)
                {
                    driverRotationTasks.Add((currentConfig[matchedCurrentIdx!.Value], profileMonitor.Rotation));
                }
                else
                {
                    activePaths[p].targetInfo.rotation = newRotation;
                }
                activePaths[p].targetInfo.refreshRate.Numerator = profileMonitor.RefreshRateNumerator;
                activePaths[p].targetInfo.refreshRate.Denominator = profileMonitor.RefreshRateDenominator;

                var srcIdx = activePaths[p].sourceInfo.modeInfoIdx;
                if (srcIdx < activeModeCount)
                {
                    activeModes[srcIdx].sourceMode.position.x = profileMonitor.PositionX;
                    activeModes[srcIdx].sourceMode.position.y = profileMonitor.PositionY;

                    var w = profileMonitor.Width;
                    var h = profileMonitor.Height;
                    if (profileMonitor.Rotation is Models.DisplayRotation.Rotate90 or Models.DisplayRotation.Rotate270)
                    {
                        if (w < h) (w, h) = (h, w);
                    }
                    activeModes[srcIdx].sourceMode.width = (uint)w;
                    activeModes[srcIdx].sourceMode.height = (uint)h;
                }
            }
        }

        // Build final arrays (remove disabled paths if any)
        DISPLAYCONFIG_PATH_INFO[] finalPaths;
        if (pathsToRemove.Count > 0)
            finalPaths = activePaths.Where((_, i) => !pathsToRemove.Contains(i)).ToArray();
        else
            finalPaths = activePaths;

        if (finalPaths.Length == 0) return DisplayApplyResult.Failed;

        // Try apply with ALLOW_CHANGES (skip validation - it can be too strict)
        result = NativeDisplayApi.SetDisplayConfig(
            (uint)finalPaths.Length, finalPaths,
            activeModeCount, activeModes,
            SDC_FLAGS.SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_FLAGS.SDC_APPLY | SDC_FLAGS.SDC_SAVE_TO_DATABASE | SDC_FLAGS.SDC_ALLOW_CHANGES);

        if (result != NativeDisplayApi.ERROR_SUCCESS)
        {
            // Retry with more permissive flags
            result = NativeDisplayApi.SetDisplayConfig(
                (uint)finalPaths.Length, finalPaths,
                activeModeCount, activeModes,
                SDC_FLAGS.SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_FLAGS.SDC_APPLY | SDC_FLAGS.SDC_ALLOW_CHANGES |
                SDC_FLAGS.SDC_SAVE_TO_DATABASE | SDC_FLAGS.SDC_FORCE_MODE_ENUMERATION | SDC_FLAGS.SDC_ALLOW_PATH_ORDER_CHANGES);
            if (result != NativeDisplayApi.ERROR_SUCCESS)
                return DisplayApplyResult.Failed;
        }

        // Apply driver-level rotation if configured
        if (driverRotationTasks.Count > 0)
        {
            try
            {
                var settings = App.Services.GetRequiredService<ISettingsService>();
                foreach (var (monitor, rotation) in driverRotationTasks)
                {
                    bool applied = settings.Settings.RotationMethod switch
                    {
                        RotationMethod.NvidiaDriver => App.Services.GetRequiredService<NvidiaRotationService>().ApplyRotation(monitor, rotation),
                        RotationMethod.AmdDriver => App.Services.GetRequiredService<AmdRotationService>().ApplyRotation(monitor, rotation),
                        RotationMethod.IntelDriver => App.Services.GetRequiredService<IntelRotationService>().ApplyRotation(monitor, rotation),
                        _ => false,
                    };
                }
            }
            catch { }

            Thread.Sleep(500);
            NativeDisplayApi.ClipCursor(IntPtr.Zero);
        }

        if (hasRotationChange)
        {
            Thread.Sleep(500);
            NativeDisplayApi.ClipCursor(IntPtr.Zero);
            NativeDisplayApi.SystemParametersInfo(
                NativeDisplayApi.SPI_SETWORKAREA, 0, IntPtr.Zero, NativeDisplayApi.SPIF_SENDCHANGE);
            Thread.Sleep(200);
            NativeDisplayApi.ClipCursor(IntPtr.Zero);
            int cx = NativeDisplayApi.GetSystemMetrics(NativeDisplayApi.SM_CXSCREEN) / 2;
            int cy = NativeDisplayApi.GetSystemMetrics(NativeDisplayApi.SM_CYSCREEN) / 2;
            NativeDisplayApi.SetCursorPos(cx, cy);
        }

        return matchResult.UnmatchedProfile.Count > 0
            ? DisplayApplyResult.PartialMatch
            : DisplayApplyResult.Success;
    }

    public ProfileCompatibility CheckCompatibility(DisplayProfile profile)
    {
        var currentConfig = GetCurrentConfiguration();
        var profileIdentities = profile.Monitors.Select(m =>
            new MonitorMatcher.MonitorIdentity(m.DevicePath, m.EdidManufacturerId, m.EdidProductCodeId, m.ConnectorInstance, m.FriendlyName)).ToList();
        var currentIdentities = currentConfig.Select(m =>
            new MonitorMatcher.MonitorIdentity(m.DevicePath, m.EdidManufacturerId, m.EdidProductCodeId, m.ConnectorInstance, m.FriendlyName)).ToList();

        var matchResult = MonitorMatcher.Match(profileIdentities, currentIdentities);

        // Check ALL_PATHS to distinguish "not connected" from "connected but disabled"
        var allConnected = GetAllConnectedTargetIdentities();
        var missingMonitors = new List<string>();
        var disabledMonitors = new List<string>();
        foreach (var idx in matchResult.UnmatchedProfile)
        {
            var pm = profile.Monitors[idx];
            bool connectedButDisabled = allConnected.Any(t =>
                t.devicePath == pm.DevicePath ||
                (t.mfrId != 0 && t.mfrId == pm.EdidManufacturerId && t.prodId == pm.EdidProductCodeId && t.connector == pm.ConnectorInstance));
            if (connectedButDisabled)
                disabledMonitors.Add(pm.FriendlyName);
            else
                missingMonitors.Add(pm.FriendlyName);
        }

        var warnings = new List<string>();

        // Only truly missing monitors matter for compatibility
        bool isFullMatch = missingMonitors.Count == 0;

        return new ProfileCompatibility(
            isFullMatch,
            missingMonitors,
            matchResult.UnmatchedCurrent.Select(i => currentConfig[i].FriendlyName).ToList(),
            warnings);
    }

    private List<(string devicePath, ushort mfrId, ushort prodId, uint connector, string name)> GetAllConnectedTargetIdentities()
    {
        var result = new List<(string, ushort, ushort, uint, string)>();
        try
        {
            int r = NativeDisplayApi.GetDisplayConfigBufferSizes(QDC_FLAGS.QDC_ALL_PATHS, out uint pc, out uint mc);
            if (r != NativeDisplayApi.ERROR_SUCCESS) return result;
            var paths = new DISPLAYCONFIG_PATH_INFO[pc];
            var modes = new DISPLAYCONFIG_MODE_INFO[mc];
            r = NativeDisplayApi.QueryDisplayConfig(QDC_FLAGS.QDC_ALL_PATHS, ref pc, paths, ref mc, modes, IntPtr.Zero);
            if (r != NativeDisplayApi.ERROR_SUCCESS) return result;

            var seen = new HashSet<uint>();
            for (int i = 0; i < pc; i++)
            {
                var tid = paths[i].targetInfo.id;
                if (!seen.Add(tid)) continue;
                var dn = new DISPLAYCONFIG_TARGET_DEVICE_NAME();
                dn.header.type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME;
                dn.header.size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>();
                dn.header.adapterId = paths[i].targetInfo.adapterId;
                dn.header.id = tid;
                if (NativeDisplayApi.DisplayConfigGetDeviceInfo(ref dn) == NativeDisplayApi.ERROR_SUCCESS)
                    result.Add((dn.monitorDevicePath ?? "", dn.edidManufactureId, dn.edidProductCodeId, dn.connectorInstance, dn.monitorFriendlyDeviceName ?? ""));
            }
        }
        catch { }
        return result;
    }

    private static Models.DisplayRotation MapRotation(DISPLAYCONFIG_ROTATION rotation) => rotation switch
    {
        DISPLAYCONFIG_ROTATION.DISPLAYCONFIG_ROTATION_ROTATE90 => Models.DisplayRotation.Rotate90,
        DISPLAYCONFIG_ROTATION.DISPLAYCONFIG_ROTATION_ROTATE180 => Models.DisplayRotation.Rotate180,
        DISPLAYCONFIG_ROTATION.DISPLAYCONFIG_ROTATION_ROTATE270 => Models.DisplayRotation.Rotate270,
        _ => Models.DisplayRotation.None,
    };

    private static DISPLAYCONFIG_ROTATION MapRotationBack(Models.DisplayRotation rotation) => rotation switch
    {
        Models.DisplayRotation.Rotate90 => DISPLAYCONFIG_ROTATION.DISPLAYCONFIG_ROTATION_ROTATE90,
        Models.DisplayRotation.Rotate180 => DISPLAYCONFIG_ROTATION.DISPLAYCONFIG_ROTATION_ROTATE180,
        Models.DisplayRotation.Rotate270 => DISPLAYCONFIG_ROTATION.DISPLAYCONFIG_ROTATION_ROTATE270,
        _ => DISPLAYCONFIG_ROTATION.DISPLAYCONFIG_ROTATION_IDENTITY,
    };
}
