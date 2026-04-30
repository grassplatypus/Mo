using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Mo.Models;
using Mo.Services;

namespace Mo.ViewModels;

// Two-way bindings to AppSettings with three concerns rolled into one helper:
//   1. value comparison so we don't spam SaveAsync,
//   2. PropertyChanged notifications,
//   3. side-effects that must happen exactly when a user-facing toggle flips
//      (registering at logon, starting AutoSwitch, registering hotkeys, …).
public partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsService _settings;

    public SettingsViewModel(ISettingsService settings)
    {
        _settings = settings;
        _ = _settings.LoadAsync();
    }

    public bool LaunchAtStartup
    {
        get => _settings.Settings.LaunchAtStartup;
        set => Set(_settings.Settings.LaunchAtStartup, value, v => _settings.Settings.LaunchAtStartup = v, v =>
        {
            // Toggle alone never registered the StartupTask / Run-key entry; do it here.
            _ = Task.Run(async () =>
            {
                try
                {
                    var startup = App.Services.GetRequiredService<IStartupService>();
                    if (v) await startup.RegisterForStartupAsync();
                    else await startup.UnregisterFromStartupAsync();
                }
                catch { }
            });
        });
    }

    public bool MinimizeToTrayOnClose
    {
        get => _settings.Settings.MinimizeToTrayOnClose;
        set => Set(_settings.Settings.MinimizeToTrayOnClose, value, v => _settings.Settings.MinimizeToTrayOnClose = v);
    }

    public bool StartMinimized
    {
        get => _settings.Settings.StartMinimized;
        set => Set(_settings.Settings.StartMinimized, value, v => _settings.Settings.StartMinimized = v);
    }

    public AppTheme Theme
    {
        get => ParseTheme(_settings.Settings.Theme);
        set => Set(ParseTheme(_settings.Settings.Theme), value,
            v => _settings.Settings.Theme = v.ToString(),
            v => App.MainWindow?.ApplyTheme(v.ToString()));
    }

    public bool AutoSwitchEnabled
    {
        get => _settings.Settings.AutoSwitchEnabled;
        set => Set(_settings.Settings.AutoSwitchEnabled, value, v => _settings.Settings.AutoSwitchEnabled = v, v =>
        {
            try
            {
                var autoSwitch = App.Services.GetRequiredService<IAutoSwitchService>();
                if (v) autoSwitch.Start(); else autoSwitch.Stop();
            }
            catch { }
        });
    }

    public bool CheckForUpdates
    {
        get => _settings.Settings.CheckForUpdates;
        set => Set(_settings.Settings.CheckForUpdates, value, v => _settings.Settings.CheckForUpdates = v);
    }

    public bool RestoreOnStartup
    {
        get => _settings.Settings.RestoreOnStartup;
        set => Set(_settings.Settings.RestoreOnStartup, value, v => _settings.Settings.RestoreOnStartup = v);
    }

    public bool RestoreColorOnStartup
    {
        get => _settings.Settings.RestoreColorOnStartup;
        set => Set(_settings.Settings.RestoreColorOnStartup, value, v => _settings.Settings.RestoreColorOnStartup = v);
    }

    public bool HotkeysEnabled
    {
        get => _settings.Settings.HotkeysEnabled;
        set => Set(_settings.Settings.HotkeysEnabled, value, v => _settings.Settings.HotkeysEnabled = v, v =>
        {
            try
            {
                var hotkeys = App.Services.GetRequiredService<IHotkeyService>();
                var profiles = App.Services.GetRequiredService<IProfileService>();
                if (v)
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
        });
    }

    public RotationMethod RotationMethod
    {
        get => _settings.Settings.RotationMethod;
        set => Set(_settings.Settings.RotationMethod, value, v => _settings.Settings.RotationMethod = v);
    }

    // Empty string means "follow Windows display language". Override takes effect on
    // next launch — surface a hint near the combo in the page.
    public string Language
    {
        get => _settings.Settings.Language ?? string.Empty;
        set => Set(_settings.Settings.Language ?? string.Empty, value ?? string.Empty,
            v => _settings.Settings.Language = v);
    }

    private void Set<T>(T current, T next, Action<T> assign, Action<T>? sideEffect = null,
        [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(current, next)) return;
        assign(next);
        _ = _settings.SaveAsync();
        OnPropertyChanged(propertyName);
        sideEffect?.Invoke(next);
    }

    private static AppTheme ParseTheme(string s) => s switch
    {
        "Light" => AppTheme.Light,
        "Dark" => AppTheme.Dark,
        _ => AppTheme.System,
    };
}
