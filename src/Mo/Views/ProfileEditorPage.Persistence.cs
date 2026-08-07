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

// Saving, unsaved-change detection and leaving the page.
public sealed partial class ProfileEditorPage
{
    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_profile == null) return;
        await SaveProfileAsync();
        _navigationService.GoBack();
    }

    // Pulls live values from text boxes / time pickers into _profile. The drag canvas,
    // toggles, and combos already mutate _profile on interaction, but plain TextBox edits
    // don't raise property-changed handlers — sync them here before save/snapshot.
    private void SyncProfileFromUi()
    {
        if (_profile == null) return;
        _profile.Name = ProfileNameBox.Text;
        _profile.Description = DescriptionBox.Text;
        if (_profile.Schedule != null)
        {
            _profile.Schedule.Days = GetSelectedDays();
            if (ScheduleTimePicker.Time != default)
                _profile.Schedule.Time = TimeOnly.FromTimeSpan(ScheduleTimePicker.Time);
        }
    }

    private async Task SaveProfileAsync()
    {
        if (_profile == null) return;
        SyncProfileFromUi();
        _profile.ModifiedAt = DateTime.UtcNow;
        await _profileService.SaveProfileAsync(_profile);
        _initialSnapshot = CaptureSnapshot();
    }

    private string CaptureSnapshot()
    {
        if (_profile == null) return string.Empty;
        try
        {
            // Clone ModifiedAt out so it doesn't flip the dirty bit on every save.
            var saved = _profile.ModifiedAt;
            _profile.ModifiedAt = default;
            var json = JsonSerializer.Serialize(_profile, MoJsonContext.Default.DisplayProfile);
            _profile.ModifiedAt = saved;
            return json;
        }
        catch { return string.Empty; }
    }

    private bool IsDirty()
    {
        if (_profile == null || string.IsNullOrEmpty(_initialSnapshot)) return false;
        SyncProfileFromUi();
        return CaptureSnapshot() != _initialSnapshot;
    }

    private async Task<bool> ConfirmExitAsync()
    {
        if (!IsDirty()) return true;

        var dialog = new ContentDialog
        {
            Title = ResourceHelper.GetString("UnsavedChangesTitle"),
            Content = ResourceHelper.GetString("UnsavedChangesContent"),
            PrimaryButtonText = ResourceHelper.GetString("Save"),
            SecondaryButtonText = ResourceHelper.GetString("Discard"),
            CloseButtonText = ResourceHelper.GetString("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot,
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            await SaveProfileAsync();
            return true;
        }
        return result == ContentDialogResult.Secondary; // Discard → exit; Cancel → stay
    }

    private async void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        if (await ConfirmExitAsync()) _navigationService.GoBack();
    }

    private async void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (await ConfirmExitAsync()) _navigationService.GoBack();
    }

    // --- Layout toolbar + available monitors ---
}
