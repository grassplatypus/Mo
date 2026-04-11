using System.Reflection;
using System.Runtime.InteropServices;
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
            var detail = BuildErrorReport(ex);

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

    private static string BuildErrorReport(Exception ex)
    {
        var version = UpdateService.CurrentVersion;
        var os = Environment.OSVersion.VersionString;
        var arch = RuntimeInformation.ProcessArchitecture.ToString();
        var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Mo v{version} | {os} | {arch}");
        sb.AppendLine($"Time: {now}");
        sb.AppendLine($".NET: {RuntimeInformation.FrameworkDescription}");

        // System hardware info
        try
        {
            sb.AppendLine($"CPU: {Environment.ProcessorCount} cores");
            sb.AppendLine($"Memory: {GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024 / 1024} MB total");
        }
        catch { }

        // Monitor info
        try
        {
            var displayService = Services?.GetService<IDisplayService>();
            if (displayService != null)
            {
                var monitors = displayService.GetCurrentConfiguration();
                sb.AppendLine($"Monitors: {monitors.Count}");
                foreach (var m in monitors)
                {
                    var name = string.IsNullOrEmpty(m.FriendlyName) ? "Unknown" : m.FriendlyName;
                    var rot = m.Rotation != Models.DisplayRotation.None ? $" {(int)m.Rotation}°" : "";
                    var primary = m.IsPrimary ? " [Primary]" : "";
                    sb.AppendLine($"  - {name}: {m.Width}x{m.Height} @ {m.RefreshRateHz:F0}Hz pos({m.PositionX},{m.PositionY}){rot}{primary}");
                    if (!string.IsNullOrEmpty(m.DevicePath))
                        sb.AppendLine($"    Path: {m.DevicePath[..Math.Min(m.DevicePath.Length, 80)]}");
                }
            }
        }
        catch { sb.AppendLine("Monitors: (failed to enumerate)"); }

        // GPU info via adapter descriptions
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher("SELECT Name, DriverVersion, AdapterRAM FROM Win32_VideoController");
            foreach (System.Management.ManagementObject obj in searcher.Get())
            {
                var name = obj["Name"]?.ToString() ?? "Unknown GPU";
                var driver = obj["DriverVersion"]?.ToString() ?? "?";
                var vram = obj["AdapterRAM"] is uint ram ? $"{ram / 1024 / 1024} MB" : "?";
                sb.AppendLine($"GPU: {name} (Driver: {driver}, VRAM: {vram})");
            }
        }
        catch { }

        sb.AppendLine(new string('-', 50));
        sb.AppendLine($"Exception: {ex.GetType().FullName}");
        sb.AppendLine($"Message: {ex.Message}");
        sb.AppendLine();
        sb.AppendLine("Stack Trace:");
        sb.AppendLine(ex.StackTrace ?? "(none)");

        var inner = ex.InnerException;
        var depth = 0;
        while (inner != null && depth < 3)
        {
            sb.AppendLine();
            sb.AppendLine($"--- Inner Exception [{depth}] ---");
            sb.AppendLine($"Exception: {inner.GetType().FullName}");
            sb.AppendLine($"Message: {inner.Message}");
            sb.AppendLine(inner.StackTrace ?? "(none)");
            inner = inner.InnerException;
            depth++;
        }

        return sb.ToString();
    }

    // ── Logging ──

    private static void LogException(string source, Exception? ex)
    {
        if (ex == null) return;
        try
        {
            var logDir = GetLogDirectory();
            Directory.CreateDirectory(logDir);
            var logFile = Path.Combine(logDir, $"crash_{DateTime.Now:yyyyMMdd}.log");
            var entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{source}]\n{BuildErrorReport(ex)}\n===\n\n";
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
