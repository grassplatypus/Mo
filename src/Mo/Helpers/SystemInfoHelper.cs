using System.Diagnostics;
using System.Management;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Mo.Models;
using Mo.Services;

namespace Mo.Helpers;

/// <summary>
/// Builds structured system/hardware/runtime reports in YAML-like format
/// optimized for both human readability and LLM analysis.
/// </summary>
public static class SystemInfoHelper
{
    /// <summary>System info only (no exception). Used in Settings page.</summary>
    public static string BuildFullReport(IDisplayService? displayService = null, IMonitorColorService? colorService = null)
    {
        var w = new ReportWriter();
        WriteAppSection(w);
        WriteSystemSection(w);
        WriteGpuSection(w);
        WriteMonitorSection(w, displayService, colorService);
        WriteRuntimeSection(w);
        return w.ToString();
    }

    /// <summary>Full report + exception detail. Used in error dialog and crash log.</summary>
    public static string BuildErrorReport(Exception ex)
    {
        var w = new ReportWriter();

        // Header for LLM: structured context block
        w.Line("```yaml");
        w.Line("# Mo Error Report — paste this to an LLM or issue tracker for analysis");
        w.Line($"report_version: 2");
        w.Line($"generated_at: \"{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}\"");
        w.Line();

        WriteAppSection(w);
        WriteSystemSection(w);
        WriteGpuSection(w);
        WriteMonitorSection(w);
        WriteRuntimeSection(w);
        WriteProfilesSection(w);
        WriteExceptionSection(w, ex);

        w.Line("```");
        return w.ToString();
    }

    // ── Sections ──

    private static void WriteAppSection(ReportWriter w)
    {
        w.Section("app");
        w.KV("name", "Mo — Monitor Profile Manager");
        w.KV("version", UpdateService.CurrentVersion);

        try
        {
            var asm = Assembly.GetExecutingAssembly();
            w.KV("assembly_version", asm.GetName().Version?.ToString() ?? "?");
            w.KV("build_config",
#if DEBUG
                "Debug"
#else
                "Release"
#endif
            );
        }
        catch { }
    }

    private static void WriteSystemSection(ReportWriter w)
    {
        w.Section("system");
        w.KV("os", Environment.OSVersion.VersionString);
        w.KV("os_arch", RuntimeInformation.OSArchitecture.ToString());
        w.KV("process_arch", RuntimeInformation.ProcessArchitecture.ToString());
        w.KV("dotnet", RuntimeInformation.FrameworkDescription);
        w.KV("locale", System.Globalization.CultureInfo.CurrentUICulture.Name);

        try
        {
            w.KV("cpu", GetCpuName());
            w.KV("cpu_cores_logical", Environment.ProcessorCount);
        }
        catch { w.KV("cpu_cores_logical", Environment.ProcessorCount); }

        try
        {
            var memInfo = GC.GetGCMemoryInfo();
            w.KV("memory_total_mb", memInfo.TotalAvailableMemoryBytes / 1024 / 1024);
        }
        catch { }

        try { w.KV("machine_name", Environment.MachineName); } catch { }
        try { w.KV("user_name", Environment.UserName); } catch { }
    }

