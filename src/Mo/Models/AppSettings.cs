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
}

public enum RotationMethod
{
    Windows,
    NvidiaDriver,
    AmdDriver,
    IntelDriver,
}

public sealed class WindowPlacement
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; } = 900;
    public int Height { get; set; } = 600;
    public bool IsMaximized { get; set; }
}
