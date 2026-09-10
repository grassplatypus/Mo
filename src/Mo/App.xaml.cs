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

    /// <summary>The one full-exit path: drop the tray icon, then close the window with the
    /// minimise-to-tray handler bypassed. Safe to call from any thread.</summary>
    public static void RequestExit()
    {
        var queue = MainWindow?.DispatcherQueue;
        if (queue != null)
        {
            if (queue.HasThreadAccess) { ExitNow(); return; }
            if (queue.TryEnqueue(ExitNow)) return;
        }

        // No window, or a dispatcher that will not take work: the windowless zombie
        // 20-architecture.md describes. There is nothing left to close politely, and
        // leaving it alive holds the single-instance key and the program files.
        BootLog.Write("exit.forced", "no window to close");
        Environment.Exit(0);

        static void ExitNow()
        {
            try { Services.GetRequiredService<ITrayService>().Dispose(); } catch { }
            MainWindow?.ForceClose();
        }
    }

    private static bool _isShowingErrorDialog;

    // False until MainWindow has been created and activated. While false, an unhandled
    // exception has no UI surface to report itself through, so it must be fatal rather
    // than silently swallowed.
    private static bool _windowReady;

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
        BootLog.Write("onlaunched.begin");
        var services = new ServiceCollection();
        ConfigureServices(services);
        Services = services.BuildServiceProvider();
        BootLog.Write("onlaunched.di.ok");

        // Pre-load settings synchronously so StartMinimized / startup-task launches can
        // honor "start in tray" before we ever activate the window. The full async init
        // still runs below; LoadAsync is idempotent (`_loaded` guard) so this is free.
        bool startMinimized = false;
        try
        {
            var settings = Services.GetRequiredService<ISettingsService>();
            // MUST be the synchronous overload: blocking the UI thread on LoadAsync()
            // deadlocks against Program.Main's DispatcherQueueSynchronizationContext,
            // intermittently. See .claude/rules/10-code-style.md.
            settings.Load();
            // Only an automatic launch starts in the tray. Opening Mo yourself, from the
            // Start menu or the installer's own button, has to produce a window: doing
            // otherwise is indistinguishable from the app failing to start.
            startMinimized = settings.Settings.StartMinimized && IsAutomaticLaunch();

            // Set the language BEFORE the first window so initial x:Uid lookups hit the
            // right .resw; an empty override falls back to the first preferred system
            // language. See .claude/rules/70-localization.md.
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
        catch (Exception ex) { LogException("OnLaunched.PreInit", ex); BootLog.WriteError("onlaunched.preinit", ex); }

        BootLog.Write("mainwindow.ctor.begin", $"startMinimized={startMinimized}");
        MainWindow = new MainWindow();
        BootLog.Write("mainwindow.ctor.end");
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
        _windowReady = true;
        BootLog.Write("mainwindow.activated");

        MainWindow.DispatcherQueue.ShutdownStarting += (_, _) => DisposeServices();

        // An entry written by an older build, or by a previous install location, makes
        // every logon look like a hand launch. Repairing it here costs one registry read.
        try { Services.GetRequiredService<IStartupService>().RepairRegistryEntry(); } catch { }

        // Secondary launches are redirected here by Program.Main's single-instance guard.
        // Bring the existing window forward instead of letting the redirect end silently —
        // otherwise the user clicks the Start-menu shortcut and nothing visible happens.
        try
        {
            Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent().Activated += (_, _) =>
                MainWindow?.DispatcherQueue?.TryEnqueue(() => MainWindow?.ShowAndActivate());
        }
        catch (Exception ex) { LogException("OnLaunched.RegisterActivated", ex); BootLog.WriteError("onlaunched.registeractivated", ex); }

        BootLog.Write("onlaunched.end");
        _ = InitializeAsync();
    }

    /// <summary>Marks the HKCU Run entry so a logon launch is recognisable. That launch
    /// is an ordinary Mo.exe with no activation kind of its own, so without the argument
    /// it is indistinguishable from a double-click.</summary>
    public const string StartupLaunchArgument = "--startup";

    /// <summary>True when Windows started Mo rather than the user: the packaged startup
    /// task, or the Run entry above.</summary>
    private static bool IsAutomaticLaunch()
    {
        if (Environment.GetCommandLineArgs()
            .Any(a => string.Equals(a, StartupLaunchArgument, StringComparison.OrdinalIgnoreCase)))
            return true;

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
        var ex = e.Exception;
        LogException("UnhandledException", ex);
        BootLog.WriteError("unhandled", ex);

        // Before the window exists there is no XamlRoot for a ContentDialog, so
        // swallowing would leave an invisible zombie holding the single-instance key.
        // Report and terminate instead — see .claude/rules/20-architecture.md.
        if (!_windowReady)
        {
            e.Handled = true; // Suppress the WinUI fail-fast so our own message wins.
            ReportStartupFailureAndExit(ex);
            return;
        }

        e.Handled = true;
        _ = ShowErrorDialogAsync(ex);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int MessageBoxW(nint hWnd, string text, string caption, uint type);

    private static void ReportStartupFailureAndExit(Exception? ex)
    {
        const uint MB_OK = 0x0, MB_ICONERROR = 0x10, MB_SETFOREGROUND = 0x10000;
        try
        {
            MessageBoxW(0,
                "Mo 창을 여는 중 오류가 발생했습니다.\n\n" +
                $"{ex?.GetType().Name}: {ex?.Message}\n\n" +
                $"로그: {Path.Combine(GetLogDirectory(), "boot.log")}",
                "Mo", MB_OK | MB_ICONERROR | MB_SETFOREGROUND);
        }
        catch { }

        // Environment.Exit, not Application.Exit: the dispatcher may already be in an
        // unusable state, and the one thing we must guarantee is that this process
        // releases the single-instance key so the next launch can start clean.
        Environment.Exit(-2);
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

            // AcceptsReturn before Text: a single-line TextBox drops everything after
            // the first break, which left this box showing only the report's ``` fence.
            var detailBox = new TextBox
            {
                AcceptsReturn = true,
                Text = detail,
                IsReadOnly = true,
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

            var result = await dialog.ShowThemedAsync();
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
        services.AddSingleton<IApplyGuardService, ApplyGuardService>();
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

        // Singleton, not transient: subscribes to the singleton ProfileApplied event and
        // never unsubscribes, so extra instances would leak and each would react.
        services.AddSingleton<ProfileListViewModel>();
        // Singleton: mirrors AppSettings, so every consumer sees PropertyChanged when
        // settings mutate from any source. Transient instances would cache stale values.
        services.AddSingleton<SettingsViewModel>();
    }

    // ── Initialization ──

    private static bool _hotkeyRebindQueued;

    /// <summary>Collapses a burst of list changes into one re-bind on the next dispatcher
    /// turn, so loading N profiles rebinds once rather than N times.</summary>
    private static void QueueHotkeyRebind()
    {
        var queue = MainWindow?.DispatcherQueue;
        if (queue == null || _hotkeyRebindQueued) return;

        _hotkeyRebindQueued = true;
        queue.TryEnqueue(() =>
        {
            _hotkeyRebindQueued = false;
            SafeInit(RegisterAllHotkeys);
        });
    }

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

            EnsureReachableWithoutTray();

            SafeInit(() => Services.GetRequiredService<IAutoSwitchService>().Start());
            SafeInit(() => Services.GetRequiredService<IScheduleService>().Start());

            // Restore last-applied profile after reboot (NVIDIA/CCD persistence is unreliable).
            _ = RestoreLastAppliedProfileAsync();

            // No first-launch driver-rotation prompt: the setting it changes is hidden
            // while the reasons for it are being re-measured. MaybeOfferDriverRotationAsync
            // is still here for when that finishes.

            SafeInit(() => RegisterAllHotkeys());

            // Re-register hotkeys whenever the profile list changes so the slot bindings
            // track the current order. Coalesced: loading raises this once per profile,
            // and re-binding every shortcut each time is work nobody asked for.
            profileService.Profiles.CollectionChanged += (_, _) => QueueHotkeyRebind();
        }
        catch (Exception ex)
        {
            LogException("InitializeAsync", ex);
        }

        // Auto-check for updates (after everything else, non-blocking)
        _ = CheckForUpdateOnStartupAsync();
    }

    /// <summary>Creates the tray icon; on failure still leaves the user a way into the
    /// app — start-minimized plus a failed Shell_NotifyIcon is the invisible zombie.</summary>
    private static void EnsureReachableWithoutTray()
    {
        bool trayReady = false;
        try { trayReady = Services.GetRequiredService<ITrayService>().Initialize(); }
        catch (Exception ex) { LogException("TrayInit", ex); BootLog.WriteError("tray.init", ex); }

        if (trayReady) return;

        BootLog.Write("tray.unavailable", "forcing window visible");
        MainWindow?.DispatcherQueue?.TryEnqueue(async () =>
        {
            try
            {
                MainWindow?.ShowAndActivate();

                if (MainWindow?.Content?.XamlRoot == null) return;
                var dialog = new ContentDialog
                {
                    Title = ResourceHelper.GetString("TrayUnavailableTitle"),
                    Content = ResourceHelper.GetString("TrayUnavailableDesc"),
                    CloseButtonText = ResourceHelper.GetString("OK"),
                    XamlRoot = MainWindow.Content.XamlRoot,
                };
                await dialog.ShowThemedAsync();
            }
            catch (Exception ex) { LogException("TrayUnavailableNotice", ex); }
        });
    }

    // On first launch with an NVIDIA/AMD GPU still on the Windows rotation path, offer to
    // switch — Windows rotation triggers a known cursor-coordinate bug. Shown once,
    // tracked via AppSettings.GpuRotationMethodPromptShown.
    /// <summary>Not called at the moment: the setting it points at is hidden, because
    /// none of the reasons for choosing a driver path survived measurement. Kept whole
    /// so it can come back if a second machine disagrees.</summary>
    private static async Task MaybeOfferDriverRotationAsync()
    {
        try
        {
            var settings = Services.GetRequiredService<ISettingsService>();
            if (settings.Settings.GpuRotationMethodPromptShown) return;
            if (settings.Settings.RotationMethod != Models.RotationMethod.Windows) return;

            // Mark "shown" BEFORE anything risky: if the dialog or the RotationMethod
            // write throws we must still never re-prompt. In 0.20.1 a NRE here left the
            // flag false and trapped users in a crash loop on every launch.
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

            var result = await dialog.ShowThemedAsync();
            if (result == ContentDialogResult.Primary)
            {
                // Write straight to the store: the VM setter raises PropertyChanged into
                // a possibly-cached SettingsPage whose ComboBox SelectedValue binding
                // NREs during the TwoWay readback (Unbox on a null Selector value).
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

            // Two CCD round trips, and this runs a second and a half into startup while
            // the window is still settling. Off the dispatcher.
            var displayService = Services.GetRequiredService<IDisplayService>();
            var compatibility = await Task.Run(() => displayService.CheckCompatibility(profile));
            if (!compatibility.IsFullMatch && compatibility.MissingMonitors.Count > 0 &&
                compatibility.MissingMonitors.Count == profile.Monitors.Count)
            {
                // No profile monitor is present — skip silently; user likely on a different setup.
                return;
            }

            // A full match re-applies a layout the user already confirmed, so prompting
            // every boot is noise that trains them to click through. A partial match is
            // what can go wrong, so only that one gets the countdown and roll-back.
            await profileService.ApplyProfileAsync(
                profileId,
                applyColor: settings.Settings.RestoreColorOnStartup,
                trigger: ApplyTrigger.Startup,
                confirm: compatibility.IsFullMatch ? false : null);
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

        var result = await dialog.ShowThemedAsync();
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

            HotkeyConflicts = [.. hs.Conflicts];
            HotkeyConflictsChanged?.Invoke(null, EventArgs.Empty);
        }
    }

    /// <summary>Bindings Windows refused because another program owns them. Surfaced in
    /// Settings so a shortcut that silently does nothing has a visible explanation.</summary>
    public static IReadOnlyList<HotkeyConflict> HotkeyConflicts { get; private set; } = [];

    public static event EventHandler? HotkeyConflictsChanged;

    private static async void OnHotkeyTriggered(object? sender, HotkeyTriggeredArgs e)
    {
        try
        {
            var profiles = Services.GetRequiredService<IProfileService>();
            switch (e.Action)
            {
                case HotkeyService.HotkeyAction.Profile when e.Payload is { } id:
                    await profiles.ApplyProfileAsync(id, trigger: ApplyTrigger.Hotkey);
                    break;
                case HotkeyService.HotkeyAction.ProfileSlot when int.TryParse(e.Payload, out int slot)
                                                              && slot < profiles.Profiles.Count:
                    await profiles.ApplyProfileAsync(profiles.Profiles[slot].Id, trigger: ApplyTrigger.Hotkey);
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
        await profiles.ApplyProfileAsync(profiles.Profiles[nextIdx].Id, trigger: ApplyTrigger.Hotkey);
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
        try { Services.GetRequiredService<AmdRotationService>().Dispose(); } catch { }

        // Last: an installer polls the presence mutex, and it should go the moment Mo
        // is genuinely on its way out rather than whenever the process finally unwinds.
        ShutdownSignal.Stop();
    }
}
