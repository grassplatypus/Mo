using Microsoft.UI.Xaml;

namespace Mo.Helpers;

public static class ThemeHelper
{
    public static void ApplyTheme(FrameworkElement root, string theme)
    {
        root.RequestedTheme = theme switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
    }
}
