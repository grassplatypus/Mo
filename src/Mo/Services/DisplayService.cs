using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Mo.Core.DisplayConfiguration;
using Mo.Helpers;
using Mo.Interop.DisplayConfig;
using Mo.Models;

namespace Mo.Services;

public sealed class DisplayService : IDisplayService
{
    /// <summary>"No mode supplied" for either modeInfoIdx. On a target it asks Windows
    /// to pick the timing itself.</summary>
    private const uint ModeIdxInvalid = 0xFFFFFFFF;

    /// <summary>Which backend the user asked for. Read at apply time, never cached: the
    /// setting can change while the app runs.</summary>
    private static RotationMethod SelectedMethod
    {
        get
        {
            try { return App.Services.GetRequiredService<ISettingsService>().Settings.RotationMethod; }
            catch { return RotationMethod.Windows; }
        }
    }

    private bool UseDriverRotation => SelectedMethod != RotationMethod.Windows;
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
                    monitor.DpiScale = ReadDpiPercent(path);

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

            // Same GDI name lookup as GetCurrentConfiguration. Without it the mode
            // pickers have nothing to enumerate, since EnumDisplaySettings is addressed
            // by \\.\DISPLAYn and not by anything CCD hands out.
            var sourceName = new DISPLAYCONFIG_SOURCE_DEVICE_NAME();
            sourceName.header.type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;
            sourceName.header.size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>();
            sourceName.header.adapterId = path.sourceInfo.adapterId;
            sourceName.header.id = path.sourceInfo.id;
            string gdiName = NativeDisplayApi.DisplayConfigGetDeviceInfo(ref sourceName) == NativeDisplayApi.ERROR_SUCCESS
                ? sourceName.viewGdiDeviceName ?? string.Empty
                : string.Empty;

            // Position and primary come from the same source mode as the dimensions.
            // Leaving them at zero would put every monitor of a captured profile at the
            // origin, and the apply writes those positions straight back out.
            int width = 1920, height = 1080, posX = 0, posY = 0;
            bool isPrimary = false;
            if (isActive && path.sourceInfo.modeInfoIdx < mc)
            {
                var source = modes[path.sourceInfo.modeInfoIdx].sourceMode;
                (width, height) = RotationGeometry.ToDesktop(
                    (int)source.width, (int)source.height, (int)rotation);
                posX = source.position.x;
                posY = source.position.y;
                isPrimary = posX == 0 && posY == 0;
            }
            else if (TryReadLastKnownMode(gdiName, out int lastW, out int lastH, out var lastRotation))
            {
                // A switched-off monitor has no source mode, and the fallback size is
                // written to disk the moment the profile is saved. Windows remembers what
                // it was last set to, which beats inventing 1920x1080.
                (width, height) = (lastW, lastH);

                // An inactive path reports rotation 0, which is not IDENTITY (1) and maps
                // to None. The DEVMODE's own orientation is what those pels are in.
                rotation = lastRotation;
            }

