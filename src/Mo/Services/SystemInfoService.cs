using System.Management;
using System.Runtime.InteropServices;
using Mo.Helpers;

namespace Mo.Services;

// Pulls hardware identity (CPU, RAM, GPU) and a detailed monitor list. WMI calls are
// expensive — this service runs them on a thread pool thread and returns plain DTOs
// that the UI can bind to without wrapping each call in its own try/catch.
public sealed class SystemInfoService : ISystemInfoService
{
    public async Task<SystemSummary> LoadAsync()
    {
        return await Task.Run(() =>
        {
            return new SystemSummary(
                Os: $"{Environment.OSVersion} ({RuntimeInformation.OSArchitecture})",
                Cpu: SafeWmi("SELECT Name FROM Win32_Processor", "Name"),
                Ram: TryGetRam(),
                Gpu: SafeWmiList("SELECT Name FROM Win32_VideoController", "Name"),
                Monitors: SystemInfoHelper.GetMonitorDetails(),
                DebugReport: TryGetReport());
        });
    }

    private static string TryGetRam()
    {
        try
        {
            var gcInfo = GC.GetGCMemoryInfo();
            return $"{gcInfo.TotalAvailableMemoryBytes / (1024 * 1024 * 1024.0):F1} GB";
        }
        catch { return "Unknown"; }
    }

    private static string TryGetReport()
    {
        try { return SystemInfoHelper.BuildFullReport(); }
        catch { return "(Failed to load)"; }
    }

    private static string SafeWmi(string query, string property)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(query);
            foreach (ManagementObject obj in searcher.Get())
                return obj[property]?.ToString()?.Trim() ?? "Unknown";
            return "Unknown";
        }
        catch { return "Unknown"; }
    }

    private static string SafeWmiList(string query, string property)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(query);
            var values = new List<string>();
            foreach (ManagementObject obj in searcher.Get())
                values.Add(obj[property]?.ToString()?.Trim() ?? "Unknown");
            return values.Count > 0 ? string.Join(", ", values) : "Unknown";
        }
        catch { return "Unknown"; }
    }
}

public interface ISystemInfoService
{
    Task<SystemSummary> LoadAsync();
}

public sealed record SystemSummary(
    string Os,
    string Cpu,
    string Ram,
    string Gpu,
    List<MonitorDisplayInfo> Monitors,
    string DebugReport);
