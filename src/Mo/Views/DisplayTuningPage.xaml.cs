using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Mo.Core.DisplayConfiguration;
using Mo.Helpers;
using Mo.Interop.Monitor;
using Mo.Models;
using Mo.Services;

namespace Mo.Views;

public sealed partial class DisplayTuningPage : Page
{
    private readonly IDisplayService _displayService;
    private readonly IMonitorColorService _colorService;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _applyTimer;

    private List<MonitorInfo> _monitors = [];
    private List<MonitorColorCapabilities> _caps = [];
    private int _selected = -1;
    private bool _loading;

    public DisplayTuningPage()
    {
        _displayService = App.Services.GetRequiredService<IDisplayService>();
        _colorService = App.Services.GetRequiredService<IMonitorColorService>();
        InitializeComponent();
        ApplyLocalization();

        _applyTimer = DispatcherQueue.CreateTimer();
        _applyTimer.Interval = TimeSpan.FromMilliseconds(60);
        _applyTimer.IsRepeating = false;
        _applyTimer.Tick += (_, _) => ApplyPendingColorSettings();

        _ = LoadMonitorsAsync();
    }

    private async Task LoadMonitorsAsync()
    {
        _loading = true;
        List<MonitorInfo> monitors;
        List<MonitorColorCapabilities> caps;
        try { monitors = _displayService.GetCurrentConfiguration(); }
        catch { monitors = []; }

        try { caps = await Task.Run(_colorService.DetectCapabilities); }
        catch { caps = []; }

        _monitors = monitors;
        _caps = caps;

        MonitorList.Items.Clear();
        for (int i = 0; i < monitors.Count; i++)
        {
            MonitorList.Items.Add(BuildMonitorRow(monitors[i], i));
        }

        if (monitors.Count > 0)
            MonitorList.SelectedIndex = 0;
        else
            DetailPanel.Visibility = Visibility.Collapsed;

        _loading = false;
    }

    private static ListViewItem BuildMonitorRow(MonitorInfo m, int idx)
    {
        var brand = EdidManufacturer.GetBrandName(m.EdidManufacturerId);
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(new TextBlock
        {
            Text = string.IsNullOrEmpty(m.FriendlyName) ? $"Display {idx + 1}" : m.FriendlyName,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"{brand ?? "Unknown"}  ·  {m.ResolutionText}",
            Opacity = 0.55,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
        });
        return new ListViewItem { Content = panel };
    }

    private void MonitorList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selected = MonitorList.SelectedIndex;
        if (_selected < 0 || _selected >= _monitors.Count)
        {
            DetailPanel.Visibility = Visibility.Collapsed;
            return;
        }

        var monitor = _monitors[_selected];
        DetailPanel.Visibility = Visibility.Visible;

        var brand = EdidManufacturer.GetBrandName(monitor.EdidManufacturerId);
        HeaderName.Text = string.IsNullOrEmpty(monitor.FriendlyName) ? $"Display {_selected + 1}" : monitor.FriendlyName;
        HeaderBrand.Text = brand ?? EdidManufacturer.GetPnpId(monitor.EdidManufacturerId);
        HeaderDetails.Text = $"{monitor.ResolutionText}  ·  {monitor.RefreshRateHz:F0} Hz";