            monitors.Add(new MonitorInfo
            {
                AdapterId = path.targetInfo.adapterId.ToInt64(),
                SourceId = path.sourceInfo.id,
                TargetId = path.targetInfo.id,
                FriendlyName = friendly,
                DevicePath = devicePath,
                GdiDeviceName = gdiName,
                EdidManufacturerId = mfr,
                EdidProductCodeId = prod,
                ConnectorInstance = connector,
                IsEnabled = isActive,
                IsPrimary = isPrimary,
                // Only a display Windows is driving has a scale to read. Recording a
                // fabricated 100 here would push every such panel down to 100% the
                // moment the profile switched it on.
                DpiScale = isActive ? ReadDpiPercent(path) : MonitorInfo.DpiScaleUnset,
                PositionX = posX,
                PositionY = posY,
                Width = width,
                Height = height,
                Rotation = rotation,
                RefreshRateNumerator = path.targetInfo.refreshRate.Numerator,
                RefreshRateDenominator = path.targetInfo.refreshRate.Denominator,
            });
        }

        return monitors;
    }

    public DisplayApplyResult ApplyProfile(DisplayProfile profile, ApplyTrigger trigger = ApplyTrigger.User)
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
        var profileIdentities = profile.Monitors.ToIdentities();
        var currentIdentities = currentConfig.ToIdentities();

        var matchResult = MonitorMatcher.Match(profileIdentities, currentIdentities);
        if (matchResult.Matches.Count == 0 && profile.Monitors.Count > 0)
            return DisplayApplyResult.Failed;

        // The vendor branches below return early, so decide here whether this apply
        // turns any panel — that is what strands the cursor plane. Named apart from the
        // CCD path's hasRotationChange on purpose; the two have different scopes.
        // Measured against the state before anything is touched. The topology extend
        // below restores each panel from Windows' own display database, so a monitor can
        // arrive already rotated and the post-extend paths then show nothing to change.
        bool matchedMonitorRotates =
            matchResult.Matches.Any(m => profile.Monitors[m.Key].Rotation != currentConfig[m.Value].Rotation);

        bool vendorPathRotates = matchedMonitorRotates ||
            // A monitor being switched on straight into a rotation has no "before" to
            // compare against, and is the case most likely to strand the plane.
            matchResult.UnmatchedProfile.Any(i =>
                profile.Monitors[i].IsEnabled && profile.Monitors[i].Rotation != DisplayRotation.None);

        // Which monitors are off right now. Read before the topology extend below, since
        // that reassigns matchResult and a panel switched on by it is indistinguishable
        // afterwards from one that was already running.
        var offBeforeApply = matchResult.UnmatchedProfile
            .Where(i => profile.Monitors[i].IsEnabled)
            .ToHashSet();

        // NVAPI owns the whole apply when the user picked it, bypassing CCD. Gated on the
        // setting like the AMD branch below: before this, "Windows" still routed every
        // NVIDIA apply through the driver, so the setting did not mean what it said.
        try
        {
            var nvService = App.Services.GetRequiredService<NvidiaRotationService>();
            if (SelectedMethod == RotationMethod.NvidiaDriver
                && nvService.IsAvailable && nvService.ApplyFullProfile(profile))
            {
                Helpers.BootLog.Write("apply.branch",
                    $"nvapi ok, {profile.Monitors.Count(m => m.IsEnabled)} enabled, rotates={vendorPathRotates}");

                // A branch that returns early owns the whole apply, scaling included.
                // NVAPI has no scaling API, so it goes through CCD either way.
                ApplyDpiScaling(profile);
                UnstickCursor();
                if (vendorPathRotates) ResetCursorPlane(trigger);

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
                Helpers.BootLog.Write("apply.branch", "adl ok");
                // No cursor-plane reset here. Radeon rotates without stranding the
                // pointer — reported from an integrated Radeon — so there is nothing
                // to pay a blackout for. See .claude/rules/30-display-apis.md.
                ApplyDpiScaling(profile);
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
            // Nothing but EXTEND and APPLY. Measured: adding ALLOW_CHANGES or
            // SAVE_TO_DATABASE makes SetDisplayConfig answer 87 and no monitor comes
            // back. See .claude/rules/30-display-apis.md.
            int extend = NativeDisplayApi.SetDisplayConfig(0, null, 0, null,
                SDC_FLAGS.SDC_TOPOLOGY_EXTEND | SDC_FLAGS.SDC_APPLY);
            Helpers.BootLog.Write("apply.topology-extend", extend.ToString());

            // Wait and retry matching until all monitors appear or timeout
            for (int attempt = 0; attempt < 3; attempt++)
            {
                Thread.Sleep(1000);
                currentConfig = GetCurrentConfiguration();
                matchResult = MonitorMatcher.Match(profileIdentities, currentConfig.ToIdentities());
                if (matchResult.UnmatchedProfile.Count(i => profile.Monitors[i].IsEnabled) == 0)
                    break;
            }
        }

        // Phase 4: Determine which monitors to disable. A profile describes the whole
        // desktop, so a monitor it switches off and a monitor it never mentions both end
        // up off; that is the only reading under which applying a profile is predictable.
        var disabledCurrentIndices = new HashSet<int>(matchResult.UnmatchedCurrent);
        foreach (var (profileIdx, currentIdx) in matchResult.Matches)
        {
            if (!profile.Monitors[profileIdx].IsEnabled)
                disabledCurrentIndices.Add(currentIdx);
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

        // GetDisplayConfigBufferSizes sizes for the worst case; the query writes back how
        // many entries it filled. Keeping the longer array hands SetDisplayConfig
        // uninitialised paths, which it rejects with ERROR_INVALID_PARAMETER.
        if (activePathCount < activePaths.Length) activePaths = activePaths[..(int)activePathCount];
        if (activeModeCount < activeModes.Length) activeModes = activeModes[..(int)activeModeCount];

        // Seeded from the pre-extend comparison, not from nothing: by the time the loop
        // below reads a path, the extend may already have applied the rotation this apply
        // is responsible for.
        bool hasRotationChange = matchedMonitorRotates;
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

                // A panel switched on by the extend above comes up at the rotation Windows
                // remembered for it, so nothing here differs and the comparison misses it.
                // Turning on into a rotation is the case most likely to strand the plane.
                if (offBeforeApply.Contains(matchedProfileIdx.Value)
                    && profileMonitor.Rotation != DisplayRotation.None)
                    hasRotationChange = true;

                if (useDriverRotation && profileMonitor.Rotation != DisplayRotation.None)
                {
                    driverRotationTasks.Add((currentConfig[matchedCurrentIdx!.Value], profileMonitor.Rotation));
                }
                else
                {
                    activePaths[p].targetInfo.rotation = newRotation;
                }
                bool rateChanged =
                    activePaths[p].targetInfo.refreshRate.Numerator != profileMonitor.RefreshRateNumerator
                    || activePaths[p].targetInfo.refreshRate.Denominator != profileMonitor.RefreshRateDenominator;

                activePaths[p].targetInfo.refreshRate.Numerator = profileMonitor.RefreshRateNumerator;
                activePaths[p].targetInfo.refreshRate.Denominator = profileMonitor.RefreshRateDenominator;

                bool sizeChanged = false;
                var srcIdx = activePaths[p].sourceInfo.modeInfoIdx;
                if (srcIdx < activeModeCount)
                {
                    activeModes[srcIdx].sourceMode.position.x = profileMonitor.PositionX;
                    activeModes[srcIdx].sourceMode.position.y = profileMonitor.PositionY;

                    var (w, h) = RotationGeometry.ToSource(
                        profileMonitor.Width, profileMonitor.Height, (int)profileMonitor.Rotation);
                    sizeChanged = activeModes[srcIdx].sourceMode.width != (uint)w
                        || activeModes[srcIdx].sourceMode.height != (uint)h;
                    activeModes[srcIdx].sourceMode.width = (uint)w;
                    activeModes[srcIdx].sourceMode.height = (uint)h;
                }

                // A new size or rate leaves the target mode describing the old timing,
                // and the stale entry wins. Dropping the index asks Windows to derive
                // one from the refresh rate, which is what a mode change needs.
                if (rateChanged || sizeChanged)
                    activePaths[p].targetInfo.modeInfoIdx = ModeIdxInvalid;
            }
        }

        // A supplied config may only carry modes its paths still point at. Both dropping
        // a path and invalidating a target mode index orphan entries, and either one on
        // its own makes SetDisplayConfig answer 87, so compact unconditionally.
        var keptPaths = pathsToRemove.Count > 0
            ? activePaths.Where((_, i) => !pathsToRemove.Contains(i)).ToArray()
            : activePaths;
        var (finalPaths, finalModes) = CompactModes(keptPaths, activeModes);

        if (finalPaths.Length == 0) return DisplayApplyResult.Failed;

        // ALLOW_CHANGES skips validation, which is too strict to be useful here.
        // Measured: adding SDC_PATH_PERSIST_IF_REQUIRED makes this 87 on every apply, so
        // every success here was really the retry. See .claude/rules/30-display-apis.md.
        var persistFlags = SDC_FLAGS.SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_FLAGS.SDC_APPLY
            | SDC_FLAGS.SDC_SAVE_TO_DATABASE | SDC_FLAGS.SDC_ALLOW_CHANGES;
        result = NativeDisplayApi.SetDisplayConfig(
            (uint)finalPaths.Length, finalPaths,
            (uint)finalModes.Length, finalModes,
            persistFlags);

        int firstResult = result;

        // Last resort: let Windows pick the modes rather than refuse the whole apply.
        // Supplying no mode array means every index has to say so too; leaving real
        // indices next to a count of zero is a second malformed call.
        if (result != NativeDisplayApi.ERROR_SUCCESS)
        {
            var modelessPaths = (DISPLAYCONFIG_PATH_INFO[])finalPaths.Clone();
            for (int i = 0; i < modelessPaths.Length; i++)
            {
                modelessPaths[i].sourceInfo.modeInfoIdx = ModeIdxInvalid;
                modelessPaths[i].targetInfo.modeInfoIdx = ModeIdxInvalid;
            }

            result = NativeDisplayApi.SetDisplayConfig(
                (uint)modelessPaths.Length, modelessPaths, 0, null,
                SDC_FLAGS.SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_FLAGS.SDC_APPLY
                | SDC_FLAGS.SDC_SAVE_TO_DATABASE
                | SDC_FLAGS.SDC_ALLOW_CHANGES);
        }

        // The only record of which backend ran and what it answered. 87 here is
        // ERROR_INVALID_PARAMETER; see .claude/rules/30-display-apis.md.
        Helpers.BootLog.Write("apply.branch",
            $"ccd {finalPaths.Length} paths, {finalModes.Length} modes, " +
            $"SetDisplayConfig -> {firstResult}" +
            (firstResult == result ? "" : $", retry -> {result}") +
            $", rotates={hasRotationChange}, trigger={trigger}");

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
                        // IntelDriver lands here: IGCL exposes no rotation, so CCD does it.
                        _ => false,
                    };
                }
            }
            catch { }

            Thread.Sleep(500);
            NativeDisplayApi.ClipCursor(IntPtr.Zero);
        }

        ApplyDpiScaling(profile);

        if (hasRotationChange)
        {
            UnstickCursor();
            ResetCursorPlane(trigger);
        }

        return matchResult.UnmatchedProfile.Count > 0
            ? DisplayApplyResult.PartialMatch
            : DisplayApplyResult.Success;
    }

    /// <summary>Applies a whole profile through the Radeon driver, verifying by reading
    /// the configuration back — a mismatch returns false so CCD corrects it. That check
    /// is what makes the resolution guess safe: .claude/rules/30-display-apis.md.</summary>
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

    /// <summary>Evaluates many profiles against a single hardware read.
    /// <see cref="CheckCompatibility"/> costs two CCD round trips each, so a loop over N
    /// profiles cost 2N on every display change; the hardware cannot change.</summary>
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
        var matchResult = MonitorMatcher.Match(profile.Monitors.ToIdentities(), currentConfig.ToIdentities());

        // allConnected comes from ALL_PATHS, which distinguishes "not connected" from
        // "connected but disabled".
        var missingMonitors = new List<string>();
        var disabledMonitors = new List<string>();
        foreach (var idx in matchResult.UnmatchedProfile)
        {
            var pm = profile.Monitors[idx];

            // An entry the profile wants switched off is satisfied by the monitor not
            // being active. It is neither missing nor about to wake up.
            if (!pm.IsEnabled) continue;

            bool connectedButDisabled = allConnected.Any(t =>
                t.devicePath == pm.DevicePath ||
                (t.mfrId != 0 && t.mfrId == pm.EdidManufacturerId && t.prodId == pm.EdidProductCodeId && t.connector == pm.ConnectorInstance));
            if (connectedButDisabled)
                disabledMonitors.Add(pm.FriendlyName);
            else
                missingMonitors.Add(pm.FriendlyName);
        }

        // A plugged-in but switched-off monitor is not a blocker — the apply turns it
        // back on — but say so, since the screen waking up is otherwise a surprise.
        // This list used to be computed and discarded, leaving the InfoBar empty.
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

    /// <summary>Keeps only the mode entries the surviving paths point at, renumbering
    /// their indices to match. Measured: without this, dropping a path leaves orphaned
    /// modes and SetDisplayConfig answers 87.</summary>
    private static (DISPLAYCONFIG_PATH_INFO[], DISPLAYCONFIG_MODE_INFO[]) CompactModes(
        DISPLAYCONFIG_PATH_INFO[] paths, DISPLAYCONFIG_MODE_INFO[] modes)
    {
        var kept = new List<DISPLAYCONFIG_MODE_INFO>();
        var moved = new Dictionary<uint, uint>();

        uint Remap(uint idx)
        {
            if (idx == ModeIdxInvalid || idx >= modes.Length) return ModeIdxInvalid;
            if (moved.TryGetValue(idx, out var to)) return to;
            to = (uint)kept.Count;
            kept.Add(modes[idx]);
            moved[idx] = to;
            return to;
        }

        var result = new DISPLAYCONFIG_PATH_INFO[paths.Length];
        for (int i = 0; i < paths.Length; i++)
        {
            var path = paths[i];
            path.sourceInfo.modeInfoIdx = Remap(path.sourceInfo.modeInfoIdx);
            path.targetInfo.modeInfoIdx = Remap(path.targetInfo.modeInfoIdx);
            result[i] = path;
        }
        return (result, kept.ToArray());
    }

    /// <summary>Blanks and wakes the panels so the GPU reseats the cursor plane. Every
    /// condition here exists to keep a full-screen blackout away from someone who did
    /// not ask for one: see .claude/rules/40-safety-invariants.md.</summary>
    private static void ResetCursorPlane(ApplyTrigger trigger)
    {
        // Unattended triggers are deliberately the quiet ones. A scheduled switch must
        // not black out a machine nobody is sitting at, and a revert must not blank a
        // second time on top of the apply that caused it.
        if (trigger != ApplyTrigger.User) return;

        try
        {
            var settings = App.Services.GetRequiredService<ISettingsService>();
            if (!settings.Settings.ResetCursorAfterRotation) return;
        }
        catch { return; }

        CursorPlaneReset.Run();
    }

    /// <summary>Releases a cursor clip left behind by a rotation. It does not fix the
    /// rotated-cursor-plane bug — only <see cref="ResetCursorPlane"/> does; see
    /// .claude/rules/30-display-apis.md.</summary>
    private static void UnstickCursor()
    {
        Thread.Sleep(500);

        for (int i = 0; i < 5; i++)
        {
            NativeDisplayApi.ClipCursor(IntPtr.Zero);
            Thread.Sleep(100);
        }

        int cx = NativeDisplayApi.GetSystemMetrics(NativeDisplayApi.SM_CXSCREEN) / 2;
        int cy = NativeDisplayApi.GetSystemMetrics(NativeDisplayApi.SM_CYSCREEN) / 2;
        NativeDisplayApi.SetCursorPos(cx, cy);
        NativeDisplayApi.ClipCursor(IntPtr.Zero);

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

    // ── Supported modes ──

    /// <summary>The mode Windows has on record for a display it is not currently driving.
    /// The extent and the rotation come out of the same DEVMODE: the pels are in that
    /// orientation, so pairing them with anything else records a mode the panel lacks.</summary>
    private static bool TryReadLastKnownMode(
        string gdiDeviceName, out int width, out int height, out Models.DisplayRotation rotation)
    {
        width = height = 0;
        rotation = Models.DisplayRotation.None;
        if (string.IsNullOrEmpty(gdiDeviceName)) return false;

        var dm = new NativeDisplayApi.DEVMODE { dmSize = (ushort)Marshal.SizeOf<NativeDisplayApi.DEVMODE>() };
        if (!NativeDisplayApi.EnumDisplaySettings(gdiDeviceName, NativeDisplayApi.ENUM_CURRENT_SETTINGS, ref dm))
            return false;

        width = (int)dm.dmPelsWidth;
        height = (int)dm.dmPelsHeight;
        if ((dm.dmFields & NativeDisplayApi.DM_DISPLAYORIENTATION) != 0)
        {
            rotation = dm.dmDisplayOrientation switch
            {
                NativeDisplayApi.DMDO_90 => Models.DisplayRotation.Rotate90,
                NativeDisplayApi.DMDO_180 => Models.DisplayRotation.Rotate180,
                NativeDisplayApi.DMDO_270 => Models.DisplayRotation.Rotate270,
                _ => Models.DisplayRotation.None,
            };
        }
        return width > 0 && height > 0;
    }

    /// <summary>Enumerates the panel's modes through GDI and normalizes every one to the
    /// panel-native orientation, so a list read while the display is on its side still
    /// matches one read upright.</summary>
    public IReadOnlyList<DisplayMode> GetAvailableModes(MonitorInfo monitor)
    {
        var device = monitor.GdiDeviceName;
        if (string.IsNullOrEmpty(device))
            return [];

        var seen = new HashSet<(int, int, int)>();
        var modes = new List<DisplayMode>();

        var dm = new NativeDisplayApi.DEVMODE();
        for (int i = 0; ; i++)
        {
            // Reset before every call, not after: a driver may write back a smaller
            // dmSize, and skipping the reset on a filtered entry would carry it forward.
            dm.dmSize = (ushort)Marshal.SizeOf<NativeDisplayApi.DEVMODE>();
            if (!NativeDisplayApi.EnumDisplaySettings(device, i, ref dm)) break;

            // 8- and 16-bit legacy entries are noise on every modern panel.
            if (dm.dmBitsPerPel < 32) continue;

            // The enumeration reports pels in the display's current orientation, but
            // dmDisplayOrientation only means anything when dmFields says it was filled
            // in. Fall back to the rotation CCD already told us about.
            int degrees = (dm.dmFields & NativeDisplayApi.DM_DISPLAYORIENTATION) != 0
                ? dm.dmDisplayOrientation switch
                {
                    NativeDisplayApi.DMDO_90 => 90,
                    NativeDisplayApi.DMDO_180 => 180,
                    NativeDisplayApi.DMDO_270 => 270,
                    _ => 0,
                }
                : (int)monitor.Rotation;

            var (w, h) = RotationGeometry.ToSource((int)dm.dmPelsWidth, (int)dm.dmPelsHeight, degrees);
            int hz = (int)dm.dmDisplayFrequency;
            if (w <= 0 || h <= 0 || hz <= 1) continue;

            if (seen.Add((w, h, hz)))
                modes.Add(new DisplayMode(w, h, hz));
        }

        return [.. modes
            .OrderByDescending(m => (long)m.Width * m.Height)
            .ThenByDescending(m => m.Width)
            .ThenByDescending(m => m.RefreshHz)];
    }

    // ── Per-monitor scaling ──

    /// <summary>Pushes each profile monitor's scaling. Runs after the topology has
    /// settled, since the source id a scale is addressed by only exists once the display
    /// is part of the desktop.</summary>
    private void ApplyDpiScaling(DisplayProfile profile)
    {
        List<MonitorInfo> live;
        try { live = GetCurrentConfiguration(); }
        catch { return; }

        int changed = 0;
        foreach (var wanted in profile.Monitors)
        {
            if (!wanted.IsEnabled || wanted.DpiScale <= 0) continue;

            var target = live.FirstOrDefault(wanted.IsSameMonitorAs);
            if (target == null || target.DpiScale == wanted.DpiScale) continue;

            try { if (SetDpiScale(target, wanted.DpiScale)) changed++; }
            catch { }
        }

        if (changed > 0) Helpers.BootLog.Write("apply.dpi", $"{changed} monitor(s) rescaled");
    }

    /// <summary>Scaling percentage straight off a path, for the enumeration loops that
    /// have not built a MonitorInfo yet.</summary>
    private static int ReadDpiPercent(DISPLAYCONFIG_PATH_INFO path)
    {
        var request = new DISPLAYCONFIG_SOURCE_DPI_SCALE_GET
        {
            header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_DPI_SCALE,
                size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DPI_SCALE_GET>(),
                adapterId = path.sourceInfo.adapterId,
                id = path.sourceInfo.id,
            },
        };
        return NativeDisplayApi.DisplayConfigGetDeviceInfo(ref request) == NativeDisplayApi.ERROR_SUCCESS
            ? DpiScaling.ToPercent(request.minScaleRel, request.curScaleRel)
            : DpiScaling.Default;
    }

    private static DISPLAYCONFIG_SOURCE_DPI_SCALE_GET BuildDpiRequest(MonitorInfo monitor) => new()
    {
        header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
        {
            type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_DPI_SCALE,
            size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DPI_SCALE_GET>(),
            adapterId = new LUID { LowPart = (uint)(monitor.AdapterId & 0xFFFFFFFF), HighPart = (int)(monitor.AdapterId >> 32) },
            id = monitor.SourceId,
        },
    };

    public DpiScaleState GetDpiScale(MonitorInfo monitor)
    {
        var request = BuildDpiRequest(monitor);
        if (NativeDisplayApi.DisplayConfigGetDeviceInfo(ref request) != NativeDisplayApi.ERROR_SUCCESS)
            return new DpiScaleState(DpiScaling.Default, []);

        return new DpiScaleState(
            DpiScaling.ToPercent(request.minScaleRel, request.curScaleRel),
            DpiScaling.AvailablePercentages(request.minScaleRel, request.maxScaleRel));
    }

    public bool SetDpiScale(MonitorInfo monitor, int percent)
    {
        var request = BuildDpiRequest(monitor);
        if (NativeDisplayApi.DisplayConfigGetDeviceInfo(ref request) != NativeDisplayApi.ERROR_SUCCESS)
            return false;

        // A percentage off the ladder entirely is refused; one on the ladder but past
        // what this display allows is clamped into range, since a profile carried over
        // from a larger monitor is better served by the nearest scale than by nothing.
        if (!DpiScaling.TryToRelative(request.minScaleRel, request.maxScaleRel, percent, out int relative))
            return false;

        if (relative == request.curScaleRel) return true;

        var set = new DISPLAYCONFIG_SOURCE_DPI_SCALE_SET
        {
            header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_SET_DPI_SCALE,
                size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DPI_SCALE_SET>(),
                adapterId = request.header.adapterId,
                id = request.header.id,
            },
            scaleRel = relative,
        };
        return NativeDisplayApi.DisplayConfigSetDeviceInfo(ref set) == NativeDisplayApi.ERROR_SUCCESS;
    }

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
