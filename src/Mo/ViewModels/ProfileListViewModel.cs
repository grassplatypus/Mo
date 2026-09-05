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

        // Raised off the UI thread by an unattended trigger, so marshal it.
        _profileService.ApplyWorkFinished += (_, _) =>
            App.MainWindow?.DispatcherQueue?.TryEnqueue(() => SetApplying(false));
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

    // Both touched only on the UI thread, which is where every caller runs.
    private bool _availabilityBusy;
    private bool _availabilityQueued;

    /// <summary>Marks which profiles can be applied with the monitors currently attached.
    /// One hardware read for all of them, off the dispatcher, refreshed when the display
    /// configuration changes rather than polled.</summary>
    /// <remarks>Coalesced deliberately: loading raises CollectionChanged once per profile,
    /// and a CCD round trip per card on the dispatcher is what made startup stutter.</remarks>
    public void RefreshAvailability()
    {
        if (_availabilityBusy) { _availabilityQueued = true; return; }

        var snapshot = Profiles.ToList();
        if (snapshot.Count == 0) return;

        _availabilityBusy = true;
        _availabilityQueued = false;

        var queue = App.MainWindow?.DispatcherQueue;

        _ = Task.Run(() =>
        {
            List<bool>? available = null;
            try
            {
                var display = App.Services.GetRequiredService<IDisplayService>();
                var results = display.CheckCompatibilityAll(snapshot);
                available = [.. results.Select(r => r.MissingMonitors.Count == 0)];
            }
            catch
            {
                // Hardware unreadable: claim nothing. Greying every card out on a
                // transient failure would be worse than saying nothing.
            }

            void Apply()
            {
                for (int i = 0; i < snapshot.Count; i++)
                    snapshot[i].IsAvailable = available == null || (i < available.Count && available[i]);

                _availabilityBusy = false;
                if (_availabilityQueued) RefreshAvailability();
            }

            if (queue == null) Apply();
            else queue.TryEnqueue(Apply);
        });
    }

    public ObservableCollection<DisplayProfile> Profiles { get; }

    private bool _isLoaded;

    /// <summary>False until the first load finishes. A count of zero means two different
    /// things before and after that, and saying "no profiles" to someone who has them is
    /// the worse of the two. See .claude/rules/80-ui-responsiveness.md.</summary>
    public bool IsLoaded
    {
        get => _isLoaded;
        private set
        {
            if (_isLoaded == value) return;
            _isLoaded = value;
            OnPropertyChanged();
            RefreshIsEmpty();
        }
    }

    public Visibility IsEmpty =>
        IsLoaded && Profiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    public Visibility IsBusy => IsLoaded ? Visibility.Collapsed : Visibility.Visible;

    [RelayCommand]
    private async Task SaveCurrentAsync()
    {
        var profile = await _profileService.CaptureCurrentAsync($"Profile {Profiles.Count + 1}");
        await _profileService.SaveProfileAsync(profile);
        OnPropertyChanged(nameof(IsEmpty));
    }

    public DisplayApplyResult LastApplyResult { get; private set; }

    private bool _isApplying;

    /// <summary>An apply drives hardware for several seconds. Without this the window
    /// just sits there and the user clicks Apply again.
    /// See .claude/rules/80-ui-responsiveness.md.</summary>
    public Visibility IsApplying => _isApplying ? Visibility.Visible : Visibility.Collapsed;

    private void SetApplying(bool value)
    {
        if (_isApplying == value) return;
        _isApplying = value;
        OnPropertyChanged(nameof(IsApplying));
    }

    [RelayCommand]
    private async Task ApplyProfileAsync(string profileId)
    {
        SetApplying(true);
        try
        {
            // ApplyWorkFinished lowers it earlier, before the confirmation countdown.
            // This finally is the backstop for the paths that never raise it.
            LastApplyResult = await _profileService.ApplyProfileAsync(profileId);
        }
        finally
        {
            SetApplying(false);
        }
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
        OnPropertyChanged(nameof(IsBusy));
    }

    private async Task LoadAsync()
    {
        try
        {
            await _profileService.LoadAllAsync();
        }
        finally
        {
            // Even a failed load has finished loading. Leaving the spinner up forever
            // is worse than showing the empty state.
            IsLoaded = true;
        }

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
