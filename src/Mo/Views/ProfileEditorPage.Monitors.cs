using System.Collections.ObjectModel;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Mo.Core.DisplayConfiguration;
using Mo.Helpers;
using Mo.Models;
using Mo.Services;

namespace Mo.Views;

// Monitor selection and layout editing: which monitor is selected, its mode and
// rotation, adding and removing monitors, and the arrangement helpers.
public sealed partial class ProfileEditorPage
{
    private void LayoutCanvas_MonitorSelected(object? sender, MonitorInfo? monitor)
    {
        _selectedMonitor = monitor;
        SetPrimaryBtn.IsEnabled = monitor != null && monitor.IsEnabled && !monitor.IsPrimary;
        if (monitor == null)
        {
            _selectedMonitorIndex = -1;
            MonitorDetailsPanel.Visibility = Visibility.Collapsed;
            ColorSettingsPanel.Visibility = Visibility.Collapsed;
            return;
        }

        _selectedMonitorIndex = _profile?.Monitors.IndexOf(monitor) ?? -1;

        MonitorDetailsPanel.Visibility = Visibility.Visible;

        _loading = true;
        MonitorEnabledToggle.IsOn = monitor.IsEnabled;
        MonitorEnabledToggle.Header = monitor.FriendlyName;
        _loading = false;

        DetailResolution.Text = monitor.ResolutionText;
        DetailRefreshRate.Text = $"{monitor.RefreshRateHz:F1} Hz";

        _loading = true;
        RotationCombo.SelectedIndex = monitor.Rotation switch
        {
            DisplayRotation.Rotate90 => 1,
            DisplayRotation.Rotate180 => 2,
            DisplayRotation.Rotate270 => 3,
            _ => 0,
        };
        _loading = false;

        RotationWarningBar.IsOpen = monitor.Rotation != DisplayRotation.None;
        RotationWarningBar.Message = ResourceHelper.GetString("RotationWarning");

        // Color settings — enable/disable based on capabilities
        var caps = _selectedMonitorIndex >= 0 && _selectedMonitorIndex < _colorCaps.Count
            ? _colorCaps[_selectedMonitorIndex] : null;
        var hasBri = caps?.SupportsBrightness == true || caps?.SupportsWmiBrightness == true;
        var hasCon = caps?.SupportsContrast == true;
        var hasR = caps?.SupportsRedGain == true;
        var hasG = caps?.SupportsGreenGain == true;
        var hasB = caps?.SupportsBlueGain == true;

        bool anyColorSupport = hasBri || hasCon || hasR || hasG || hasB;
        ColorSettingsPanel.Visibility = anyColorSupport ? Visibility.Visible : Visibility.Collapsed;

        if (!anyColorSupport) return;

        _loading = true;
        var cs = monitor.ColorSettings;

        BrightnessSlider.IsEnabled = hasBri;
        BrightnessSlider.Value = cs?.Brightness ?? 50;

        ContrastSlider.IsEnabled = hasCon;
        ContrastSlider.Value = cs?.Contrast ?? 50;

        RedSlider.IsEnabled = hasR;
        RedSlider.Value = cs?.RedGain ?? 50;

        GreenSlider.IsEnabled = hasG;
        GreenSlider.Value = cs?.GreenGain ?? 50;

        BlueSlider.IsEnabled = hasB;
        BlueSlider.Value = cs?.BlueGain ?? 50;

        UpdateColorLabels();

        // Show source hint (DDC/CI vs WMI)
        if (caps?.SupportsWmiBrightness == true && caps?.SupportsBrightness != true)
            BrightnessLabel.Text = ResourceHelper.GetString("Brightness") + " (WMI)";
        else
            BrightnessLabel.Text = ResourceHelper.GetString("Brightness");

        _loading = false;
    }

