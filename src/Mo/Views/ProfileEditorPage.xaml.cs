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

public sealed partial class ProfileEditorPage : Page
{
    private readonly IProfileService _profileService;
    private readonly INavigationService _navigationService;
    private readonly IDisplayService _displayService;
    private DisplayProfile? _profile;
    private MonitorInfo? _selectedMonitor;
    private int _selectedMonitorIndex = -1;
    private List<(string id, string name)> _audioDevices = [];
    private List<MonitorColorCapabilities> _colorCaps = [];
    private bool _loading = true;

    public ProfileEditorPage()
    {
        _profileService = App.Services.GetRequiredService<IProfileService>();
        _navigationService = App.Services.GetRequiredService<INavigationService>();
        _displayService = App.Services.GetRequiredService<IDisplayService>();
        InitializeComponent();
        ApplyLocalization();
        LayoutCanvas.MonitorPositionChanged += (_, _) => RefreshAvailableMonitors();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _loading = true;

        if (e.Parameter is DisplayProfile profile)
        {
            _profile = profile;
            LoadProfileImmediate();
            _ = LoadProfileDeferredAsync();
        }

        _loading = false;
    }

    private void LoadProfileImmediate()
    {
        if (_profile == null) return;

        ProfileNameBox.Text = _profile.Name;
        DescriptionBox.Text = _profile.Description;
        LayoutCanvas.SetMonitors(_profile.Monitors);
        RefreshAvailableMonitors();

        // Wallpaper (immediate — no I/O)
        WallpaperPathText.Text = string.IsNullOrEmpty(_profile.WallpaperPath)
            ? ResourceHelper.GetString("AudioNone")
            : Path.GetFileName(_profile.WallpaperPath);

        // Night light
        var nlIndex = _profile.NightLightEnabled switch
        {
            true => 1,
            false => 2,
            null => 0,
        };
        NightLightCombo.SelectedIndex = nlIndex;

        // Live wallpaper
        if (_profile.LiveWallpaper is { Provider: not LiveWallpaperProvider.None, Entries.Count: > 0 })
        {
            LiveWallpaperCard.Visibility = Visibility.Visible;
            LiveWpProviderText.Text = _profile.LiveWallpaper.Provider switch
            {
                LiveWallpaperProvider.WallpaperEngine => "Wallpaper Engine",
                LiveWallpaperProvider.Lively => "Lively Wallpaper",
                _ => "",
            };
            LiveWpEntries.ItemsSource = _profile.LiveWallpaper.Entries
                .Select(e => $"Monitor {e.MonitorIndex}: {Path.GetFileName(e.FilePath)}").ToList();
        }
        else
        {
            LiveWallpaperCard.Visibility = Visibility.Collapsed;
        }

        // Unmatched monitor action
        UnmatchedCombo.SelectedIndex = (int)_profile.UnmatchedAction;

        // Auto-switch
        AutoSwitchToggle.IsOn = _profile.AutoSwitch;

        // Schedule
        var sched = _profile.Schedule;
        ScheduleToggle.IsOn = sched?.Enabled ?? false;
        ScheduleDetails.Visibility = ScheduleToggle.IsOn ? Visibility.Visible : Visibility.Collapsed;
        if (sched?.Time != null)
            ScheduleTimePicker.Time = sched.Time.Value.ToTimeSpan();

        LoadScheduleDays(sched);
    }

    private async Task LoadProfileDeferredAsync()
    {
        if (_profile == null) return;

        // Run slow I/O off the UI thread
        var (colorCaps, audioDevices) = await Task.Run(() =>
        {
            List<MonitorColorCapabilities> caps;
            try
            {
                var colorService = App.Services.GetRequiredService<IMonitorColorService>();
                caps = colorService.DetectCapabilities();
            }
            catch { caps = []; }

            List<(string id, string name)> audio;
            try
            {
                var audioService = App.Services.GetRequiredService<IAudioService>();
                audio = audioService.GetAudioDevices();
            }
            catch { audio = []; }

            return (caps, audio);
        });

        _colorCaps = colorCaps;
        _audioDevices = audioDevices;

        // Update UI on dispatcher thread
        AudioCombo.Items.Clear();
        AudioCombo.Items.Add(ResourceHelper.GetString("AudioNone"));
        int selectedIndex = 0;
        for (int i = 0; i < _audioDevices.Count; i++)
        {
            AudioCombo.Items.Add(_audioDevices[i].name);
            if (_profile?.AudioDeviceId == _audioDevices[i].id)
                selectedIndex = i + 1;
        }
        AudioCombo.SelectedIndex = selectedIndex;
    }

