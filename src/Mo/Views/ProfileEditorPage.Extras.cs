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

// Everything a profile carries besides the display layout itself: DDC/CI colour,
// default audio device, wallpaper, night light and the auto-switch / schedule rules.
public sealed partial class ProfileEditorPage
{
    // LoadAudioDevices() used to live here: it called IAudioService.GetAudioDevices()
    // straight from the UI thread, and that method blocks on WinRT device enumeration.
    // Nothing called it any more — LoadProfileDeferredAsync does the same work inside
    // Task.Run — so it was the last UI-thread caller of a blocking path and is gone.

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
}