    private void RotationCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _selectedMonitor == null) return;
        var previous = _selectedMonitor.Rotation;
        var next = RotationCombo.SelectedIndex switch
        {
            1 => DisplayRotation.Rotate90,
            2 => DisplayRotation.Rotate180,
            3 => DisplayRotation.Rotate270,
            _ => DisplayRotation.None,
        };

        bool wasPortrait = previous is DisplayRotation.Rotate90 or DisplayRotation.Rotate270;
        bool willBePortrait = next is DisplayRotation.Rotate90 or DisplayRotation.Rotate270;
        if (wasPortrait != willBePortrait)
        {
            (_selectedMonitor.Width, _selectedMonitor.Height) = (_selectedMonitor.Height, _selectedMonitor.Width);
        }

        _selectedMonitor.Rotation = next;
        RotationWarningBar.IsOpen = next != DisplayRotation.None;
        LayoutCanvas.SetMonitors(_profile!.Monitors);
    }

    private void RemoveMonitor_Click(object sender, RoutedEventArgs e)
    {
        if (_profile == null || _selectedMonitor == null) return;
        _profile.Monitors.Remove(_selectedMonitor);
        _selectedMonitor = null;
        _selectedMonitorIndex = -1;
        MonitorDetailsPanel.Visibility = Visibility.Collapsed;
        ColorSettingsPanel.Visibility = Visibility.Collapsed;
        LayoutCanvas.SetMonitors(_profile.Monitors);
        RefreshAvailableMonitors();
    }

    private void MonitorEnabledToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading || _selectedMonitor == null) return;
        _selectedMonitor.IsEnabled = MonitorEnabledToggle.IsOn;
        LayoutCanvas.SetMonitors(_profile!.Monitors);
    }

    private void UnmatchedCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _profile == null) return;
        _profile.UnmatchedAction = (UnmatchedMonitorAction)UnmatchedCombo.SelectedIndex;
    }

    private void RefreshAvailableMonitors()
    {
        if (_profile == null)
        {
            AvailableMonitors.Clear();
            return;
        }

        // Pull every connected monitor (active + inactive) so a display that's
        // currently off still shows up in the inventory and can be dragged into
        // the layout. Falls back to active-only enumeration on failure.
        List<MonitorInfo> connected;
        try { connected = _displayService.GetAllConnectedMonitors(); }
        catch
        {
            try { connected = _displayService.GetCurrentConfiguration(); }
            catch { connected = []; }
        }

        AvailableMonitors.Clear();
        foreach (var monitor in connected)
        {
            bool inProfile = _profile.Monitors.Any(p => MatchesProfileMonitor(p, monitor));
            AvailableMonitors.Add(new AvailableMonitorItem(monitor, inProfile));
        }
    }

    private static bool MatchesProfileMonitor(MonitorInfo profile, MonitorInfo current)
    {
        if (!string.IsNullOrEmpty(profile.DevicePath) && profile.DevicePath == current.DevicePath) return true;
        if (profile.EdidManufacturerId != 0 &&
            profile.EdidManufacturerId == current.EdidManufacturerId &&
            profile.EdidProductCodeId == current.EdidProductCodeId &&
            profile.ConnectorInstance == current.ConnectorInstance) return true;
        return false;
    }

    // Double-clicking a row in the inventory adds (or focuses) the monitor — same as
    // clicking the small + button. Mirrors classic shell affordances and is more
    // discoverable than the single icon button.
    private void AvailableRow_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is AvailableMonitorItem item)
            AddOrFocusMonitorCore(item);
    }

    private void AddOrFocusMonitor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not AvailableMonitorItem item) return;
        AddOrFocusMonitorCore(item);
    }

    private void AddOrFocusMonitorCore(AvailableMonitorItem item)
    {
        if (_profile == null) return;

        if (item.InProfile)
        {
            // Already in the profile — focus its tile rather than duplicating.
            var existing = _profile.Monitors.FirstOrDefault(p => MatchesProfileMonitor(p, item.Monitor));
            if (existing != null)
            {
                _selectedMonitor = existing;
                _selectedMonitorIndex = _profile.Monitors.IndexOf(existing);
                LayoutCanvas_MonitorSelected(this, existing);
            }
            return;
        }

        var source = item.Monitor;
        // Place flush against the current layout's right edge so SnapCalculator's
        // adjacency enforcement leaves it where the user expects.
        int placeX = _profile.Monitors.Count > 0
            ? _profile.Monitors.Max(m => m.PositionX + m.Width)
            : 0;

        _profile.Monitors.Add(new MonitorInfo
        {
            DevicePath = source.DevicePath,
            FriendlyName = source.FriendlyName,
            EdidManufacturerId = source.EdidManufacturerId,
            EdidProductCodeId = source.EdidProductCodeId,
            ConnectorInstance = source.ConnectorInstance,
            PositionX = placeX,
            PositionY = 0,
            Width = source.Width,
            Height = source.Height,
            Rotation = source.Rotation,
            RefreshRateNumerator = source.RefreshRateNumerator,
            RefreshRateDenominator = source.RefreshRateDenominator,
            DpiScale = source.DpiScale,
            IsPrimary = false,
            IsEnabled = true,
            HdrEnabled = source.HdrEnabled,
            AdapterId = source.AdapterId,
            SourceId = source.SourceId,
            TargetId = source.TargetId,
        });
        LayoutCanvas.SetMonitors(_profile.Monitors);
        RefreshAvailableMonitors();
    }

    private void ImportCurrent_Click(object sender, RoutedEventArgs e)
    {
        if (_profile == null) return;
        List<MonitorInfo> current;
        try { current = _displayService.GetCurrentConfiguration(); }
        catch { return; }

        // Preserve any color settings by matching via identity before replace.
        var oldByKey = _profile.Monitors.ToDictionary(
            m => m.DevicePath ?? string.Empty, m => m, StringComparer.OrdinalIgnoreCase);

        foreach (var m in current)
        {
            if (oldByKey.TryGetValue(m.DevicePath, out var prev) && prev.ColorSettings != null)
                m.ColorSettings = prev.ColorSettings;
        }

        _profile.Monitors.Clear();
        foreach (var m in current) _profile.Monitors.Add(m);

        _selectedMonitor = null;
        _selectedMonitorIndex = -1;
        MonitorDetailsPanel.Visibility = Visibility.Collapsed;
        ColorSettingsPanel.Visibility = Visibility.Collapsed;
        LayoutCanvas.SetMonitors(_profile.Monitors);
        RefreshAvailableMonitors();
    }

    private void AlignHorizontal_Click(object sender, RoutedEventArgs e)
    {
        if (_profile == null || _profile.Monitors.Count == 0) return;

        int x = 0;
        foreach (var m in _profile.Monitors)
        {
            m.PositionX = x;
            m.PositionY = 0;
            x += m.Width;
        }
        LayoutCanvas.SetMonitors(_profile.Monitors);
    }

    private void SetPrimary_Click(object sender, RoutedEventArgs e)
    {
        if (_profile == null || _selectedMonitor == null) return;

        int dx = _selectedMonitor.PositionX;
        int dy = _selectedMonitor.PositionY;

        foreach (var m in _profile.Monitors)
        {
            m.PositionX -= dx;
            m.PositionY -= dy;
            m.IsPrimary = ReferenceEquals(m, _selectedMonitor);
        }

        LayoutCanvas.SetMonitors(_profile.Monitors);
        SetPrimaryBtn.IsEnabled = false;
    }
}