    private static void WriteGpuSection(ReportWriter w)
    {
        w.Section("gpu");
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, DriverVersion, AdapterRAM, VideoProcessor, CurrentRefreshRate FROM Win32_VideoController");
            int idx = 0;
            foreach (ManagementObject obj in searcher.Get())
            {
                w.Line($"  - index: {idx}");
                w.Line($"    name: \"{obj["Name"]}\"");
                w.Line($"    driver: \"{obj["DriverVersion"]}\"");
                if (obj["AdapterRAM"] is uint ram)
                    w.Line($"    vram_mb: {ram / 1024 / 1024}");
                if (obj["VideoProcessor"] is string vp && !string.IsNullOrEmpty(vp))
                    w.Line($"    processor: \"{vp}\"");
                if (obj["CurrentRefreshRate"] is uint rr && rr > 0)
                    w.Line($"    current_refresh: {rr}");
                idx++;
            }
            if (idx == 0) w.Line("  (none detected)");
        }
        catch { w.Line("  (failed to query)"); }
    }

    private static void WriteMonitorSection(ReportWriter w,
        IDisplayService? displayService = null, IMonitorColorService? colorService = null)
    {
        w.Section("monitors");
        try
        {
            displayService ??= App.Services?.GetService(typeof(IDisplayService)) as IDisplayService;
            colorService ??= App.Services?.GetService(typeof(IMonitorColorService)) as IMonitorColorService;

            if (displayService == null) { w.Line("  (service unavailable)"); return; }

            var monitors = displayService.GetCurrentConfiguration();
            var caps = new List<MonitorColorCapabilities>();
            try { caps = colorService?.DetectCapabilities() ?? []; } catch { }

            w.KV("count", monitors.Count);

            for (int i = 0; i < monitors.Count; i++)
            {
                var m = monitors[i];
                w.Line($"  - index: {i}");
                w.Line($"    name: \"{(string.IsNullOrEmpty(m.FriendlyName) ? "Unknown" : m.FriendlyName)}\"");
                w.Line($"    primary: {m.IsPrimary.ToString().ToLower()}");
                w.Line($"    resolution: \"{m.Width}x{m.Height}\"");
                w.Line($"    refresh_hz: {m.RefreshRateHz:F1}");
                w.Line($"    position: [{m.PositionX}, {m.PositionY}]");
                w.Line($"    rotation: {(int)m.Rotation}");
                w.Line($"    edid_mfr: \"0x{m.EdidManufacturerId:X4}\"");
                w.Line($"    edid_product: \"0x{m.EdidProductCodeId:X4}\"");
                w.Line($"    connector: {m.ConnectorInstance}");
                if (!string.IsNullOrEmpty(m.DevicePath))
                    w.Line($"    device_path: \"{m.DevicePath}\"");

                // DDC/CI capabilities
                if (i < caps.Count)
                {
                    var c = caps[i];
                    w.Line($"    ddc_ci:");
                    w.Line($"      brightness: {(c.SupportsBrightness ? "yes" : c.SupportsWmiBrightness ? "wmi_only" : "no")}");
                    w.Line($"      contrast: {YN(c.SupportsContrast)}");
                    w.Line($"      red_gain: {YN(c.SupportsRedGain)}");
                    w.Line($"      green_gain: {YN(c.SupportsGreenGain)}");
                    w.Line($"      blue_gain: {YN(c.SupportsBlueGain)}");
                }

                // Current color values
                var cs = m.ColorSettings;
                if (cs is { HasValues: true })
                {
                    w.Line($"    color_current:");
                    if (cs.Brightness.HasValue) w.Line($"      brightness: {cs.Brightness}");
                    if (cs.Contrast.HasValue) w.Line($"      contrast: {cs.Contrast}");
                    if (cs.RedGain.HasValue) w.Line($"      red_gain: {cs.RedGain}");
                    if (cs.GreenGain.HasValue) w.Line($"      green_gain: {cs.GreenGain}");
                    if (cs.BlueGain.HasValue) w.Line($"      blue_gain: {cs.BlueGain}");
                }
            }
        }
        catch { w.Line("  (failed to enumerate)"); }
    }

    private static void WriteRuntimeSection(ReportWriter w)
    {
        w.Section("runtime");

        // Process info
        try
        {
            using var proc = Process.GetCurrentProcess();
            w.KV("pid", proc.Id);
            w.KV("working_set_mb", proc.WorkingSet64 / 1024 / 1024);
            w.KV("private_memory_mb", proc.PrivateMemorySize64 / 1024 / 1024);
            w.KV("thread_count", proc.Threads.Count);
            w.KV("uptime_seconds", (int)(DateTime.Now - proc.StartTime).TotalSeconds);
        }
        catch { }

        // GC stats
        try
        {
            var gcInfo = GC.GetGCMemoryInfo();
            w.KV("gc_heap_mb", gcInfo.HeapSizeBytes / 1024 / 1024);
            w.KV("gc_gen0", GC.CollectionCount(0));
            w.KV("gc_gen1", GC.CollectionCount(1));
            w.KV("gc_gen2", GC.CollectionCount(2));
        }
        catch { }

        // Thread pool
        try
        {
            ThreadPool.GetAvailableThreads(out int workerAvail, out int ioAvail);
            ThreadPool.GetMaxThreads(out int workerMax, out int ioMax);
            w.KV("threadpool_workers", $"{workerMax - workerAvail}/{workerMax}");
            w.KV("threadpool_io", $"{ioMax - ioAvail}/{ioMax}");
        }
        catch { }

        // Key loaded assemblies
        try
        {
            w.Line("  key_assemblies:");
            var asmNames = new[] { "Mo", "Microsoft.WindowsAppSDK", "Microsoft.WinUI", "CommunityToolkit.Mvvm", "H.NotifyIcon", "System.Text.Json" };
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var name = asm.GetName();
                if (asmNames.Any(n => name.Name?.Contains(n, StringComparison.OrdinalIgnoreCase) == true))
                    w.Line($"    - \"{name.Name}\": \"{name.Version}\"");
            }
        }
        catch { }
    }

    private static void WriteProfilesSection(ReportWriter w)
    {
        w.Section("profiles");
        try
        {
            var profileService = App.Services?.GetService(typeof(IProfileService)) as IProfileService;
            if (profileService == null) { w.Line("  (service unavailable)"); return; }

            w.KV("count", profileService.Profiles.Count);
            foreach (var p in profileService.Profiles)
            {
                w.Line($"  - name: \"{p.Name}\"");
                w.Line($"    monitors: {p.MonitorCount}");
                w.Line($"    auto_switch: {p.AutoSwitch.ToString().ToLower()}");
                w.Line($"    has_hotkey: {(p.Hotkey != null).ToString().ToLower()}");
                w.Line($"    has_audio: {(!string.IsNullOrEmpty(p.AudioDeviceId)).ToString().ToLower()}");
                w.Line($"    has_wallpaper: {(!string.IsNullOrEmpty(p.WallpaperPath)).ToString().ToLower()}");
                w.Line($"    has_live_wallpaper: {(p.LiveWallpaper != null).ToString().ToLower()}");
                w.Line($"    has_schedule: {(p.Schedule?.Enabled == true).ToString().ToLower()}");
            }
        }
        catch { w.Line("  (failed to read)"); }
    }

    private static void WriteExceptionSection(ReportWriter w, Exception ex)
    {
        w.Section("exception");
        w.KV("type", ex.GetType().FullName ?? "Unknown");
        w.KV("message", $"\"{EscapeYaml(ex.Message)}\"");
        w.KV("hresult", $"0x{ex.HResult:X8}");
        if (ex.Source != null)
            w.KV("source", $"\"{ex.Source}\"");
        if (ex.TargetSite != null)
            w.KV("target_method", $"\"{ex.TargetSite.DeclaringType?.FullName}.{ex.TargetSite.Name}\"");

        w.Line("  stack_trace: |");
        foreach (var line in (ex.StackTrace ?? "(none)").Split('\n'))
            w.Line($"    {line.TrimEnd()}");

        var inner = ex.InnerException;
        int depth = 0;
        while (inner != null && depth < 5)
        {
            w.Line($"  inner_exception_{depth}:");
            w.Line($"    type: \"{inner.GetType().FullName}\"");
            w.Line($"    message: \"{EscapeYaml(inner.Message)}\"");
            w.Line($"    hresult: \"0x{inner.HResult:X8}\"");
            w.Line($"    stack_trace: |");
            foreach (var line in (inner.StackTrace ?? "(none)").Split('\n'))
                w.Line($"      {line.TrimEnd()}");
            inner = inner.InnerException;
            depth++;
        }
    }

    // ── Utilities ──

    private static string GetCpuName()
    {
        using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_Processor");
        foreach (ManagementObject obj in searcher.Get())
            return obj["Name"]?.ToString()?.Trim() ?? "Unknown";
        return "Unknown";
    }

    private static string YN(bool val) => val ? "yes" : "no";

    private static string EscapeYaml(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "");

    /// <summary>Simple writer that builds YAML-like structured output.</summary>
    private sealed class ReportWriter
    {
        private readonly StringBuilder _sb = new();

        public void Section(string name) => _sb.AppendLine($"{name}:");
        public void KV(string key, object? value) => _sb.AppendLine($"  {key}: {value}");
        public void Line(string line = "") => _sb.AppendLine(line);
        public override string ToString() => _sb.ToString();
    }
}
