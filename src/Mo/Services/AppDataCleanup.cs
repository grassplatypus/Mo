using Microsoft.Win32;

namespace Mo.Services;

// Removes everything Mo writes outside its install folder: the ZIP build has no
// installer, and MSIX only knows about its own LocalFolder. Reachable from Settings
// and from `Mo.exe --cleanup` for uninstall scripts.
public static class AppDataCleanup
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "Mo";

    public sealed record Result(List<string> Removed, List<string> Failed);

    /// <summary>Describes what a cleanup would delete, without deleting it.</summary>
    public static List<string> Preview()
    {
        var items = new List<string>();

        var dir = UnpackagedDataDirectory();
        if (Directory.Exists(dir)) items.Add(dir);

        if (HasRunEntry()) items.Add($@"HKCU\{RunKeyPath}\{RunValueName}");

        return items;
    }

    public static Result Run()
    {
        var removed = new List<string>();
        var failed = new List<string>();

        // Auto-start first: a half-failed cleanup must not leave a program that keeps
        // relaunching itself.
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key?.GetValue(RunValueName) != null)
            {
                key.DeleteValue(RunValueName, throwOnMissingValue: false);
                removed.Add($@"HKCU\{RunKeyPath}\{RunValueName}");
            }
        }
        catch (Exception ex) { failed.Add($"Run entry: {ex.Message}"); }

        // The packaged LocalFolder is Windows' to remove — deleting it here would wipe
        // a still-installed MSIX copy's data.
        var dir = UnpackagedDataDirectory();
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
                removed.Add(dir);
            }
        }
        catch (Exception ex)
        {
            // A log viewer or AV scanner may hold a handle; empty what we can.
            failed.Add($"{dir}: {ex.Message}");
            TryDeleteContents(dir, removed, failed);
        }

        return new Result(removed, failed);
    }

    private static void TryDeleteContents(string dir, List<string> removed, List<string> failed)
    {
        if (!Directory.Exists(dir)) return;

        foreach (var file in SafeEnumerate(dir))
        {
            try { File.Delete(file); removed.Add(file); }
            catch (Exception ex) { failed.Add($"{file}: {ex.Message}"); }
        }
    }

    private static IEnumerable<string> SafeEnumerate(string dir)
    {
        try { return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).ToList(); }
        catch { return []; }
    }

    private static bool HasRunEntry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(RunValueName) != null;
        }
        catch { return false; }
    }

    /// <summary>
    /// %LOCALAPPDATA%\Mo. Resolved directly, not via ApplicationData.Current, which
    /// for a packaged process points at the container.
    /// </summary>
    public static string UnpackagedDataDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mo");
}
