using Microsoft.Win32;

namespace Mo.Services;

/// <summary>
/// Removes everything Mo writes outside its own install folder.
/// </summary>
/// <remarks>
/// Neither of Mo's distribution shapes cleans up on its own: the ZIP build has no
/// installer at all, and while MSIX deletes its own LocalFolder, an unpackaged run on
/// the same machine writes to <c>%LOCALAPPDATA%\Mo</c>, which MSIX knows nothing about.
/// The HKCU Run entry survives both. Deleting the app folder therefore used to leave
/// profiles, settings, logs and an auto-start entry that relaunched a program the user
/// had already removed.
///
/// Exposed two ways: Settings → About → "Remove Mo's data", and <c>Mo.exe --cleanup</c>
/// so an uninstaller or script can call it without any UI.
/// </remarks>
public static class AppDataCleanup
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "Mo";

    public sealed record Result(List<string> Removed, List<string> Failed)
    {
        public bool AnythingRemoved => Removed.Count > 0;
    }

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

        // 1. Auto-start entry. Removed first: if the data delete fails halfway, the
        //    worst outcome should still not be a program that keeps relaunching itself.
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

        // 2. Profiles, settings and logs for unpackaged runs. The packaged LocalFolder
        //    is Windows' to remove and is deliberately left alone — deleting it here
        //    would wipe a still-installed MSIX copy's data.
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
            // boot.log is held open only briefly, but a log viewer or AV scanner can
            // still have a handle. Fall back to emptying what we can.
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
    /// <c>%LOCALAPPDATA%\Mo</c> — where unpackaged runs keep profiles, settings and
    /// logs. Resolved directly rather than through ApplicationData.Current, which for
    /// a packaged process would point at the container instead.
    /// </summary>
    public static string UnpackagedDataDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mo");
}
