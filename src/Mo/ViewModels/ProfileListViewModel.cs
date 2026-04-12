using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
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
        _ = LoadAsync();
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
        OnPropertyChanged(nameof(IsEmpty));
    }
}
