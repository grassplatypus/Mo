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

    // Static binding sources for the ComboBoxes. Themes carry a localized label rather
    // than the bare enum — ToString() put an English "Dark" in a Korean page. Same shape
    // as LanguageOptions/RotationOptions so all three wire identically.
    public IReadOnlyList<ThemeOption> ThemeOptions { get; } = new[]
    {
        new ThemeOption(AppTheme.System, ResourceHelper.GetString("ThemeSystem")),
        new ThemeOption(AppTheme.Light, ResourceHelper.GetString("ThemeLight")),
        new ThemeOption(AppTheme.Dark, ResourceHelper.GetString("ThemeDark")),
    };

    private readonly ObservableCollection<RotationMethodOption> _rotationOptions = new();

    /// <summary>Filled when the rotation combo is first realized, not at construction.
    /// Naming a driver as available means loading and initialising it, and that ran on the
    /// dispatcher to populate a control inside a collapsed expander.</summary>
    /// <remarks>Typed as the read-only view so the XAML type generator keeps treating the
    /// option record as data; against the concrete collection it wants settable members.</remarks>
    public IReadOnlyList<RotationMethodOption> RotationOptions => _rotationOptions;
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
        InitializeComponent();
        ApplyOneOffStrings();
        RefreshHotkeyLabels();
        // SettingsViewModel is a singleton that outlives any page, so both handlers are
        // detached on Unload rather than left to accumulate.
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        App.HotkeyConflictsChanged += OnHotkeyConflictsChanged;
        Unloaded += (_, _) =>
        {
            ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            App.HotkeyConflictsChanged -= OnHotkeyConflictsChanged;
        };

        Loaded += async (_, _) => await LoadSystemInfoAsync();
        RefreshHotkeyConflicts();
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        => RefreshHotkeyLabels();

    private void OnHotkeyConflictsChanged(object? sender, EventArgs e) =>
        DispatcherQueue.TryEnqueue(RefreshHotkeyConflicts);

    /// <summary>Lists any shortcut Windows refused, so a binding that another process
    /// already owns does not sit visibly in the UI doing nothing.</summary>
    private void RefreshHotkeyConflicts()
    {
        var conflicts = App.HotkeyConflicts;
        if (conflicts.Count == 0)
        {
            HotkeyConflictCard.Visibility = Visibility.Collapsed;
            return;
        }

        HotkeyConflictBar.Title = ResourceHelper.GetString("HotkeyConflictTitle");
        HotkeyConflictBar.Message = ResourceHelper.GetString(
            "HotkeyConflictMessage",
            string.Join(", ", conflicts.Select(c => c.Binding.ToString()).Distinct()));
        HotkeyConflictCard.Visibility = Visibility.Visible;
    }

    // ── ComboBox initial-selection wiring ──
    // Manual on purpose: x:Bind SelectedValue/SelectedValuePath loses a binding race in
    // WinUI 3 and NREs on an enum. See .claude/rules/20-architecture.md.

    private bool _syncingLanguageCombo;
    private bool _syncingRotationCombo;
    private bool _syncingThemeCombo;

    private void ThemeCombo_Loaded(object sender, RoutedEventArgs e)
    {
        _syncingThemeCombo = true;
        try
        {
            ThemeCombo.SelectedItem =
                ThemeOptions.FirstOrDefault(o => o.Theme == ViewModel.Theme)
                ?? ThemeOptions.FirstOrDefault(o => o.Theme == AppTheme.System);
        }
        finally { _syncingThemeCombo = false; }
    }

    private void ThemeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingThemeCombo) return;
        if (ThemeCombo.SelectedItem is ThemeOption opt)
            ViewModel.Theme = opt.Theme;
    }

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

    private async void RotationMethodCombo_Loaded(object sender, RoutedEventArgs e)
    {
        await FillRotationOptionsAsync();

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
    /// <summary>Resolving either driver service constructs it, which loads nvapi64.dll or
    /// atiadlxx.dll and enumerates adapters. Off the dispatcher, and only once the combo
    /// that shows the result actually exists.</summary>
    private async Task FillRotationOptionsAsync()
    {
        if (RotationOptions.Count > 0) return;

        var (nv, amd) = await Task.Run(() =>
        {
            bool n = false, a = false;
            try { n = App.Services.GetRequiredService<NvidiaRotationService>().IsAvailable; } catch { }
            try { a = App.Services.GetRequiredService<AmdRotationService>().IsAvailable; } catch { }
            return (n, a);
        });

        // No Intel entry: IGCL has no rotation API at all, so Windows is the only
        // option there. See .claude/rules/30-display-apis.md.
        _rotationOptions.Add(new(RotationMethod.Windows, ResourceHelper.GetString("RotationWindows")));
        _rotationOptions.Add(new(RotationMethod.NvidiaDriver,
            ResourceHelper.GetString(nv ? "RotationNvidia" : "RotationNvidiaUnavailable")));
        _rotationOptions.Add(new(RotationMethod.AmdDriver,
            ResourceHelper.GetString(amd ? "RotationAmd" : "RotationAmdUnavailable")));
    }

    private void ApplyOneOffStrings()
    {
        TitleText.Text = ResourceHelper.GetString("SettingsTitle");
        // Hidden if it never arrives, so a failure leaves no blank square beside the name.
        // 44px drawn, which reaches 132 at 300% scaling.
        var icon = AppImages.AppIcon(decodePixelWidth: 132);
        icon.ImageFailed += (_, _) => AboutIcon.Visibility = Visibility.Collapsed;
        AboutIcon.Source = icon;

        AboutName.Text = ResourceHelper.GetString("AboutName");
        AboutVersion.Text = ResourceHelper.GetString("AboutVersionFormat", UpdateService.CurrentVersion);
        AboutDesc.Text = ResourceHelper.GetString("AboutDescription");

        SysSectionTitle.Text = ResourceHelper.GetString("SysSectionTitle");
        SysMonitorsTitle.Text = ResourceHelper.GetString("SysMonitorsTitle");
        SysDiagnosticsTitle.Text = ResourceHelper.GetString("SysDiagnosticsTitle");
        SysOsLabel.Text = ResourceHelper.GetString("SysLabelOs");
        SysCpuLabel.Text = ResourceHelper.GetString("SysLabelCpu");
        SysRamLabel.Text = ResourceHelper.GetString("SysLabelRam");
        SysGpuLabel.Text = ResourceHelper.GetString("SysLabelGpu");
    }

    private async Task LoadSystemInfoAsync()
    {
        var info = await App.Services.GetRequiredService<ISystemInfoService>().LoadAsync();

        // Values only; the labels are in the grid's first column and are localized.
        SysOsText.Text = info.Os;
        SysCpuText.Text = info.Cpu;
        SysRamText.Text = info.Ram;
        SysGpuText.Text = info.Gpu;

        Monitors.Clear();
        foreach (var m in info.Monitors) Monitors.Add(m);
    }

    /// <summary>Built on demand and kept, so the second press is instant.</summary>
    private async Task<string> DebugReportAsync()
    {
        if (_debugReport.Length == 0)
            _debugReport = await App.Services.GetRequiredService<ISystemInfoService>().GetDebugReportAsync();

        return _debugReport;
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
                if (await dialog.ShowThemedAsync() == ContentDialogResult.Primary)
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

    private async void CopySystemInfo_Click(object sender, RoutedEventArgs e)
    {
        CopySystemInfoBtn.IsEnabled = false;
        try
        {
            var dp = new DataPackage();
            dp.SetText(await DebugReportAsync());
            Clipboard.SetContent(dp);
            if (CopySystemInfoBtn.Content is TextBlock t) t.Text = ResourceHelper.GetString("Copied");
        }
        finally { CopySystemInfoBtn.IsEnabled = true; }
    }

    private async void ShowDebugInfo_Click(object sender, RoutedEventArgs e)
    {
        bool show = SystemInfoBorder.Visibility == Visibility.Collapsed;
        if (show)
        {
            ShowDebugBtn.IsEnabled = false;
            try { SystemInfoBox.Text = await DebugReportAsync(); }
            finally { ShowDebugBtn.IsEnabled = true; }
        }

        SystemInfoBorder.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (ShowDebugBtn.Content is TextBlock t)
            t.Text = ResourceHelper.GetString(show ? "HideDebugInfo" : "ShowDebugInfo");
    }

    private async void OpenLogFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = Path.Combine(AppDataCleanup.UnpackagedDataDirectory(), "logs");
            Directory.CreateDirectory(dir);
            await Windows.System.Launcher.LaunchFolderPathAsync(dir);
        }
        catch { }
    }

    /// <summary>Deletes Mo's user data and auto-start entry, then closes the app — the
    /// state it would be in after a proper uninstall.</summary>
    private async void RemoveData_Click(object sender, RoutedEventArgs e)
    {
        var items = AppDataCleanup.Preview();

        var body = items.Count == 0
            ? ResourceHelper.GetString("RemoveDataNothing")
            : ResourceHelper.GetString("RemoveDataConfirm") + "\n\n" + string.Join("\n", items);

        var confirm = new ContentDialog
        {
            Title = ResourceHelper.GetString("RemoveDataTitle"),
            Content = new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = ResourceHelper.GetString("RemoveDataButton.Text"),
            CloseButtonText = ResourceHelper.GetString("Cancel"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };

        if (items.Count == 0)
        {
            confirm.PrimaryButtonText = null;
            confirm.CloseButtonText = ResourceHelper.GetString("OK");
            await confirm.ShowThemedAsync();
            return;
        }

        if (await confirm.ShowThemedAsync() != ContentDialogResult.Primary) return;

        var result = AppDataCleanup.Run();

        // Exit rather than carry on: the settings and profiles this session holds in
        // memory would otherwise be written straight back out on the next save.
        var done = new ContentDialog
        {
            Title = ResourceHelper.GetString("RemoveDataTitle"),
            Content = new TextBlock
            {
                Text = result.Failed.Count == 0
                    ? ResourceHelper.GetString("RemoveDataDone")
                    : ResourceHelper.GetString("RemoveDataPartial") + "\n\n" + string.Join("\n", result.Failed),
                TextWrapping = TextWrapping.Wrap,
            },
            CloseButtonText = ResourceHelper.GetString("ExitApp"),
            XamlRoot = XamlRoot,
        };
        await done.ShowThemedAsync();

        App.MainWindow?.ForceClose();
    }
}

public sealed record RotationMethodOption(RotationMethod Method, string DisplayName);
public sealed record LanguageOption(string Tag, string Display);
public sealed record ThemeOption(AppTheme Theme, string Display);
