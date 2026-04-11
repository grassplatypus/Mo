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

        // Global exception handlers
        UnhandledException += App_UnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var services = new ServiceCollection();
        ConfigureServices(services);
        Services = services.BuildServiceProvider();

        MainWindow = new MainWindow();
        MainWindow.Activate();

        // DispatcherQueue unhandled exceptions
        MainWindow.DispatcherQueue.ShutdownStarting += (_, _) =>
        {
            // Cleanup services on shutdown
            DisposeServices();
        };

        _ = InitializeAsync();
    }

    // ── Global Exception Handlers ──

    private void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        // Prevent app crash — log and swallow
        e.Handled = true;
        LogException("UnhandledException", e.Exception);
    }

    private static void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        // Prevent crash from fire-and-forget tasks
        e.SetObserved();
        LogException("UnobservedTaskException", e.Exception);
    }

    private static void LogException(string source, Exception? ex)
    {
        if (ex == null) return;

        try
        {
            var logDir = GetLogDirectory();
            Directory.CreateDirectory(logDir);

            var logFile = Path.Combine(logDir, $"crash_{DateTime.Now:yyyyMMdd}.log");
            var entry = $"""
                [{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{source}]
                {ex.GetType().FullName}: {ex.Message}
                {ex.StackTrace}
                {(ex.InnerException != null ? $"Inner: {ex.InnerException.Message}\n{ex.InnerException.StackTrace}" : "")}
                ---

                """;

            File.AppendAllText(logFile, entry);
        }
        catch
        {
            // Logging itself failed — nothing we can do
        }
    }

    private static string GetLogDirectory()
    {
        try
        {
            return Path.Combine(Windows.Storage.ApplicationData.Current.LocalFolder.Path, "logs");
        }
        catch
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Mo", "logs");
        }
    }

    // ── DI Configuration ──

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

    // ── Initialization ──

    private static async Task InitializeAsync()
    {
        try
        {
            var settingsService = Services.GetRequiredService<ISettingsService>();
            await settingsService.LoadAsync();

            // Apply saved theme
            if (MainWindow.Content is FrameworkElement root)
                ThemeHelper.ApplyTheme(root, settingsService.Settings.Theme);

            var profileService = Services.GetRequiredService<IProfileService>();
            await profileService.LoadAllAsync();

            // Initialize tray
            SafeInit(() =>
            {
                var trayService = Services.GetRequiredService<ITrayService>();
                trayService.Initialize();
            });

            // Start auto-switch
            SafeInit(() => Services.GetRequiredService<IAutoSwitchService>().Start());

            // Start schedule
            SafeInit(() => Services.GetRequiredService<IScheduleService>().Start());

            // Register hotkeys
            SafeInit(() =>
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
            });
        }
        catch (Exception ex)
        {
            LogException("InitializeAsync", ex);
        }
    }

    private static void SafeInit(Action action)
    {
        try { action(); }
        catch (Exception ex) { LogException("SafeInit", ex); }
    }

    // ── Cleanup ──

    private static void DisposeServices()
    {
        try { Services.GetRequiredService<IHotkeyService>().Dispose(); } catch { }
        try { Services.GetRequiredService<IAutoSwitchService>().Dispose(); } catch { }
        try { Services.GetRequiredService<IScheduleService>().Dispose(); } catch { }
        try { Services.GetRequiredService<ITrayService>().Dispose(); } catch { }
    }
}
