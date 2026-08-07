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

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon("Assets/AppIcon.ico");
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1000, 680));

        RootFrame.Navigate(typeof(ShellPage));

        // Intercept close to hide to tray instead
        AppWindow.Closing += AppWindow_Closing;
    }

    private void AppWindow_Closing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        if (_isClosing) return;

        try
        {
            var settings = App.Services.GetRequiredService<ISettingsService>();
            if (!settings.Settings.MinimizeToTrayOnClose)
                return;

            // Hiding is only safe if there is a tray icon to get back in through.
            // Shell_NotifyIcon can fail (Explorer restarting, notification area full,
            // some elevated-session combos), and hiding anyway leaves a live process
            // with no window and no icon — unreachable, and still holding the
            // single-instance key so every later launch also appears to do nothing.
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
