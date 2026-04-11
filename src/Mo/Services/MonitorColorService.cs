using Mo.Interop.Monitor;
using Mo.Models;
using static Mo.Interop.Monitor.MonitorConfigApi;

namespace Mo.Services;

public sealed class MonitorColorService : IMonitorColorService
{
    public List<MonitorColorSettings> CaptureAllMonitors()
    {
        var handles = GetPhysicalMonitorHandles();
        var results = new List<MonitorColorSettings>();

        foreach (var (physicalMonitors, _) in handles)
        {
            foreach (var pm in physicalMonitors)
            {
                var settings = ReadSettings(pm.hPhysicalMonitor);
                results.Add(settings);
            }
            DestroyPhysicalMonitors((uint)physicalMonitors.Length, physicalMonitors);
        }

        return results;
    }

    public void ApplyToMonitor(int monitorIndex, MonitorColorSettings settings)
    {
        var handles = GetPhysicalMonitorHandles();
        int idx = 0;

        foreach (var (physicalMonitors, _) in handles)
        {
            foreach (var pm in physicalMonitors)
            {
                if (idx == monitorIndex)
                {
                    WriteSettings(pm.hPhysicalMonitor, settings);
                    DestroyPhysicalMonitors((uint)physicalMonitors.Length, physicalMonitors);
                    return;
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
                    WriteSettings(pm.hPhysicalMonitor, match.settings);
                }
                idx++;
            }
            DestroyPhysicalMonitors((uint)physicalMonitors.Length, physicalMonitors);
        }
    }

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

    private static void WriteSettings(nint hPhysicalMonitor, MonitorColorSettings s)
    {
        if (s.Brightness.HasValue)
        {
            try
            {
                if (GetMonitorBrightness(hPhysicalMonitor, out uint minB, out _, out uint maxB))
                {
                    uint val = (uint)(minB + (maxB - minB) * s.Brightness.Value / 100);
                    SetMonitorBrightness(hPhysicalMonitor, val);
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
                    SetMonitorContrast(hPhysicalMonitor, val);
                }
            }
            catch { }
        }

        if (s.RedGain.HasValue)
        {
            try
            {
                if (GetMonitorRedGreenOrBlueGain(hPhysicalMonitor, MC_GAIN_TYPE.MC_RED_GAIN, out uint min, out _, out uint max))
                    SetMonitorRedGreenOrBlueGain(hPhysicalMonitor, MC_GAIN_TYPE.MC_RED_GAIN, (uint)(min + (max - min) * s.RedGain.Value / 100));
            }
            catch { }
        }

        if (s.GreenGain.HasValue)
        {
            try
            {
                if (GetMonitorRedGreenOrBlueGain(hPhysicalMonitor, MC_GAIN_TYPE.MC_GREEN_GAIN, out uint min, out _, out uint max))
                    SetMonitorRedGreenOrBlueGain(hPhysicalMonitor, MC_GAIN_TYPE.MC_GREEN_GAIN, (uint)(min + (max - min) * s.GreenGain.Value / 100));
            }
            catch { }
        }

        if (s.BlueGain.HasValue)
        {
            try
            {
                if (GetMonitorRedGreenOrBlueGain(hPhysicalMonitor, MC_GAIN_TYPE.MC_BLUE_GAIN, out uint min, out _, out uint max))
                    SetMonitorRedGreenOrBlueGain(hPhysicalMonitor, MC_GAIN_TYPE.MC_BLUE_GAIN, (uint)(min + (max - min) * s.BlueGain.Value / 100));
            }
            catch { }
        }
    }

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
                    {
                        result.Add((monitors, hMonitor));
                    }
                }
            }
            catch { }
            return true;
        }, IntPtr.Zero);

        return result;
    }
}
