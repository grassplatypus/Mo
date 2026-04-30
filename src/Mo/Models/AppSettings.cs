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

    // Re-apply the last-applied profile on app startup so reboots don't lose the layout.
    public bool RestoreOnStartup { get; set; } = true;
    // Re-push DDC/CI brightness/contrast/RGB gain on startup (Windows doesn't persist these).
    public bool RestoreColorOnStartup { get; set; } = true;

    // Flipped to true the first time the app detects an NVIDIA or AMD GPU and offers
    // to switch the rotation backend. Prevents the prompt from nagging on every launch.
    public bool GpuRotationMethodPromptShown { get; set; }
}

public enum RotationMethod
{
    Windows,
    NvidiaDriver,
    AmdDriver,
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
