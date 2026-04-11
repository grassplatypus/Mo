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

        // Apply localized strings
        NavProfilesItem.Content = ResourceHelper.GetString("NavProfiles");

        Loaded += ShellPage_Loaded;
    }

    private void ShellPage_Loaded(object sender, RoutedEventArgs e)
    {
        NavView.SelectedItem = NavView.MenuItems[0];
        _navigationService.NavigateTo(typeof(ProfileListPage));
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
