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

// Profile editor shell: construction, navigation, initial load and localization.
// Monitor editing, the non-layout extras and persistence live in the sibling
// ProfileEditorPage.*.cs partials.
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

    // JSON snapshot of the profile at load time. Compared on exit to detect unsaved edits.
    private string _initialSnapshot = string.Empty;

    // All currently-connected monitors. Each item exposes an InProfile flag so the
    // template can show a "✓ added" check or a "+" affordance, and the row stays in
    // place when toggled (no list churn).
    public ObservableCollection<AvailableMonitorItem> AvailableMonitors { get; } = new();

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

        // Baseline for dirty-state detection. Adjacency normalization inside SetMonitors
        // may have moved tiles — capture after that so the user isn't prompted to save
        // repositions they didn't make.
        _initialSnapshot = CaptureSnapshot();
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

    /// <summary>
    /// Sets a control's tooltip and its accessible name from the same resource, so an
    /// icon-only button reads the same way to a mouse user and to a screen reader.
    /// </summary>
    private static void SetHint(Microsoft.UI.Xaml.FrameworkElement element, string resourceKey)
    {
        var text = ResourceHelper.GetString(resourceKey);
        ToolTipService.SetToolTip(element, text);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(element, text);
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
        SetPrimaryLabel.Text = ResourceHelper.GetString("SetPrimary");

        // Tooltips were hardcoded English in the XAML, so they stayed English on a
        // Korean UI. The back and remove buttons are icon-only, which also left them
        // with nothing for a screen reader to announce.
        SetHint(ImportCurrentBtn, "TooltipImportCurrent");
        SetHint(AlignHorizontalBtn, "TooltipAlignHorizontal");
        SetHint(SetPrimaryBtn, "TooltipSetPrimary");
        SetHint(RemoveMonitorBtn, "TooltipRemoveMonitor");
        SetHint(BackBtn, "TooltipBack");
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

// One row in the Available Monitors panel. The visibility helpers let the DataTemplate
// switch between the "+" affordance and the "✓ already added" indicator without an
// IValueConverter lookup.
public sealed class AvailableMonitorItem
{
    public AvailableMonitorItem(MonitorInfo monitor, bool inProfile)
    {
        Monitor = monitor;
        InProfile = inProfile;
        Title = string.IsNullOrEmpty(monitor.FriendlyName) ? monitor.DevicePath : monitor.FriendlyName;
        // Annotate inactive monitors so the user knows the inventory entry is a
        // currently-off display (cable plugged in but Windows path disabled).
        var status = monitor.IsEnabled ? string.Empty : $"  ·  {ResourceHelper.GetString("MonitorOff")}";
        Subtitle = $"{monitor.ResolutionText}  ·  {monitor.RefreshRateHz:F0} Hz{status}";
    }

    public MonitorInfo Monitor { get; }
    public bool InProfile { get; }
    public string Title { get; }
    public string Subtitle { get; }
    public double RowOpacity => Monitor.IsEnabled ? 1.0 : 0.55;
    public Visibility AddIconVisibility => InProfile ? Visibility.Collapsed : Visibility.Visible;
    public Visibility CheckIconVisibility => InProfile ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Tooltip and accessible name for the row's single icon button, whose glyph flips
    /// between "+" and "✓". The icon alone carried the entire meaning of the action.
    /// </summary>
    public string ActionHint => ResourceHelper.GetString(
        InProfile ? "TooltipMonitorInProfile" : "TooltipAddMonitor", Title);
}
