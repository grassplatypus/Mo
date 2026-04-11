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
        // Get all paths (including inactive) for matching
        int result = NativeDisplayApi.GetDisplayConfigBufferSizes(
            QDC_FLAGS.QDC_ALL_PATHS,
            out uint pathCount,
            out uint modeCount);

        if (result != NativeDisplayApi.ERROR_SUCCESS)
            return DisplayApplyResult.Failed;

        var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
        var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];

        result = NativeDisplayApi.QueryDisplayConfig(
            QDC_FLAGS.QDC_ALL_PATHS,
            ref pathCount, paths,
            ref modeCount, modes,
            IntPtr.Zero);

        if (result != NativeDisplayApi.ERROR_SUCCESS)
            return DisplayApplyResult.Failed;

        // Get active paths for matching
        var currentConfig = GetCurrentConfiguration();
        var profileIdentities = profile.Monitors.Select(m =>
            new MonitorMatcher.MonitorIdentity(m.DevicePath, m.EdidManufacturerId, m.EdidProductCodeId, m.ConnectorInstance, m.FriendlyName)).ToList();
        var currentIdentities = currentConfig.Select(m =>
            new MonitorMatcher.MonitorIdentity(m.DevicePath, m.EdidManufacturerId, m.EdidProductCodeId, m.ConnectorInstance, m.FriendlyName)).ToList();

        var matchResult = MonitorMatcher.Match(profileIdentities, currentIdentities);

        if (matchResult.Matches.Count == 0 && profile.Monitors.Count > 0)
            return DisplayApplyResult.Failed;

        // Build the active path set from current active paths
        result = NativeDisplayApi.GetDisplayConfigBufferSizes(
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

        // Apply changes to active paths based on profile
        foreach (var (profileIdx, currentIdx) in matchResult.Matches)
        {
            var profileMonitor = profile.Monitors[profileIdx];
            var currentMonitor = currentConfig[currentIdx];

            // Find the corresponding active path
            for (int p = 0; p < activePathCount; p++)
            {
                ref var activePath = ref activePaths[p];
                if (activePath.sourceInfo.id == currentMonitor.SourceId &&
                    activePath.targetInfo.id == currentMonitor.TargetId)
                {
                    // Update rotation
                    activePath.targetInfo.rotation = MapRotationBack(profileMonitor.Rotation);

                    // Update refresh rate
                    activePath.targetInfo.refreshRate.Numerator = profileMonitor.RefreshRateNumerator;
                    activePath.targetInfo.refreshRate.Denominator = profileMonitor.RefreshRateDenominator;

                    // Update source mode (position + resolution)
                    if (activePath.sourceInfo.modeInfoIdx < activeModeCount)
                    {
                        ref var mode = ref activeModes[activePath.sourceInfo.modeInfoIdx];
                        mode.sourceMode.position.x = profileMonitor.PositionX;
                        mode.sourceMode.position.y = profileMonitor.PositionY;
                        mode.sourceMode.width = (uint)profileMonitor.Width;
                        mode.sourceMode.height = (uint)profileMonitor.Height;
                    }

                    break;
                }
            }
        }

        // Validate first
        result = NativeDisplayApi.SetDisplayConfig(
            activePathCount, activePaths,
            activeModeCount, activeModes,
            SDC_FLAGS.SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_FLAGS.SDC_VALIDATE);

        if (result != NativeDisplayApi.ERROR_SUCCESS)
            return DisplayApplyResult.ValidationError;

        // Apply
        result = NativeDisplayApi.SetDisplayConfig(
            activePathCount, activePaths,
            activeModeCount, activeModes,
            SDC_FLAGS.SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_FLAGS.SDC_APPLY | SDC_FLAGS.SDC_SAVE_TO_DATABASE | SDC_FLAGS.SDC_ALLOW_CHANGES);

        if (result != NativeDisplayApi.ERROR_SUCCESS)
            return DisplayApplyResult.Failed;

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
