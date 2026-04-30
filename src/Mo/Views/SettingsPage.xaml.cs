using System.Collections.ObjectModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Mo.Helpers;
using Mo.Models;
using Mo.Services;
using Mo.ViewModels;
using Windows.ApplicationModel.DataTransfer;

namespace Mo.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsViewModel ViewModel { get; }

    // Static binding sources for the ComboBoxes.
    public IReadOnlyList<AppTheme> ThemeOptions { get; } = new[]
    {
        AppTheme.System, AppTheme.Light, AppTheme.Dark,
    };

    public IReadOnlyList<RotationMethodOption> RotationOptions { get; }
    public IReadOnlyList<LanguageOption> LanguageOptions { get; } = new[]
    {
        new LanguageOption(string.Empty, ResourceHelper.GetString("LanguageAuto")),
        new LanguageOption("ko-KR", "한국어"),
        new LanguageOption("en-US", "English"),
    };
    public ObservableCollection<MonitorDisplayInfo> Monitors { get; } = new();

    private string _debugReport = string.Empty;

    public SettingsPage()
    {
        ViewModel = App.Services.GetRequiredService<SettingsViewModel>();
        RotationOptions = BuildRotationOptions();
        InitializeComponent();
        ApplyOneOffStrings();
        Loaded += async (_, _) => await LoadSystemInfoAsync();
    }

    // Keeps the user from selecting a driver backend that isn't actually present.
    // The XAML ComboBox binds SelectedValuePath="Method" so unavailable rows still
    // appear (greyed-out via DisplayName), but selecting one falls back at apply time.
    private static IReadOnlyList<RotationMethodOption> BuildRotationOptions()
    {
        bool nv = App.Services.GetRequiredService<NvidiaRotationService>().IsAvailable;
        bool amd = App.Services.GetRequiredService<AmdRotationService>().IsAvailable;
        bool intel = App.Services.GetRequiredService<IntelRotationService>().IsAvailable;
        return new[]
        {
            new RotationMethodOption(RotationMethod.Windows,      ResourceHelper.GetString("RotationWindows")),
            new RotationMethodOption(RotationMethod.NvidiaDriver, ResourceHelper.GetString(nv ? "RotationNvidia" : "RotationNvidiaUnavailable")),
            new RotationMethodOption(RotationMethod.AmdDriver,    ResourceHelper.GetString(amd ? "RotationAmd" : "RotationAmdUnavailable")),
            new RotationMethodOption(RotationMethod.IntelDriver,  ResourceHelper.GetString(intel ? "RotationIntel" : "RotationIntelUnavailable")),
        };
    }

    private void ApplyOneOffStrings()
    {
        TitleText.Text = ResourceHelper.GetString("SettingsTitle");
        AboutName.Text = ResourceHelper.GetString("AboutName");
        AboutVersion.Text = $"Version {UpdateService.CurrentVersion}";
        AboutDesc.Text = ResourceHelper.GetString("AboutDescription");
    }

    private async Task LoadSystemInfoAsync()
    {
        var info = await App.Services.GetRequiredService<ISystemInfoService>().LoadAsync();

        SysOsText.Text = $"OS: {info.Os}";
        SysCpuText.Text = $"CPU: {info.Cpu}";
        SysRamText.Text = $"RAM: {info.Ram}";
        SysGpuText.Text = $"GPU: {info.Gpu}";

        Monitors.Clear();
        foreach (var m in info.Monitors) Monitors.Add(m);

        _debugReport = info.DebugReport;
    }

    private async void CheckNowButton_Click(object sender, RoutedEventArgs e)
    {
        CheckNowButton.IsEnabled = false;
        try
        {
            var (available, version, url) = await App.Services.GetRequiredService<IUpdateService>().CheckForUpdateAsync();

            UpdateStatusText.Visibility = Visibility.Visible;
            UpdateStatusText.Text = available
                ? ResourceHelper.GetString("UpdateAvailable", version ?? "")
                : ResourceHelper.GetString("UpToDate");

            if (available && !string.IsNullOrEmpty(url))
            {
                var dialog = new ContentDialog
                {
                    Title = ResourceHelper.GetString("UpdateAvailableTitle"),
                    Content = ResourceHelper.GetString("UpdateAvailable", version ?? ""),
                    PrimaryButtonText = ResourceHelper.GetString("DownloadUpdate"),
                    CloseButtonText = ResourceHelper.GetString("Later"),
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = this.XamlRoot,
                };
                if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                    await Windows.System.Launcher.LaunchUriAsync(new Uri(url));
            }
        }
        catch
        {
            UpdateStatusText.Visibility = Visibility.Collapsed;
        }
        finally
        {
            CheckNowButton.IsEnabled = true;
        }
    }

    private void CopySystemInfo_Click(object sender, RoutedEventArgs e)
    {
        var dp = new DataPackage();
        dp.SetText(_debugReport);
        Clipboard.SetContent(dp);
        if (CopySystemInfoBtn.Content is TextBlock t) t.Text = ResourceHelper.GetString("Copied");
    }

    private void ShowDebugInfo_Click(object sender, RoutedEventArgs e)
    {
        bool show = SystemInfoBox.Visibility == Visibility.Collapsed;
        SystemInfoBox.Text = show ? _debugReport : SystemInfoBox.Text;
        SystemInfoBox.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (ShowDebugBtn.Content is TextBlock t)
            t.Text = ResourceHelper.GetString(show ? "HideDebugInfo" : "ShowDebugInfo");
    }
}

public sealed record RotationMethodOption(RotationMethod Method, string DisplayName);
public sealed record LanguageOption(string Tag, string Display);
