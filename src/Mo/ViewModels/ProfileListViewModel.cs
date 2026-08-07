using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.Win32;
using Mo.Models;
using Mo.Services;

namespace Mo.ViewModels;

public partial class ProfileListViewModel : ObservableObject
{
    private readonly IProfileService _profileService;

    public ProfileListViewModel(IProfileService profileService)
    {
        _profileService = profileService;
        Profiles = _profileService.Profiles;

        // Keep the "currently applied" marker correct no matter what triggered the
        // apply — button, tray, hotkey, schedule or auto-switch all raise this.
        _profileService.ProfileApplied += (_, applied) => MarkActive(applied.Id);
        Profiles.CollectionChanged += (_, _) =>
        {
            MarkActive(_activeProfileId);
            RefreshAvailability();
        };

        // Availability depends on what is plugged in, so it is recomputed when that
        // changes rather than polled. Raised on a system thread; marshalled here.
        SystemEvents.DisplaySettingsChanged += (_, _) =>
            App.MainWindow?.DispatcherQueue?.TryEnqueue(RefreshAvailability);

        _ = LoadAsync();
    }

    private string? _activeProfileId;

    private void MarkActive(string? profileId)
    {
        _activeProfileId = profileId;
        foreach (var p in Profiles)
            p.IsActive = p.Id == profileId;
    }

    /// <summary>
    /// Marks which profiles can be applied with the monitors currently attached.
    /// </summary>
    /// <remarks>
    /// One CheckCompatibility per profile against a single hardware read, refreshed
    /// when the display configuration changes rather than on a timer. Without it the
    /// only way to learn a profile is unusable was to apply it and read the warning.
    /// </remarks>
    public void RefreshAvailability()
    {
        try
        {
            var display = App.Services.GetRequiredService<IDisplayService>();
            var snapshot = Profiles.ToList();

            // One hardware read for the whole list, not one per profile.
            var results = display.CheckCompatibilityAll(snapshot);
            for (int i = 0; i < snapshot.Count && i < results.Count; i++)
                snapshot[i].IsAvailable = results[i].MissingMonitors.Count == 0;
        }
        catch
        {
            // If the hardware cannot be read, claim nothing — greying every card out
            // on a transient failure would be worse than saying nothing.
            foreach (var p in Profiles) p.IsAvailable = true;
        }
    }

    public ObservableCollection<DisplayProfile> Profiles { get; }

    public Visibility IsEmpty => Profiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    [RelayCommand]
    private async Task SaveCurrentAsync()
    {
        var profile = await _profileService.CaptureCurrentAsync($"Profile {Profiles.Count + 1}");
        await _profileService.SaveProfileAsync(profile);
        OnPropertyChanged(nameof(IsEmpty));
    }

    public DisplayApplyResult LastApplyResult { get; private set; }

    [RelayCommand]
    private async Task ApplyProfileAsync(string profileId)
    {
        LastApplyResult = await _profileService.ApplyProfileAsync(profileId);
    }

    [RelayCommand]
    private async Task DeleteProfileAsync(string profileId)
    {
        await _profileService.DeleteProfileAsync(profileId);
        OnPropertyChanged(nameof(IsEmpty));
    }

    public void RefreshIsEmpty()
    {
        OnPropertyChanged(nameof(IsEmpty));
    }

    private async Task LoadAsync()
    {
        await _profileService.LoadAllAsync();

        // On a cold start nothing has been applied this session, so seed the marker
        // from the profile the app restored (or last applied before it was closed).
        try
        {
            var settings = App.Services.GetRequiredService<ISettingsService>();
            MarkActive(settings.Settings.LastAppliedProfileId);
        }
        catch { }

        RefreshAvailability();
        OnPropertyChanged(nameof(IsEmpty));
    }
}
