using System.Management;
using Mo.Interop.Monitor;
using Mo.Models;
using static Mo.Interop.Monitor.MonitorConfigApi;

namespace Mo.Services;

public sealed class MonitorColorService : IMonitorColorService
{
    public List<MonitorColorCapabilities> DetectCapabilities()
    {
        var result = new List<MonitorColorCapabilities>();
        var handles = GetPhysicalMonitorHandles();

        foreach (var (physicalMonitors, _) in handles)
        {
            foreach (var pm in physicalMonitors)
            {
                result.Add(ProbeCapabilities(pm.hPhysicalMonitor));
            }
            DestroyPhysicalMonitors((uint)physicalMonitors.Length, physicalMonitors);
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
        var handles = GetPhysicalMonitorHandles();
        var results = new List<MonitorColorSettings>();

        foreach (var (physicalMonitors, _) in handles)
        {
            foreach (var pm in physicalMonitors)
            {
                results.Add(ReadSettings(pm.hPhysicalMonitor));
            }
            DestroyPhysicalMonitors((uint)physicalMonitors.Length, physicalMonitors);
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
        var handles = GetPhysicalMonitorHandles();
        int idx = 0;
        bool applied = false;

        foreach (var (physicalMonitors, _) in handles)
        {
            foreach (var pm in physicalMonitors)
            {
                if (idx == monitorIndex)
                {
                    applied = WriteSettings(pm.hPhysicalMonitor, settings);
                    // WMI fallback for brightness
                    if (!applied && settings.Brightness.HasValue && idx == 0)
                        SetWmiBrightness(settings.Brightness.Value);
                }
                idx++;
            }
            DestroyPhysicalMonitors((uint)physicalMonitors.Length, physicalMonitors);
        }
    }

    public void ApplyAll(List<(int index, MonitorColorSettings settings)> entries)
    {
        var handles = GetPhysicalMonitorHandles();
        int idx = 0;

        foreach (var (physicalMonitors, _) in handles)
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
            DestroyPhysicalMonitors((uint)physicalMonitors.Length, physicalMonitors);
        }
    }

    public (uint current, uint max)? GetVcpFeature(int monitorIndex, byte vcpCode)
    {
        var handles = GetPhysicalMonitorHandles();
        int idx = 0;
        (uint current, uint max)? result = null;
        try
        {
            foreach (var (physicalMonitors, _) in handles)
            {
                foreach (var pm in physicalMonitors)
                {
                    if (idx == monitorIndex)
                    {
                        try
                        {
                            if (GetVCPFeatureAndVCPFeatureReply(pm.hPhysicalMonitor, vcpCode, IntPtr.Zero, out uint cur, out uint max))
                                result = (cur, max);
                        }
                        catch { }
                    }
                    idx++;
                }
            }
        }
        finally
        {
            foreach (var (physicalMonitors, _) in handles)
                DestroyPhysicalMonitors((uint)physicalMonitors.Length, physicalMonitors);
        }
        return result;
    }

    public bool SetVcpFeature(int monitorIndex, byte vcpCode, uint value)
    {
        var handles = GetPhysicalMonitorHandles();
        int idx = 0;
        bool applied = false;
        try
        {
            foreach (var (physicalMonitors, _) in handles)
            {
                foreach (var pm in physicalMonitors)
                {
                    if (idx == monitorIndex)
                    {
                        try { applied = SetVCPFeature(pm.hPhysicalMonitor, vcpCode, value); }
                        catch { }
                    }
                    idx++;
                }
            }
        }
        finally
        {
            foreach (var (physicalMonitors, _) in handles)
                DestroyPhysicalMonitors((uint)physicalMonitors.Length, physicalMonitors);
        }
        return applied;
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
