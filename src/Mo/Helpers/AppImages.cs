using Microsoft.UI.Xaml.Media.Imaging;

namespace Mo.Helpers;

/// <summary>Loads Mo's own icon for use in the UI.</summary>
/// <remarks>A PNG through `ms-appx:`, not the .ico. A filesystem path fails silently as
/// `E_NETWORK_ERROR`, and an .ico leaves the decoder to pick a frame: asking for a width
/// between two of them decodes the smaller and stretches it.</remarks>
public static class AppImages
{
    private const string IconUri = "ms-appx:///Assets/AppIcon.png";

    /// <summary>Mo's icon, decoded at the given width. The source is 256px square, so any
    /// width at or below that decodes cleanly. Pass the largest physical size it will be
    /// drawn at: a 44px slot reaches 132px at 300% scaling.</summary>
    public static BitmapImage AppIcon(int decodePixelWidth)
    {
        var image = new BitmapImage { DecodePixelWidth = decodePixelWidth };

        // Loading is asynchronous and silent on failure, so record which way it went.
        image.ImageFailed += (_, e) => BootLog.Write("appicon.failed", e.ErrorMessage);
        image.ImageOpened += (_, _) => BootLog.Write("appicon.loaded", $"{decodePixelWidth}px");

        image.UriSource = new Uri(IconUri);
        return image;
    }
}
