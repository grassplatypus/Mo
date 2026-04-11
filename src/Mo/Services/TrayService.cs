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
    }

    public void Initialize()
    {
        _trayIcon = new TaskbarIcon
        {
            ToolTipText = ResourceHelper.GetString("TrayTooltip"),
            ContextMenuMode = ContextMenuMode.SecondWindow,
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

        _trayIcon.LeftClickCommand = new SimpleCommand(ShowMainWindow);
        UpdateContextMenu();
        _trayIcon.ForceCreate();
    }

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
        _trayIcon?.Dispose();
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
