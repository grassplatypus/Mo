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
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            LogException("AppDomain.UnhandledException", e.ExceptionObject as Exception);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var services = new ServiceCollection();
        ConfigureServices(services);
        Services = services.BuildServiceProvider();

        // Pre-load settings synchronously so StartMinimized / startup-task launches can
        // honor "start in tray" before we ever activate the window. The full async init
        // still runs below; LoadAsync is idempotent (`_loaded` guard) so this is free.
        bool startMinimized = false;
        try
        {
            var settings = Services.GetRequiredService<ISettingsService>();
            settings.LoadAsync().GetAwaiter().GetResult();
            startMinimized = settings.Settings.StartMinimized || IsStartupTaskActivation();

            // Apply user language override BEFORE the first window is created so initial
            // resource lookups (window title, x:Uid bindings) hit the right .resw. When
            // the user hasn't picked an override, fall back to the first preferred system
            // language so Korean Windows shows Korean UI even when our DefaultLanguage is
            // en-US (without this, the WinAppSDK ResourceLoader sometimes refuses to
            // resolve ko-KR resources for unpackaged or sideloaded MSIX builds).
            var lang = settings.Settings.Language;
            if (string.IsNullOrWhiteSpace(lang))
            {
                try
                {
                    var preferred = Windows.System.UserProfile.GlobalizationPreferences.Languages;
                    if (preferred?.Count > 0) lang = preferred[0];
                }
                catch { }
            }
            if (!string.IsNullOrWhiteSpace(lang))
            {
                try { Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = lang; }
                catch { }
            }
        }
        catch (Exception ex) { LogException("OnLaunched.PreInit", ex); }

        MainWindow = new MainWindow();
        if (startMinimized)
        {
            // Hide the OS window FIRST (AppWindow.Hide is valid pre-Activate). We
            // still must Activate so the dispatcher / message loop wires up properly
            // — without it, HideWindow re-show by the tray later silently fails.
            MainWindow.HideWindow();
            MainWindow.Activate();
            // Activate forces a brief WS_VISIBLE flip; re-hide immediately after the
            // first paint to swallow the flash.
            MainWindow.HideWindow();
        }
        else
        {
            MainWindow.Activate();
        }

        MainWindow.DispatcherQueue.ShutdownStarting += (_, _) => DisposeServices();

        // Secondary launches are redirected here by Program.Main's single-instance guard.
        // Bring the existing window forward instead of letting the redirect end silently —
        // otherwise the user clicks the Start-menu shortcut and nothing visible happens.
        try
        {
            Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent().Activated += (_, _) =>
                MainWindow?.DispatcherQueue?.TryEnqueue(() => MainWindow?.ShowAndActivate());
        }
        catch (Exception ex) { LogException("OnLaunched.RegisterActivated", ex); }

        _ = InitializeAsync();
    }

    // True when Windows launched the app via the StartupTask contract (logon auto-run).
    // Lets us start in tray on boot even if the user hasn't toggled StartMinimized.
    private static bool IsStartupTaskActivation()
    {
        try
        {
            var aea = Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent().GetActivatedEventArgs();
            return aea?.Kind == Microsoft.Windows.AppLifecycle.ExtendedActivationKind.StartupTask;
        }
        catch { return false; }
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
        services.AddSingleton<AmdColorService>();
        services.AddSingleton<ISystemInfoService, SystemInfoService>();
        services.AddSingleton<IntelRotationService>();

        services.AddTransient<ProfileListViewModel>();
        // Singleton: SettingsViewModel mirrors AppSettings, so a single instance lets
        // every consumer (Settings page, prompts, hotkey editor) see PropertyChanged
        // when settings mutate from any source. Transient instances would each cache
        // a stale view of values the user changed elsewhere.
        services.AddSingleton<SettingsViewModel>();
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

            // Restore last-applied profile after reboot (NVIDIA/CCD persistence is unreliable).
            _ = RestoreLastAppliedProfileAsync();

            // First-launch: offer to switch rotation backend if a driver SDK is available.
            _ = MaybeOfferDriverRotationAsync();

            SafeInit(() => RegisterAllHotkeys());

            // Re-register hotkeys whenever the profile list changes so the 0–9 slot
            // bindings track the current profile order.
            profileService.Profiles.CollectionChanged += (_, _) =>
                MainWindow?.DispatcherQueue?.TryEnqueue(() => SafeInit(RegisterAllHotkeys));
        }
        catch (Exception ex)
        {
            LogException("InitializeAsync", ex);
        }

        // Auto-check for updates (after everything else, non-blocking)
        _ = CheckForUpdateOnStartupAsync();
    }

    // On first launch, if the user is on an NVIDIA or AMD GPU and still using the default
    // Windows rotation path, offer to switch. Windows rotation triggers a known cursor-
    // coordinate bug; driver-level rotation avoids it. Shown once — tracked via
    // AppSettings.GpuRotationMethodPromptShown.
    private static async Task MaybeOfferDriverRotationAsync()
    {
        try
        {
            var settings = Services.GetRequiredService<ISettingsService>();
            if (settings.Settings.GpuRotationMethodPromptShown) return;
            if (settings.Settings.RotationMethod != Models.RotationMethod.Windows) return;

            // Mark "shown" BEFORE doing anything risky. If the dialog flow or the
            // RotationMethod write below throws, we must still never re-prompt — the
            // 0.20.1 bug where the SettingsPage Selector binding NREed left this
            // flag false and trapped users in an infinite crash loop on every launch.
            settings.Settings.GpuRotationMethodPromptShown = true;
            try { await settings.SaveAsync(); }
            catch (Exception saveEx) { LogException("MaybeOfferDriverRotationAsync.MarkShown", saveEx); }

            // Give the shell a beat to settle so the dialog doesn't race MainWindow.
            await Task.Delay(2500);

            Models.RotationMethod? suggestion = null;
            string? vendorName = null;

            if (Services.GetRequiredService<NvidiaRotationService>().IsAvailable)
            {
                suggestion = Models.RotationMethod.NvidiaDriver;
                vendorName = "NVIDIA";
            }
            else if (Services.GetRequiredService<AmdRotationService>().IsAvailable)
            {
                suggestion = Models.RotationMethod.AmdDriver;
                vendorName = "AMD";
            }

            // No supported driver detected — nothing to offer.
            if (suggestion == null || vendorName == null) return;

            if (MainWindow?.Content?.XamlRoot == null) return;

            var dialog = new ContentDialog
            {
                Title = ResourceHelper.GetString("GpuPromptTitle"),
                Content = ResourceHelper.GetString("GpuPromptContent", vendorName),
                PrimaryButtonText = ResourceHelper.GetString("GpuPromptUseDriver", vendorName),
                CloseButtonText = ResourceHelper.GetString("GpuPromptKeepWindows"),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = MainWindow.Content.XamlRoot,
            };

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                // Write directly to the settings store — going through the VM setter
                // raises PropertyChanged into a possibly-cached SettingsPage whose
                // SelectedValue/SelectedValuePath ComboBox binding throws NRE during
                // the TwoWay readback (CastHelpers.Unbox on a null Selector value).
                settings.Settings.RotationMethod = suggestion.Value;
                try { await settings.SaveAsync(); }
                catch (Exception saveEx) { LogException("MaybeOfferDriverRotationAsync.SaveRotation", saveEx); }
            }
        }
        catch (Exception ex)
        {
            LogException("MaybeOfferDriverRotationAsync", ex);
        }
    }

    private static async Task RestoreLastAppliedProfileAsync()
    {
        try
        {
            var settings = Services.GetRequiredService<ISettingsService>();
            if (!settings.Settings.RestoreOnStartup) return;

            var profileId = settings.Settings.LastAppliedProfileId;
            if (string.IsNullOrEmpty(profileId)) return;

            var profileService = Services.GetRequiredService<IProfileService>();
            var profile = profileService.Profiles.FirstOrDefault(p => p.Id == profileId);
            if (profile == null) return;

            // Let the shell settle before touching displays.
            await Task.Delay(1500);

            var displayService = Services.GetRequiredService<IDisplayService>();
            var compatibility = displayService.CheckCompatibility(profile);
            if (!compatibility.IsFullMatch && compatibility.MissingMonitors.Count > 0 &&
                compatibility.MissingMonitors.Count == profile.Monitors.Count)
            {
                // No profile monitor is present — skip silently; user likely on a different setup.
                return;
            }

            await profileService.ApplyProfileAsync(profileId, applyColor: settings.Settings.RestoreColorOnStartup);
        }
        catch (Exception ex)
        {
            LogException("RestoreLastAppliedProfileAsync", ex);
        }
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

    // Registers every hotkey from settings + profiles. Idempotent — safe to call again
    // after a profile is created/deleted or the user edits hotkey settings.
    public static void RegisterAllHotkeys()
    {
        var hotkeys = Services.GetRequiredService<IHotkeyService>();
        var profiles = Services.GetRequiredService<IProfileService>();
        var settings = Services.GetRequiredService<ISettingsService>();

        hotkeys.SetWindowHandle(WindowHelper.GetHwnd(MainWindow));
        hotkeys.UnregisterAll();
        if (!settings.Settings.HotkeysEnabled) return;

        foreach (var profile in profiles.Profiles)
            if (profile.Hotkey != null)
                hotkeys.RegisterProfileHotkey(profile.Id, profile.Hotkey);

        if (settings.Settings.NextProfileHotkey is { } nb)
            hotkeys.RegisterNextProfile(nb);
        if (settings.Settings.PrevProfileHotkey is { } pb)
            hotkeys.RegisterPrevProfile(pb);

        // Profile-slot hotkeys: <modifier> + 0..9 → apply Profiles[0..9].
        if (settings.Settings.ProfileSlotModifier is { } mod)
        {
            for (int i = 0; i < Math.Min(10, profiles.Profiles.Count); i++)
            {
                var key = i == 0 ? Windows.System.VirtualKey.Number0
                                 : (Windows.System.VirtualKey)((int)Windows.System.VirtualKey.Number0 + i);
                hotkeys.RegisterProfileSlot(i, new Models.HotkeyBinding
                {
                    Key = key,
                    Ctrl = mod.Ctrl,
                    Alt = mod.Alt,
                    Shift = mod.Shift,
                    Win = mod.Win,
                });
            }
        }

        // Single subscription point — clear & re-add so we never accumulate handlers.
        if (hotkeys is HotkeyService hs)
        {
            hs.HotkeyTriggered -= OnHotkeyTriggered;
            hs.HotkeyTriggered += OnHotkeyTriggered;
        }
    }

    private static async void OnHotkeyTriggered(object? sender, HotkeyTriggeredArgs e)
    {
        try
        {
            var profiles = Services.GetRequiredService<IProfileService>();
            switch (e.Action)
            {
                case HotkeyService.HotkeyAction.Profile when e.Payload is { } id:
                    await profiles.ApplyProfileAsync(id);
                    break;
                case HotkeyService.HotkeyAction.ProfileSlot when int.TryParse(e.Payload, out int slot)
                                                              && slot < profiles.Profiles.Count:
                    await profiles.ApplyProfileAsync(profiles.Profiles[slot].Id);
                    break;
                case HotkeyService.HotkeyAction.NextProfile:
                    await CycleProfileAsync(+1);
                    break;
                case HotkeyService.HotkeyAction.PrevProfile:
                    await CycleProfileAsync(-1);
                    break;
            }
        }
        catch (Exception ex) { LogException("Hotkey", ex); }
    }

    private static async Task CycleProfileAsync(int delta)
    {
        var profiles = Services.GetRequiredService<IProfileService>();
        var settings = Services.GetRequiredService<ISettingsService>();
        if (profiles.Profiles.Count == 0) return;

        int currentIdx = -1;
        var lastId = settings.Settings.LastAppliedProfileId;
        if (!string.IsNullOrEmpty(lastId))
        {
            for (int i = 0; i < profiles.Profiles.Count; i++)
                if (profiles.Profiles[i].Id == lastId) { currentIdx = i; break; }
        }
        int nextIdx = (currentIdx + delta + profiles.Profiles.Count) % profiles.Profiles.Count;
        await profiles.ApplyProfileAsync(profiles.Profiles[nextIdx].Id);
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
        try { Services.GetRequiredService<IMonitorColorService>().Dispose(); } catch { }
        try { Services.GetRequiredService<AmdColorService>().Dispose(); } catch { }
    }
}
