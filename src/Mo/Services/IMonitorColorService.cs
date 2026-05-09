using Mo.Models;

namespace Mo.Services;

public interface IMonitorColorService : IDisposable
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

    // Device-name-based overloads. Preferred over index-based methods because the
    // EnumDisplayMonitors order does NOT match QueryDisplayConfig order; passing an
    // index from the CCD list to the DDC/CI cache often hits the wrong physical
    // monitor. Pass MonitorInfo.GdiDeviceName ("\\.\DISPLAY1") to target reliably.
    bool ApplyToMonitorByDeviceName(string gdiDeviceName, MonitorColorSettings settings);
    MonitorColorCapabilities? DetectCapabilitiesByDeviceName(string gdiDeviceName);
    MonitorColorSettings? CaptureByDeviceName(string gdiDeviceName);
    bool SetVcpFeatureByDeviceName(string gdiDeviceName, byte vcpCode, uint value);
    (uint current, uint max)? GetVcpFeatureByDeviceName(string gdiDeviceName, byte vcpCode);
}
