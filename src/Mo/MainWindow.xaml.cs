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
            if (settings.Settings.MinimizeToTrayOnClose)
            {
                args.Cancel = true;
                HideWindow();
            }
        }
        catch
        {
            // If services aren't available, let it close normally
        }
    }

    public void HideWindow()
    {
        var hwnd = WindowHelper.GetHwnd(this);
        ShowWindow(hwnd, 0); // SW_HIDE
    }

    public void ShowAndActivate()
    {
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
