using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Mo.Helpers;
using Mo.Services;
using Mo.ViewModels;

namespace Mo;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;
    public static MainWindow MainWindow { get; private set; } = null!;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var services = new ServiceCollection();
        ConfigureServices(services);
        Services = services.BuildServiceProvider();

        MainWindow = new MainWindow();
        MainWindow.Activate();

        _ = InitializeAsync();
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<IDisplayService, DisplayService>();
        services.AddSingleton<IProfileService, ProfileService>();
        services.AddSingleton<ITrayService, TrayService>();
        services.AddSingleton<IHotkeyService, HotkeyService>();
        services.AddSingleton<IStartupService, StartupService>();
        services.AddSingleton<IAudioService, AudioService>();
        services.AddSingleton<IWallpaperService, WallpaperService>();
        services.AddSingleton<IAutoSwitchService, AutoSwitchService>();
        services.AddSingleton<IScheduleService, ScheduleService>();
        services.AddSingleton<IUpdateService, UpdateService>();
        services.AddSingleton<ExportImportService>();
        services.AddSingleton<ILiveWallpaperService, LiveWallpaperService>();
        services.AddSingleton<IMonitorColorService, MonitorColorService>();

        services.AddTransient<ProfileListViewModel>();
        services.AddTransient<SettingsViewModel>();
    }

    private static async Task InitializeAsync()
    {
        var settingsService = Services.GetRequiredService<ISettingsService>();
        await settingsService.LoadAsync();

        // Apply saved theme
        if (MainWindow.Content is FrameworkElement root)
        {
            ThemeHelper.ApplyTheme(root, settingsService.Settings.Theme);
        }

        var profileService = Services.GetRequiredService<IProfileService>();
        await profileService.LoadAllAsync();

        // Initialize tray (after profiles loaded so menu is populated)
        try
        {
            var trayService = Services.GetRequiredService<ITrayService>();
            trayService.Initialize();
        }
        catch
        {
            // Tray initialization can fail in some environments
        }

        // Start auto-switch service
        try
        {
            var autoSwitchService = Services.GetRequiredService<IAutoSwitchService>();
            autoSwitchService.Start();
        }
        catch
        {
            // Auto-switch initialization can fail
        }

        // Start schedule service
        try
        {
            var scheduleService = Services.GetRequiredService<IScheduleService>();
            scheduleService.Start();
        }
        catch
        {
            // Schedule initialization can fail
        }

        // Register hotkeys
        try
        {
            var hotkeyService = (HotkeyService)Services.GetRequiredService<IHotkeyService>();
            hotkeyService.SetWindowHandle(WindowHelper.GetHwnd(MainWindow));

            foreach (var profile in profileService.Profiles)
            {
                if (profile.Hotkey != null)
                    hotkeyService.RegisterProfileHotkey(profile.Id, profile.Hotkey);
            }

            hotkeyService.HotkeyTriggered += async (_, profileId) =>
            {
                await profileService.ApplyProfileAsync(profileId);
            };
        }
        catch
        {
            // Hotkey registration can fail
        }
    }
}