        LoadDdcSection(monitor);
        LoadPresetSection();
        LoadHdrSection(monitor);
    }

    private void LoadDdcSection(MonitorInfo monitor)
    {
        var caps = _selected < _caps.Count ? _caps[_selected] : null;
        bool any = caps?.SupportsAny == true;
        DdcEmptyText.Visibility = any ? Visibility.Collapsed : Visibility.Visible;

        bool hasBri = caps?.SupportsBrightness == true || caps?.SupportsWmiBrightness == true;
        bool hasCon = caps?.SupportsContrast == true;
        bool hasR = caps?.SupportsRedGain == true;
        bool hasG = caps?.SupportsGreenGain == true;
        bool hasB = caps?.SupportsBlueGain == true;

        BrightnessSlider.IsEnabled = hasBri;
        ContrastSlider.IsEnabled = hasCon;
        RedSlider.IsEnabled = hasR;
        GreenSlider.IsEnabled = hasG;
        BlueSlider.IsEnabled = hasB;

        // Capture current values if possible.
        var captured = TryCapture(_selected);
        _loading = true;
        BrightnessSlider.Value = captured?.Brightness ?? 50;
        ContrastSlider.Value = captured?.Contrast ?? 50;
        RedSlider.Value = captured?.RedGain ?? 50;
        GreenSlider.Value = captured?.GreenGain ?? 50;
        BlueSlider.Value = captured?.BlueGain ?? 50;
        _loading = false;

        UpdateValueLabels();
    }

    private MonitorColorSettings? TryCapture(int index)
    {
        try
        {
            var all = _colorService.CaptureAllMonitors();
            return index >= 0 && index < all.Count ? all[index] : null;
        }
        catch { return null; }
    }

    private void LoadPresetSection()
    {
        if (_selected < 0)
        {
            PresetCard.Visibility = Visibility.Collapsed;
            return;
        }

        var probe = _colorService.GetVcpFeature(_selected, MonitorConfigApi.VCP_SELECT_COLOR_PRESET);
        if (probe == null)
        {
            PresetCard.Visibility = Visibility.Collapsed;
            return;
        }

        PresetCard.Visibility = Visibility.Visible;

        _loading = true;
        PresetCombo.Items.Clear();
        // These are the common VCP 14 values; monitors that don't support a given
        // value simply reject the SetVCPFeature call.
        PresetCombo.Items.Add(BuildPresetItem(ResourceHelper.GetString("PresetNative"), 0x00));
        PresetCombo.Items.Add(BuildPresetItem("sRGB", 0x01));
        PresetCombo.Items.Add(BuildPresetItem("5000K", 0x04));
        PresetCombo.Items.Add(BuildPresetItem("6500K", 0x05));
        PresetCombo.Items.Add(BuildPresetItem("7500K", 0x06));
        PresetCombo.Items.Add(BuildPresetItem("9300K", 0x08));
        PresetCombo.Items.Add(BuildPresetItem(ResourceHelper.GetString("PresetUser"), 0x0B));

        // Select the item that matches current value (if any).
        int matchIdx = 0;
        for (int i = 0; i < PresetCombo.Items.Count; i++)
        {
            if (PresetCombo.Items[i] is ComboBoxItem item && item.Tag is uint code && code == probe.Value.current)
            { matchIdx = i; break; }
        }
        PresetCombo.SelectedIndex = matchIdx;
        _loading = false;
    }

    private static ComboBoxItem BuildPresetItem(string text, uint code)
        => new() { Content = text, Tag = code };

    private void LoadHdrSection(MonitorInfo monitor)
    {
        HdrState state;
        try { state = _displayService.GetHdrState(monitor); }
        catch { state = new HdrState(false, false, false); }

        if (!state.Supported)
        {
            HdrCard.Visibility = Visibility.Collapsed;
            return;
        }

        HdrCard.Visibility = Visibility.Visible;
        _loading = true;
        HdrToggle.IsOn = state.Enabled;
        HdrToggle.IsEnabled = !state.ForceDisabled;
        _loading = false;
    }

    // ── Change handlers ──

    private void Slider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_loading || _selected < 0) return;
        UpdateValueLabels();
        _applyTimer.Stop();
        _applyTimer.Start();
    }

    private void UpdateValueLabels()
    {
        BrightnessValue.Text = $"{(int)BrightnessSlider.Value}";
        ContrastValue.Text = $"{(int)ContrastSlider.Value}";
        RedValue.Text = $"{(int)RedSlider.Value}";
        GreenValue.Text = $"{(int)GreenSlider.Value}";
        BlueValue.Text = $"{(int)BlueSlider.Value}";
    }

    private void ApplyPendingColorSettings()
    {
        if (_selected < 0) return;
        var caps = _selected < _caps.Count ? _caps[_selected] : null;
        if (caps == null) return;

        var settings = new MonitorColorSettings();
        if (caps.SupportsBrightness || caps.SupportsWmiBrightness) settings.Brightness = (int)BrightnessSlider.Value;
        if (caps.SupportsContrast) settings.Contrast = (int)ContrastSlider.Value;
        if (caps.SupportsRedGain) settings.RedGain = (int)RedSlider.Value;
        if (caps.SupportsGreenGain) settings.GreenGain = (int)GreenSlider.Value;
        if (caps.SupportsBlueGain) settings.BlueGain = (int)BlueSlider.Value;

        if (!settings.HasValues) return;
        try { _colorService.ApplyToMonitor(_selected, settings); }
        catch { }
    }

    private void PresetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _selected < 0) return;
        if (PresetCombo.SelectedItem is ComboBoxItem item && item.Tag is uint code)
        {
            _colorService.SetVcpFeature(_selected, MonitorConfigApi.VCP_SELECT_COLOR_PRESET, code);
        }
    }

    private void HdrToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading || _selected < 0 || _selected >= _monitors.Count) return;
        _displayService.SetHdrEnabled(_monitors[_selected], HdrToggle.IsOn);
    }

    private void ResetBtn_Click(object sender, RoutedEventArgs e)
    {
        _loading = true;
        BrightnessSlider.Value = 50;
        ContrastSlider.Value = 50;
        RedSlider.Value = 50;
        GreenSlider.Value = 50;
        BlueSlider.Value = 50;
        _loading = false;
        UpdateValueLabels();
        ApplyPendingColorSettings();
    }

    private void RefreshBtn_Click(object sender, RoutedEventArgs e)
    {
        _ = LoadMonitorsAsync();
    }

    private void ApplyLocalization()
    {
        TitleText.Text = ResourceHelper.GetString("DisplayTuningTitle");
        SubtitleText.Text = ResourceHelper.GetString("DisplayTuningSubtitle");
        RefreshLabel.Text = ResourceHelper.GetString("Refresh");
        DdcTitle.Text = ResourceHelper.GetString("DdcCiSection");
        BrightnessLabel.Text = ResourceHelper.GetString("Brightness");
        ContrastLabel.Text = ResourceHelper.GetString("Contrast");
        ResetBtn.Content = ResourceHelper.GetString("ResetDefaults");
        DdcEmptyText.Text = ResourceHelper.GetString("DdcCiUnsupported");
        PresetLabel.Text = ResourceHelper.GetString("ColorPreset");
        PresetDesc.Text = ResourceHelper.GetString("ColorPresetDesc");
        HdrLabel.Text = ResourceHelper.GetString("Hdr");
        HdrDesc.Text = ResourceHelper.GetString("HdrDesc");
    }
}
