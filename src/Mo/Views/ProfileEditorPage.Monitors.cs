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
        SelectedMonitorName.Text = monitor.FriendlyName;

        PopulateModePickers(monitor);

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

        // Hidden, not disabled, matching DisplayTuningPage: a greyed-out slider only
        // raises the question of why it is greyed out.
        BrightnessRow.Visibility = hasBri ? Visibility.Visible : Visibility.Collapsed;
        ContrastRow.Visibility = hasCon ? Visibility.Visible : Visibility.Collapsed;
        RedRow.Visibility = hasR ? Visibility.Visible : Visibility.Collapsed;
        GreenRow.Visibility = hasG ? Visibility.Visible : Visibility.Collapsed;
        BlueRow.Visibility = hasB ? Visibility.Visible : Visibility.Collapsed;

        BrightnessSlider.Value = cs?.Brightness ?? 50;
        ContrastSlider.Value = cs?.Contrast ?? 50;
        RedSlider.Value = cs?.RedGain ?? 50;
        GreenSlider.Value = cs?.GreenGain ?? 50;
        BlueSlider.Value = cs?.BlueGain ?? 50;

        UpdateColorLabels();

        // No "(WMI)" suffix: which interface carries the value is Mo's business, and the
        // slider behaves the same either way.
        BrightnessLabel.Text = ResourceHelper.GetString("Brightness");

        _loading = false;
    }

    // Mode lists come from the driver and never change while the page is open, so one
    // read per monitor is enough. Keyed by device path, empty list meaning "asked, none".
    private readonly Dictionary<string, IReadOnlyList<DisplayMode>> _modeCache = [];

    // What the two pickers are currently showing. The handlers index these, not the
    // driver list, because a mode the driver does not enumerate is prepended.
    private readonly Dictionary<string, DpiScaleState> _scaleCache = [];
    private List<(int Width, int Height)> _shownResolutions = [];
    private List<int> _shownRates = [];
    private List<int> _shownScales = [];

    private static string ModeKey(MonitorInfo m) =>
        string.IsNullOrEmpty(m.DevicePath) ? m.GdiDeviceName : m.DevicePath;

    /// <summary>Fills the resolution and refresh pickers for a monitor. A monitor Windows
    /// is not driving has no mode list, so its stored values are offered read-only rather
    /// than an empty dropdown.</summary>
    private void PopulateModePickers(MonitorInfo monitor)
    {
        var key = ModeKey(monitor);
        if (_modeCache.TryGetValue(key, out var cached))
        {
            FillModePickers(monitor, cached);
            return;
        }

        FillModePickers(monitor, []);

        _ = Task.Run(() =>
        {
            IReadOnlyList<DisplayMode> modes;
            DpiScaleState? scales;
            try
            {
                // Active paths only, resolved against live hardware rather than the
                // profile. Windows reassigns \\.\DISPLAYn across reboots, and an inactive
                // path names a source that is currently driving some other display.
                var live = _displayService.GetCurrentConfiguration()
                    .FirstOrDefault(c => MatchesProfileMonitor(monitor, c));
                modes = live == null ? [] : _displayService.GetAvailableModes(live);
                scales = live == null ? null : _displayService.GetDpiScale(live);
            }
            catch { modes = []; scales = null; }

            DispatcherQueue.TryEnqueue(() =>
            {
                _modeCache[key] = modes;
                if (scales != null) _scaleCache[key] = scales;
                if (ReferenceEquals(_selectedMonitor, monitor))
                    FillModePickers(monitor, modes);
            });
        });
    }

    private void FillScalePicker(MonitorInfo monitor)
    {
        _scaleCache.TryGetValue(ModeKey(monitor), out var live);
        var scales = (live?.Available ?? []).ToList();

        // A profile that never captured scaling shows what the monitor is on right now,
        // and only records a value if the user picks one. Filling must not write, or
        // clicking a monitor would turn "leave scaling alone" into a demand for 100%.
        int current = monitor.DpiScale > 0 ? monitor.DpiScale
            : live?.Current ?? DpiScaling.Default;

        if (!scales.Contains(current))
            scales.Insert(0, current);

        _shownScales = scales;
        ScaleCombo.ItemsSource = scales.Select(s => $"{s}%").ToList();
        ScaleCombo.SelectedIndex = scales.IndexOf(current);
        ScaleCombo.IsEnabled = scales.Count > 1;
    }

    private void ScaleCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _selectedMonitor == null) return;

        int index = ScaleCombo.SelectedIndex;
        if (index < 0 || index >= _shownScales.Count) return;

        _selectedMonitor.DpiScale = _shownScales[index];
    }

    private void FillModePickers(MonitorInfo monitor, IReadOnlyList<DisplayMode> modes)
    {
        _loading = true;
        try
        {
            // The list is panel-native, matching what Windows' own display settings show
            // for a rotated monitor; the profile stores desktop extent.
            var (srcW, srcH) = RotationGeometry.ToSource(monitor.Width, monitor.Height, (int)monitor.Rotation);

            // Kept as a field: the selection handler has to index the list that is on
            // screen, and a mode the driver does not enumerate is prepended to it.
            _shownResolutions = modes.Select(m => (m.Width, m.Height)).Distinct().ToList();
            if (!_shownResolutions.Contains((srcW, srcH)))
                _shownResolutions.Insert(0, (srcW, srcH));

            ResolutionCombo.ItemsSource = _shownResolutions.Select(r => $"{r.Width} x {r.Height}").ToList();
            ResolutionCombo.SelectedIndex = _shownResolutions.IndexOf((srcW, srcH));
            ResolutionCombo.IsEnabled = modes.Count > 0;

            FillRefreshPicker(monitor, modes, srcW, srcH);
            FillScalePicker(monitor);
        }
        finally { _loading = false; }
    }

    private void FillRefreshPicker(MonitorInfo monitor, IReadOnlyList<DisplayMode> modes, int srcW, int srcH)
    {
        int currentHz = (int)Math.Round(monitor.RefreshRateHz);

        var rates = modes
            .Where(m => m.Width == srcW && m.Height == srcH)
            .Select(m => m.RefreshHz)
            .Distinct()
            .OrderByDescending(hz => hz)
            .ToList();

        // The stored rate is offered only when the driver told us nothing, which is the
        // case for a display Windows is not driving. Adding it to a real list would let
        // the user keep 240 Hz after switching to a resolution that tops out at 120.
        if (rates.Count == 0 && currentHz > 0)
            rates.Add(currentHz);

        _shownRates = rates;
        RefreshCombo.ItemsSource = rates.Select(hz => $"{hz} Hz").ToList();
        RefreshCombo.SelectedIndex = rates.IndexOf(currentHz) is var i && i >= 0 ? i : (rates.Count > 0 ? 0 : -1);
        RefreshCombo.IsEnabled = rates.Count > 1;
    }

    /// <summary>Only ever called from the picker's own handler. Filling the picker must
    /// not write: a captured 59951/1000 would be rounded to 60/1 just by clicking the
    /// monitor, asking for a timing the panel may not have.</summary>
    private static void SetRefreshRate(MonitorInfo monitor, int hz)
    {
        monitor.RefreshRateNumerator = (uint)hz;
        monitor.RefreshRateDenominator = 1;
    }

    private void ResolutionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _selectedMonitor == null) return;
        if (!_modeCache.TryGetValue(ModeKey(_selectedMonitor), out var modes)) return;

        int index = ResolutionCombo.SelectedIndex;
        if (index < 0 || index >= _shownResolutions.Count) return;

        var (srcW, srcH) = _shownResolutions[index];
        var (w, h) = RotationGeometry.ToDesktop(srcW, srcH, (int)_selectedMonitor.Rotation);
        _selectedMonitor.Width = w;
        _selectedMonitor.Height = h;

        _loading = true;
        try { FillRefreshPicker(_selectedMonitor, modes, srcW, srcH); }
        finally { _loading = false; }

        // The old rate may not exist at the new resolution. Writing whatever the picker
        // settled on keeps the profile asking for a mode the panel actually has, and this
        // is a user action, so writing is what they expect.
        if (RefreshCombo.SelectedIndex is var rateIdx && rateIdx >= 0 && rateIdx < _shownRates.Count)
            SetRefreshRate(_selectedMonitor, _shownRates[rateIdx]);

        LayoutCanvas.SetMonitors(_profile!.Monitors);
    }

    private void RefreshCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _selectedMonitor == null) return;

        int index = RefreshCombo.SelectedIndex;
        if (index < 0 || index >= _shownRates.Count) return;

        SetRefreshRate(_selectedMonitor, _shownRates[index]);
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

        // The stored size is the desktop extent, so a turn between landscape and
        // portrait transposes it. Same predicate the geometry helper uses.
        if (RotationGeometry.IsQuarterTurn((int)previous) != RotationGeometry.IsQuarterTurn((int)next))
            (_selectedMonitor.Width, _selectedMonitor.Height) = (_selectedMonitor.Height, _selectedMonitor.Width);

        _selectedMonitor.Rotation = next;
        RotationWarningBar.IsOpen = next != DisplayRotation.None;
        LayoutCanvas.SetMonitors(_profile!.Monitors);
    }

    /// <summary>Takes the monitor out of the profile, which is how a profile says the
    /// display should be off. There is no separate "off" state to keep it in.</summary>
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

    /// <summary>Every connected monitor, active or not, so a display that is currently
    /// off still shows up in the inventory. Falls back to active-only on failure.</summary>
    private List<MonitorInfo> ReadConnectedMonitors()
    {
        try { return _displayService.GetAllConnectedMonitors(); }
        catch
        {
            try { return _displayService.GetCurrentConfiguration(); }
            catch { return []; }
        }
    }

    private void RefreshAvailableMonitors(List<MonitorInfo>? known = null)
    {
        if (_profile == null)
        {
            AvailableMonitors.Clear();
            return;
        }

        var connected = known ?? ReadConnectedMonitors();

        AvailableMonitors.Clear();
        foreach (var monitor in connected)
        {
            bool inProfile = _profile.Monitors.Any(p => MatchesProfileMonitor(p, monitor));
            AvailableMonitors.Add(new AvailableMonitorItem(monitor, inProfile));
        }
    }

    private static bool MatchesProfileMonitor(MonitorInfo profile, MonitorInfo current) =>
        profile.IsSameMonitorAs(current);

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
            GdiDeviceName = source.GdiDeviceName,
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
        // Every connected monitor, so the ones currently switched off are imported as
        // off instead of being left out and silently kept running on apply.
        List<MonitorInfo> current;
        try { current = _displayService.GetAllConnectedMonitors(); }
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

        var placements = LayoutArranger.Row(
            [.. _profile.Monitors.Select(m => (m.Width, m.Height))]);

        for (int i = 0; i < _profile.Monitors.Count; i++)
        {
            _profile.Monitors[i].PositionX = placements[i].X;
            _profile.Monitors[i].PositionY = placements[i].Y;
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
