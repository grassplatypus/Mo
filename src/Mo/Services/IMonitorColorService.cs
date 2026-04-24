using Mo.Models;

namespace Mo.Services;

public interface IMonitorColorService
{
    /// <summary>Detect DDC/CI capabilities for each monitor.</summary>
    List<MonitorColorCapabilities> DetectCapabilities();

    List<MonitorColorSettings> CaptureAllMonitors();
    void ApplyToMonitor(int monitorIndex, MonitorColorSettings settings);
    void ApplyAll(List<(int index, MonitorColorSettings settings)> entries);

    /// <summary>Read a raw VCP feature value. Returns (current, max) or null if unsupported.</summary>
    (uint current, uint max)? GetVcpFeature(int monitorIndex, byte vcpCode);

    /// <summary>Write a raw VCP feature value. Returns true on success.</summary>
    bool SetVcpFeature(int monitorIndex, byte vcpCode, uint value);
}
