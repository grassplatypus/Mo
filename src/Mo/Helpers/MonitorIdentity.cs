using Mo.Core.DisplayConfiguration;
using Mo.Models;

namespace Mo.Helpers;

/// <summary>Bridges the app's MonitorInfo to the identity Mo.Core matches on, so one rule
/// decides "same monitor" for the editor, the compatibility check and the colour push.</summary>
public static class MonitorIdentityExtensions
{
    public static MonitorMatcher.MonitorIdentity ToIdentity(this MonitorInfo monitor) =>
        new(monitor.DevicePath, monitor.EdidManufacturerId, monitor.EdidProductCodeId,
            monitor.ConnectorInstance, monitor.FriendlyName);

    public static bool IsSameMonitorAs(this MonitorInfo monitor, MonitorInfo other) =>
        MonitorMatcher.IsSameMonitor(monitor.ToIdentity(), other.ToIdentity());

    public static List<MonitorMatcher.MonitorIdentity> ToIdentities(this IEnumerable<MonitorInfo> monitors) =>
        [.. monitors.Select(ToIdentity)];
}
