using System;

namespace Mo.Services;

public interface ITrayService : IDisposable
{
    /// <summary>
    /// True once a tray icon actually exists. Anything that would leave the window
    /// hidden must check this first: with no tray icon and no window, the process is
    /// running but the user has no way to reach it.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>Creates the tray icon. Returns false if it could not be created.</summary>
    bool Initialize();

    /// <summary>
    /// Re-creates the icon if it went away — an Explorer restart destroys every tray
    /// icon, and a shell that was busy at startup can refuse the first attempt.
    /// Returns the resulting availability.
    /// </summary>
    bool EnsureCreated();

    void UpdateContextMenu();
}