    private void LoadAudioDevices()
    {
        AudioCombo.Items.Clear();
        AudioCombo.Items.Add(ResourceHelper.GetString("AudioNone"));

        try
        {
            var audioService = App.Services.GetRequiredService<IAudioService>();
            _audioDevices = audioService.GetAudioDevices();
            int selectedIndex = 0;

            for (int i = 0; i < _audioDevices.Count; i++)
            {
                AudioCombo.Items.Add(_audioDevices[i].name);
                if (_profile?.AudioDeviceId == _audioDevices[i].id)
                    selectedIndex = i + 1;
            }

            AudioCombo.SelectedIndex = selectedIndex;
        }
        catch
        {
            AudioCombo.SelectedIndex = 0;
        }
    }

    private void LoadScheduleDays(ScheduleConfig? sched)
    {
        var days = sched?.Days ?? [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday];
        DayMon.IsChecked = days.Contains(DayOfWeek.Monday);
        DayTue.IsChecked = days.Contains(DayOfWeek.Tuesday);
        DayWed.IsChecked = days.Contains(DayOfWeek.Wednesday);
        DayThu.IsChecked = days.Contains(DayOfWeek.Thursday);
        DayFri.IsChecked = days.Contains(DayOfWeek.Friday);
        DaySat.IsChecked = days.Contains(DayOfWeek.Saturday);
        DaySun.IsChecked = days.Contains(DayOfWeek.Sunday);
    }

    private List<DayOfWeek> GetSelectedDays()
    {
        var days = new List<DayOfWeek>();
        if (DayMon.IsChecked == true) days.Add(DayOfWeek.Monday);
        if (DayTue.IsChecked == true) days.Add(DayOfWeek.Tuesday);
        if (DayWed.IsChecked == true) days.Add(DayOfWeek.Wednesday);
        if (DayThu.IsChecked == true) days.Add(DayOfWeek.Thursday);
        if (DayFri.IsChecked == true) days.Add(DayOfWeek.Friday);
        if (DaySat.IsChecked == true) days.Add(DayOfWeek.Saturday);
        if (DaySun.IsChecked == true) days.Add(DayOfWeek.Sunday);
        return days;
    }

    // --- Event handlers ---

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

    private void UpdateColorLabels()
    {
        BrightnessValue.Text = $"{(int)BrightnessSlider.Value}";
        ContrastValue.Text = $"{(int)ContrastSlider.Value}";
        RedValue.Text = $"{(int)RedSlider.Value}";
        GreenValue.Text = $"{(int)GreenSlider.Value}";
        BlueValue.Text = $"{(int)BlueSlider.Value}";
    }

    private void ColorSlider_Changed(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_loading || _selectedMonitor == null) return;

        _selectedMonitor.ColorSettings ??= new MonitorColorSettings();

        // Only save values for supported (enabled) sliders
        _selectedMonitor.ColorSettings.Brightness = BrightnessSlider.IsEnabled ? (int)BrightnessSlider.Value : null;
        _selectedMonitor.ColorSettings.Contrast = ContrastSlider.IsEnabled ? (int)ContrastSlider.Value : null;
        _selectedMonitor.ColorSettings.RedGain = RedSlider.IsEnabled ? (int)RedSlider.Value : null;
        _selectedMonitor.ColorSettings.GreenGain = GreenSlider.IsEnabled ? (int)GreenSlider.Value : null;
        _selectedMonitor.ColorSettings.BlueGain = BlueSlider.IsEnabled ? (int)BlueSlider.Value : null;

