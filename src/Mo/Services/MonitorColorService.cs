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
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool GetMonitorInfoW(nint hMonitor, ref MONITORINFOEX info);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    private readonly object _cacheLock = new();
    private List<(PHYSICAL_MONITOR[] physicalMonitors, nint hMonitor, string gdiDeviceName)>? _cachedHandles;
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

    /// <summary>
    /// Runs <paramref name="body"/> against the cached physical-monitor handles while
    /// holding the cache lock.
    /// </summary>
    /// <remarks>
    /// Every DDC/CI call must go through here. The previous shape returned raw handles
    /// and let callers use them after the lock was released, which is a use-after-free:
    /// <c>SystemEvents.DisplaySettingsChanged</c> is raised on a system thread and calls
    /// <see cref="DestroyCache"/>, so a concurrent <c>DestroyPhysicalMonitors</c> could
    /// free a handle mid-transaction. That race is not theoretical here — applying a
    /// profile changes the display configuration (raising the event) and then
    /// immediately pushes colour settings down the very handles being freed.
    ///
    /// Holding the lock across a DDC/CI round trip costs ~50 ms, but these calls were
    /// already serialized in practice, and DestroyCache now simply waits its turn.
    /// Never call WMI from inside <paramref name="body"/> — see SetWmiBrightness.
    /// </remarks>
    private T WithHandles<T>(Func<List<(PHYSICAL_MONITOR[] physicalMonitors, nint hMonitor, string gdiDeviceName)>, T> body)
    {
        lock (_cacheLock)
        {
            // After Dispose the cache must stay empty; rebuilding it here would open
            // handles that nothing is left to free.
            if (_disposed) return body([]);

            _cachedHandles ??= GetPhysicalMonitorHandles();
            return body(_cachedHandles);
        }
    }

    /// <summary>Enumerates every physical monitor handle in EnumDisplayMonitors order.</summary>
    private T WithEachHandle<T>(Func<IEnumerable<nint>, T> body) =>
        WithHandles(entries => body(entries.SelectMany(e => e.physicalMonitors).Select(pm => pm.hPhysicalMonitor)));

    private void DestroyCache()
    {
        lock (_cacheLock)
        {
            if (_cachedHandles == null) return;
            foreach (var (monitors, _, _) in _cachedHandles)
            {
                try { DestroyPhysicalMonitors((uint)monitors.Length, monitors); } catch { }
            }
            _cachedHandles = null;
        }
    }

    public List<MonitorColorCapabilities> DetectCapabilities()
    {
        var result = WithEachHandle(handles => handles.Select(ProbeCapabilities).ToList());

        // WMI is queried outside the handle lock — it is slow and unrelated to DDC/CI,
        // and holding the lock across it would stall every other colour operation.
        if (result.Count > 0 && !result[0].SupportsBrightness)
            result[0].SupportsWmiBrightness = DetectWmiBrightness();

        return result;
    }

    public List<MonitorColorSettings> CaptureAllMonitors()
    {
        var results = WithEachHandle(handles => handles.Select(ReadSettings).ToList());

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
        bool? applied = WithEachHandle(handles =>
        {
            var handle = handles.Skip(monitorIndex).Select(h => (nint?)h).FirstOrDefault();
            return handle == null ? (bool?)null : WriteSettings(handle.Value, settings);
        });

        if (applied == false && settings.Brightness.HasValue && monitorIndex == 0)
            SetWmiBrightness(settings.Brightness.Value);
    }

    public void ApplyAll(List<(int index, MonitorColorSettings settings)> entries)
    {
        // Collect which entries fell back so the WMI writes happen after the lock.
        var needsWmi = WithEachHandle(handles =>
        {
            var fallbacks = new List<MonitorColorSettings>();
            int idx = 0;
            foreach (var handle in handles)
            {
                var match = entries.FirstOrDefault(e => e.index == idx);
                if (match.settings != null && !WriteSettings(handle, match.settings) && idx == 0)
                    fallbacks.Add(match.settings);
                idx++;
            }
            return fallbacks;
        });

        foreach (var settings in needsWmi)
            if (settings.Brightness.HasValue)
                SetWmiBrightness(settings.Brightness.Value);
    }

    public (uint current, uint max)? GetVcpFeature(int monitorIndex, byte vcpCode) =>
        WithEachHandle(handles =>
        {
            var handle = handles.Skip(monitorIndex).Select(h => (nint?)h).FirstOrDefault();
            if (handle == null) return null;
            try
            {
                if (GetVCPFeatureAndVCPFeatureReply(handle.Value, vcpCode, IntPtr.Zero, out uint cur, out uint max))
                    return ((uint current, uint max)?)(cur, max);
            }
            catch { }
            return null;
        });

    public bool SetVcpFeature(int monitorIndex, byte vcpCode, uint value) =>
        WithEachHandle(handles =>
        {
            var handle = handles.Skip(monitorIndex).Select(h => (nint?)h).FirstOrDefault();
            if (handle == null) return false;
            try { return SetVCPFeature(handle.Value, vcpCode, value); }
            catch { return false; }
        });

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

    private static List<(PHYSICAL_MONITOR[] physicalMonitors, nint hMonitor, string gdiDeviceName)> GetPhysicalMonitorHandles()
    {
        var result = new List<(PHYSICAL_MONITOR[], nint, string)>();

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (nint hMonitor, nint _, ref RECT __, nint ___) =>
        {
            try
            {
                if (GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, out uint count) && count > 0)
                {
                    var monitors = new PHYSICAL_MONITOR[count];
                    if (GetPhysicalMonitorsFromHMONITOR(hMonitor, count, monitors))
                    {
                        var info = new MONITORINFOEX();
                        info.cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFOEX>();
                        string name = GetMonitorInfoW(hMonitor, ref info) ? info.szDevice ?? string.Empty : string.Empty;
                        result.Add((monitors, hMonitor, name));
                    }
                }
            }
            catch { }
            return true;
        }, IntPtr.Zero);

        return result;
    }

    // ── Device-name lookup (preferred) ──

    public bool ApplyToMonitorByDeviceName(string gdiDeviceName, MonitorColorSettings settings)
    {
        bool? applied = WithHandleFor(gdiDeviceName, h => WriteSettings(h, settings));

        if (applied == false && settings.Brightness.HasValue)
            SetWmiBrightness(settings.Brightness.Value);

        return applied == true;
    }

    public MonitorColorCapabilities? DetectCapabilitiesByDeviceName(string gdiDeviceName)
    {
        var caps = WithHandleFor(gdiDeviceName, ProbeCapabilities);
        if (caps == null) return null;

        // WMI fallback only meaningful for the laptop internal panel; identify it
        // crudely as DISPLAY1 with no DDC/CI brightness.
        if (!caps.SupportsBrightness && gdiDeviceName.EndsWith("DISPLAY1", StringComparison.OrdinalIgnoreCase))
            caps.SupportsWmiBrightness = DetectWmiBrightness();

        return caps;
    }

    public MonitorColorSettings? CaptureByDeviceName(string gdiDeviceName)
    {
        var s = WithHandleFor(gdiDeviceName, ReadSettings);
        if (s == null) return null;

        if (!s.Brightness.HasValue && gdiDeviceName.EndsWith("DISPLAY1", StringComparison.OrdinalIgnoreCase))
        {
            var w = GetWmiBrightness();
            if (w.HasValue) s.Brightness = w.Value;
        }
        return s;
    }

    public bool SetVcpFeatureByDeviceName(string gdiDeviceName, byte vcpCode, uint value) =>
        WithHandleFor(gdiDeviceName, h =>
        {
            try { return SetVCPFeature(h, vcpCode, value); }
            catch { return false; }
        }) == true;

    public (uint current, uint max)? GetVcpFeatureByDeviceName(string gdiDeviceName, byte vcpCode) =>
        WithHandleFor<(uint current, uint max)?>(gdiDeviceName, h =>
        {
            try
            {
                if (GetVCPFeatureAndVCPFeatureReply(h, vcpCode, IntPtr.Zero, out uint cur, out uint max))
                    return (cur, max);
            }
            catch { }
            return null;
        });

    /// <summary>
    /// Runs <paramref name="body"/> against the handle for a GDI device name, under the
    /// cache lock. Returns default when the monitor is not present.
    /// </summary>
    private TResult? WithHandleFor<TResult>(string gdiDeviceName, Func<nint, TResult> body)
    {
        if (string.IsNullOrEmpty(gdiDeviceName)) return default;

        return WithHandles(entries =>
        {
            foreach (var (monitors, _, name) in entries)
            {
                if (string.Equals(name, gdiDeviceName, StringComparison.OrdinalIgnoreCase) && monitors.Length > 0)
                    return body(monitors[0].hPhysicalMonitor);
            }
            return default;
        });
    }
}
