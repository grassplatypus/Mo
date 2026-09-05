using System.Management;

namespace Mo.Helpers;

/// <summary>Panel size in inches, taken from the image size the EDID reports.</summary>
/// <remarks>Nothing in CCD carries it, so this is a WMI read keyed by the device
/// instance both sides happen to share.</remarks>
public static class MonitorPhysicalSize
{
    /// <summary>Diagonal inches per device instance. Empty when WMI has nothing, which
    /// is normal for a virtual display and for some laptop panels.</summary>
    public static Dictionary<string, double> ReadAll()
    {
        var sizes = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\wmi", "SELECT InstanceName, MaxHorizontalImageSize, MaxVerticalImageSize FROM WmiMonitorBasicDisplayParams");

            foreach (ManagementObject obj in searcher.Get())
            {
                var instance = obj["InstanceName"]?.ToString();
                if (string.IsNullOrEmpty(instance)) continue;

                // Centimetres, and a panel reporting 0 has simply not filled the field in.
                if (obj["MaxHorizontalImageSize"] is not byte cmH || cmH == 0) continue;
                if (obj["MaxVerticalImageSize"] is not byte cmV || cmV == 0) continue;

                var inches = Math.Sqrt(cmH * cmH + cmV * cmV) / 2.54;
                sizes[Normalize(instance)] = inches;
            }
        }
        catch { }

        return sizes;
    }

    /// <summary>Looks a monitor up by the device path CCD gave us.</summary>
    public static double? For(string devicePath, IReadOnlyDictionary<string, double> sizes)
    {
        if (string.IsNullOrEmpty(devicePath)) return null;
        return sizes.TryGetValue(Normalize(devicePath), out var inches) ? inches : null;
    }

    /// <summary>Reduces both spellings of the same device to one key. CCD gives
    /// <c>\\?\DISPLAY#GSM5B09#5&amp;1a2b&amp;0&amp;UID4353#{guid}</c> and WMI gives
    /// <c>DISPLAY\GSM5B09\5&amp;1a2b&amp;0&amp;UID4353_0</c>.</summary>
    private static string Normalize(string id)
    {
        var parts = id
            .Replace(@"\\?\", string.Empty)
            .Split(['#', '\\'], StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length < 3) return id;

        var instance = parts[2];
        int underscore = instance.LastIndexOf('_');
        if (underscore > 0) instance = instance[..underscore];

        return $"{parts[0]}|{parts[1]}|{instance}";
    }
}
