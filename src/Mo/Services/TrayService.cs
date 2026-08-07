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
