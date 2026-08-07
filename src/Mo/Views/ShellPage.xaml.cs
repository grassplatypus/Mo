using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Mo.Helpers;
using Mo.Services;

namespace Mo.Views;

public sealed partial class ShellPage : Page
{
    private readonly INavigationService _navigationService;

    public ShellPage()
    {
        InitializeComponent();

        _navigationService = App.Services.GetRequiredService<INavigationService>();
        _navigationService.Frame = ContentFrame;

        // Apply localized strings. The ToolTip matters in LeftCompact mode, where the
        // rail is icons-only until the user expands it.
        NavProfilesItem.Content = ResourceHelper.GetString("NavProfiles");
        NavDisplayTuningItem.Content = ResourceHelper.GetString("NavDisplayTuning");
        ToolTipService.SetToolTip(NavProfilesItem, NavProfilesItem.Content);
        ToolTipService.SetToolTip(NavDisplayTuningItem, NavDisplayTuningItem.Content);

        // The back button was permanently greyed out: NavigationView.IsBackEnabled
        // defaults to false and nothing ever set it, so the arrow rendered but could
        // never be pressed — leaving the profile editor with no way back to the list
        // except the nav rail. Keep it in step with the frame's own history.
        ContentFrame.Navigated += (_, _) =>
        {
            NavView.IsBackEnabled = ContentFrame.CanGoBack;
            SyncSelectedNavItem();
        };

        Loaded += ShellPage_Loaded;
    }

    private void ShellPage_Loaded(object sender, RoutedEventArgs e)
    {
        NavView.SelectedItem = NavView.MenuItems[0];
        _navigationService.NavigateTo(typeof(ProfileListPage));
    }

    /// <summary>
    /// Keeps the rail highlight on the section the frame is actually showing.
    /// Without this, going back from the editor to the list leaves the highlight on
    /// whatever was clicked last — or on nothing at all after visiting Settings.
    /// </summary>
    private void SyncSelectedNavItem()
    {
        var current = ContentFrame.CurrentSourcePageType;

        if (current == typeof(SettingsPage))
        {
            NavView.SelectedItem = NavView.SettingsItem;
            return;
        }

        // The editor is a detail view of the profile list, so it keeps that section lit.
        if (current == typeof(ProfileListPage) || current == typeof(ProfileEditorPage))
            NavView.SelectedItem = NavProfilesItem;
        else if (current == typeof(DisplayTuningPage))
            NavView.SelectedItem = NavDisplayTuningItem;
    }

    private void NavView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.IsSettingsInvoked)
        {
            _navigationService.NavigateTo(typeof(SettingsPage));
            return;
        }

        if (args.InvokedItemContainer is NavigationViewItem item)
        {
            var pageType = item.Tag?.ToString() switch
            {
                "Profiles" => typeof(ProfileListPage),
                "DisplayTuning" => typeof(DisplayTuningPage),
                _ => typeof(ProfileListPage),
            };
            _navigationService.NavigateTo(pageType);
        }
    }

    private void NavView_BackRequested(NavigationView sender, NavigationViewBackRequestedEventArgs args)
    {
        _navigationService.GoBack();
    }
}
