using System.Management;
using Microsoft.Win32;
using Mo.Interop.Monitor;
using Mo.Models;
using static Mo.Interop.Monitor.MonitorConfigApi;

namespace Mo.Services;

// Physical monitor handles are expensive to open (~50-100 ms round trip). Interactive
// slider UIs cannot pay that cost per-change, so we open once and hold onto them until
// Windows reports a display-settings change — at which point we rebuild the cache.
public sealed class MonitorColorService : IMonitorColorService
{
    private readonly object _cacheLock = new();
    private List<(PHYSICAL_MONITOR[] physicalMonitors, nint hMonitor)>? _cachedHandles;
    private bool _disposed;

    public MonitorColorService()
    {
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        DestroyCache();
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => DestroyCache();

    private List<(PHYSICAL_MONITOR[] physicalMonitors, nint hMonitor)> GetHandles()
    {
        lock (_cacheLock)
        {
            if (_cachedHandles != null) return _cachedHandles;
            _cachedHandles = GetPhysicalMonitorHandles();
            return _cachedHandles;
        }
    }

    private void DestroyCache()
    {
        lock (_cacheLock)
        {
            if (_cachedHandles == null) return;
            foreach (var (monitors, _) in _cachedHandles)
            {
                try { DestroyPhysicalMonitors((uint)monitors.Length, monitors); } catch { }
            }
            _cachedHandles = null;
        }
    }

    public List<MonitorColorCapabilities> DetectCapabilities()
    {
        var result = new List<MonitorColorCapabilities>();

        foreach (var (physicalMonitors, _) in GetHandles())
        {
            foreach (var pm in physicalMonitors)
                result.Add(ProbeCapabilities(pm.hPhysicalMonitor));
        }

        // If no DDC/CI brightness found, check WMI for laptop display (first monitor)
        if (result.Count > 0 && !result[0].SupportsBrightness)
        {
            result[0].SupportsWmiBrightness = DetectWmiBrightness();
        }

        return result;
    }

    public List<MonitorColorSettings> CaptureAllMonitors()
    {
        var results = new List<MonitorColorSettings>();

        foreach (var (physicalMonitors, _) in GetHandles())
        {
            foreach (var pm in physicalMonitors)
                results.Add(ReadSettings(pm.hPhysicalMonitor));
        }

        // WMI fallback for first monitor if DDC/CI brightness not available
        if (results.Count > 0 && !results[0].Brightness.HasValue)
        {
            var wmiBrightness = GetWmiBrightness();
            if (wmiBrightness.HasValue)
                results[0].Brightness = wmiBrightness.Value;
        }

        return results;
    }

    public void ApplyToMonitor(int monitorIndex, MonitorColorSettings settings)
    {
        int idx = 0;
        foreach (var (physicalMonitors, _) in GetHandles())
        {
            foreach (var pm in physicalMonitors)
            {
                if (idx == monitorIndex)
                {
                    bool applied = WriteSettings(pm.hPhysicalMonitor, settings);
                    if (!applied && settings.Brightness.HasValue && idx == 0)
                        SetWmiBrightness(settings.Brightness.Value);
                    return;
                }
                idx++;
            }
        }
    }

    public void ApplyAll(List<(int index, MonitorColorSettings settings)> entries)
    {
        int idx = 0;
        foreach (var (physicalMonitors, _) in GetHandles())
        {
            foreach (var pm in physicalMonitors)
            {
                var match = entries.FirstOrDefault(e => e.index == idx);
                if (match.settings != null)
                {
                    bool ddcWorked = WriteSettings(pm.hPhysicalMonitor, match.settings);
                    if (!ddcWorked && match.settings.Brightness.HasValue && idx == 0)
                        SetWmiBrightness(match.settings.Brightness.Value);
                }
                idx++;
            }
        }
    }

    public (uint current, uint max)? GetVcpFeature(int monitorIndex, byte vcpCode)
    {
        int idx = 0;
        foreach (var (physicalMonitors, _) in GetHandles())
        {
            foreach (var pm in physicalMonitors)
            {
                if (idx == monitorIndex)
                {
                    try
                    {
                        if (GetVCPFeatureAndVCPFeatureReply(pm.hPhysicalMonitor, vcpCode, IntPtr.Zero, out uint cur, out uint max))
                            return (cur, max);
                    }
                    catch { }
                    return null;
                }
                idx++;
            }
        }
        return null;
    }

    public bool SetVcpFeature(int monitorIndex, byte vcpCode, uint value)
    {
        int idx = 0;
        foreach (var (physicalMonitors, _) in GetHandles())
        {
            foreach (var pm in physicalMonitors)
            {
                if (idx == monitorIndex)
                {
                    try { return SetVCPFeature(pm.hPhysicalMonitor, vcpCode, value); }
                    catch { return false; }
                }
                idx++;
            }
        }
        return false;
    }

    // ── Capability Detection ──

    private static MonitorColorCapabilities ProbeCapabilities(nint hPhysicalMonitor)
    {
        var caps = new MonitorColorCapabilities();

        try { caps.SupportsBrightness = GetMonitorBrightness(hPhysicalMonitor, out _, out _, out _); } catch { }
        try { caps.SupportsContrast = GetMonitorContrast(hPhysicalMonitor, out _, out _, out _); } catch { }
        try { caps.SupportsRedGain = GetMonitorRedGreenOrBlueGain(hPhysicalMonitor, MC_GAIN_TYPE.MC_RED_GAIN, out _, out _, out _); } catch { }
        try { caps.SupportsGreenGain = GetMonitorRedGreenOrBlueGain(hPhysicalMonitor, MC_GAIN_TYPE.MC_GREEN_GAIN, out _, out _, out _); } catch { }
        try { caps.SupportsBlueGain = GetMonitorRedGreenOrBlueGain(hPhysicalMonitor, MC_GAIN_TYPE.MC_BLUE_GAIN, out _, out _, out _); } catch { }

        return caps;
    }

    // ── DDC/CI Read/Write ──

    private static MonitorColorSettings ReadSettings(nint hPhysicalMonitor)
    {
        var s = new MonitorColorSettings();

        try
        {
            if (GetMonitorBrightness(hPhysicalMonitor, out _, out uint brightness, out uint maxBri) && maxBri > 0)
                s.Brightness = (int)(brightness * 100 / maxBri);
        }
        catch { }

        try
        {
            if (GetMonitorContrast(hPhysicalMonitor, out _, out uint contrast, out uint maxCon) && maxCon > 0)
                s.Contrast = (int)(contrast * 100 / maxCon);
        }
        catch { }

        try
        {
            if (GetMonitorRedGreenOrBlueGain(hPhysicalMonitor, MC_GAIN_TYPE.MC_RED_GAIN, out _, out uint r, out uint maxR) && maxR > 0)
                s.RedGain = (int)(r * 100 / maxR);
        }
        catch { }

        try
        {
            if (GetMonitorRedGreenOrBlueGain(hPhysicalMonitor, MC_GAIN_TYPE.MC_GREEN_GAIN, out _, out uint g, out uint maxG) && maxG > 0)
                s.GreenGain = (int)(g * 100 / maxG);
        }
        catch { }

        try
        {
            if (GetMonitorRedGreenOrBlueGain(hPhysicalMonitor, MC_GAIN_TYPE.MC_BLUE_GAIN, out _, out uint b, out uint maxB) && maxB > 0)
                s.BlueGain = (int)(b * 100 / maxB);
        }
        catch { }

        return s;
    }

    /// <returns>true if at least one DDC/CI value was written</returns>
    private static bool WriteSettings(nint hPhysicalMonitor, MonitorColorSettings s)
    {
        bool any = false;

        if (s.Brightness.HasValue)
        {
            try
            {
                if (GetMonitorBrightness(hPhysicalMonitor, out uint minB, out _, out uint maxB))
                {
                    uint val = (uint)(minB + (maxB - minB) * s.Brightness.Value / 100);
                    if (SetMonitorBrightness(hPhysicalMonitor, val)) any = true;
                }
            }
            catch { }
        }

        if (s.Contrast.HasValue)
        {
            try
            {
                if (GetMonitorContrast(hPhysicalMonitor, out uint minC, out _, out uint maxC))
                {
                    uint val = (uint)(minC + (maxC - minC) * s.Contrast.Value / 100);
                    if (SetMonitorContrast(hPhysicalMonitor, val)) any = true;
                }
            }
            catch { }
        }

        if (s.RedGain.HasValue)
        {
            try
            {
                if (GetMonitorRedGreenOrBlueGain(hPhysicalMonitor, MC_GAIN_TYPE.MC_RED_GAIN, out uint min, out _, out uint max))
                    if (SetMonitorRedGreenOrBlueGain(hPhysicalMonitor, MC_GAIN_TYPE.MC_RED_GAIN, (uint)(min + (max - min) * s.RedGain.Value / 100))) any = true;
            }
            catch { }
        }

        if (s.GreenGain.HasValue)
        {
            try
            {
                if (GetMonitorRedGreenOrBlueGain(hPhysicalMonitor, MC_GAIN_TYPE.MC_GREEN_GAIN, out uint min, out _, out uint max))
                    if (SetMonitorRedGreenOrBlueGain(hPhysicalMonitor, MC_GAIN_TYPE.MC_GREEN_GAIN, (uint)(min + (max - min) * s.GreenGain.Value / 100))) any = true;
            }
            catch { }
        }

        if (s.BlueGain.HasValue)
        {
            try
            {
                if (GetMonitorRedGreenOrBlueGain(hPhysicalMonitor, MC_GAIN_TYPE.MC_BLUE_GAIN, out uint min, out _, out uint max))
                    if (SetMonitorRedGreenOrBlueGain(hPhysicalMonitor, MC_GAIN_TYPE.MC_BLUE_GAIN, (uint)(min + (max - min) * s.BlueGain.Value / 100))) any = true;
            }
            catch { }
        }

        return any;
    }

    // ── WMI Brightness (laptop internal display) ──

    private static bool DetectWmiBrightness()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("root\\WMI", "SELECT * FROM WmiMonitorBrightness");
            return searcher.Get().Count > 0;
        }
        catch { return false; }
    }

    private static int? GetWmiBrightness()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("root\\WMI", "SELECT CurrentBrightness FROM WmiMonitorBrightness");
            foreach (var obj in searcher.Get())
            {
                return Convert.ToInt32(obj["CurrentBrightness"]);
            }
        }
        catch { }
        return null;
    }

    private static void SetWmiBrightness(int brightness)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("root\\WMI", "SELECT * FROM WmiMonitorBrightnessMethods");
            foreach (ManagementObject obj in searcher.Get())
            {
                obj.InvokeMethod("WmiSetBrightness", [
                    (uint)1, // timeout
                    (byte)Math.Clamp(brightness, 0, 100),
                ]);
            }
        }
        catch { }
    }

    // ── Monitor Handle Enumeration ──

    private static List<(PHYSICAL_MONITOR[] physicalMonitors, nint hMonitor)> GetPhysicalMonitorHandles()
    {
        var result = new List<(PHYSICAL_MONITOR[], nint)>();

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (nint hMonitor, nint _, ref RECT __, nint ___) =>
        {
            try
            {
                if (GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, out uint count) && count > 0)
                {
                    var monitors = new PHYSICAL_MONITOR[count];
                    if (GetPhysicalMonitorsFromHMONITOR(hMonitor, count, monitors))
                        result.Add((monitors, hMonitor));
                }
            }
            catch { }
            return true;
        }, IntPtr.Zero);

        return result;
    }
}
