using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Mo;

// Explicit Main so we can wrap process startup in try/catch and surface failures to
// Windows Event Viewer + a crash log file. The XAML-generated Main runs after
// XamlCheckProcessRequirements / WinRT init, by which point a corrupted PRI or
// settings file would have already killed the process with no diagnostic trail.
//
// Activated by <DefineConstants>DISABLE_XAML_GENERATED_MAIN</DefineConstants> in
// the .csproj; without that define WinUI3 source-generates its own Main and refuses
// to compile a second one.
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            // If a previous launch corrupted the user data files (settings.json,
            // profiles/*.json), they will throw deserialization exceptions during
            // service init and kill the process with no UI surface. Quarantine
            // bad files BEFORE handing control to WinUI so the next launch starts
            // clean. The bad files are renamed, never deleted, so the user can
            // recover manually.
            QuarantineCorruptUserData();

            global::WinRT.ComWrappersSupport.InitializeComWrappers();

            // Single-instance redirect. Without this every Start-menu / shell:AppsFolder
            // activation spawns a new Mo.exe; the first survives invisibly (start-minimized
            // + tray icon collision) and the user sees nothing happen. Must run BEFORE
            // Application.Start so secondary instances never spin up the dispatcher.
            var primary = Microsoft.Windows.AppLifecycle.AppInstance.FindOrRegisterForKey("Mo.SingleInstance");
            if (!primary.IsCurrent)
            {
                var activated = Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent().GetActivatedEventArgs();
                primary.RedirectActivationToAsync(activated).AsTask().GetAwaiter().GetResult();
                return 0;
            }

            Application.Start(p =>
            {
                var ctx = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
                System.Threading.SynchronizationContext.SetSynchronizationContext(ctx);
                _ = new App();
            });
            return 0;
        }
        catch (Exception ex)
        {
            WriteFatal(ex);
            return -1;
        }
    }

    private static void QuarantineCorruptUserData()
    {
        try
        {
            var local = GetLocalFolder();
            var settingsPath = Path.Combine(local, "settings.json");
            if (File.Exists(settingsPath))
            {
                try
                {
                    var json = File.ReadAllText(settingsPath);
                    System.Text.Json.JsonSerializer.Deserialize(json, Helpers.MoJsonContext.Default.AppSettings);
                }
                catch
                {
                    QuarantineFile(settingsPath);
                }
            }

            var profilesDir = Path.Combine(local, "profiles");
            if (Directory.Exists(profilesDir))
            {
                foreach (var file in Directory.EnumerateFiles(profilesDir, "*.json"))
                {
                    try
                    {
                        var json = File.ReadAllText(file);
                        System.Text.Json.JsonSerializer.Deserialize(json, Helpers.MoJsonContext.Default.DisplayProfile);
                    }
                    catch
                    {
                        QuarantineFile(file);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // Quarantine itself failing is non-fatal — log but proceed.
            WriteEventLog($"User-data quarantine failed: {ex}", EventLogEntryType.Warning);
        }
    }

    private static void QuarantineFile(string path)
    {
        var quarantinePath = path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
        try { File.Move(path, quarantinePath); }
        catch { try { File.Delete(path); } catch { } }
        WriteEventLog($"Quarantined corrupt file: {path} -> {quarantinePath}", EventLogEntryType.Warning);
    }

    private static string GetLocalFolder()
    {
        try { return Windows.Storage.ApplicationData.Current.LocalFolder.Path; }
        catch
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Mo");
        }
    }

    private static void WriteFatal(Exception ex)
    {
        var msg = $"Mo failed to start.\n\n{ex}\n\nOS: {Environment.OSVersion}\nCLR: {Environment.Version}\nTime: {DateTime.Now:O}";
        WriteEventLog(msg, EventLogEntryType.Error);
        WriteCrashFile(msg);
    }

    private static void WriteEventLog(string message, EventLogEntryType type)
    {
        try
        {
            // EventLog.WriteEntry auto-creates the source on first call when running
            // elevated; on standard user accounts it falls back to the Application
            // log under the generic ".NET Runtime" source — still visible.
            const string source = "Mo";
            try
            {
                if (!EventLog.SourceExists(source))
                    EventLog.CreateEventSource(source, "Application");
            }
            catch { /* Source registration needs admin once; ignore if denied. */ }

            EventLog.WriteEntry(EventLog.SourceExists(source) ? source : ".NET Runtime",
                message, type);
        }
        catch { /* Best-effort; never throw from logger. */ }
    }

    private static void WriteCrashFile(string message)
    {
        try
        {
            var dir = Path.Combine(GetLocalFolder(), "logs");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, $"startup_crash_{DateTime.Now:yyyyMMdd_HHmmss}.log");
            File.WriteAllText(file, message);
        }
        catch { }
    }
}