        UpdateColorLabels();
    }

    private void AudioCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _profile == null) return;
        var idx = AudioCombo.SelectedIndex;
        if (idx <= 0)
        {
            _profile.AudioDeviceId = null;
            _profile.AudioDeviceName = null;
        }
        else if (idx - 1 < _audioDevices.Count)
        {
            _profile.AudioDeviceId = _audioDevices[idx - 1].id;
            _profile.AudioDeviceName = _audioDevices[idx - 1].name;
        }
    }

    private async void WallpaperBrowse_Click(object sender, RoutedEventArgs e)
    {
        if (_profile == null) return;
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowHelper.GetHwnd(App.MainWindow));
        picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.PicturesLibrary;
        picker.FileTypeFilter.Add(".jpg");
        picker.FileTypeFilter.Add(".jpeg");
        picker.FileTypeFilter.Add(".png");
        picker.FileTypeFilter.Add(".bmp");

        var file = await picker.PickSingleFileAsync();
        if (file != null)
        {
            _profile.WallpaperPath = file.Path;
            WallpaperPathText.Text = file.Name;
        }
    }

    private void WallpaperClear_Click(object sender, RoutedEventArgs e)
    {
        if (_profile == null) return;
        _profile.WallpaperPath = null;
        WallpaperPathText.Text = ResourceHelper.GetString("AudioNone");
    }

    private void NightLightCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _profile == null) return;
        _profile.NightLightEnabled = NightLightCombo.SelectedIndex switch
        {
            1 => true,
            2 => false,
            _ => null,
        };
    }

    private void LiveWpClear_Click(object sender, RoutedEventArgs e)
    {
        if (_profile == null) return;
        _profile.LiveWallpaper = null;
        LiveWallpaperCard.Visibility = Visibility.Collapsed;
    }

    private void AutoSwitchToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading || _profile == null) return;
        _profile.AutoSwitch = AutoSwitchToggle.IsOn;
    }

    private void ScheduleToggle_Toggled(object sender, RoutedEventArgs e)
    {
        ScheduleDetails.Visibility = ScheduleToggle.IsOn ? Visibility.Visible : Visibility.Collapsed;
        if (_loading || _profile == null) return;
        _profile.Schedule ??= new ScheduleConfig();
        _profile.Schedule.Enabled = ScheduleToggle.IsOn;
    }

    private void ScheduleTime_Changed(object sender, TimePickerValueChangedEventArgs e)
    {
        if (_loading || _profile == null) return;
        _profile.Schedule ??= new ScheduleConfig();
        _profile.Schedule.Time = TimeOnly.FromTimeSpan(e.NewTime);
    }

    private void DayToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_loading || _profile == null) return;
        _profile.Schedule ??= new ScheduleConfig();
        _profile.Schedule.Days = GetSelectedDays();
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_profile == null) return;

        _profile.Name = ProfileNameBox.Text;
        _profile.Description = DescriptionBox.Text;
        _profile.ModifiedAt = DateTime.UtcNow;

        // Schedule sync
        if (_profile.Schedule != null)
        {
            _profile.Schedule.Days = GetSelectedDays();
            if (ScheduleTimePicker.Time != default)
                _profile.Schedule.Time = TimeOnly.FromTimeSpan(ScheduleTimePicker.Time);
        }

        await _profileService.SaveProfileAsync(_profile);
        _navigationService.GoBack();
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
        DescriptionBox.Text = $"{_profile.Monitors.Count} monitor(s)";
    }

    private void UnmatchedCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _profile == null) return;
        _profile.UnmatchedAction = (UnmatchedMonitorAction)UnmatchedCombo.SelectedIndex;
    }

    private void MonitorEnabledToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading || _selectedMonitor == null) return;
        _selectedMonitor.IsEnabled = MonitorEnabledToggle.IsOn;
        LayoutCanvas.SetMonitors(_profile!.Monitors);
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => _navigationService.GoBack();
    private void BackButton_Click(object sender, RoutedEventArgs e) => _navigationService.GoBack();

    // --- Layout toolbar + available monitors ---

    private void RefreshAvailableMonitors()
    {
        if (_profile == null) return;

        List<MonitorInfo> current;
        try { current = _displayService.GetCurrentConfiguration(); }
        catch { current = []; }

        var profileIds = _profile.Monitors
            .Select(m => new MonitorMatcher.MonitorIdentity(
                m.DevicePath, m.EdidManufacturerId, m.EdidProductCodeId, m.ConnectorInstance, m.FriendlyName))
            .ToList();
        var currentIds = current
            .Select(m => new MonitorMatcher.MonitorIdentity(
                m.DevicePath, m.EdidManufacturerId, m.EdidProductCodeId, m.ConnectorInstance, m.FriendlyName))
            .ToList();

        var match = MonitorMatcher.Match(profileIds, currentIds);
        var available = match.UnmatchedCurrent
            .Select(i => current[i])
            .ToList();

        AvailableMonitorsList.Items.Clear();
        foreach (var monitor in available)
        {
            AvailableMonitorsList.Items.Add(BuildAvailableRow(monitor));
        }

        AvailableMonitorsPanel.Visibility = available.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private UIElement BuildAvailableRow(MonitorInfo monitor)
    {
        var grid = new Grid { Padding = new Thickness(8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Background = (Brush)Application.Current.Resources["SubtleFillColorTransparentBrush"];
        grid.CornerRadius = new CornerRadius(6);

        var info = new StackPanel();
        info.Children.Add(new TextBlock { Text = string.IsNullOrEmpty(monitor.FriendlyName) ? monitor.DevicePath : monitor.FriendlyName, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        info.Children.Add(new TextBlock
        {
            Text = $"{monitor.ResolutionText}  ·  {monitor.RefreshRateHz:F0} Hz",
            Opacity = 0.6,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
        });

        var addBtn = new Button
        {
            Content = new FontIcon { Glyph = "\uE710", FontSize = 14 },
            VerticalAlignment = VerticalAlignment.Center,
            Tag = monitor,
        };
        addBtn.Click += AddMonitor_Click;

        Grid.SetColumn(info, 0);
        Grid.SetColumn(addBtn, 1);
        grid.Children.Add(info);
        grid.Children.Add(addBtn);
        return grid;
    }

    private void AddMonitor_Click(object sender, RoutedEventArgs e)
    {
        if (_profile == null || sender is not Button btn || btn.Tag is not MonitorInfo source) return;

        // Place to the right of the current layout.
        int placeX = 0;
        if (_profile.Monitors.Count > 0)
        {
            placeX = _profile.Monitors.Max(m => m.PositionX + m.Width);
        }

        var copy = new MonitorInfo
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
        };
        _profile.Monitors.Add(copy);
        LayoutCanvas.SetMonitors(_profile.Monitors);
        RefreshAvailableMonitors();
        DescriptionBox.Text = $"{_profile.Monitors.Count} monitor(s)";
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
        DescriptionBox.Text = $"{_profile.Monitors.Count} monitor(s)";
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

    private void AlignGrid_Click(object sender, RoutedEventArgs e)
    {
        if (_profile == null || _profile.Monitors.Count == 0) return;

        int cols = (int)Math.Ceiling(Math.Sqrt(_profile.Monitors.Count));
        int colX = 0, rowY = 0, colIdx = 0, rowHeight = 0;

        foreach (var m in _profile.Monitors)
        {
            m.PositionX = colX;
            m.PositionY = rowY;
            colX += m.Width;
            rowHeight = Math.Max(rowHeight, m.Height);
            colIdx++;
            if (colIdx >= cols)
            {
                colIdx = 0;
                colX = 0;
                rowY += rowHeight;
                rowHeight = 0;
            }
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

    private void ApplyLocalization()
    {
        ProfileNameBox.PlaceholderText = ResourceHelper.GetString("ProfileNamePlaceholder");
        DescriptionBox.PlaceholderText = ResourceHelper.GetString("DescriptionPlaceholder");
        ResLabel.Text = ResourceHelper.GetString("Resolution");
        RefreshLabel.Text = ResourceHelper.GetString("RefreshRate");
        RotLabel.Text = ResourceHelper.GetString("Rotation");
        // Position is now editable via drag in canvas
        ExtrasTitle.Text = ResourceHelper.GetString("GeneralSection");
        AudioLabel.Text = ResourceHelper.GetString("AudioDevice");
        AudioDesc.Text = ResourceHelper.GetString("AudioDeviceDesc");
        WallpaperLabel.Text = ResourceHelper.GetString("Wallpaper");
        WallpaperBrowseBtn.Content = ResourceHelper.GetString("WallpaperBrowse");
        WallpaperClearBtn.Content = ResourceHelper.GetString("WallpaperClear");
        NightLightLabel.Text = ResourceHelper.GetString("NightLight");
        NightLightDesc.Text = ResourceHelper.GetString("NightLightDesc");
        UnmatchedLabel.Text = ResourceHelper.GetString("UnmatchedMonitors");
        UnmatchedDesc.Text = ResourceHelper.GetString("UnmatchedMonitorsDesc");
        ImportCurrentLabel.Text = ResourceHelper.GetString("ImportCurrent");
        AlignHorizontalLabel.Text = ResourceHelper.GetString("AlignHorizontal");
        AlignGridLabel.Text = ResourceHelper.GetString("AlignGrid");
        SetPrimaryLabel.Text = ResourceHelper.GetString("SetPrimary");
        AvailableMonitorsTitle.Text = ResourceHelper.GetString("AvailableMonitors");
        AvailableMonitorsDesc.Text = ResourceHelper.GetString("AvailableMonitorsDesc");
        UnmatchedCombo.Items.Clear();
        UnmatchedCombo.Items.Add(ResourceHelper.GetString("UnmatchedKeep"));
        UnmatchedCombo.Items.Add(ResourceHelper.GetString("UnmatchedDisable"));
        AutoSwitchLabel.Text = ResourceHelper.GetString("AutoSwitch");
        AutoSwitchDescText.Text = ResourceHelper.GetString("AutoSwitchDesc");
        ScheduleLabel.Text = ResourceHelper.GetString("ScheduleSection");
        ScheduleDescText.Text = ResourceHelper.GetString("ScheduleDesc");
        ScheduleTimeLabel.Text = ResourceHelper.GetString("ScheduleTime");
        ScheduleDaysLabel.Text = ResourceHelper.GetString("ScheduleDays");
        ColorTitle.Text = ResourceHelper.GetString("MonitorColor");
        BrightnessLabel.Text = ResourceHelper.GetString("Brightness");
        ContrastLabel.Text = ResourceHelper.GetString("Contrast");
        LiveWpLabel.Text = ResourceHelper.GetString("LiveWallpaper");
        LiveWpClearBtn.Content = ResourceHelper.GetString("WallpaperClear");
        CancelBtn.Content = ResourceHelper.GetString("Cancel");
        SaveBtn.Content = ResourceHelper.GetString("Save");

        // Night light combo
        NightLightCombo.Items.Clear();
        NightLightCombo.Items.Add(ResourceHelper.GetString("NightLightUnchanged"));
        NightLightCombo.Items.Add(ResourceHelper.GetString("NightLightOn"));
        NightLightCombo.Items.Add(ResourceHelper.GetString("NightLightOff"));
        NightLightCombo.SelectedIndex = 0;

        // Day buttons
        DayMon.Content = ResourceHelper.GetString("Monday");
        DayTue.Content = ResourceHelper.GetString("Tuesday");
        DayWed.Content = ResourceHelper.GetString("Wednesday");
        DayThu.Content = ResourceHelper.GetString("Thursday");
        DayFri.Content = ResourceHelper.GetString("Friday");
        DaySat.Content = ResourceHelper.GetString("Saturday");
        DaySun.Content = ResourceHelper.GetString("Sunday");
    }
}
