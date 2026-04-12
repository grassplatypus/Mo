using System.Runtime.InteropServices;
using Mo.Core.DisplayConfiguration;
using Mo.Interop.DisplayConfig;
using Mo.Models;

namespace Mo.Services;

public sealed class DisplayService : IDisplayService
{
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

        // Phase 2: Check if any enabled profile monitors are currently inactive
        bool needsTopologyExtend = false;
        foreach (var (profileIdx, _) in matchResult.Matches)
        {
            if (!profile.Monitors[profileIdx].IsEnabled) continue;
            var pm = profile.Monitors[profileIdx];
            bool isActive = currentConfig.Any(c =>
                c.DevicePath == pm.DevicePath ||
                (c.EdidManufacturerId == pm.EdidManufacturerId && c.EdidProductCodeId == pm.EdidProductCodeId && c.ConnectorInstance == pm.ConnectorInstance));
            if (!isActive) { needsTopologyExtend = true; break; }
        }

        // Also check unmatched profile monitors — they might be connected but inactive
        foreach (var unmatchedIdx in matchResult.UnmatchedProfile)
        {
            var pm = profile.Monitors[unmatchedIdx];
            if (!pm.IsEnabled) continue;
            foreach (var (tid, info) in allTargetIdentities)
            {
                if (info.devicePath == pm.DevicePath ||
                    (info.mfrId == pm.EdidManufacturerId && info.prodId == pm.EdidProductCodeId && info.connector == pm.ConnectorInstance))
                {
                    needsTopologyExtend = true;
                    break;
                }
            }
            if (needsTopologyExtend) break;
        }

