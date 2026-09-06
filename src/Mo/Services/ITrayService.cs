using System;

namespace Mo.Services;

public interface ITrayService : IDisposable
{
    /// <summary>True once a tray icon actually exists. Anything that would leave the
    /// window hidden must check this first — no icon and no window is unreachable.</summary>
    bool IsAvailable { get; }

    /// <summary>Creates the tray icon. Returns false if it could not be created.</summary>
    bool Initialize();

    /// <summary>Re-creates the icon if it went away — an Explorer restart destroys every
    /// tray icon, and a busy shell can refuse the first attempt.</summary>
    bool EnsureCreated();

    void UpdateContextMenu();
}
