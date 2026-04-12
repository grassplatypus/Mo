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

    public bool ApplyFullProfile(DisplayProfile profile, List<MonitorInfo> currentConfig, MonitorMatcher.MatchResult matchResult)
    {
        if (!IsAvailable) return false;

        try
        {
            var gpu = PhysicalGPU.GetPhysicalGPUs().FirstOrDefault();
            if (gpu == null) return false;

            var allConnected = gpu.GetConnectedDisplayDevices(ConnectedIdsFlag.None);
            var currentPaths = PathInfo.GetDisplaysConfig();

            // Build a map of DisplayDevice by DisplayId for quick lookup
            var deviceById = new Dictionary<uint, DisplayDevice>();
            foreach (var d in allConnected)
                deviceById[d.DisplayId] = d;
            foreach (var path in currentPaths)
                foreach (var target in path.TargetsInfo)
                    deviceById.TryAdd(target.DisplayDevice.DisplayId, target.DisplayDevice);

            // Build new NVAPI config from the profile
            var newPaths = new List<PathInfo>();

            foreach (var (profileIdx, currentIdx) in matchResult.Matches)
            {
                var pm = profile.Monitors[profileIdx];
                if (!pm.IsEnabled) continue;

                var cm = currentConfig.Count > currentIdx ? currentConfig[currentIdx] : null;

                // Find the DisplayDevice for this monitor
                DisplayDevice? device = null;
                foreach (var path in currentPaths)
                {
                    foreach (var target in path.TargetsInfo)
                    {
                        if (cm != null && target.DisplayDevice.DisplayId == cm.TargetId)
                        { device = target.DisplayDevice; break; }
                    }
                    if (device != null) break;
                }

                // If not in active paths, find in all connected
                if (device == null && cm != null)
                    deviceById.TryGetValue(cm.TargetId, out device);

                if (device == null) continue;

                var targetInfo = new PathTargetInfo(device);
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

                var pathInfo = new PathInfo(
                    new NvAPIWrapper.Native.Display.Structures.Resolution(w, h, 32),
                    ColorFormat.A8R8G8B8,
                    [targetInfo]);

                newPaths.Add(pathInfo);
            }

            // Also handle unmatched profile monitors (connected but inactive)
            foreach (var unmatchedIdx in matchResult.UnmatchedProfile)
            {
                var pm = profile.Monitors[unmatchedIdx];
                if (!pm.IsEnabled) continue;

                DisplayDevice? device = null;
                foreach (var d in allConnected)
                {
                    if (d.DisplayId == pm.TargetId)
                    { device = d; break; }
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

            // Keep unmatched current monitors if UnmatchedAction == Keep
            if (profile.UnmatchedAction == UnmatchedMonitorAction.Keep)
            {
                foreach (var unmatchedCurrentIdx in matchResult.UnmatchedCurrent)
                {
                    var cm = currentConfig[unmatchedCurrentIdx];
                    foreach (var path in currentPaths)
                    {
                        foreach (var target in path.TargetsInfo)
                        {
                            if (target.DisplayDevice.DisplayId == cm.TargetId)
                            {
                                newPaths.Add(path);
                                goto nextUnmatched;
                            }
                        }
                    }
                    nextUnmatched:;
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
            bool modified = false;

            foreach (var path in currentPaths)
            {
                foreach (var target in path.TargetsInfo)
                {
                    if (target.DisplayDevice.DisplayId != monitor.TargetId) continue;
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
}
