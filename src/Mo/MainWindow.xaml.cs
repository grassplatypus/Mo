using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Mo.Helpers;
using Mo.Services;
using Mo.Views;

namespace Mo;

public sealed partial class MainWindow : Window
{
    private bool _isClosing;

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hWnd);

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon("Assets/AppIcon.ico");

        ApplySizeConstraints();
        RestorePlacement();

        // Windows applies its own default placement on first show, discarding a
        // position set beforehand (size survives). Re-apply once, then stop listening.
        Activated += MainWindow_FirstActivated;

        RootFrame.Navigate(typeof(ShellPage));

        // Intercept close to hide to tray instead
        AppWindow.Closing += AppWindow_Closing;

        // Persist moves and resizes. AppWindow.Changed fires continuously during a
        // drag, so the write is debounced to the end of the gesture.
        AppWindow.Changed += AppWindow_Changed;
    }

    // ── Window placement ──

    private DispatcherTimer? _placementSaveTimer;

    // Blocks the save path until restore finishes, so the OS default position is not
    // written back over the one being restored.
    private bool _placementRestored;

    private void MainWindow_FirstActivated(object sender, WindowActivatedEventArgs args)
    {
        Activated -= MainWindow_FirstActivated;

        // Queued, not inline: the OS default placement lands in the same show sequence
        // that raises Activated, so an inline move would be overwritten.
        DispatcherQueue.TryEnqueue(() =>
        {
            RestorePlacement();
            _placementRestored = true;
            Helpers.BootLog.Write("window.placement.restored",
                $"{AppWindow.Position.X},{AppWindow.Position.Y} {AppWindow.Size.Width}x{AppWindow.Size.Height}");
        });
    }

    /// <summary>Default logical size, scaled to the window's DPI.</summary>
    private const int DefaultWidthDip = 1000;
    private const int DefaultHeightDip = 680;
    private const int MinWidthDip = 640;
    private const int MinHeightDip = 480;

    private double ScaleFactor
    {
        get
        {
            try
            {
                uint dpi = GetDpiForWindow(WindowHelper.GetHwnd(this));
                return dpi == 0 ? 1.0 : dpi / 96.0;
            }
            catch { return 1.0; }
        }
    }

    private void ApplySizeConstraints()
    {
        // Below this the profile grid and tuning sliders stop being usable.
        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = (int)(MinWidthDip * ScaleFactor);
            presenter.PreferredMinimumHeight = (int)(MinHeightDip * ScaleFactor);
        }
    }

    /// <summary>
    /// Restores the last window position and size. The stored rectangle is validated
    /// first — the display it sat on may be gone.
    /// </summary>
    private void RestorePlacement()
    {
        double scale = ScaleFactor;
        var fallback = new Windows.Graphics.SizeInt32(
            (int)(DefaultWidthDip * scale), (int)(DefaultHeightDip * scale));

        Models.WindowPlacement? saved = null;
        try { saved = App.Services.GetRequiredService<ISettingsService>().Settings.WindowPlacement; }
        catch { }

        if (saved == null || saved.Width <= 0 || saved.Height <= 0)
        {
            AppWindow.Resize(fallback);
            return;
        }

        var workAreas = WindowHelper.GetWorkAreas();
        var wanted = new Core.WindowPlacementValidator.Rect(saved.X, saved.Y, saved.Width, saved.Height);
        Helpers.BootLog.Write("window.placement.restore",
            $"want {wanted.X},{wanted.Y} {wanted.Width}x{wanted.Height}; workAreas=" +
            (workAreas.Count == 0 ? "none" : string.Join(" ", workAreas.Select(a => $"{a.X},{a.Y} {a.Width}x{a.Height}"))));

        if (!Core.WindowPlacementValidator.IsReachable(wanted, workAreas))
        {
            // Its monitor is gone; keep the size, let the OS pick the position.
            AppWindow.Resize(new Windows.Graphics.SizeInt32(saved.Width, saved.Height));
            return;
        }

        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(
            wanted.X, wanted.Y, wanted.Width, wanted.Height));

        if (saved.IsMaximized && AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter p)
            p.Maximize();
    }

    private void AppWindow_Changed(
        Microsoft.UI.Windowing.AppWindow sender,
        Microsoft.UI.Windowing.AppWindowChangedEventArgs args)
    {
        if (!args.DidPositionChange && !args.DidSizeChange) return;
        if (_isClosing || !_placementRestored) return;

        _placementSaveTimer ??= CreatePlacementSaveTimer();
        _placementSaveTimer.Stop();
        _placementSaveTimer.Start();
    }

    private DispatcherTimer CreatePlacementSaveTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        timer.Tick += (_, _) => { timer.Stop(); SavePlacement(); };
        return timer;
    }

    /// <summary>Records the current position and size. Ignores the hidden-to-tray state.</summary>
    public void SavePlacement()
    {
        try
        {
            if (!_placementRestored) return;

            // A hidden window reports a stale rect.
            var hwnd = WindowHelper.GetHwnd(this);
            if (hwnd == 0 || !IsWindowVisible(hwnd)) return;

            bool maximized = AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter
            {
                State: Microsoft.UI.Windowing.OverlappedPresenterState.Maximized
            };

            var settings = App.Services.GetRequiredService<ISettingsService>();
            var existing = settings.Settings.WindowPlacement;

            // While maximized the reported rect is the maximized one; keep the previous
            // restore rect.
            var placement = new Models.WindowPlacement
            {
                X = maximized ? existing?.X ?? AppWindow.Position.X : AppWindow.Position.X,
                Y = maximized ? existing?.Y ?? AppWindow.Position.Y : AppWindow.Position.Y,
                Width = maximized ? existing?.Width ?? AppWindow.Size.Width : AppWindow.Size.Width,
                Height = maximized ? existing?.Height ?? AppWindow.Size.Height : AppWindow.Size.Height,
                IsMaximized = maximized,
            };

            settings.Settings.WindowPlacement = placement;
            _ = settings.SaveAsync();
        }
        catch { /* Never let bookkeeping break window interaction. */ }
    }

    private void AppWindow_Closing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        if (_isClosing) return;

        SavePlacement();

        try
        {
            var settings = App.Services.GetRequiredService<ISettingsService>();
            if (!settings.Settings.MinimizeToTrayOnClose)
                return;

            // Only safe if there is a tray icon to get back in through: hiding without
            // one leaves an unreachable process still holding the single-instance key.
            var tray = App.Services.GetRequiredService<ITrayService>();
            if (!tray.EnsureCreated())
            {
                Helpers.BootLog.Write("close.tray-unavailable", "closing instead of hiding");
                return; // Let the window close normally; the app exits cleanly.
            }

            args.Cancel = true;
            HideWindow();
        }
        catch
        {
            // If services aren't available, let it close normally
        }
    }

    public void HideWindow()
    {
        // Capture before hiding: once hidden the reported rect is no longer meaningful.
        SavePlacement();

        // AppWindow.Hide() works before the first Activate() call (Win32 ShowWindow does
        // not), so use it as the primary hide path. ShowWindow remains as a belt-and-
        // suspenders fallback for environments where AppWindow.Hide is a no-op (e.g.
        // some Win10 builds).
        try { AppWindow?.Hide(); } catch { }
        try
        {
            var hwnd = WindowHelper.GetHwnd(this);
            if (hwnd != 0) ShowWindow(hwnd, 0); // SW_HIDE
        }
        catch { }
    }

    public void ShowAndActivate()
    {
        try { AppWindow?.Show(); } catch { }
        var hwnd = WindowHelper.GetHwnd(this);
        ShowWindow(hwnd, 9); // SW_RESTORE
        SetForegroundWindow(hwnd);
        Activate();
    }

    public void ForceClose()
    {
        SavePlacement();
        _isClosing = true;
        Close();
    }

    public void ApplyTheme(string theme)
    {
        if (RootGrid != null)
        {
            ThemeHelper.ApplyTheme(RootGrid, theme);
        }
    }
}
