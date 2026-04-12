using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;
using Mo.Models;

namespace Mo.Services;

public sealed class LiveWallpaperService : ILiveWallpaperService
{
    public LiveWallpaperProvider DetectProvider()
    {
        if (IsProviderRunning(LiveWallpaperProvider.WallpaperEngine))
            return LiveWallpaperProvider.WallpaperEngine;
        if (IsProviderRunning(LiveWallpaperProvider.Lively))
            return LiveWallpaperProvider.Lively;

        // Not running but installed?
        if (GetWallpaperEnginePath() != null)
            return LiveWallpaperProvider.WallpaperEngine;
        if (GetLivelyPath() != null)
            return LiveWallpaperProvider.Lively;

        return LiveWallpaperProvider.None;
    }

    public bool IsProviderRunning(LiveWallpaperProvider provider)
    {
        try
        {
            var names = provider switch
            {
                LiveWallpaperProvider.WallpaperEngine => new[] { "wallpaper32", "wallpaper64" },
                LiveWallpaperProvider.Lively => new[] { "Lively", "livelywpf" },
                _ => Array.Empty<string>(),
            };
            foreach (var name in names)
            {
                if (Process.GetProcessesByName(name).Length > 0)
                    return true;
            }
        }
        catch { }
        return false;
    }

    public LiveWallpaperConfig? CaptureCurrentConfig()
    {
        var provider = DetectProvider();
        if (provider == LiveWallpaperProvider.None) return null;

        return provider switch
        {
            LiveWallpaperProvider.WallpaperEngine => CaptureWallpaperEngine(),
            LiveWallpaperProvider.Lively => CaptureLively(),
            _ => null,
        };
    }

    public void ApplyConfig(LiveWallpaperConfig config)
    {
        switch (config.Provider)
        {
            case LiveWallpaperProvider.WallpaperEngine:
                ApplyWallpaperEngine(config);
                break;
            case LiveWallpaperProvider.Lively:
                ApplyLively(config);
                break;
        }
    }

    // ── WallpaperEngine ──

    private LiveWallpaperConfig? CaptureWallpaperEngine()
    {
        var exePath = GetWallpaperEnginePath();
        if (exePath == null) return null;

        var config = new LiveWallpaperConfig { Provider = LiveWallpaperProvider.WallpaperEngine };

        // Use CLI getWallpaper for each monitor (try up to 8 monitors)
        for (int i = 0; i < 8; i++)
        {
            try
            {
                var output = RunProcess(exePath, $"-control getWallpaper -monitor {i}");
                if (!string.IsNullOrWhiteSpace(output) && !output.Contains("error", StringComparison.OrdinalIgnoreCase))
                {
                    config.Entries.Add(new LiveWallpaperEntry
                    {
                        MonitorIndex = i,
                        FilePath = output.Trim(),
                    });
                }
                else
                {
                    break; // No more monitors
                }
            }
            catch { break; }
        }

        // Fallback: parse config.json if CLI returned nothing
        if (config.Entries.Count == 0)
        {
            var configJson = CaptureWallpaperEngineFromConfig();
            if (configJson != null) return configJson;
        }

        return config.Entries.Count > 0 ? config : null;
    }

    private LiveWallpaperConfig? CaptureWallpaperEngineFromConfig()
    {
        try
        {
            var installDir = Path.GetDirectoryName(GetWallpaperEnginePath());
            if (installDir == null) return null;

            var configPath = Path.Combine(installDir, "config.json");
            if (!File.Exists(configPath)) return null;

            var json = File.ReadAllText(configPath);
            using var doc = JsonDocument.Parse(json);

            var config = new LiveWallpaperConfig { Provider = LiveWallpaperProvider.WallpaperEngine };
            int idx = 0;

            // config.json has user-named sections with "monitors" objects
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Name.StartsWith("?")) continue;
                if (prop.Value.ValueKind != JsonValueKind.Object) continue;

                if (prop.Value.TryGetProperty("monitors", out var monitors) && monitors.ValueKind == JsonValueKind.Object)
                {
                    foreach (var mon in monitors.EnumerateObject())
                    {
                        if (mon.Value.TryGetProperty("file", out var fileProp))
                        {
                            config.Entries.Add(new LiveWallpaperEntry
                            {
                                MonitorIndex = idx++,
                                FilePath = fileProp.GetString() ?? "",
                            });
                        }
                    }
                }
            }

