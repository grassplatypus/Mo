using Mo.Models;

namespace Mo.Services;

/// <summary>
/// Undo safety net for display changes.
///
/// A profile can position a monitor off-screen, pick a mode the panel cannot sync to,
/// disable the only visible display, or drive DDC/CI brightness to zero. Any of those
/// leaves the user unable to see Mo — and therefore unable to undo it from inside Mo.
/// Windows guards its own display-settings changes with a 15-second "Keep these
/// settings?" prompt for exactly this reason; this is Mo's equivalent.
/// </summary>
public interface IApplyGuardService
{
    /// <summary>Captures the live display + color state so it can be restored later.</summary>
    DisplaySnapshot Capture();

    /// <summary>
    /// Asks the user to confirm the change now in effect, reverting to
    /// <paramref name="snapshot"/> if they decline or do not answer in time.
    /// Returns true when the new configuration was kept.
    /// </summary>
    Task<bool> ConfirmOrRevertAsync(DisplaySnapshot snapshot, ApplyTrigger trigger);

    /// <summary>Restores a snapshot immediately, without prompting.</summary>
    bool Restore(DisplaySnapshot snapshot);
}

/// <summary>What caused a profile to be applied. Governs whether the guard engages.</summary>
public enum ApplyTrigger
{
    /// <summary>Clicked Apply, tray menu, or the profile editor.</summary>
    User,
    /// <summary>Global hotkey — the window may well be hidden.</summary>
    Hotkey,
    /// <summary>Monitor hot-plug matched an auto-switch profile.</summary>
    AutoSwitch,
    /// <summary>A time-of-day schedule fired, possibly with nobody at the machine.</summary>
    Schedule,
    /// <summary>Post-launch restore of AppSettings.LastAppliedProfileId.</summary>
    Startup,
}

/// <summary>
/// Point-in-time copy of everything an apply can change destructively. Held in memory
/// only — it describes the machine right now, so persisting it would be meaningless
/// on the next boot.
/// </summary>
public sealed class DisplaySnapshot
{
    public required List<MonitorInfo> Monitors { get; init; }

    /// <summary>Per-monitor DDC/CI state, keyed by GDI device name ("\\.\DISPLAY1").</summary>
    public required Dictionary<string, MonitorColorSettings> Color { get; init; }

    /// <summary>
    /// Stable description of the layout, used to tell "the apply actually changed
    /// something" from "the apply was a no-op". Prompting on a no-op trains users to
    /// dismiss the dialog without reading it, which defeats the whole safety net.
    /// </summary>
    public required string Signature { get; init; }
}
