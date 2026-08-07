using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Mo.Models;

/// <summary>
/// A saved display configuration.
/// </summary>
/// <remarks>
/// Observable because the list UI binds straight to these instances. As a plain POCO
/// it happened to keep working only because <c>ProfileService.SaveProfileAsync</c>
/// re-assigns the collection slot, forcing the item container to re-realize and
/// re-run its OneTime bindings. Any code path that mutated a profile without going
/// through Save would silently leave a stale card — and <see cref="IsActive"/>, which
/// changes without a save at all, could not have worked that way.
/// </remarks>
public sealed class DisplayProfile : ObservableObject
{
    // Properties are written out by hand rather than with [ObservableProperty].
    //
    // MoJsonContext is a System.Text.Json *source generator*, and it runs against the
    // same original compilation as the MVVM toolkit's generator — so it can only see
    // what is written here. Declaring `[ObservableProperty] private string _name`
    // makes STJ see a private field and no public property, silently dropping the
    // member from the JSON contract: profiles round-trip as blank. SetProperty gives
    // the same change notification while keeping the property visible to both.

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
    /// Position in the user's own ordering. Lower comes first.
    /// </summary>
    /// <remarks>
    /// Before this existed, the list order was whatever <c>Directory.GetFiles</c>
    /// returned — effectively GUID filename order. That order is what the slot hotkeys
    /// (Ctrl+Alt+1..9 → Profiles[0..9]), the next/previous cycling, and the tray menu
    /// all index into, so those shortcuts pointed at arbitrary profiles and could
    /// silently change meaning whenever a profile was added or removed.
    /// </remarks>
    public int SortOrder { get; set; }

    // The setters coalesce null away. These properties are non-nullable to the rest of
    // the app, but System.Text.Json will happily assign null from `"name": null` in a
    // hand-edited or third-party profile file — and Program.QuarantineCorruptUserData
    // only catches files that fail to *parse*, not ones that parse to nulls. Without
    // this, importing such a file NREs while rendering the card.

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
            // A null here would take down every consumer: MonitorCount, the layout
            // thumbnail, the editor and CheckCompatibility all dereference it.
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

    /// <summary>
    /// True for the profile currently in effect. Runtime-only: it describes the
    /// machine's present state, not anything about the profile worth saving.
    /// </summary>
    [JsonIgnore]
    public bool IsActive
    {
        get => _isActive;
        set => SetProperty(ref _isActive, value);
    }

    /// <summary>
    /// False when a monitor this profile needs is not currently attached.
    /// </summary>
    /// <remarks>
    /// Runtime-only, like <see cref="IsActive"/>. Lets the list answer "which of these
    /// can I actually switch to right now" without the user clicking Apply on each one
    /// to find out.
    /// </remarks>
    [JsonIgnore]
    public bool IsAvailable
    {
        get => _isAvailable;
        set => SetProperty(ref _isAvailable, value);
    }

    [JsonIgnore]
    public int MonitorCount => Monitors.Count;

    /// <summary>
    /// Raises change notification for values derived from the monitor list, which is
    /// mutated in place by the editor rather than replaced.
    /// </summary>
    public void NotifyMonitorsChanged() => OnPropertyChanged(nameof(MonitorCount));

    /// <summary>
    /// The profile's name.
    /// </summary>
    /// <remarks>
    /// This is what accessibility tooling actually reads. A GridViewItem's automation
    /// peer names itself from the bound item's ToString() unless the *container* has
    /// an explicit AutomationProperties.Name — which a DataTemplate cannot set — so
    /// the default implementation had every card in the list announcing itself as
    /// "Mo.Models.DisplayProfile".
    /// </remarks>
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
