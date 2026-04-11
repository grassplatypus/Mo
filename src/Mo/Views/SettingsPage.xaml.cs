using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Mo.Helpers;
using Mo.Services;
using Mo.ViewModels;

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
    }

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
            var (available, version, _) = await updateService.CheckForUpdateAsync();

            UpdateStatusText.Visibility = Visibility.Visible;
            if (available)
            {
                UpdateStatusText.Text = ResourceHelper.GetString("UpdateAvailable", version ?? "");
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
        ThemeLabel.Text = ResourceHelper.GetString("Theme");
        ThemeDesc.Text = ResourceHelper.GetString("ThemeDesc");
        AutoSwitchLabel.Text = ResourceHelper.GetString("AutoSwitchSetting");
        AutoSwitchDesc.Text = ResourceHelper.GetString("AutoSwitchSettingDesc");
        UpdateLabel.Text = ResourceHelper.GetString("CheckForUpdates");
        UpdateDesc.Text = ResourceHelper.GetString("CheckForUpdatesDesc");
        CheckNowButton.Content = ResourceHelper.GetString("CheckNow");
        AboutText.Text = ResourceHelper.GetString("AboutSection");
        AboutName.Text = ResourceHelper.GetString("AboutName");
        AboutVersion.Text = $"Version {UpdateService.CurrentVersion}";
        AboutDesc.Text = ResourceHelper.GetString("AboutDescription");

        ThemeCombo.Items.Clear();
        ThemeCombo.Items.Add(ResourceHelper.GetString("ThemeSystem"));
        ThemeCombo.Items.Add(ResourceHelper.GetString("ThemeLight"));
        ThemeCombo.Items.Add(ResourceHelper.GetString("ThemeDark"));
    }
}
