using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Mo.Controls;
using Mo.Helpers;
using Mo.Models;
using Mo.Services;
using Mo.ViewModels;

namespace Mo.Views;

public sealed partial class ProfileListPage : Page
{
    // Static resource keys for x:Bind in DataTemplate
    public static readonly string SetHotkeyKey = "SetHotkey";
    public static readonly string DeleteKey = "Delete";
    public static readonly string MonitorsKey = "MonitorsSuffix";
    public static readonly string ApplyKey = "Apply";
    public static readonly string ExportKey = "Export";
    public static readonly string AutoSwitchKey = "AutoSwitch";
    public static readonly string RenameKey = "Rename";

    public static string L(string key) => ResourceHelper.GetString(key);

    public ProfileListViewModel ViewModel { get; }

    public ProfileListPage()
    {
        ViewModel = App.Services.GetRequiredService<ProfileListViewModel>();
        InitializeComponent();
        ApplyLocalization();
    }

    private async void NewLayoutButton_Click(object sender, RoutedEventArgs e)
    {
        var displayService = App.Services.GetRequiredService<IDisplayService>();
        var monitors = displayService.GetCurrentConfiguration();
        var profile = new DisplayProfile
        {
            Name = $"Layout {ViewModel.Profiles.Count + 1}",
            Description = $"{monitors.Count} monitor(s) — {DateTime.Now:g}",
            Monitors = monitors,
        };

        var profileService = App.Services.GetRequiredService<IProfileService>();
        await profileService.SaveProfileAsync(profile);
        ViewModel.RefreshIsEmpty();

        var nav = App.Services.GetRequiredService<INavigationService>();
        nav.NavigateTo(typeof(ProfileEditorPage), profile);
    }

    private void ApplyLocalization()
    {
        TitleText.Text = ResourceHelper.GetString("ProfilesTitle");
        SaveCurrentText.Text = ResourceHelper.GetString("SaveCurrent");
        NewLayoutText.Text = ResourceHelper.GetString("NewLayout");
        ImportText.Text = ResourceHelper.GetString("Import");
        EmptyTitleText.Text = ResourceHelper.GetString("EmptyTitle");
        EmptyDescText.Text = ResourceHelper.GetString("EmptyDescription");
    }

    private async void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        var profileId = (sender as FrameworkElement)?.Tag as string;
        if (profileId == null) return;

        var displayService = App.Services.GetRequiredService<IDisplayService>();
        var profile = ViewModel.Profiles.FirstOrDefault(p => p.Id == profileId);
        if (profile == null) return;

        // Pre-apply confirmation
        var compat = displayService.CheckCompatibility(profile);
        var confirmContent = new StackPanel { Spacing = 8 };

        // Profile summary
        confirmContent.Children.Add(new TextBlock
        {
            Text = $"{profile.Monitors.Count(m => m.IsEnabled)} {ResourceHelper.GetString("MonitorsSuffix")}",
            Opacity = 0.7,
        });

        // Warnings if any mismatch
        if (compat.MissingMonitors.Count > 0)
            confirmContent.Children.Add(new InfoBar
            {
                Severity = InfoBarSeverity.Error,
                Title = ResourceHelper.GetString("MissingMonitors"),
                Message = string.Join(", ", compat.MissingMonitors),
                IsOpen = true, IsClosable = false,
            });
        if (compat.Warnings.Count > 0)
            confirmContent.Children.Add(new InfoBar
            {
                Severity = InfoBarSeverity.Informational,
                Message = string.Join("\n", compat.Warnings),
                IsOpen = true, IsClosable = false,
            });
        if (compat.ExtraMonitors.Count > 0)
            confirmContent.Children.Add(new InfoBar
            {
                Severity = InfoBarSeverity.Warning,
                Title = ResourceHelper.GetString("ExtraMonitors"),
                Message = string.Join(", ", compat.ExtraMonitors),
                IsOpen = true, IsClosable = false,
            });

