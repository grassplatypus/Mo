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
        RefreshHotkeyLabels();
        ViewModel.PropertyChanged += (_, _) => RefreshHotkeyLabels();
        Loaded += async (_, _) => await LoadSystemInfoAsync();
    }

    // ── ComboBox initial-selection wiring ──
    //
    // The previous x:Bind SelectedValue/SelectedValuePath approach is brittle in WinUI 3:
    // when SelectedValue is evaluated before SelectedValuePath finishes binding, the
    // initial lookup falls through to null. The combo renders blank AND the TwoWay
    // listener pushes that null back into the source — fine for `string Language`,
    // but a `RotationMethod` enum unbox of null throws NullReferenceException through
    // CastHelpers.Unbox (this killed 0.20.1 first-launches for NVIDIA/AMD users).
    //
    // Wiring the selection manually in Loaded + writing back on SelectionChanged side-
    // steps the entire binding race: by Loaded, both ItemsSource and the items are
    // fully realized, so SelectedItem assignment is safe.

    private bool _syncingLanguageCombo;
    private bool _syncingRotationCombo;

    private void LanguageCombo_Loaded(object sender, RoutedEventArgs e)
    {
        _syncingLanguageCombo = true;
        try
        {
            LanguageCombo.SelectedItem =
                LanguageOptions.FirstOrDefault(o => o.Tag == ViewModel.Language)
                ?? LanguageOptions.FirstOrDefault(o => o.Tag == string.Empty);
        }
        finally { _syncingLanguageCombo = false; }
    }

    private void LanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingLanguageCombo) return;
        if (LanguageCombo.SelectedItem is LanguageOption opt)
            ViewModel.Language = opt.Tag;
    }

    private void RotationMethodCombo_Loaded(object sender, RoutedEventArgs e)
    {
        _syncingRotationCombo = true;
        try
        {
            RotationMethodCombo.SelectedItem =
                RotationOptions.FirstOrDefault(o => o.Method == ViewModel.RotationMethod)
                ?? RotationOptions.FirstOrDefault(o => o.Method == RotationMethod.Windows);
        }
        finally { _syncingRotationCombo = false; }
    }

    private void RotationMethodCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingRotationCombo) return;
        if (RotationMethodCombo.SelectedItem is RotationMethodOption opt)
            ViewModel.RotationMethod = opt.Method;
    }

    private void RefreshHotkeyLabels()
    {
        NextHotkeyText.Text = ViewModel.NextProfileHotkey?.ToString() ?? ResourceHelper.GetString("HotkeyNone");
        PrevHotkeyText.Text = ViewModel.PrevProfileHotkey?.ToString() ?? ResourceHelper.GetString("HotkeyNone");
        var mod = ViewModel.ProfileSlotModifier;
        SlotHotkeyText.Text = mod == null
            ? ResourceHelper.GetString("HotkeyNone")
            : ResourceHelper.GetString("ProfileSlotPattern", FormatModifier(mod));
    }

    private static string FormatModifier(Mo.Models.HotkeyBinding b)
    {
        var parts = new List<string>();
        if (b.Ctrl) parts.Add("Ctrl");
        if (b.Alt) parts.Add("Alt");
        if (b.Shift) parts.Add("Shift");
        if (b.Win) parts.Add("Win");
        return parts.Count == 0 ? "—" : string.Join(" + ", parts);
    }

    private async void NextHotkeyBtn_Click(object sender, RoutedEventArgs e)
    {
        var r = await HotkeyCaptureDialog.ShowAsync(this.XamlRoot, ViewModel.NextProfileHotkey);
        if (r != null) ViewModel.NextProfileHotkey = r.Binding;
    }

    private async void PrevHotkeyBtn_Click(object sender, RoutedEventArgs e)
    {
        var r = await HotkeyCaptureDialog.ShowAsync(this.XamlRoot, ViewModel.PrevProfileHotkey);
        if (r != null) ViewModel.PrevProfileHotkey = r.Binding;
    }

    private async void SlotHotkeyBtn_Click(object sender, RoutedEventArgs e)
    {
        // Capture only modifier — bind it to a dummy key so the dialog can finalize.
        var r = await HotkeyCaptureDialog.ShowAsync(this.XamlRoot, ViewModel.ProfileSlotModifier);
        if (r == null) return;
        if (r.Binding == null) { ViewModel.ProfileSlotModifier = null; return; }
        // Strip the Key — only the modifier portion is meaningful for slot bindings.
        ViewModel.ProfileSlotModifier = new Mo.Models.HotkeyBinding
        {
            Ctrl = r.Binding.Ctrl, Alt = r.Binding.Alt,
            Shift = r.Binding.Shift, Win = r.Binding.Win,
        };
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
