using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Mo.Models;

public sealed class DisplayProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ModifiedAt { get; set; } = DateTime.UtcNow;
    public HotkeyBinding? Hotkey { get; set; }
    public List<MonitorInfo> Monitors { get; set; } = [];

    // Audio
    public string? AudioDeviceId { get; set; }
    public string? AudioDeviceName { get; set; }

    // Wallpaper
    public string? WallpaperPath { get; set; }

    // Night light
    public bool? NightLightEnabled { get; set; }

    // Auto-switch: when these monitors are detected, auto-apply this profile
    public bool AutoSwitch { get; set; }

    // Schedule
    public ScheduleConfig? Schedule { get; set; }

    [JsonIgnore]
    public int MonitorCount => Monitors.Count;
}

public sealed class ScheduleConfig
{
    public bool Enabled { get; set; }
    public TimeOnly? Time { get; set; }
    public List<DayOfWeek> Days { get; set; } =
    [
        DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday,
        DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday
    ];
}
