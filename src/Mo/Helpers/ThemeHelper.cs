using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using Windows.UI;

namespace Mo.Helpers;

public static class ThemeHelper
{
    /// <summary>Shows a dialog on the app's own theme.</summary>
    /// <remarks>A ContentDialog is hosted in a popup rooted at the XamlRoot rather than
    /// under the window's content, so a RequestedTheme set on that content never reaches
    /// it and the dialog follows the system while the app does not.</remarks>
    public static IAsyncOperation<ContentDialogResult> ShowThemedAsync(this ContentDialog dialog)
    {
        if ((dialog.XamlRoot?.Content as FrameworkElement)?.ActualTheme is { } theme)
            dialog.RequestedTheme = theme;

        return dialog.ShowAsync();
    }

    public static ElementTheme Parse(string theme) => theme switch
    {
        "Light" => ElementTheme.Light,
        "Dark" => ElementTheme.Dark,
        _ => ElementTheme.Default,
    };

    public static void ApplyTheme(FrameworkElement root, string theme)
    {
        root.RequestedTheme = Parse(theme);
    }

    /// <summary>Repaints the system-drawn caption buttons for the theme now in effect.
    /// They are the window's, not the content's, so a runtime switch to a theme opposite
    /// the system's leaves them the old colour: dark glyphs on a dark bar.</summary>
    public static void ApplyCaptionButtonColors(Microsoft.UI.Windowing.AppWindow? appWindow, ElementTheme actual)
    {
        if (appWindow?.TitleBar is not { } titleBar) return;

        bool dark = actual == ElementTheme.Dark;
        var foreground = dark ? Colors.White : Colors.Black;

        // Backgrounds stay transparent so Mica shows through; only the hover and pressed
        // states paint, and they use a low-alpha overlay of the foreground.
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        titleBar.ButtonForegroundColor = foreground;
        titleBar.ButtonHoverBackgroundColor = Overlay(foreground, 0x18);
        titleBar.ButtonHoverForegroundColor = foreground;
        titleBar.ButtonPressedBackgroundColor = Overlay(foreground, 0x28);
        titleBar.ButtonPressedForegroundColor = foreground;

        // An unfocused window dims its glyphs rather than hiding them.
        titleBar.ButtonInactiveForegroundColor = Overlay(foreground, 0x9B, opaqueOn: dark);
    }

    private static Color Overlay(Color of, byte alpha) =>
        Color.FromArgb(alpha, of.R, of.G, of.B);

    /// <summary>Caption colours take no alpha, so a dimmed glyph has to be mixed against
    /// the bar rather than drawn over it.</summary>
    private static Color Overlay(Color of, byte weight, bool opaqueOn)
    {
        byte ground = opaqueOn ? (byte)0 : (byte)255;
        byte Mix(byte c) => (byte)((c * weight + ground * (255 - weight)) / 255);
        return Color.FromArgb(255, Mix(of.R), Mix(of.G), Mix(of.B));
    }
}
