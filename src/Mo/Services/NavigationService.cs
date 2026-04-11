using System;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;

namespace Mo.Services;

public sealed class NavigationService : INavigationService
{
    public Frame? Frame { get; set; }

    public bool CanGoBack => Frame?.CanGoBack ?? false;

    public void NavigateTo(Type pageType, object? parameter = null)
    {
        Frame?.Navigate(pageType, parameter, new SlideNavigationTransitionInfo
        {
            Effect = SlideNavigationTransitionEffect.FromRight
        });
    }

    public void GoBack()
    {
        if (Frame?.CanGoBack == true)
        {
            Frame.GoBack();
        }
    }
}