        var confirmDialog = new ContentDialog
        {
            Title = $"{ResourceHelper.GetString("Apply")} \"{profile.Name}\"?",
            Content = confirmContent,
            PrimaryButtonText = ResourceHelper.GetString("Apply"),
            CloseButtonText = ResourceHelper.GetString("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot,
        };
        if (await confirmDialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        // Capture the current configuration before applying so we can revert
        var previousConfig = displayService.GetCurrentConfiguration();

        await ViewModel.ApplyProfileCommand.ExecuteAsync(profileId);

        var result = ViewModel.LastApplyResult;

        if (result is DisplayApplyResult.Failed or DisplayApplyResult.ValidationError)
        {
            var errorMsg = result == DisplayApplyResult.ValidationError
                ? ResourceHelper.GetString("ApplyValidationError")
                : ResourceHelper.GetString("ApplyFailed");

            var errorDialog = new ContentDialog
            {
                Title = ResourceHelper.GetString("ApplyErrorTitle"),
                Content = errorMsg,
                CloseButtonText = ResourceHelper.GetString("OK"),
                XamlRoot = this.XamlRoot,
            };
            await errorDialog.ShowAsync();
            return;
        }

        // Show the revert countdown dialog
        var revertDialog = new ApplyConfirmationDialog
        {
            XamlRoot = this.XamlRoot,
        };

        var confirmed = await revertDialog.ShowAndWaitAsync();

        if (!confirmed)
        {
            // Revert: build a temporary profile from the previous config and apply it
            var revertProfile = new DisplayProfile
            {
                Name = "_revert",
                Monitors = previousConfig,
            };
            var profileService = App.Services.GetRequiredService<IProfileService>();
            await profileService.SaveProfileAsync(revertProfile);
            await profileService.ApplyProfileAsync(revertProfile.Id);
            await profileService.DeleteProfileAsync(revertProfile.Id);
        }
    }

    private async void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        var profileId = (sender as FrameworkElement)?.Tag as string;
        if (profileId == null) return;

        var profile = ViewModel.Profiles.FirstOrDefault(p => p.Id == profileId);
        if (profile == null) return;

        var dialog = new ContentDialog
        {
            Title = ResourceHelper.GetString("DeleteProfileTitle"),
            Content = ResourceHelper.GetString("DeleteProfileConfirm", profile.Name),
            PrimaryButtonText = ResourceHelper.GetString("Delete"),
            CloseButtonText = ResourceHelper.GetString("Cancel"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.XamlRoot,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            var hotkeyService = App.Services.GetRequiredService<IHotkeyService>();
            hotkeyService.UnregisterProfileHotkey(profileId);
            await ViewModel.DeleteProfileCommand.ExecuteAsync(profileId);
        }
    }

    private async void HotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        var profileId = (sender as FrameworkElement)?.Tag as string;
        if (profileId == null) return;

        var profile = ViewModel.Profiles.FirstOrDefault(p => p.Id == profileId);
        if (profile == null) return;

        var picker = new HotkeyPicker();
        picker.SetBinding(profile.Hotkey);

        var dialog = new ContentDialog
        {
            Title = ResourceHelper.GetString("HotkeyDialogTitle", profile.Name),
            Content = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    new TextBlock
                    {
                        Text = ResourceHelper.GetString("HotkeyDialogDescription"),
                        TextWrapping = TextWrapping.Wrap,
                        Opacity = 0.7,
                    },
                    picker,
                }
            },
            PrimaryButtonText = ResourceHelper.GetString("Save"),
            CloseButtonText = ResourceHelper.GetString("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            profile.Hotkey = picker.CurrentBinding;
            profile.ModifiedAt = DateTime.UtcNow;

            var profileService = App.Services.GetRequiredService<IProfileService>();
            await profileService.SaveProfileAsync(profile);

            var hotkeyService = App.Services.GetRequiredService<IHotkeyService>();
            if (profile.Hotkey != null)
                hotkeyService.RegisterProfileHotkey(profile.Id, profile.Hotkey);
            else
                hotkeyService.UnregisterProfileHotkey(profile.Id);
        }
    }

    private async void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        var profileId = (sender as FrameworkElement)?.Tag as string;
        if (profileId == null) return;

        var profile = ViewModel.Profiles.FirstOrDefault(p => p.Id == profileId);
        if (profile == null) return;

        var picker = new Windows.Storage.Pickers.FileSavePicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, Mo.Helpers.WindowHelper.GetHwnd(App.MainWindow));
        picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary;
        picker.SuggestedFileName = profile.Name;
        picker.FileTypeChoices.Add(ResourceHelper.GetString("ExportProfileType"), new List<string> { ".moprofile" });

        var file = await picker.PickSaveFileAsync();
        if (file != null)
        {
            var json = JsonSerializer.Serialize(profile, JsonHelper.Options);
            await Windows.Storage.FileIO.WriteTextAsync(file, json);
        }
    }

    private async void RenameButton_Click(object sender, RoutedEventArgs e)
    {
        var profileId = (sender as FrameworkElement)?.Tag as string;
        if (profileId == null) return;

        var profile = ViewModel.Profiles.FirstOrDefault(p => p.Id == profileId);
        if (profile == null) return;

        var nameBox = new TextBox
        {
            Text = profile.Name,
            PlaceholderText = ResourceHelper.GetString("NewName"),
            SelectionStart = 0,
            SelectionLength = profile.Name.Length,
        };

        var dialog = new ContentDialog
        {
            Title = ResourceHelper.GetString("RenameProfileTitle"),
            Content = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    new TextBlock
                    {
                        Text = ResourceHelper.GetString("RenameProfilePrompt", profile.Name),
                        TextWrapping = TextWrapping.Wrap,
                        Opacity = 0.7,
                    },
                    nameBox,
                }
            },
            PrimaryButtonText = ResourceHelper.GetString("Save"),
            CloseButtonText = ResourceHelper.GetString("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            var newName = nameBox.Text?.Trim();
            if (!string.IsNullOrEmpty(newName))
            {
                profile.Name = newName;
                profile.ModifiedAt = DateTime.UtcNow;

                var profileService = App.Services.GetRequiredService<IProfileService>();
                await profileService.SaveProfileAsync(profile);
            }
        }
    }

    private async void AutoSwitchToggle_Click(object sender, RoutedEventArgs e)
    {
        var profileId = (sender as FrameworkElement)?.Tag as string;
        if (profileId == null) return;

        var profile = ViewModel.Profiles.FirstOrDefault(p => p.Id == profileId);
        if (profile == null) return;

        // The ToggleMenuFlyoutItem already toggled its IsChecked, so we read the new state
        if (sender is ToggleMenuFlyoutItem toggle)
        {
            profile.AutoSwitch = toggle.IsChecked;
        }
        else
        {
            profile.AutoSwitch = !profile.AutoSwitch;
        }

        profile.ModifiedAt = DateTime.UtcNow;

        var profileService = App.Services.GetRequiredService<IProfileService>();
        await profileService.SaveProfileAsync(profile);
    }

    private async void SaveCurrentButton_Click(object sender, RoutedEventArgs e)
    {
        // Build options dialog
        var nameBox = new TextBox
        {
            Text = $"Profile {ViewModel.Profiles.Count + 1}",
            PlaceholderText = ResourceHelper.GetString("ProfileNamePlaceholder"),
        };
        var chkAudio = new CheckBox { Content = ResourceHelper.GetString("AudioDevice"), IsChecked = false };
        var chkWallpaper = new CheckBox { Content = ResourceHelper.GetString("Wallpaper"), IsChecked = false };
        var chkNightLight = new CheckBox { Content = ResourceHelper.GetString("NightLight"), IsChecked = false };
        var chkAutoSwitch = new CheckBox { Content = ResourceHelper.GetString("AutoSwitch"), IsChecked = false };

        // Detect live wallpaper provider
        CheckBox? chkLiveWallpaper = null;
        try
        {
            var liveWpService = App.Services.GetRequiredService<ILiveWallpaperService>();
            var provider = liveWpService.DetectProvider();
            if (provider != Models.LiveWallpaperProvider.None)
            {
                var label = provider == Models.LiveWallpaperProvider.WallpaperEngine
                    ? "Wallpaper Engine" : "Lively Wallpaper";
                chkLiveWallpaper = new CheckBox { Content = $"{ResourceHelper.GetString("LiveWallpaper")} ({label})", IsChecked = false };
            }
        }
        catch { }

        var chkColor = new CheckBox { Content = ResourceHelper.GetString("MonitorColor"), IsChecked = true };

        var optionsPanel = new StackPanel { Spacing = 10 };
        optionsPanel.Children.Add(nameBox);
        optionsPanel.Children.Add(chkAudio);
        optionsPanel.Children.Add(chkWallpaper);
        if (chkLiveWallpaper != null) optionsPanel.Children.Add(chkLiveWallpaper);
        optionsPanel.Children.Add(chkColor);
        optionsPanel.Children.Add(chkNightLight);
        optionsPanel.Children.Add(chkAutoSwitch);

        var dialog = new ContentDialog
        {
            Title = ResourceHelper.GetString("SaveCurrent"),
            Content = optionsPanel,
            PrimaryButtonText = ResourceHelper.GetString("Save"),
            CloseButtonText = ResourceHelper.GetString("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var profileName = string.IsNullOrWhiteSpace(nameBox.Text) ? $"Profile {ViewModel.Profiles.Count + 1}" : nameBox.Text.Trim();

        // Show loading indicator
        var loadingDialog = new ContentDialog
        {
            Content = new StackPanel
            {
                Spacing = 16,
                HorizontalAlignment = HorizontalAlignment.Center,
                Children =
                {
                    new ProgressRing { IsActive = true, Width = 40, Height = 40 },
                    new TextBlock { Text = ResourceHelper.GetString("CapturingProfile"), HorizontalAlignment = HorizontalAlignment.Center },
                }
            },
            XamlRoot = this.XamlRoot,
        };
        _ = loadingDialog.ShowAsync();

        var profileService = App.Services.GetRequiredService<IProfileService>();
        var profile = await profileService.CaptureCurrentAsync(profileName);

        loadingDialog.Hide();

        // Selectively clear unwanted data
        if (chkAudio.IsChecked != true)
        {
            profile.AudioDeviceId = null;
            profile.AudioDeviceName = null;
        }
        if (chkWallpaper.IsChecked != true)
        {
            profile.WallpaperPath = null;
        }
        if (chkNightLight.IsChecked != true)
        {
            profile.NightLightEnabled = null;
        }
        profile.AutoSwitch = chkAutoSwitch.IsChecked == true;
        if (chkColor.IsChecked != true)
        {
            foreach (var m in profile.Monitors)
                m.ColorSettings = null;
        }
        if (chkLiveWallpaper?.IsChecked == true)
        {
            try
            {
                var liveWpService = App.Services.GetRequiredService<ILiveWallpaperService>();
                profile.LiveWallpaper = liveWpService.CaptureCurrentConfig();
            }
            catch { }
        }
        else
        {
            profile.LiveWallpaper = null;
        }

        await profileService.SaveProfileAsync(profile);
        ViewModel.RefreshIsEmpty();
    }

    private async void ImportButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, Mo.Helpers.WindowHelper.GetHwnd(App.MainWindow));
        picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary;
        picker.FileTypeFilter.Add(".moprofile");

        var file = await picker.PickSingleFileAsync();
        if (file != null)
        {
            var json = await Windows.Storage.FileIO.ReadTextAsync(file);
            var profile = JsonSerializer.Deserialize<DisplayProfile>(json, JsonHelper.Options);
            if (profile != null)
            {
                // Assign a new ID to avoid collisions with existing profiles
                profile.Id = Guid.NewGuid().ToString("N");
                profile.ModifiedAt = DateTime.UtcNow;

                var profileService = App.Services.GetRequiredService<IProfileService>();
                await profileService.SaveProfileAsync(profile);
                ViewModel.RefreshIsEmpty();
            }
        }
    }

    private void ProfileGrid_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is DisplayProfile profile)
        {
            var nav = App.Services.GetRequiredService<INavigationService>();
            nav.NavigateTo(typeof(ProfileEditorPage), profile);
        }
    }

    public static Visibility HasHotkey(HotkeyBinding? hotkey)
        => hotkey != null ? Visibility.Visible : Visibility.Collapsed;

    public static string FormatHotkey(HotkeyBinding? hotkey)
        => hotkey?.ToString() ?? "";
}
