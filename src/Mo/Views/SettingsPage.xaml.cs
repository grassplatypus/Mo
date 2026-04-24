using System.Runtime.InteropServices;
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
    private bool _themeLoaded;

    public SettingsPage()
    {
        ViewModel = App.Services.GetRequiredService<SettingsViewModel>();
        InitializeComponent();
        ApplyLocalization();
        Loaded += SettingsPage_Loaded;
    }

    private void SettingsPage_Loaded(object sender, RoutedEventArgs e)
    {
        var themeIndex = ViewModel.Theme switch
        {
            "Light" => 1,
            "Dark" => 2,
            _ => 0,
        };
        ThemeCombo.SelectedIndex = themeIndex;
        _themeLoaded = true;

        // Rotation method
        var nvService = App.Services.GetRequiredService<NvidiaRotationService>();
        var settingsService = App.Services.GetRequiredService<ISettingsService>();
        RotationMethodCombo.SelectedIndex = (int)settingsService.Settings.RotationMethod;

        // Reset to Windows if selected driver is unavailable
        bool driverAvailable = settingsService.Settings.RotationMethod switch
        {
            RotationMethod.NvidiaDriver => nvService.IsAvailable,
            RotationMethod.AmdDriver => App.Services.GetRequiredService<AmdRotationService>().IsAvailable,
            RotationMethod.IntelDriver => App.Services.GetRequiredService<IntelRotationService>().IsAvailable,
            _ => true,
        };
        if (!driverAvailable)
        {
            settingsService.Settings.RotationMethod = RotationMethod.Windows;
            RotationMethodCombo.SelectedIndex = 0;
        }

        _ = LoadSystemInfoAsync();
    }

    private void RotationMethodCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_themeLoaded) return;
        var settingsService = App.Services.GetRequiredService<ISettingsService>();
        settingsService.Settings.RotationMethod = (RotationMethod)RotationMethodCombo.SelectedIndex;
        _ = settingsService.SaveAsync();
    }

    private async Task LoadSystemInfoAsync()
    {
        var (os, cpu, ram, gpu, monitors, debugReport) = await Task.Run(() =>
        {
            string osInfo = $"{Environment.OSVersion} ({RuntimeInformation.OSArchitecture})";
            string cpuInfo, ramInfo, gpuInfo;
            try
            {
                using var cpuSearcher = new System.Management.ManagementObjectSearcher("SELECT Name FROM Win32_Processor");
                cpuInfo = "";
                foreach (System.Management.ManagementObject obj in cpuSearcher.Get())
                    cpuInfo = obj["Name"]?.ToString()?.Trim() ?? "Unknown";
            }
            catch { cpuInfo = "Unknown"; }

            try
            {
                var gcInfo = GC.GetGCMemoryInfo();
                ramInfo = $"{gcInfo.TotalAvailableMemoryBytes / (1024 * 1024 * 1024.0):F1} GB";
            }
            catch { ramInfo = "Unknown"; }

            try
            {
                using var gpuSearcher = new System.Management.ManagementObjectSearcher("SELECT Name FROM Win32_VideoController");
                var gpus = new List<string>();
                foreach (System.Management.ManagementObject obj in gpuSearcher.Get())
                    gpus.Add(obj["Name"]?.ToString()?.Trim() ?? "Unknown");
                gpuInfo = string.Join(", ", gpus);
            }
            catch { gpuInfo = "Unknown"; }

            var monitorList = SystemInfoHelper.GetMonitorDetails();
            string debug;
            try { debug = SystemInfoHelper.BuildFullReport(); }
            catch { debug = "(Failed to load)"; }

            return (osInfo, cpuInfo, ramInfo, gpuInfo, monitorList, debug);
        });

        SysOsText.Text = $"OS: {os}";
        SysCpuText.Text = $"CPU: {cpu}";
        SysRamText.Text = $"RAM: {ram}";
        SysGpuText.Text = $"GPU: {gpu}";

        MonitorCardsPanel.Children.Clear();
        foreach (var m in monitors)
        {
            var card = new Grid
            {
                Padding = new Thickness(16),
                CornerRadius = new CornerRadius(8),
                Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
                BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                BorderThickness = new Thickness(1),
            };

            var stack = new StackPanel { Spacing = 4 };

            var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            header.Children.Add(new TextBlock
            {
                Text = m.Name,
                Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
            });
            if (m.IsPrimary)
            {
                header.Children.Add(new Border
                {
                    Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AccentFillColorDefaultBrush"],
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(6, 1, 6, 1),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock { Text = "P", FontSize = 10, Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White) },
                });
            }
            stack.Children.Add(header);

            stack.Children.Add(MakeInfoLine($"{m.Manufacturer}  ·  {m.Model}"));
            stack.Children.Add(MakeInfoLine($"{m.Resolution}  ·  {m.RefreshRate:F1} Hz" +
                (m.Rotation != DisplayRotation.None ? $"  ·  {(int)m.Rotation}°" : "")));
            stack.Children.Add(MakeInfoLine($"Position: {m.Position}"));

            card.Children.Add(stack);
            MonitorCardsPanel.Children.Add(card);
        }

        _debugReport = debugReport;
    }

    private string _debugReport = string.Empty;

    private static TextBlock MakeInfoLine(string text) => new()
    {
        Text = text,
        Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
        Opacity = 0.7,
    };

    private void ThemeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_themeLoaded) return;
        var theme = ThemeCombo.SelectedIndex switch
        {
            1 => "Light",
            2 => "Dark",
            _ => "System",
        };
        ViewModel.Theme = theme;
        App.MainWindow.ApplyTheme(theme);
    }

    private async void CheckNowButton_Click(object sender, RoutedEventArgs e)
    {
        CheckNowButton.IsEnabled = false;
        try
        {
            var updateService = App.Services.GetRequiredService<IUpdateService>();
            var (available, version, url) = await updateService.CheckForUpdateAsync();

            UpdateStatusText.Visibility = Visibility.Visible;
            if (available)
            {
                UpdateStatusText.Text = ResourceHelper.GetString("UpdateAvailable", version ?? "");

                // Open download in browser
                if (!string.IsNullOrEmpty(url))
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
            else
            {
                UpdateStatusText.Text = ResourceHelper.GetString("UpToDate");
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
        CopySystemInfoBtn.Content = ResourceHelper.GetString("Copied");
    }

    private void ShowDebugInfo_Click(object sender, RoutedEventArgs e)
    {
        if (SystemInfoBox.Visibility == Visibility.Collapsed)
        {
            SystemInfoBox.Text = _debugReport;
            SystemInfoBox.Visibility = Visibility.Visible;
            ShowDebugBtn.Content = ResourceHelper.GetString("HideDebugInfo");
        }
        else
        {
            SystemInfoBox.Visibility = Visibility.Collapsed;
            ShowDebugBtn.Content = ResourceHelper.GetString("ShowDebugInfo");
        }
    }

    private void ApplyLocalization()
    {
        TitleText.Text = ResourceHelper.GetString("SettingsTitle");
        GeneralText.Text = ResourceHelper.GetString("GeneralSection");
        StartupLabel.Text = ResourceHelper.GetString("LaunchAtStartup");
        StartupDesc.Text = ResourceHelper.GetString("LaunchAtStartupDesc");
        TrayLabel.Text = ResourceHelper.GetString("MinimizeToTray");
        TrayDesc.Text = ResourceHelper.GetString("MinimizeToTrayDesc");
        MinimizedLabel.Text = ResourceHelper.GetString("StartMinimized");
        MinimizedDesc.Text = ResourceHelper.GetString("StartMinimizedDesc");
        AppearanceText.Text = ResourceHelper.GetString("AppearanceSection");
        ThemeLabel.Text = ResourceHelper.GetString("Theme");
        ThemeDesc.Text = ResourceHelper.GetString("ThemeDesc");
        ProfilesText.Text = ResourceHelper.GetString("ProfilesSection");
        AutoSwitchLabel.Text = ResourceHelper.GetString("AutoSwitchSetting");
        AutoSwitchDesc.Text = ResourceHelper.GetString("AutoSwitchSettingDesc");
        HotkeysLabel.Text = ResourceHelper.GetString("HotkeysEnabled");
        HotkeysDesc.Text = ResourceHelper.GetString("HotkeysEnabledDesc");
        RestoreLabel.Text = ResourceHelper.GetString("RestoreOnStartup");
        RestoreDesc.Text = ResourceHelper.GetString("RestoreOnStartupDesc");
        RestoreColorLabel.Text = ResourceHelper.GetString("RestoreColorOnStartup");
        RestoreColorDesc.Text = ResourceHelper.GetString("RestoreColorOnStartupDesc");
        UpdatesText.Text = ResourceHelper.GetString("UpdatesSection");
        UpdateLabel.Text = ResourceHelper.GetString("CheckForUpdates");
        UpdateDesc.Text = ResourceHelper.GetString("CheckForUpdatesDesc");
        CheckNowButton.Content = ResourceHelper.GetString("CheckNow");
        AboutName.Text = ResourceHelper.GetString("AboutName");
        AboutVersion.Text = $"Version {UpdateService.CurrentVersion}";
        AboutDesc.Text = ResourceHelper.GetString("AboutDescription");
        AdvancedText.Text = ResourceHelper.GetString("AdvancedSection");
        RotationMethodLabel.Text = ResourceHelper.GetString("RotationMethod");
        RotationMethodDesc.Text = ResourceHelper.GetString("RotationMethodDesc");
        RotationMethodCombo.Items.Clear();
        var nv = App.Services.GetRequiredService<NvidiaRotationService>();
        var amd = App.Services.GetRequiredService<AmdRotationService>();
        var intel = App.Services.GetRequiredService<IntelRotationService>();
        RotationMethodCombo.Items.Add(ResourceHelper.GetString("RotationWindows"));
        RotationMethodCombo.Items.Add(nv.IsAvailable
            ? ResourceHelper.GetString("RotationNvidia")
            : ResourceHelper.GetString("RotationNvidiaUnavailable"));
        RotationMethodCombo.Items.Add(amd.IsAvailable
            ? ResourceHelper.GetString("RotationAmd")
            : ResourceHelper.GetString("RotationAmdUnavailable"));
        RotationMethodCombo.Items.Add(intel.IsAvailable
            ? ResourceHelper.GetString("RotationIntel")
            : ResourceHelper.GetString("RotationIntelUnavailable"));
        MonitorSectionTitle.Text = ResourceHelper.GetString("MonitorSection");
        DebugInfoDesc.Text = ResourceHelper.GetString("DebugInfoDesc");
        CopySystemInfoBtn.Content = ResourceHelper.GetString("CopyDebugInfo");
        ShowDebugBtn.Content = ResourceHelper.GetString("ShowDebugInfo");

        ThemeCombo.Items.Clear();
        ThemeCombo.Items.Add(ResourceHelper.GetString("ThemeSystem"));
        ThemeCombo.Items.Add(ResourceHelper.GetString("ThemeLight"));
        ThemeCombo.Items.Add(ResourceHelper.GetString("ThemeDark"));
    }
}
