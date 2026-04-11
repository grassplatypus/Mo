using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using Mo.Models;
using Mo.Services;

namespace Mo.Helpers;

public static class SystemInfoHelper
{
    public static string BuildFullReport(IDisplayService? displayService = null, IMonitorColorService? colorService = null)
    {
        var sb = new StringBuilder();

        // App
        sb.AppendLine($"Mo v{UpdateService.CurrentVersion}");
        sb.AppendLine($"OS: {Environment.OSVersion.VersionString} ({RuntimeInformation.OSArchitecture})");
        sb.AppendLine($".NET: {RuntimeInformation.FrameworkDescription}");
        sb.AppendLine($"Process: {RuntimeInformation.ProcessArchitecture}");

        // CPU
        try
        {
            sb.AppendLine($"CPU: {GetCpuName()} ({Environment.ProcessorCount} cores)");
        }
        catch
        {
            sb.AppendLine($"CPU: {Environment.ProcessorCount} cores");
        }

        // Memory
        try
        {
            var mem = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            sb.AppendLine($"Memory: {mem / 1024 / 1024:N0} MB");
        }
        catch { }

        // GPU
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name, DriverVersion, AdapterRAM FROM Win32_VideoController");
            foreach (ManagementObject obj in searcher.Get())
            {
                var name = obj["Name"]?.ToString() ?? "Unknown";
                var driver = obj["DriverVersion"]?.ToString() ?? "?";
                var vram = obj["AdapterRAM"] is uint ram ? $"{ram / 1024 / 1024} MB" : "?";
                sb.AppendLine($"GPU: {name} (Driver: {driver}, VRAM: {vram})");
            }
        }
        catch { }

        sb.AppendLine();

        // Monitors
        try
        {
            displayService ??= App.Services?.GetService(typeof(IDisplayService)) as IDisplayService;
            colorService ??= App.Services?.GetService(typeof(IMonitorColorService)) as IMonitorColorService;

            if (displayService != null)
            {
                var monitors = displayService.GetCurrentConfiguration();
                var caps = new List<MonitorColorCapabilities>();
                try { caps = colorService?.DetectCapabilities() ?? []; } catch { }

                sb.AppendLine($"=== Monitors ({monitors.Count}) ===");

                for (int i = 0; i < monitors.Count; i++)
                {
                    var m = monitors[i];
                    sb.AppendLine();
                    var name = string.IsNullOrEmpty(m.FriendlyName) ? $"Display {i + 1}" : m.FriendlyName;
                    sb.AppendLine($"[{i + 1}] {name}{(m.IsPrimary ? " [Primary]" : "")}");
                    sb.AppendLine($"    Resolution: {m.Width}x{m.Height}");
                    sb.AppendLine($"    Refresh Rate: {m.RefreshRateHz:F1} Hz");
                    sb.AppendLine($"    Position: ({m.PositionX}, {m.PositionY})");
                    sb.AppendLine($"    Rotation: {(m.Rotation == DisplayRotation.None ? "None" : $"{(int)m.Rotation}")}");
                    sb.AppendLine($"    EDID: Mfr=0x{m.EdidManufacturerId:X4} Product=0x{m.EdidProductCodeId:X4} Connector={m.ConnectorInstance}");

                    if (!string.IsNullOrEmpty(m.DevicePath))
                        sb.AppendLine($"    Path: {m.DevicePath}");

                    // Color capabilities
                    if (i < caps.Count)
                    {
                        var c = caps[i];
                        sb.AppendLine($"    DDC/CI Support:");
                        sb.AppendLine($"      Brightness: {(c.SupportsBrightness ? "Yes" : c.SupportsWmiBrightness ? "WMI only" : "No")}");
                        sb.AppendLine($"      Contrast:   {(c.SupportsContrast ? "Yes" : "No")}");
                        sb.AppendLine($"      RGB Gain:   R={YesNo(c.SupportsRedGain)} G={YesNo(c.SupportsGreenGain)} B={YesNo(c.SupportsBlueGain)}");
                    }

                    // Current color values
                    if (m.ColorSettings is { HasValues: true } cs)
                    {
                        sb.AppendLine($"    Current Color:");
                        if (cs.Brightness.HasValue) sb.AppendLine($"      Brightness: {cs.Brightness}%");
                        if (cs.Contrast.HasValue) sb.AppendLine($"      Contrast:   {cs.Contrast}%");
                        if (cs.RedGain.HasValue || cs.GreenGain.HasValue || cs.BlueGain.HasValue)
                            sb.AppendLine($"      RGB Gain:   R={cs.RedGain ?? 0}% G={cs.GreenGain ?? 0}% B={cs.BlueGain ?? 0}%");
                    }
                }
            }
        }
        catch
        {
            sb.AppendLine("Monitors: (failed to enumerate)");
        }

        return sb.ToString();
    }

    public static string BuildErrorReport(Exception ex)
    {
        var sb = new StringBuilder();
        sb.AppendLine(BuildFullReport());
        sb.AppendLine();
        sb.AppendLine($"=== Exception ===");
        sb.AppendLine($"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Type: {ex.GetType().FullName}");
        sb.AppendLine($"Message: {ex.Message}");
        sb.AppendLine();
        sb.AppendLine("Stack Trace:");
        sb.AppendLine(ex.StackTrace ?? "(none)");

        var inner = ex.InnerException;
        var depth = 0;
        while (inner != null && depth < 3)
        {
            sb.AppendLine();
            sb.AppendLine($"--- Inner Exception [{depth}] ---");
            sb.AppendLine($"Type: {inner.GetType().FullName}");
            sb.AppendLine($"Message: {inner.Message}");
            sb.AppendLine(inner.StackTrace ?? "(none)");
            inner = inner.InnerException;
            depth++;
        }

        return sb.ToString();
    }

    private static string GetCpuName()
    {
        using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_Processor");
        foreach (ManagementObject obj in searcher.Get())
            return obj["Name"]?.ToString()?.Trim() ?? "Unknown";
        return "Unknown";
    }

    private static string YesNo(bool val) => val ? "Yes" : "No";
}
