namespace Mo.Models;

public sealed class AppSettings
{
    public bool LaunchAtStartup { get; set; }
    public bool MinimizeToTrayOnClose { get; set; } = true;
    public bool StartMinimized { get; set; }
    public string Theme { get; set; } = "System";
    public bool HotkeysEnabled { get; set; } = true;
    public string? LastAppliedProfileId { get; set; }
    public bool AutoSwitchEnabled { get; set; } = true;
    public bool CheckForUpdates { get; set; } = true;
    public string? LastUpdateCheck { get; set; }
    public RotationMethod RotationMethod { get; set; } = RotationMethod.Windows;
    public WindowPlacement? WindowPlacement { get; set; }

    // Countdown dialog after an apply, rolling back unless confirmed. On by default: a
    // bad layout can leave a monitor black with no way to undo it from inside Mo.
    // See .claude/rules/40-safety-invariants.md.
    public bool ConfirmApply { get; set; } = true;

    // Re-apply the last-applied profile on app startup so reboots don't lose the layout.
    public bool RestoreOnStartup { get; set; } = true;
    // Re-push DDC/CI brightness/contrast/RGB gain on startup (Windows doesn't persist these).
    public bool RestoreColorOnStartup { get; set; } = true;

    // Blank and wake the panels after the user applies a rotation, so the GPU reseats
    // the cursor plane. Off by default: it was measured on one NVIDIA machine and the
    // cost is a full blackout. See .claude/rules/30-display-apis.md.
    public bool ResetCursorAfterRotation { get; set; }

    // Flipped to true the first time the app detects an NVIDIA or AMD GPU and offers
    // to switch the rotation backend. Prevents the prompt from nagging on every launch.
    public bool GpuRotationMethodPromptShown { get; set; }

    // BCP-47 language tag override. Empty = follow Windows display language. Non-empty
    // values like "ko-KR" or "en-US" force the matching .resw bundle on next launch.
    public string Language { get; set; } = string.Empty;

    // Global hotkey to cycle to the next profile in list order.
    public HotkeyBinding? NextProfileHotkey { get; set; }
    // Global hotkey to cycle to the previous profile in list order.
    public HotkeyBinding? PrevProfileHotkey { get; set; }
    // Modifier combination prepended to digit keys 0–9 to apply profile slots 0–9.
    // Stored without a Key so we can compose at registration time. Null = slots disabled.
    public HotkeyBinding? ProfileSlotModifier { get; set; }
        = new() { Ctrl = true, Alt = true };
}

public enum RotationMethod
{
    Windows,
    NvidiaDriver,
    AmdDriver,

    // Kept only so a settings.json written before the Intel path was removed still
    // deserializes. Nothing offers or handles it, so it falls through to CCD.
    IntelDriver,
}

public enum AppTheme
{
    System,
    Light,
    Dark,
}

public sealed class WindowPlacement
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; } = 900;
    public int Height { get; set; } = 600;
    public bool IsMaximized { get; set; }
}
