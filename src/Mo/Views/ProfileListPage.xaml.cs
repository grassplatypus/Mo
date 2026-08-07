using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Mo.Controls;
using Mo.Helpers;
using Mo.Models;
using Mo.Services;
using Mo.ViewModels;
using Windows.ApplicationModel.DataTransfer;

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
    public static readonly string ActiveKey = "ActiveProfile";
    public static readonly string MoreActionsKey = "MoreActions";

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
        EmptyCtaText.Text = ResourceHelper.GetString("SaveCurrent");

        // A Button whose Content is a panel (icon + label) does not hand its peer a
        // name, so these three announced themselves as an unnamed "button" despite
        // showing a caption. Name them from the same resource as the caption.
        AutomationProperties.SetName(NewLayoutBtn, NewLayoutText.Text);
        AutomationProperties.SetName(ImportBtn, ImportText.Text);
        AutomationProperties.SetName(SaveCurrentBtn, SaveCurrentText.Text);
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
        if (compat.ExtraMonitors.Count > 0 && profile.UnmatchedAction == UnmatchedMonitorAction.Disable)
            confirmContent.Children.Add(new InfoBar
            {
                Severity = InfoBarSeverity.Warning,
                Title = ResourceHelper.GetString("ExtraMonitorsWillDisable"),
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

        // The snapshot + countdown + revert now lives in ApplyGuardService, wired into
        // ProfileService.ApplyProfileAsync so every trigger gets it — not just this
        // button. The previous version here also round-tripped a throwaway "_revert"
        // profile through disk, which briefly published it to the profile list, the
        // tray menu and the hotkey registration.
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
            var json = JsonSerializer.Serialize(profile, MoJsonContext.Default.DisplayProfile);
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
            var profile = JsonSerializer.Deserialize(json, MoJsonContext.Default.DisplayProfile);
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

    public static Visibility HasText(string? value)
        => string.IsNullOrWhiteSpace(value) ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility BoolToVisibility(bool value)
        => value ? Visibility.Visible : Visibility.Collapsed;

    // ── Slot badge ──
    //
    // App.RegisterAllHotkeys binds <modifier>+0..9 to Profiles[0..9], so a profile's
    // position in the list *is* its shortcut. The badge states that rather than
    // decorating the card with an index.

    /// <summary>Slots exist for the first nine profiles only.</summary>
    public static Visibility SlotVisibility(int sortOrder)
        => sortOrder is >= 0 and < 9 ? Visibility.Visible : Visibility.Collapsed;

    public static string SlotNumber(int sortOrder) => (sortOrder + 1).ToString();

    public static string SlotTooltip(int sortOrder)
    {
        var modifier = TryGetSlotModifier();
        return modifier == null
            ? ResourceHelper.GetString("SlotNoShortcut")
            : ResourceHelper.GetString("SlotShortcut", $"{modifier}{sortOrder + 1}");
    }

    /// <summary>Formats the configured slot modifier as a "Ctrl+Alt+" prefix.</summary>
    private static string? TryGetSlotModifier()
    {
        try
        {
            var mod = App.Services.GetRequiredService<ISettingsService>().Settings.ProfileSlotModifier;
            if (mod == null) return null;

            var parts = new List<string>();
            if (mod.Ctrl) parts.Add("Ctrl");
            if (mod.Alt) parts.Add("Alt");
            if (mod.Shift) parts.Add("Shift");
            if (mod.Win) parts.Add("Win");
            return parts.Count == 0 ? null : string.Join("+", parts) + "+";
        }
        catch { return null; }
    }

    public static Brush CardBorder(bool isActive) => (Brush)Application.Current.Resources[
        isActive ? "AccentFillColorDefaultBrush" : "CardStrokeColorDefaultBrush"];

    public static Thickness CardBorderThickness(bool isActive) => new(isActive ? 2 : 1);

    /// <summary>"Updated 3 min ago" — derived at render time, never stored.</summary>
    public static string FormatModified(DateTime modifiedUtc)
        => ResourceHelper.GetString("ModifiedPrefix", RelativeTimeText.Format(modifiedUtc));

    /// <summary>
    /// The card's single caption line: monitor count, when it last changed, and the
    /// shortcut if one is assigned.
    /// </summary>
    /// <remarks>
    /// These were three separate elements. The count in particular restated in words
    /// what the plan view above it already draws, so they are folded into one quiet
    /// line and the drawing is left to carry the card.
    /// </remarks>
    public static string CardCaption(int monitorCount, DateTime modifiedUtc, HotkeyBinding? hotkey)
    {
        var parts = new List<string>
        {
            $"{monitorCount} {ResourceHelper.GetString("MonitorsSuffix")}",
            RelativeTimeText.Format(modifiedUtc),
        };

        if (hotkey != null) parts.Add(hotkey.ToString());

        return string.Join("  ·  ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
    }

    // ── Profile card menu ──
    //
    // The right-click menu and the "..." button menu are the same list of actions, and
    // were previously two identical 30-line MenuFlyout blocks in the DataTemplate.
    // Building it once here means the two can no longer drift apart.

    private void ProfileCard_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement card || card.DataContext is not DisplayProfile profile)
            return;

        // Rebuild rather than reuse: GridView recycles containers, so a cached flyout
        // would still be bound to whichever profile the container held before.
        card.ContextFlyout = BuildProfileMenu(profile);
    }

    private void MoreActionsButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement button) return;

        // Reuse the card's own flyout so the two entry points are literally the same
        // menu instance, not two that merely look alike.
        var card = FindCardRoot(button);
        var flyout = card?.ContextFlyout
                     ?? (button.DataContext is DisplayProfile p ? BuildProfileMenu(p) : null);

        flyout?.ShowAt(button);
    }

    private static FrameworkElement? FindCardRoot(DependencyObject start)
    {
        for (var node = VisualTreeHelper.GetParent(start); node != null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is FrameworkElement { ContextFlyout: not null } candidate)
                return candidate;
        }
        return null;
    }

    private MenuFlyout BuildProfileMenu(DisplayProfile profile)
    {
        var flyout = new MenuFlyout();

        flyout.Items.Add(MenuItem("Apply", "", profile.Id, ApplyButton_Click));
        flyout.Items.Add(MenuItem("SetHotkey", "", profile.Id, HotkeyButton_Click));
        flyout.Items.Add(MenuItem("Export", "", profile.Id, ExportButton_Click));
        flyout.Items.Add(new MenuFlyoutSeparator());

        // Reordering needs a path that is not a mouse gesture. Dragging a card competes
        // with clicking it to open the editor, and it offers nothing to a keyboard or
        // screen-reader user — while the order decides which profile each Ctrl+Alt+N
        // shortcut applies, so it has to be reachable by everyone.
        int index = ViewModel.Profiles.IndexOf(profile);

        var moveUp = MenuItem("MoveUp", "", profile.Id, MoveUp_Click);
        moveUp.IsEnabled = index > 0;
        flyout.Items.Add(moveUp);

        var moveDown = MenuItem("MoveDown", "", profile.Id, MoveDown_Click);
        moveDown.IsEnabled = index >= 0 && index < ViewModel.Profiles.Count - 1;
        flyout.Items.Add(moveDown);

        flyout.Items.Add(new MenuFlyoutSeparator());

        var autoSwitch = new ToggleMenuFlyoutItem
        {
            Text = ResourceHelper.GetString("AutoSwitch"),
            Icon = new FontIcon { Glyph = "" },
            IsChecked = profile.AutoSwitch,
            Tag = profile.Id,
        };
        autoSwitch.Click += AutoSwitchToggle_Click;
        flyout.Items.Add(autoSwitch);

        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(MenuItem("Rename", null, profile.Id, RenameButton_Click));
        flyout.Items.Add(MenuItem("Delete", "", profile.Id, DeleteButton_Click));

        return flyout;

        static MenuFlyoutItem MenuItem(string key, string? glyph, string tag, RoutedEventHandler onClick)
        {
            var item = new MenuFlyoutItem
            {
                Text = ResourceHelper.GetString(key),
                Tag = tag,
            };
            if (glyph != null) item.Icon = new FontIcon { Glyph = glyph };
            item.Click += onClick;
            return item;
        }
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e) => MoveProfile(sender, -1);
    private void MoveDown_Click(object sender, RoutedEventArgs e) => MoveProfile(sender, +1);

    private async void MoveProfile(object sender, int delta)
    {
        if ((sender as FrameworkElement)?.Tag is not string profileId) return;

        var profiles = App.Services.GetRequiredService<IProfileService>();
        var profile = profiles.Profiles.FirstOrDefault(p => p.Id == profileId);
        if (profile == null) return;

        int from = profiles.Profiles.IndexOf(profile);
        int to = from + delta;
        if (from < 0 || to < 0 || to >= profiles.Profiles.Count) return;

        // Move on the shared collection the grid is bound to, so the reorder is visible
        // immediately; PersistOrderAsync then writes the new SortOrder values.
        profiles.Profiles.Move(from, to);

        try
        {
            await profiles.PersistOrderAsync();
            // The slot shortcuts index into this list, so they have to be rebound.
            App.RegisterAllHotkeys();
        }
        catch (Exception ex) { Helpers.BootLog.WriteError("profile.move", ex); }
    }

    /// <summary>
    /// Persists the new order after a drag.
    /// </summary>
    /// <remarks>
    /// The GridView has already reordered the bound collection by this point — it is
    /// the same ObservableCollection the service owns — so all that is left is writing
    /// SortOrder back out and re-pointing the slot hotkeys, which index into this list.
    /// </remarks>
    private async void ProfileGrid_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        if (args.DropResult != DataPackageOperation.Move) return;

        try
        {
            await App.Services.GetRequiredService<IProfileService>().PersistOrderAsync();
            App.RegisterAllHotkeys();
        }
        catch (Exception ex) { Helpers.BootLog.WriteError("profile.reorder", ex); }
    }

    // ── Responsive card sizing ──

    /// <summary>Smallest width at which a card still reads well.</summary>
    private const double MinCardWidth = 260;

    /// <summary>
    /// Divides the row evenly between as many cards as fit, so they stretch to fill
    /// the window instead of leaving a growing empty gutter on the right. GridView's
    /// wrap grid sizes items from the template, which is why this has to be computed.
    /// </summary>
    private void ProfileGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (ProfileGrid.ItemsPanelRoot is not ItemsWrapGrid panel) return;

        // Matches the 12px right margin in the container style.
        const double gutter = 12;
        double available = e.NewSize.Width;
        if (available <= 0) return;

        int columns = Math.Max(1, (int)((available + gutter) / (MinCardWidth + gutter)));
        panel.ItemWidth = Math.Floor(available / columns) - gutter;
    }

}
