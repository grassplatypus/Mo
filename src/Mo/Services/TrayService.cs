using System.Drawing;
using H.NotifyIcon;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Mo.Helpers;

namespace Mo.Services;

public sealed class TrayService : ITrayService
{
    private TaskbarIcon? _trayIcon;
    private readonly IProfileService _profileService;

    public TrayService(IProfileService profileService)
    {
        _profileService = profileService;
        // Refresh menu when profiles are added / renamed / removed so right-click
        // never shows a stale list.
        _profileService.Profiles.CollectionChanged += (_, _) =>
        {
            try { App.MainWindow?.DispatcherQueue?.TryEnqueue(UpdateContextMenu); } catch { }
        };
    }

    public bool IsAvailable { get; private set; }

    public bool Initialize()
    {
        try
        {
            _trayIcon?.Dispose();
            _trayIcon = new TaskbarIcon
            {
                ToolTipText = ResourceHelper.GetString("TrayTooltip"),
                // PopupMenu is the most compatible mode — SecondWindow can fail on some
                // session/elevated combinations, leaving right-click silently broken.
                ContextMenuMode = ContextMenuMode.PopupMenu,
            };

            // Load tray icon from file
            try
            {
                var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "TrayIcon.ico");
                if (File.Exists(iconPath))
                {
                    _trayIcon.Icon = new Icon(iconPath);
                }
                else
                {
                    // Fallback to app icon
                    iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
                    if (File.Exists(iconPath))
                        _trayIcon.Icon = new Icon(iconPath);
                }
            }
            catch
            {
                // Icon loading failed, tray will show default
            }

            // Only a double-click opens the window — matches Windows convention for
            // secondary tray apps (OneDrive, Dropbox, etc.). Single-click does nothing.
            _trayIcon.DoubleClickCommand = new SimpleCommand(ShowMainWindow);
            UpdateContextMenu();
            _trayIcon.ForceCreate();

            IsAvailable = true;
        }
        catch (Exception ex)
        {
            // Shell_NotifyIcon can fail outright when Explorer is restarting, when the
            // notification area is saturated, or under some elevated-session combos.
            // Report it rather than swallowing: with MinimizeToTrayOnClose on, a silent
            // failure here is what turns "close the window" into "lose the app".
            Helpers.BootLog.WriteError("tray.initialize", ex);
            try { _trayIcon?.Dispose(); } catch { }
            _trayIcon = null;
            IsAvailable = false;
        }

        return IsAvailable;
    }

    public bool EnsureCreated() => IsAvailable || Initialize();

    public void UpdateContextMenu()
    {
        if (_trayIcon == null) return;

        var flyout = new MenuFlyout();

        foreach (var profile in _profileService.Profiles)
        {
            var profileId = profile.Id;
            flyout.Items.Add(new MenuFlyoutItem
            {
                Text = profile.Name,
                Command = new SimpleCommand(() => _ = _profileService.ApplyProfileAsync(profileId)),
            });
        }

        if (_profileService.Profiles.Count > 0)
            flyout.Items.Add(new MenuFlyoutSeparator());

        // Capturing the current arrangement is the one thing worth doing without
        // opening the window: you finish arranging your monitors and want to keep it,
        // and the tray is already where you are.
        flyout.Items.Add(new MenuFlyoutItem
        {
            Text = ResourceHelper.GetString("TraySaveCurrent"),
            Command = new SimpleCommand(SaveCurrentConfiguration),
        });

        flyout.Items.Add(new MenuFlyoutItem
        {
            Text = ResourceHelper.GetString("TrayOpen"),
            Command = new SimpleCommand(ShowMainWindow),
        });

        flyout.Items.Add(new MenuFlyoutSeparator());

        flyout.Items.Add(new MenuFlyoutItem
        {
            Text = ResourceHelper.GetString("TrayExit"),
            Command = new SimpleCommand(ExitApp),
        });

        _trayIcon.ContextFlyout = flyout;
    }

    public void Dispose()
    {
        IsAvailable = false;
        _trayIcon?.Dispose();
        _trayIcon = null;
    }

    private static void ShowMainWindow()
    {
        App.MainWindow?.ShowAndActivate();
    }

    /// <summary>
    /// Captures the current display arrangement as a new profile, then shows the window
    /// on it so the user can name it.
    /// </summary>
    /// <remarks>
    /// Marshalled onto the UI thread: the tray command runs on whatever thread
    /// H.NotifyIcon dispatches from, while capture touches the profile collection that
    /// the list is bound to.
    /// </remarks>
    private void SaveCurrentConfiguration()
    {
        var queue = App.MainWindow?.DispatcherQueue;
        if (queue == null) return;

        queue.TryEnqueue(async () =>
        {
            try
            {
                var name = ResourceHelper.GetString("TraySavedNameFormat", _profileService.Profiles.Count + 1);
                var profile = await _profileService.CaptureCurrentAsync(name);
                await _profileService.SaveProfileAsync(profile);

                // Surface the window rather than saving silently — an unnamed profile
                // appearing in the tray list with no feedback is worse than none.
                App.MainWindow?.ShowAndActivate();
            }
            catch (Exception ex) { Helpers.BootLog.WriteError("tray.savecurrent", ex); }
        });
    }

    private void ExitApp()
    {
        _trayIcon?.Dispose();
        _trayIcon = null;
        App.MainWindow?.ForceClose();
    }

    private sealed class SimpleCommand : System.Windows.Input.ICommand
    {
        private readonly Action _execute;
        public SimpleCommand(Action execute) => _execute = execute;
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => _execute();
    }
}
