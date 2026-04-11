using System;
using Microsoft.UI.Xaml.Controls;

namespace Mo.Services;

public interface INavigationService
{
    Frame? Frame { get; set; }
    bool CanGoBack { get; }
    void NavigateTo(Type pageType, object? parameter = null);
    void GoBack();
}
