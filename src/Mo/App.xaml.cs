using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Mo.Helpers;
using Mo.Services;
using Mo.ViewModels;
using Windows.ApplicationModel.DataTransfer;

namespace Mo;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;
    public static MainWindow MainWindow { get; private set; } = null!;

    private static bool _isShowingErrorDialog;

    public App()
    {
        InitializeComponent();
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

        MainWindow.DispatcherQueue.ShutdownStarting += (_, _) => DisposeServices();

        _ = InitializeAsync();
    }

    // ── Global Exception Handlers ──

    private void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        var ex = e.Exception;
        LogException("UnhandledException", ex);
        _ = ShowErrorDialogAsync(ex);
    }

    private static void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        e.SetObserved();
        var ex = e.Exception?.InnerException ?? e.Exception;
        LogException("UnobservedTaskException", ex);

        MainWindow?.DispatcherQueue?.TryEnqueue(() => _ = ShowErrorDialogAsync(ex));
    }

    // ── Error Dialog ──

    private static async Task ShowErrorDialogAsync(Exception? ex)
    {
        if (ex == null || _isShowingErrorDialog) return;
        if (MainWindow?.Content == null) return;

        _isShowingErrorDialog = true;
        try
        {
            var detail = SystemInfoHelper.BuildErrorReport(ex);

            var detailBox = new TextBox
            {
                Text = detail,
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                FontSize = 11,
                MaxHeight = 300,
                MinHeight = 120,
            };

            var copyButton = new Button
            {
                Content = ResourceHelper.GetString("CopyErrorInfo"),
                Margin = new Thickness(0, 8, 0, 0),
            };
            copyButton.Click += (_, _) =>
            {
                var dp = new DataPackage();
                dp.SetText(detail);
                Clipboard.SetContent(dp);
                copyButton.Content = ResourceHelper.GetString("Copied");
            };

            var panel = new StackPanel { Spacing = 4 };
            panel.Children.Add(new TextBlock
            {
                Text = ResourceHelper.GetString("UnhandledErrorDesc"),
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.8,
            });
            panel.Children.Add(detailBox);
            panel.Children.Add(copyButton);

            var dialog = new ContentDialog
            {
                Title = ResourceHelper.GetString("UnhandledErrorTitle"),
                Content = panel,
                PrimaryButtonText = ResourceHelper.GetString("ContinueRunning"),
                SecondaryButtonText = ResourceHelper.GetString("ExitApp"),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = MainWindow.Content.XamlRoot,
            };

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Secondary)
            {
                DisposeServices();
                MainWindow?.ForceClose();
            }
        }
        catch
        {
            // Dialog itself failed — already logged
        }
        finally
        {
            _isShowingErrorDialog = false;
        }
    }

    // BuildErrorReport and BuildFullReport are in SystemInfoHelper

    // ── Logging ──

    private static void LogException(string source, Exception? ex)
    {
        if (ex == null) return;
        try
        {
            var logDir = GetLogDirectory();
            Directory.CreateDirectory(logDir);
            var logFile = Path.Combine(logDir, $"crash_{DateTime.Now:yyyyMMdd}.log");
            var entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{source}]\n{SystemInfoHelper.BuildErrorReport(ex)}\n===\n\n";
            File.AppendAllText(logFile, entry);
        }
        catch { }
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
        services.AddSingleton<NvidiaRotationService>();
        services.AddSingleton<AmdRotationService>();
        services.AddSingleton<IntelRotationService>();

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

            if (MainWindow.Content is FrameworkElement root)
                ThemeHelper.ApplyTheme(root, settingsService.Settings.Theme);

            var profileService = Services.GetRequiredService<IProfileService>();
            await profileService.LoadAllAsync();

            SafeInit(() => Services.GetRequiredService<ITrayService>().Initialize());
            SafeInit(() => Services.GetRequiredService<IAutoSwitchService>().Start());
            SafeInit(() => Services.GetRequiredService<IScheduleService>().Start());

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
                    await profileService.ApplyProfileAsync(profileId);
            });
        }
        catch (Exception ex)
        {
            LogException("InitializeAsync", ex);
        }

        // Auto-check for updates (after everything else, non-blocking)
        _ = CheckForUpdateOnStartupAsync();
    }

    private static async Task CheckForUpdateOnStartupAsync()
    {
        try
        {
            var settings = Services.GetRequiredService<ISettingsService>();
            if (!settings.Settings.CheckForUpdates) return;

            // Don't check more than once per 12 hours
            if (DateTime.TryParse(settings.Settings.LastUpdateCheck, out var last) &&
                (DateTime.UtcNow - last).TotalHours < 12)
                return;

            await Task.Delay(5000); // Wait 5s after startup

            var updateService = Services.GetRequiredService<IUpdateService>();
            var (available, version, url) = await updateService.CheckForUpdateAsync();

            settings.Settings.LastUpdateCheck = DateTime.UtcNow.ToString("O");
            await settings.SaveAsync();

            if (available && !string.IsNullOrEmpty(version))
            {
                // Show notification via dispatcher
                MainWindow?.DispatcherQueue.TryEnqueue(async () =>
                {
                    await ShowUpdateNotificationAsync(version, url);
                });
            }
        }
        catch { }
    }

    private static async Task ShowUpdateNotificationAsync(string version, string? url)
    {
        if (MainWindow?.Content?.XamlRoot == null) return;

        var dialog = new ContentDialog
        {
            Title = ResourceHelper.GetString("UpdateAvailableTitle"),
            Content = ResourceHelper.GetString("UpdateAvailable", version),
            PrimaryButtonText = ResourceHelper.GetString("DownloadUpdate"),
            CloseButtonText = ResourceHelper.GetString("Later"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = MainWindow.Content.XamlRoot,
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary && !string.IsNullOrEmpty(url))
        {
            await Windows.System.Launcher.LaunchUriAsync(new Uri(url));
        }
    }

    private static void SafeInit(Action action)
    {
        try { action(); }
        catch (Exception ex) { LogException("SafeInit", ex); }
    }

    private static void DisposeServices()
    {
        try { Services.GetRequiredService<IHotkeyService>().Dispose(); } catch { }
        try { Services.GetRequiredService<IAutoSwitchService>().Dispose(); } catch { }
        try { Services.GetRequiredService<IScheduleService>().Dispose(); } catch { }
        try { Services.GetRequiredService<ITrayService>().Dispose(); } catch { }
    }
}
