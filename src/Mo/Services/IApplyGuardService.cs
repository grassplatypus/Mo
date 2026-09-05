using Mo.Models;

namespace Mo.Services;

/// <summary>Undo safety net for display changes — Mo's equivalent of Windows' own
/// "Keep these settings?" prompt, for the same reason.
/// See .claude/rules/40-safety-invariants.md.</summary>
public interface IApplyGuardService
{
    /// <summary>Captures the live display + color state so it can be restored later.</summary>
    DisplaySnapshot Capture();

    /// <summary>Asks the user to confirm the change now in effect, reverting to
    /// <paramref name="snapshot"/> on decline or timeout. True when it was kept.</summary>
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

/// <summary>Point-in-time copy of everything an apply can change destructively. Memory
/// only — it describes the machine now, so persisting it would be meaningless.</summary>
public sealed class DisplaySnapshot
{
    public required List<MonitorInfo> Monitors { get; init; }

    /// <summary>Per-monitor DDC/CI state, keyed by GDI device name ("\\.\DISPLAY1").</summary>
    public required Dictionary<string, MonitorColorSettings> Color { get; init; }

    /// <summary>Stable layout description, telling a real change from a no-op apply.
    /// Prompting on a no-op trains users to dismiss the dialog unread.</summary>
    public required string Signature { get; init; }
}
