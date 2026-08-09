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
                    monitor.PositionX = mode.sourceMode.position.x;
                    monitor.PositionY = mode.sourceMode.position.y;
                    monitor.IsPrimary = mode.sourceMode.position.x == 0 && mode.sourceMode.position.y == 0;

                    // Source mode is the panel's own (unrotated) mode; MonitorInfo carries
                    // the desktop extent.
                    (monitor.Width, monitor.Height) = RotationGeometry.ToDesktop(
                        (int)mode.sourceMode.width, (int)mode.sourceMode.height, (int)monitor.Rotation);
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

            // GDI device name (\\.\DISPLAY1) — needed to map CCD targets to HMONITOR
            // handles for DDC/CI calls, which order monitors differently.
            var sourceName = new DISPLAYCONFIG_SOURCE_DEVICE_NAME();
            sourceName.header.type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;
            sourceName.header.size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>();
            sourceName.header.adapterId = path.sourceInfo.adapterId;
            sourceName.header.id = path.sourceInfo.id;
            if (NativeDisplayApi.DisplayConfigGetDeviceInfo(ref sourceName) == NativeDisplayApi.ERROR_SUCCESS)
                monitor.GdiDeviceName = sourceName.viewGdiDeviceName ?? string.Empty;

            monitors.Add(monitor);
        }

        return monitors;
    }

    public List<MonitorInfo> GetAllConnectedMonitors()
    {
        // QDC_ALL_PATHS includes inactive paths (cable connected but display turned
        // off in Windows). De-duplicate by target id since a single physical
        // monitor can be reported through multiple paths.
        var monitors = new List<MonitorInfo>();
        var seen = new HashSet<uint>();

        if (NativeDisplayApi.GetDisplayConfigBufferSizes(QDC_FLAGS.QDC_ALL_PATHS, out uint pc, out uint mc) != NativeDisplayApi.ERROR_SUCCESS)
            return monitors;

        var paths = new DISPLAYCONFIG_PATH_INFO[pc];
        var modes = new DISPLAYCONFIG_MODE_INFO[mc];
        if (NativeDisplayApi.QueryDisplayConfig(QDC_FLAGS.QDC_ALL_PATHS, ref pc, paths, ref mc, modes, IntPtr.Zero)
            != NativeDisplayApi.ERROR_SUCCESS) return monitors;

        for (int i = 0; i < pc; i++)
        {
            ref var path = ref paths[i];
            if (!seen.Add(path.targetInfo.id)) continue;

            var dn = new DISPLAYCONFIG_TARGET_DEVICE_NAME();
            dn.header.type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME;
            dn.header.size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>();
            dn.header.adapterId = path.targetInfo.adapterId;
            dn.header.id = path.targetInfo.id;
            string friendly = string.Empty, devicePath = string.Empty;
            ushort mfr = 0, prod = 0; uint connector = 0;
            if (NativeDisplayApi.DisplayConfigGetDeviceInfo(ref dn) == NativeDisplayApi.ERROR_SUCCESS)
            {
                friendly = dn.monitorFriendlyDeviceName ?? string.Empty;
                devicePath = dn.monitorDevicePath ?? string.Empty;
                mfr = dn.edidManufactureId;
                prod = dn.edidProductCodeId;
                connector = dn.connectorInstance;
            }

            // Skip "phantom" entries with no monitor on the other end.
            if (string.IsNullOrEmpty(devicePath) && string.IsNullOrEmpty(friendly)) continue;

            // Active iff the path's flag bit 0 (DISPLAYCONFIG_PATH_ACTIVE) is set.
            bool isActive = (path.flags & 0x1) != 0;

            // Rotation has to come along with the dimensions: the source mode holds the
            // panel's unrotated mode, so a rotated monitor listed without it would be
            // offered to the editor as a landscape tile.
            var rotation = MapRotation(path.targetInfo.rotation);
            var (width, height) = isActive && path.sourceInfo.modeInfoIdx < mc
                ? RotationGeometry.ToDesktop(
                    (int)modes[path.sourceInfo.modeInfoIdx].sourceMode.width,
                    (int)modes[path.sourceInfo.modeInfoIdx].sourceMode.height,
                    (int)rotation)
                : (1920, 1080);

            monitors.Add(new MonitorInfo
            {
                AdapterId = path.targetInfo.adapterId.ToInt64(),
                SourceId = path.sourceInfo.id,
                TargetId = path.targetInfo.id,
                FriendlyName = friendly,
                DevicePath = devicePath,
                EdidManufacturerId = mfr,
                EdidProductCodeId = prod,
                ConnectorInstance = connector,
                IsEnabled = isActive,
                Width = width,
                Height = height,
                Rotation = rotation,
                RefreshRateNumerator = path.targetInfo.refreshRate.Numerator,
                RefreshRateDenominator = path.targetInfo.refreshRate.Denominator,
            });
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

        // Try NVAPI full-profile apply first (bypasses CCD completely)
        try
        {
            var nvService = App.Services.GetRequiredService<NvidiaRotationService>();
            if (nvService.IsAvailable && nvService.ApplyFullProfile(profile))
            {
                UnstickCursor();

                return matchResult.UnmatchedProfile.Count > 0
                    ? DisplayApplyResult.PartialMatch
                    : DisplayApplyResult.Success;
            }
        }
        catch { }

        // Radeon equivalent. Gated on the user having chosen the AMD backend, unlike
        // the NVIDIA branch above: the ADL path could not be exercised on real Radeon
        // hardware during development, so it is opt-in rather than the default.
        try
        {
            if (TryApplyAmdFullProfile(profile, currentConfig, matchResult))
            {
                UnstickCursor();

                return matchResult.UnmatchedProfile.Count > 0
                    ? DisplayApplyResult.PartialMatch
                    : DisplayApplyResult.Success;
            }
        }
        catch { }

        // Fallback to CCD path
        // Phase 2: Determine if topology extend is needed
        int enabledProfileMonitors = profile.Monitors.Count(m => m.IsEnabled);
        bool needsTopologyExtend = enabledProfileMonitors > currentConfig.Count ||
            matchResult.UnmatchedProfile.Any(i => profile.Monitors[i].IsEnabled);

        // Phase 3 (CCD fallback): If inactive monitors need activation, extend topology
        if (needsTopologyExtend)
        {
            NativeDisplayApi.SetDisplayConfig(0, null, 0, null,
                SDC_FLAGS.SDC_TOPOLOGY_EXTEND | SDC_FLAGS.SDC_APPLY | SDC_FLAGS.SDC_ALLOW_CHANGES
                | SDC_FLAGS.SDC_SAVE_TO_DATABASE | SDC_FLAGS.SDC_VIRTUAL_MODE_AWARE | SDC_FLAGS.SDC_PATH_PERSIST_IF_REQUIRED);

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

                    var (w, h) = RotationGeometry.ToSource(
                        profileMonitor.Width, profileMonitor.Height, (int)profileMonitor.Rotation);
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

        // Try apply with ALLOW_CHANGES (skip validation - it can be too strict).
        // VIRTUAL_MODE_AWARE + PATH_PERSIST_IF_REQUIRED ensures Windows 10 1903+ persists
        // DPI/rotation-aware configuration across reboots.
        var persistFlags = SDC_FLAGS.SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_FLAGS.SDC_APPLY
            | SDC_FLAGS.SDC_SAVE_TO_DATABASE | SDC_FLAGS.SDC_ALLOW_CHANGES
            | SDC_FLAGS.SDC_VIRTUAL_MODE_AWARE | SDC_FLAGS.SDC_PATH_PERSIST_IF_REQUIRED;
        result = NativeDisplayApi.SetDisplayConfig(
            (uint)finalPaths.Length, finalPaths,
            activeModeCount, activeModes,
            persistFlags);

        // Some older configs reject VIRTUAL_MODE_AWARE; retry without it.
        if (result != NativeDisplayApi.ERROR_SUCCESS)
        {
            result = NativeDisplayApi.SetDisplayConfig(
                (uint)finalPaths.Length, finalPaths,
                activeModeCount, activeModes,
                SDC_FLAGS.SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_FLAGS.SDC_APPLY
                | SDC_FLAGS.SDC_SAVE_TO_DATABASE | SDC_FLAGS.SDC_ALLOW_CHANGES);
        }

        if (result != NativeDisplayApi.ERROR_SUCCESS)
            return DisplayApplyResult.Failed;

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
            UnstickCursor();

        return matchResult.UnmatchedProfile.Count > 0
            ? DisplayApplyResult.PartialMatch
            : DisplayApplyResult.Success;
    }

    /// <summary>
    /// Applies a whole profile through the Radeon driver, verifying the result.
    /// </summary>
    /// <remarks>
    /// The apply is checked by reading the configuration back and comparing it with the
    /// profile. If it does not match, this reports failure so the caller falls through
    /// to the CCD path, which then corrects whatever the driver did.
    ///
    /// That read-back exists because the ADL path is unverified against real Radeon
    /// hardware — in particular whether ADL wants the panel's native resolution with a
    /// separate orientation, which is the assumption ApplyOne encodes. A wrong guess
    /// there would set the wrong resolution; with the check it becomes a brief detour
    /// through CCD instead.
    /// </remarks>
    private bool TryApplyAmdFullProfile(
        DisplayProfile profile,
        List<MonitorInfo> currentConfig,
        MonitorMatcher.MatchResult matchResult)
    {
        var settings = App.Services.GetRequiredService<ISettingsService>();
        if (settings.Settings.RotationMethod != RotationMethod.AmdDriver) return false;

        var amd = App.Services.GetRequiredService<AmdRotationService>();
        if (!amd.IsAvailable) return false;

        var targets = new List<AmdRotationService.DisplayTarget>();
        foreach (var (profileIdx, currentIdx) in matchResult.Matches)
        {
            var wanted = profile.Monitors[profileIdx];
            var actual = currentConfig[currentIdx];

            if (!wanted.IsEnabled) return false;                     // ADL path cannot disable outputs.
            if (string.IsNullOrEmpty(actual.GdiDeviceName)) return false;

            targets.Add(new AmdRotationService.DisplayTarget(
                actual.GdiDeviceName,
                wanted.PositionX, wanted.PositionY,
                wanted.Width, wanted.Height,
                wanted.RefreshRateHz,
                wanted.Rotation));
        }

        // Anything the driver cannot express — a monitor to switch off, a profile
        // monitor with no attached counterpart — belongs to CCD.
        if (targets.Count == 0 || targets.Count != profile.Monitors.Count(m => m.IsEnabled))
            return false;

        if (!amd.ApplyFullProfile(targets)) return false;

        // Let the driver settle before reading back.
        Thread.Sleep(500);
        return AmdResultMatches(profile, matchResult);
    }

    private bool AmdResultMatches(DisplayProfile profile, MonitorMatcher.MatchResult matchResult)
    {
        var after = GetCurrentConfiguration();

        foreach (var (profileIdx, _) in matchResult.Matches)
        {
            var wanted = profile.Monitors[profileIdx];

            var landed = after.FirstOrDefault(m =>
                (!string.IsNullOrEmpty(wanted.DevicePath) && m.DevicePath == wanted.DevicePath) ||
                (wanted.EdidManufacturerId != 0 &&
                 m.EdidManufacturerId == wanted.EdidManufacturerId &&
                 m.EdidProductCodeId == wanted.EdidProductCodeId));

            if (landed == null) return Reject("monitor missing after apply");
            if (landed.PositionX != wanted.PositionX || landed.PositionY != wanted.PositionY)
                return Reject($"position {landed.PositionX},{landed.PositionY} != {wanted.PositionX},{wanted.PositionY}");
            if (landed.Width != wanted.Width || landed.Height != wanted.Height)
                return Reject($"size {landed.Width}x{landed.Height} != {wanted.Width}x{wanted.Height}");
            if (landed.Rotation != wanted.Rotation)
                return Reject($"rotation {landed.Rotation} != {wanted.Rotation}");
        }

        return true;

        static bool Reject(string why)
        {
            Helpers.BootLog.Write("amd.fullprofile.rejected", why + "; falling back to CCD");
            return false;
        }
    }

    public ProfileCompatibility CheckCompatibility(DisplayProfile profile) =>
        CheckCompatibilityCore(profile, GetCurrentConfiguration(), GetAllConnectedTargetIdentities());

    /// <summary>
    /// Evaluates many profiles against a single hardware read.
    /// </summary>
    /// <remarks>
    /// <see cref="CheckCompatibility"/> costs two full CCD round trips — QueryDisplayConfig
    /// for the active paths plus another for ALL_PATHS, each followed by a
    /// DisplayConfigGetDeviceInfo per target. Calling it in a loop to answer "which of
    /// these profiles can I apply right now" made that 2N round trips for N profiles,
    /// on every display change. The hardware does not change between iterations, so it
    /// is read once here.
    /// </remarks>
    public IReadOnlyList<ProfileCompatibility> CheckCompatibilityAll(IReadOnlyList<DisplayProfile> profiles)
    {
        if (profiles.Count == 0) return [];

        var currentConfig = GetCurrentConfiguration();
        var allConnected = GetAllConnectedTargetIdentities();

        return [.. profiles.Select(p => CheckCompatibilityCore(p, currentConfig, allConnected))];
    }

    private static ProfileCompatibility CheckCompatibilityCore(
        DisplayProfile profile,
        List<MonitorInfo> currentConfig,
        List<(string devicePath, ushort mfrId, ushort prodId, uint connector, string name)> allConnected)
    {
        var profileIdentities = profile.Monitors.Select(m =>
            new MonitorMatcher.MonitorIdentity(m.DevicePath, m.EdidManufacturerId, m.EdidProductCodeId, m.ConnectorInstance, m.FriendlyName)).ToList();
        var currentIdentities = currentConfig.Select(m =>
            new MonitorMatcher.MonitorIdentity(m.DevicePath, m.EdidManufacturerId, m.EdidProductCodeId, m.ConnectorInstance, m.FriendlyName)).ToList();

        var matchResult = MonitorMatcher.Match(profileIdentities, currentIdentities);

        // allConnected comes from ALL_PATHS, which distinguishes "not connected" from
        // "connected but disabled".
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

        // A monitor that is plugged in but currently switched off is not a blocker —
        // applying the profile turns it back on — but the user deserves to be told,
        // because the screen coming to life is otherwise a surprise. This list was
        // already being computed and then discarded, leaving the apply dialog's warning
        // InfoBar permanently empty.
        var warnings = new List<string>();
        if (disabledMonitors.Count > 0)
        {
            warnings.Add(Helpers.ResourceHelper.GetString(
                "CompatDisabledMonitors", string.Join(", ", disabledMonitors)));
        }

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

    private static void UnstickCursor()
    {
        Thread.Sleep(500);

        for (int i = 0; i < 5; i++)
        {
            NativeDisplayApi.ClipCursor(IntPtr.Zero);
            Thread.Sleep(100);
        }

        NativeDisplayApi.SystemParametersInfo(
            NativeDisplayApi.SPI_SETWORKAREA, 0, IntPtr.Zero, NativeDisplayApi.SPIF_SENDCHANGE);

        NativeDisplayApi.ClipCursor(IntPtr.Zero);
        int cx = NativeDisplayApi.GetSystemMetrics(NativeDisplayApi.SM_CXSCREEN) / 2;
        int cy = NativeDisplayApi.GetSystemMetrics(NativeDisplayApi.SM_CYSCREEN) / 2;
        NativeDisplayApi.SetCursorPos(cx, cy);
        NativeDisplayApi.ClipCursor(IntPtr.Zero);

        // Simulate mouse movement to force coordinate recalculation
        var input = new NativeDisplayApi.INPUT
        {
            type = NativeDisplayApi.INPUT_MOUSE,
            mi = new NativeDisplayApi.MOUSEINPUT
            {
                dx = 10, dy = 10,
                dwFlags = NativeDisplayApi.MOUSEEVENTF_MOVE,
            }
        };
        NativeDisplayApi.SendInput(1, [input], Marshal.SizeOf<NativeDisplayApi.INPUT>());
        Thread.Sleep(50);
        input.mi.dx = -10; input.mi.dy = -10;
        NativeDisplayApi.SendInput(1, [input], Marshal.SizeOf<NativeDisplayApi.INPUT>());
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

    // ── HDR / Advanced Color ──

    public HdrState GetHdrState(MonitorInfo monitor)
    {
        var request = new DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO
        {
            header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO,
                size = (uint)Marshal.SizeOf<DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO>(),
                adapterId = new LUID { LowPart = (uint)(monitor.AdapterId & 0xFFFFFFFF), HighPart = (int)(monitor.AdapterId >> 32) },
                id = monitor.TargetId,
            },
        };

        if (NativeDisplayApi.DisplayConfigGetDeviceInfo(ref request) != NativeDisplayApi.ERROR_SUCCESS)
            return new HdrState(false, false, false);

        bool supported = (request.value & 0x1) != 0;
        bool enabled = (request.value & 0x2) != 0;
        bool forceDisabled = (request.value & 0x8) != 0;
        return new HdrState(supported, enabled, forceDisabled);
    }

    public bool SetHdrEnabled(MonitorInfo monitor, bool enabled)
    {
        var request = new DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE
        {
            header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_SET_ADVANCED_COLOR_STATE,
                size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE>(),
                adapterId = new LUID { LowPart = (uint)(monitor.AdapterId & 0xFFFFFFFF), HighPart = (int)(monitor.AdapterId >> 32) },
                id = monitor.TargetId,
            },
            enableAdvancedColor = enabled ? 1u : 0u,
        };

        return NativeDisplayApi.DisplayConfigSetDeviceInfo(ref request) == NativeDisplayApi.ERROR_SUCCESS;
    }
}
