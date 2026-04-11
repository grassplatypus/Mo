using Microsoft.Windows.ApplicationModel.Resources;

namespace Mo.Helpers;

public static class ResourceHelper
{
    private static readonly ResourceLoader _loader = new();

    public static string GetString(string key)
    {
        try
        {
            return _loader.GetString(key);
        }
        catch
        {
            return key;
        }
    }

    public static string GetString(string key, params object[] args)
    {
        var template = GetString(key);
        try
        {
            return string.Format(template, args);
        }
        catch
        {
            return template;
        }
    }
}
