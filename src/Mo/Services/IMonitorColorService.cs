using Mo.Models;

namespace Mo.Services;

public interface IMonitorColorService
{
    List<MonitorColorSettings> CaptureAllMonitors();
    void ApplyToMonitor(int monitorIndex, MonitorColorSettings settings);
    void ApplyAll(List<(int index, MonitorColorSettings settings)> entries);
}
