using Mo.Models;

namespace Mo.Services;

public interface IMonitorColorService
{
    /// <summary>Detect DDC/CI capabilities for each monitor.</summary>
    List<MonitorColorCapabilities> DetectCapabilities();

    List<MonitorColorSettings> CaptureAllMonitors();
    void ApplyToMonitor(int monitorIndex, MonitorColorSettings settings);
    void ApplyAll(List<(int index, MonitorColorSettings settings)> entries);
}
