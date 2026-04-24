using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Mo.Services;

namespace Mo.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsService _settingsService;

    public SettingsViewModel(ISettingsService settingsService)
    {
        _settingsService = settingsService;
        _ = _settingsService.LoadAsync();
    }

    public bool LaunchAtStartup
    {
        get => _settingsService.Settings.LaunchAtStartup;
        set { if (_settingsService.Settings.LaunchAtStartup != value) { _settingsService.Settings.LaunchAtStartup = value; _ = _settingsService.SaveAsync(); OnPropertyChanged(); } }
    }

    public bool MinimizeToTrayOnClose
    {
        get => _settingsService.Settings.MinimizeToTrayOnClose;
        set { if (_settingsService.Settings.MinimizeToTrayOnClose != value) { _settingsService.Settings.MinimizeToTrayOnClose = value; _ = _settingsService.SaveAsync(); OnPropertyChanged(); } }
    }

    public bool StartMinimized
    {
        get => _settingsService.Settings.StartMinimized;
        set { if (_settingsService.Settings.StartMinimized != value) { _settingsService.Settings.StartMinimized = value; _ = _settingsService.SaveAsync(); OnPropertyChanged(); } }
    }

    public string Theme
    {
        get => _settingsService.Settings.Theme;
        set { if (_settingsService.Settings.Theme != value) { _settingsService.Settings.Theme = value; _ = _settingsService.SaveAsync(); OnPropertyChanged(); } }
    }

    public bool AutoSwitchEnabled
    {
        get => _settingsService.Settings.AutoSwitchEnabled;
        set
        {
            if (_settingsService.Settings.AutoSwitchEnabled != value)
            {
                _settingsService.Settings.AutoSwitchEnabled = value;
                _ = _settingsService.SaveAsync();
                OnPropertyChanged();

                // Start/stop the auto-switch service
                try
                {
                    var autoSwitch = App.Services.GetRequiredService<IAutoSwitchService>();
                    if (value) autoSwitch.Start(); else autoSwitch.Stop();
                }
                catch { }
            }
        }
    }

    public bool CheckForUpdates
    {
        get => _settingsService.Settings.CheckForUpdates;
        set { if (_settingsService.Settings.CheckForUpdates != value) { _settingsService.Settings.CheckForUpdates = value; _ = _settingsService.SaveAsync(); OnPropertyChanged(); } }
    }

    public bool RestoreOnStartup
    {
        get => _settingsService.Settings.RestoreOnStartup;
        set { if (_settingsService.Settings.RestoreOnStartup != value) { _settingsService.Settings.RestoreOnStartup = value; _ = _settingsService.SaveAsync(); OnPropertyChanged(); } }
    }

    public bool RestoreColorOnStartup
    {
        get => _settingsService.Settings.RestoreColorOnStartup;
        set { if (_settingsService.Settings.RestoreColorOnStartup != value) { _settingsService.Settings.RestoreColorOnStartup = value; _ = _settingsService.SaveAsync(); OnPropertyChanged(); } }
    }

    public bool HotkeysEnabled
    {
        get => _settingsService.Settings.HotkeysEnabled;
        set
        {
            if (_settingsService.Settings.HotkeysEnabled != value)
            {
                _settingsService.Settings.HotkeysEnabled = value;
                _ = _settingsService.SaveAsync();
                OnPropertyChanged();

                // Register/unregister the global hotkey subscriptions accordingly.
                try
                {
                    var hotkeys = App.Services.GetRequiredService<IHotkeyService>();
                    var profiles = App.Services.GetRequiredService<IProfileService>();
                    if (value)
                    {
                        foreach (var p in profiles.Profiles)
                            if (p.Hotkey != null) hotkeys.RegisterProfileHotkey(p.Id, p.Hotkey);
                    }
                    else
                    {
                        hotkeys.UnregisterAll();
                    }
                }
                catch { }
            }
        }
    }
}
