using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Mo.Helpers;
using Mo.Models;
using Mo.Services;

namespace Mo.Views;

public sealed partial class ProfileEditorPage : Page
{
    private readonly IProfileService _profileService;
    private readonly INavigationService _navigationService;
    private DisplayProfile? _profile;
    private List<(string id, string name)> _audioDevices = [];
    private bool _loading = true;

    public ProfileEditorPage()
    {
        _profileService = App.Services.GetRequiredService<IProfileService>();
        _navigationService = App.Services.GetRequiredService<INavigationService>();
        InitializeComponent();
        ApplyLocalization();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _loading = true;

        if (e.Parameter is DisplayProfile profile)
        {
            _profile = profile;
            LoadProfile();
        }

        _loading = false;
    }

    private void LoadProfile()
    {
        if (_profile == null) return;

        ProfileNameBox.Text = _profile.Name;
        DescriptionBox.Text = _profile.Description;
        LayoutCanvas.SetMonitors(_profile.Monitors);

        // Audio devices
        LoadAudioDevices();

        // Wallpaper
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
        if (monitor == null) { MonitorDetailsPanel.Visibility = Visibility.Collapsed; return; }
        MonitorDetailsPanel.Visibility = Visibility.Visible;
        DetailResolution.Text = monitor.ResolutionText;
        DetailRefreshRate.Text = $"{monitor.RefreshRateHz:F1} Hz";
        DetailRotation.Text = monitor.Rotation == DisplayRotation.None
            ? ResourceHelper.GetString("RotationNone") : $"{(int)monitor.Rotation}°";
        DetailPosition.Text = $"({monitor.PositionX}, {monitor.PositionY})";
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

    private void CancelButton_Click(object sender, RoutedEventArgs e) => _navigationService.GoBack();
    private void BackButton_Click(object sender, RoutedEventArgs e) => _navigationService.GoBack();

    private void ApplyLocalization()
    {
        ProfileNameBox.PlaceholderText = ResourceHelper.GetString("ProfileNamePlaceholder");
        DescriptionBox.PlaceholderText = ResourceHelper.GetString("DescriptionPlaceholder");
        ResLabel.Text = ResourceHelper.GetString("Resolution");
        RefreshLabel.Text = ResourceHelper.GetString("RefreshRate");
        RotLabel.Text = ResourceHelper.GetString("Rotation");
        PosLabel.Text = ResourceHelper.GetString("Position");
        ExtrasTitle.Text = ResourceHelper.GetString("GeneralSection");
        AudioLabel.Text = ResourceHelper.GetString("AudioDevice");
        AudioDesc.Text = ResourceHelper.GetString("AudioDeviceDesc");
        WallpaperLabel.Text = ResourceHelper.GetString("Wallpaper");
        WallpaperBrowseBtn.Content = ResourceHelper.GetString("WallpaperBrowse");
        WallpaperClearBtn.Content = ResourceHelper.GetString("WallpaperClear");
        NightLightLabel.Text = ResourceHelper.GetString("NightLight");
        NightLightDesc.Text = ResourceHelper.GetString("NightLightDesc");
        AutoSwitchLabel.Text = ResourceHelper.GetString("AutoSwitch");
        AutoSwitchDescText.Text = ResourceHelper.GetString("AutoSwitchDesc");
        ScheduleLabel.Text = ResourceHelper.GetString("ScheduleSection");
        ScheduleDescText.Text = ResourceHelper.GetString("ScheduleDesc");
        ScheduleTimeLabel.Text = ResourceHelper.GetString("ScheduleTime");
        ScheduleDaysLabel.Text = ResourceHelper.GetString("ScheduleDays");
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