        // Phase 3: If inactive monitors need activation, extend topology first
        if (needsTopologyExtend)
        {
            NativeDisplayApi.SetDisplayConfig(0, null, 0, null,
                SDC_FLAGS.SDC_TOPOLOGY_EXTEND | SDC_FLAGS.SDC_APPLY | SDC_FLAGS.SDC_ALLOW_CHANGES);
            Thread.Sleep(500);

            // Re-read current config after topology change
            currentConfig = GetCurrentConfiguration();
            currentIdentities = currentConfig.Select(m =>
                new MonitorMatcher.MonitorIdentity(m.DevicePath, m.EdidManufacturerId, m.EdidProductCodeId, m.ConnectorInstance, m.FriendlyName)).ToList();
            matchResult = MonitorMatcher.Match(profileIdentities, currentIdentities);
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

        // Phase 5: Query active paths and build new configuration
        result = NativeDisplayApi.GetDisplayConfigBufferSizes(
            QDC_FLAGS.QDC_ONLY_ACTIVE_PATHS, out uint activePathCount, out uint activeModeCount);
        if (result != NativeDisplayApi.ERROR_SUCCESS) return DisplayApplyResult.Failed;

        var activePaths = new DISPLAYCONFIG_PATH_INFO[activePathCount];
        var activeModes = new DISPLAYCONFIG_MODE_INFO[activeModeCount];
        result = NativeDisplayApi.QueryDisplayConfig(
            QDC_FLAGS.QDC_ONLY_ACTIVE_PATHS, ref activePathCount, activePaths, ref activeModeCount, activeModes, IntPtr.Zero);
        if (result != NativeDisplayApi.ERROR_SUCCESS) return DisplayApplyResult.Failed;

        var newPaths = new List<DISPLAYCONFIG_PATH_INFO>();
        var newModes = new List<DISPLAYCONFIG_MODE_INFO>();
        bool hasRotationChange = false;

        for (int p = 0; p < activePathCount; p++)
        {
            var activePath = activePaths[p];

            int? matchedCurrentIdx = null;
            int? matchedProfileIdx = null;
            for (int c = 0; c < currentConfig.Count; c++)
            {
                if (activePath.sourceInfo.id == currentConfig[c].SourceId &&
                    activePath.targetInfo.id == currentConfig[c].TargetId)
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
                continue;

            if (matchedProfileIdx.HasValue)
            {
                var profileMonitor = profile.Monitors[matchedProfileIdx.Value];
                var newRotation = MapRotationBack(profileMonitor.Rotation);
                if (activePath.targetInfo.rotation != newRotation) hasRotationChange = true;

                activePath.targetInfo.rotation = newRotation;
                activePath.targetInfo.refreshRate.Numerator = profileMonitor.RefreshRateNumerator;
                activePath.targetInfo.refreshRate.Denominator = profileMonitor.RefreshRateDenominator;

                if (activePath.sourceInfo.modeInfoIdx < activeModeCount)
                {
                    var mode = activeModes[activePath.sourceInfo.modeInfoIdx];
                    mode.sourceMode.position.x = profileMonitor.PositionX;
                    mode.sourceMode.position.y = profileMonitor.PositionY;

                    var w = profileMonitor.Width;
                    var h = profileMonitor.Height;
                    if (profileMonitor.Rotation is Models.DisplayRotation.Rotate90 or Models.DisplayRotation.Rotate270)
                    {
                        if (w < h) (w, h) = (h, w);
                    }
                    mode.sourceMode.width = (uint)w;
                    mode.sourceMode.height = (uint)h;

                    activePath.sourceInfo.modeInfoIdx = (uint)newModes.Count;
                    newModes.Add(mode);
                }
            }
            else
            {
                if (activePath.sourceInfo.modeInfoIdx < activeModeCount)
                {
                    var mode = activeModes[activePath.sourceInfo.modeInfoIdx];
                    activePath.sourceInfo.modeInfoIdx = (uint)newModes.Count;
                    newModes.Add(mode);
                }
            }

            if (activePath.targetInfo.modeInfoIdx < activeModeCount)
            {
                var targetMode = activeModes[activePath.targetInfo.modeInfoIdx];
                activePath.targetInfo.modeInfoIdx = (uint)newModes.Count;
                newModes.Add(targetMode);
            }

            newPaths.Add(activePath);
        }

        if (newPaths.Count == 0) return DisplayApplyResult.Failed;

        var pathArray = newPaths.ToArray();
        var modeArray = newModes.ToArray();
        var pathLen = (uint)pathArray.Length;
        var modeLen = (uint)modeArray.Length;

        result = NativeDisplayApi.SetDisplayConfig(pathLen, pathArray, modeLen, modeArray,
            SDC_FLAGS.SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_FLAGS.SDC_VALIDATE);
        if (result != NativeDisplayApi.ERROR_SUCCESS) return DisplayApplyResult.ValidationError;

        result = NativeDisplayApi.SetDisplayConfig(pathLen, pathArray, modeLen, modeArray,
            SDC_FLAGS.SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_FLAGS.SDC_APPLY | SDC_FLAGS.SDC_SAVE_TO_DATABASE | SDC_FLAGS.SDC_ALLOW_CHANGES);
        if (result != NativeDisplayApi.ERROR_SUCCESS) return DisplayApplyResult.Failed;

        if (hasRotationChange)
        {
            Thread.Sleep(300);
            // Release any cursor clipping region that may be stale after rotation
            NativeDisplayApi.ClipCursor(IntPtr.Zero);
            // Nudge the coordinate system
            NativeDisplayApi.SystemParametersInfo(
                NativeDisplayApi.SPI_SETWORKAREA, 0, IntPtr.Zero, NativeDisplayApi.SPIF_SENDCHANGE);
            // Move cursor to center of primary monitor to unstick it
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

        return new ProfileCompatibility(
            matchResult.UnmatchedProfile.Count == 0 && matchResult.UnmatchedCurrent.Count == 0,
            matchResult.UnmatchedProfile.Select(i => profile.Monitors[i].FriendlyName).ToList(),
            matchResult.UnmatchedCurrent.Select(i => currentConfig[i].FriendlyName).ToList(),
            []);
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
