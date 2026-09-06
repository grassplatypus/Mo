using System.Management;
using System.Runtime.InteropServices;
using Mo.Helpers;

namespace Mo.Services;

// Pulls hardware identity (CPU, RAM, GPU) and a detailed monitor list. WMI calls are
// expensive, so this service runs them on a thread pool thread and returns plain DTOs
// the UI can bind to without wrapping each call in its own try/catch.
public sealed class SystemInfoService : ISystemInfoService
{
    private const string Unknown = "Unknown";

    public async Task<SystemSummary> LoadAsync()
    {
        return await Task.Run(() =>
        {
            return new SystemSummary(
                Os: $"{Environment.OSVersion} ({RuntimeInformation.OSArchitecture})",
                Cpu: SafeWmi("SELECT Name FROM Win32_Processor", "Name"),
                Ram: TryGetRam(),
                Gpu: TryGetGpus(),
                Monitors: SystemInfoHelper.GetMonitorDetails());
        });
    }

    private static string TryGetRam()
    {
        try
        {
            var gcInfo = GC.GetGCMemoryInfo();
            return $"{gcInfo.TotalAvailableMemoryBytes / (1024 * 1024 * 1024.0):F1} GB";
        }
        catch { return Unknown; }
    }

    /// <summary>The graphics hardware actually driving a screen. Win32_VideoController
    /// lists every adapter Windows knows about, including software ones that report a
    /// resolution without a panel behind them.</summary>
    private static string TryGetGpus()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, PNPDeviceID, CurrentHorizontalResolution FROM Win32_VideoController");

            var names = new List<string>();
            foreach (ManagementObject obj in searcher.Get())
            {
                // PCI rules out the virtual adapters remote-desktop and screen-sharing
                // tools install, which sit on ROOT and drive a display nobody can see.
                var pnp = obj["PNPDeviceID"]?.ToString() ?? string.Empty;
                if (!pnp.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase)) continue;

                // A resolution is set only while the adapter has an active mode, which
                // is the closest WMI gets to "something is plugged into this".
                if (obj["CurrentHorizontalResolution"] is not uint width || width == 0) continue;

                var name = obj["Name"]?.ToString()?.Trim();
                if (!string.IsNullOrEmpty(name)) names.Add(name);
            }

            if (names.Count > 0) return string.Join(", ", names.Distinct());
        }
        catch { }

        // Nothing matched, which a filter this strict can manage on an odd machine.
        // The unfiltered list beats reporting nothing.
        return SafeWmiList("SELECT Name FROM Win32_VideoController", "Name");
    }

    /// <summary>The full hardware report, built only when something asks for it. It walks
    /// WMI and every display path, and building it on the way into Settings competed with
    /// the page's own first layout for no reason: almost nobody opens it.</summary>
    public async Task<string> GetDebugReportAsync() => await Task.Run(() =>
    {
        try { return SystemInfoHelper.BuildFullReport(); }
        catch { return "(Failed to load)"; }
    });

    private static string SafeWmi(string query, string property)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(query);
            foreach (ManagementObject obj in searcher.Get())
                return obj[property]?.ToString()?.Trim() ?? Unknown;
            return Unknown;
        }
        catch { return Unknown; }
    }

    private static string SafeWmiList(string query, string property)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(query);
            var values = new List<string>();
            foreach (ManagementObject obj in searcher.Get())
                values.Add(obj[property]?.ToString()?.Trim() ?? Unknown);

            // Distinct: a machine can list the same adapter model more than once.
            return values.Count > 0 ? string.Join(", ", values.Distinct()) : Unknown;
        }
        catch { return Unknown; }
    }
}

public interface ISystemInfoService
{
    Task<SystemSummary> LoadAsync();

    /// <summary>The full hardware report. Expensive, so ask for it on demand.</summary>
    Task<string> GetDebugReportAsync();
}

public sealed record SystemSummary(
    string Os,
    string Cpu,
    string Ram,
    string Gpu,
    List<MonitorDisplayInfo> Monitors);
