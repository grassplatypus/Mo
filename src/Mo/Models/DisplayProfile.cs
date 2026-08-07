using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Mo.Models;

// A saved display configuration. Observable because the list binds straight to these
// instances; IsActive/IsAvailable change without any save at all.
public sealed class DisplayProfile : ObservableObject
{
    // Hand-written rather than [ObservableProperty]: MoJsonContext is a source
    // generator running on the same original compilation as the MVVM one, so it would
    // see only the private field and drop the member from the JSON contract.

    private string _name = string.Empty;
    private string _description = string.Empty;
    private DateTime _modifiedAt = DateTime.UtcNow;
    private HotkeyBinding? _hotkey;
    private List<MonitorInfo> _monitors = [];
    private bool _autoSwitch;
    private bool _isActive;
    private bool _isAvailable = true;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// Position in the user's own ordering; lower comes first. Load-bearing — the slot
    /// hotkeys and next/previous cycling index into this order.
    /// </summary>
    public int SortOrder { get; set; }

    // Setters coalesce null away: a hand-edited file can parse cleanly and still hold
    // nulls, which the corrupt-file quarantine does not catch.

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value ?? string.Empty);
    }

    /// <summary>The user's own note. Never written by the app; see LegacyDescription.</summary>
    public string Description
    {
        get => _description;
        set => SetProperty(ref _description, value ?? string.Empty);
    }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime ModifiedAt
    {
        get => _modifiedAt;
        set => SetProperty(ref _modifiedAt, value);
    }

    public HotkeyBinding? Hotkey
    {
        get => _hotkey;
        set => SetProperty(ref _hotkey, value);
    }

    public List<MonitorInfo> Monitors
    {
        get => _monitors;
        set
        {
            if (SetProperty(ref _monitors, value ?? []))
                OnPropertyChanged(nameof(MonitorCount));
        }
    }

    // Audio
    public string? AudioDeviceId { get; set; }
    public string? AudioDeviceName { get; set; }

    // Wallpaper
    public string? WallpaperPath { get; set; }

    // Night light
    public bool? NightLightEnabled { get; set; }

    // Auto-switch: when these monitors are detected, auto-apply this profile
    public bool AutoSwitch
    {
        get => _autoSwitch;
        set => SetProperty(ref _autoSwitch, value);
    }

    // Schedule
    public ScheduleConfig? Schedule { get; set; }

    // Live wallpaper (WallpaperEngine / Lively)
    public LiveWallpaperConfig? LiveWallpaper { get; set; }

    // What to do with monitors not listed in this profile
    public UnmatchedMonitorAction UnmatchedAction { get; set; } = UnmatchedMonitorAction.Keep;

    /// <summary>True for the profile currently in effect. Runtime-only.</summary>
    [JsonIgnore]
    public bool IsActive
    {
        get => _isActive;
        set => SetProperty(ref _isActive, value);
    }

    /// <summary>
    /// False when a monitor this profile needs is not attached. Runtime-only.
    /// </summary>
    [JsonIgnore]
    public bool IsAvailable
    {
        get => _isAvailable;
        set => SetProperty(ref _isAvailable, value);
    }

    [JsonIgnore]
    public int MonitorCount => Monitors.Count;

    /// <summary>
    /// The profile's name. A GridViewItem's automation peer names itself from this,
    /// and a DataTemplate cannot set AutomationProperties.Name on the container.
    /// </summary>
    public override string ToString() => Name;
}

public sealed class LiveWallpaperConfig
{
    public LiveWallpaperProvider Provider { get; set; }
    public List<LiveWallpaperEntry> Entries { get; set; } = [];
}

public enum LiveWallpaperProvider
{
    None,
    WallpaperEngine,
    Lively,
}

public sealed class LiveWallpaperEntry
{
    public int MonitorIndex { get; set; }
    public string FilePath { get; set; } = string.Empty;
}

public enum UnmatchedMonitorAction
{
    Keep,
    Disable,
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