            return config.Entries.Count > 0 ? config : null;
        }
        catch { return null; }
    }

    private void ApplyWallpaperEngine(LiveWallpaperConfig config)
    {
        var exePath = GetWallpaperEnginePath();
        if (exePath == null) return;

        // Only configure wallpapers if WallpaperEngine is already running
        if (!IsProviderRunning(LiveWallpaperProvider.WallpaperEngine))
            return;

        foreach (var entry in config.Entries)
        {
            if (string.IsNullOrEmpty(entry.FilePath)) continue;
            try
            {
                RunProcess(exePath, $"-control openWallpaper -file \"{entry.FilePath}\" -monitor {entry.MonitorIndex}");
                Thread.Sleep(500); // Brief pause between monitor commands
            }
            catch { }
        }
    }

    private static string? GetWallpaperEnginePath()
    {
        // Check registry for auto-start entry
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run");
            var val = key?.GetValue("Wallpaper Engine") as string;
            if (!string.IsNullOrEmpty(val))
            {
                var path = val.Trim('"');
                if (File.Exists(path)) return path;
            }
        }
        catch { }

        // Check common Steam paths
        var steamPaths = new[]
        {
            @"C:\Program Files (x86)\Steam\steamapps\common\wallpaper_engine",
            @"D:\Steam\steamapps\common\wallpaper_engine",
            @"D:\SteamLibrary\steamapps\common\wallpaper_engine",
            @"E:\SteamLibrary\steamapps\common\wallpaper_engine",
        };

        foreach (var dir in steamPaths)
        {
            var exe64 = Path.Combine(dir, "wallpaper64.exe");
            if (File.Exists(exe64)) return exe64;
            var exe32 = Path.Combine(dir, "wallpaper32.exe");
            if (File.Exists(exe32)) return exe32;
        }

        return null;
    }

    // ── Lively Wallpaper ──

    private LiveWallpaperConfig? CaptureLively()
    {
        try
        {
            var settingsPath = GetLivelySettingsPath();
            if (settingsPath == null || !File.Exists(settingsPath)) return null;

            var json = File.ReadAllText(settingsPath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var config = new LiveWallpaperConfig { Provider = LiveWallpaperProvider.Lively };

            // Try to find wallpaper entries in settings
            if (root.TryGetProperty("WallpaperArrangement", out _) &&
                root.TryGetProperty("SelectedWallpaper", out var selWp))
            {
                // Single wallpaper mode
                if (selWp.TryGetProperty("FilePath", out var fp))
                {
                    config.Entries.Add(new LiveWallpaperEntry
                    {
                        MonitorIndex = 0,
                        FilePath = fp.GetString() ?? "",
                    });
                }
            }

            // Check per-screen wallpapers
            if (root.TryGetProperty("WallpaperPerScreen", out var perScreen) && perScreen.ValueKind == JsonValueKind.Array)
            {
                config.Entries.Clear();
                int idx = 0;
                foreach (var item in perScreen.EnumerateArray())
                {
                    if (item.TryGetProperty("FilePath", out var filePath))
                    {
                        config.Entries.Add(new LiveWallpaperEntry
                        {
                            MonitorIndex = idx++,
                            FilePath = filePath.GetString() ?? "",
                        });
                    }
                }
            }

            return config.Entries.Count > 0 ? config : null;
        }
        catch { return null; }
    }

    private void ApplyLively(LiveWallpaperConfig config)
    {
        var exePath = GetLivelyPath();
        if (exePath == null) return;

        // Only configure wallpapers if Lively is already running
        if (!IsProviderRunning(LiveWallpaperProvider.Lively))
            return;

        foreach (var entry in config.Entries)
        {
            if (string.IsNullOrEmpty(entry.FilePath)) continue;
            try
            {
                RunProcess(exePath, $"setwp --file \"{entry.FilePath}\" --monitor {entry.MonitorIndex}");
                Thread.Sleep(500);
            }
            catch { }
        }
    }

    private static string? GetLivelyPath()
    {
        // Check registry auto-start
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run");
            var val = key?.GetValue("Lively") as string;
            if (!string.IsNullOrEmpty(val))
            {
                var path = val.Trim('"');
                if (File.Exists(path)) return path;
            }
        }
        catch { }

        // Check common paths
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var paths = new[]
        {
            Path.Combine(appData, @"Programs\Lively Wallpaper\Lively.exe"),
            @"C:\Program Files\Lively Wallpaper\Lively.exe",
        };

        foreach (var p in paths)
        {
            if (File.Exists(p)) return p;
        }

        return null;
    }

    private static string? GetLivelySettingsPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var paths = new[]
        {
            Path.Combine(appData, @"Lively Wallpaper\Settings.json"),
            Path.Combine(appData, @"Packages\12030rocksdanister.LivelyWallpaper_97hta09mmv6hy\LocalCache\Local\Lively Wallpaper\Settings.json"),
        };

        foreach (var p in paths)
        {
            if (File.Exists(p)) return p;
        }
        return null;
    }

    // ── Utility ──

    private static string RunProcess(string exePath, string args)
    {
        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            }
        };
        proc.Start();
        var output = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(5000);
        return output;
    }
}
