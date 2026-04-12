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
        // Match profile monitors to currently connected hardware
        var currentConfig = GetCurrentConfiguration();
        var profileIdentities = profile.Monitors.Select(m =>
            new MonitorMatcher.MonitorIdentity(m.DevicePath, m.EdidManufacturerId, m.EdidProductCodeId, m.ConnectorInstance, m.FriendlyName)).ToList();
        var currentIdentities = currentConfig.Select(m =>
            new MonitorMatcher.MonitorIdentity(m.DevicePath, m.EdidManufacturerId, m.EdidProductCodeId, m.ConnectorInstance, m.FriendlyName)).ToList();

        var matchResult = MonitorMatcher.Match(profileIdentities, currentIdentities);

        if (matchResult.Matches.Count == 0 && profile.Monitors.Count > 0)
            return DisplayApplyResult.Failed;

        // Build set of current monitor indices that should remain active
        var enabledCurrentIndices = new HashSet<int>();
        foreach (var (profileIdx, currentIdx) in matchResult.Matches)
        {
            if (profile.Monitors[profileIdx].IsEnabled)
                enabledCurrentIndices.Add(currentIdx);
        }

        // Query current active paths
        int result = NativeDisplayApi.GetDisplayConfigBufferSizes(
            QDC_FLAGS.QDC_ONLY_ACTIVE_PATHS,
            out uint activePathCount,
            out uint activeModeCount);

        if (result != NativeDisplayApi.ERROR_SUCCESS)
            return DisplayApplyResult.Failed;

        var activePaths = new DISPLAYCONFIG_PATH_INFO[activePathCount];
        var activeModes = new DISPLAYCONFIG_MODE_INFO[activeModeCount];

        result = NativeDisplayApi.QueryDisplayConfig(
            QDC_FLAGS.QDC_ONLY_ACTIVE_PATHS,
            ref activePathCount, activePaths,
            ref activeModeCount, activeModes,
            IntPtr.Zero);

        if (result != NativeDisplayApi.ERROR_SUCCESS)
            return DisplayApplyResult.Failed;

        // Build filtered path/mode arrays: keep only enabled matched monitors
        var newPaths = new List<DISPLAYCONFIG_PATH_INFO>();
        var newModes = new List<DISPLAYCONFIG_MODE_INFO>();
        bool hasRotationChange = false;

        for (int p = 0; p < activePathCount; p++)
        {
            var activePath = activePaths[p];

            // Find which current monitor this path corresponds to
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

            // Skip paths for monitors not in the enabled set
            if (matchedCurrentIdx.HasValue && !enabledCurrentIndices.Contains(matchedCurrentIdx.Value))
                continue;

            // Apply profile settings to matched paths
            if (matchedProfileIdx.HasValue)
            {
                var profileMonitor = profile.Monitors[matchedProfileIdx.Value];
                var newRotation = MapRotationBack(profileMonitor.Rotation);

                if (activePath.targetInfo.rotation != newRotation)
                    hasRotationChange = true;

                activePath.targetInfo.rotation = newRotation;
                activePath.targetInfo.refreshRate.Numerator = profileMonitor.RefreshRateNumerator;
                activePath.targetInfo.refreshRate.Denominator = profileMonitor.RefreshRateDenominator;

                if (activePath.sourceInfo.modeInfoIdx < activeModeCount)
                {
                    var mode = activeModes[activePath.sourceInfo.modeInfoIdx];
                    mode.sourceMode.position.x = profileMonitor.PositionX;
                    mode.sourceMode.position.y = profileMonitor.PositionY;

                    // Profile stores logical (rotated) dimensions; reverse for CCD source mode
                    var w = profileMonitor.Width;
                    var h = profileMonitor.Height;
                    if (profileMonitor.Rotation is Models.DisplayRotation.Rotate90 or Models.DisplayRotation.Rotate270)
                    {
                        if (w < h)
                            (w, h) = (h, w);
                    }
                    mode.sourceMode.width = (uint)w;
                    mode.sourceMode.height = (uint)h;

                    activePath.sourceInfo.modeInfoIdx = (uint)newModes.Count;
                    newModes.Add(mode);
                }
            }
            else
            {
                // Unmatched but still active (not in profile) — keep mode as-is with remapped index
                if (activePath.sourceInfo.modeInfoIdx < activeModeCount)
                {
                    var mode = activeModes[activePath.sourceInfo.modeInfoIdx];
                    activePath.sourceInfo.modeInfoIdx = (uint)newModes.Count;
                    newModes.Add(mode);
                }
            }

            // Remap target mode index if present
            if (activePath.targetInfo.modeInfoIdx < activeModeCount)
            {
                var targetMode = activeModes[activePath.targetInfo.modeInfoIdx];
                activePath.targetInfo.modeInfoIdx = (uint)newModes.Count;
                newModes.Add(targetMode);
            }

            newPaths.Add(activePath);
        }

        if (newPaths.Count == 0)
            return DisplayApplyResult.Failed;

        var pathArray = newPaths.ToArray();
        var modeArray = newModes.ToArray();
        var pathLen = (uint)pathArray.Length;
        var modeLen = (uint)modeArray.Length;

        // Validate first
        result = NativeDisplayApi.SetDisplayConfig(
            pathLen, pathArray,
            modeLen, modeArray,
            SDC_FLAGS.SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_FLAGS.SDC_VALIDATE);

        if (result != NativeDisplayApi.ERROR_SUCCESS)
            return DisplayApplyResult.ValidationError;

        // Apply
        result = NativeDisplayApi.SetDisplayConfig(
            pathLen, pathArray,
            modeLen, modeArray,
            SDC_FLAGS.SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_FLAGS.SDC_APPLY | SDC_FLAGS.SDC_SAVE_TO_DATABASE | SDC_FLAGS.SDC_ALLOW_CHANGES);

        if (result != NativeDisplayApi.ERROR_SUCCESS)
            return DisplayApplyResult.Failed;

        // Workaround for Windows CCD rotation bug: nudge the coordinate system
        if (hasRotationChange)
        {
            Thread.Sleep(200);
            NativeDisplayApi.SystemParametersInfo(
                NativeDisplayApi.SPI_SETWORKAREA, 0, IntPtr.Zero, NativeDisplayApi.SPIF_SENDCHANGE);
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
