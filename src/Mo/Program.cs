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
        Helpers.BootLog.BeginSession(typeof(Program).Assembly.GetName().Version?.ToString() ?? "?");

        try
        {
            // If a previous launch corrupted the user data files (settings.json,
            // profiles/*.json), they will throw deserialization exceptions during
            // service init and kill the process with no UI surface. Quarantine
            // bad files BEFORE handing control to WinUI so the next launch starts
            // clean. The bad files are renamed, never deleted, so the user can
            // recover manually.
            Helpers.BootLog.Write("quarantine.begin");
            QuarantineCorruptUserData();
            Helpers.BootLog.Write("quarantine.end");

            global::WinRT.ComWrappersSupport.InitializeComWrappers();
            Helpers.BootLog.Write("comwrappers.ok");

            // Single-instance redirect. Without this every Start-menu / shell:AppsFolder
            // activation spawns a new Mo.exe; the first survives invisibly (start-minimized
            // + tray icon collision) and the user sees nothing happen. Must run BEFORE
            // Application.Start so secondary instances never spin up the dispatcher.
            var primary = Microsoft.Windows.AppLifecycle.AppInstance.FindOrRegisterForKey("Mo.SingleInstance");
            Helpers.BootLog.Write("singleinstance.registered", $"IsCurrent={primary.IsCurrent}");
            if (!primary.IsCurrent)
            {
                if (RedirectToPrimary(primary))
                    return 0;

                // Primary is wedged (hung UI thread, windowless zombie, or blocked by
                // an elevation mismatch). Rather than exiting silently — which is what
                // made Mo look permanently "un-launchable" — offer to end it and take
                // over as the primary instance ourselves.
                Helpers.BootLog.Write("redirect.timeout", "prompting user to recover");
                if (!OfferToKillWedgedPrimary())
                    return 0;
                Helpers.BootLog.Write("redirect.recovered", "continuing as primary");
            }

            Helpers.BootLog.Write("application.start.begin");
            Application.Start(p =>
            {
                var ctx = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
                System.Threading.SynchronizationContext.SetSynchronizationContext(ctx);
                Helpers.BootLog.Write("app.ctor.begin");
                _ = new App();
                Helpers.BootLog.Write("app.ctor.end");
            });
            Helpers.BootLog.Write("application.start.returned");
            return 0;
        }
        catch (Exception ex)
        {
            Helpers.BootLog.WriteError("main", ex);
            WriteFatal(ex);
            return -1;
        }
    }

    // ── Single-instance redirect ──

    [System.Runtime.InteropServices.DllImport("ole32.dll")]
    private static extern int CoWaitForMultipleObjects(uint flags, uint timeout, ulong count,
        nint[] handles, out uint index);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern nint CreateEvent(nint attrs, bool manualReset, bool initialState, string? name);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool SetEvent(nint handle);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool CloseHandle(nint handle);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int MessageBoxW(nint hWnd, string text, string caption, uint type);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);

    /// <summary>
    /// Hands this activation to the already-running instance.
    /// Returns false if the primary did not accept it within the timeout.
    /// </summary>
    /// <remarks>
    /// <c>RedirectActivationToAsync</c> must NOT be waited on with
    /// <c>.GetAwaiter().GetResult()</c>: Main is <c>[STAThread]</c>, the call completes
    /// via a COM cross-apartment callback, and a plain block starves the message pump
    /// that callback needs — the secondary process then hangs forever, invisible, and
    /// each further launch attempt piles up another hung Mo.exe. The redirect therefore
    /// runs on a thread-pool thread while this thread waits inside
    /// <c>CoWaitForMultipleObjects</c>, which keeps pumping COM messages. This is the
    /// pattern from Microsoft's own AppLifecycle instancing sample.
    /// </remarks>
    private static bool RedirectToPrimary(Microsoft.Windows.AppLifecycle.AppInstance primary)
    {
        const uint CWMO_DEFAULT = 0;
        const uint WAIT_OBJECT_0 = 0;
        // Generous but finite: a healthy primary answers in milliseconds. Anything
        // past this is wedged, and the user is already staring at a desktop where
        // "nothing happened" when they double-clicked.
        const uint TimeoutMs = 5000;

        nint doneEvent = 0;
        try
        {
            var activated = Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent().GetActivatedEventArgs();
            Helpers.BootLog.Write("redirect.begin", $"args={(activated == null ? "null" : activated.Kind.ToString())}");

            // We were launched by the user, so we hold the foreground-activation right;
            // hand it to the primary. Without this, the primary's SetForegroundWindow is
            // demoted to a taskbar flash and the user still sees "nothing happened".
            const int ASFW_ANY = -1;
            try { AllowSetForegroundWindow(ASFW_ANY); } catch { }

            doneEvent = CreateEvent(0, true, false, null);
            if (doneEvent == 0)
            {
                // Without an event to wait on we cannot pump safely; fail over to the
                // recovery prompt rather than risking the blocking wait.
                Helpers.BootLog.Write("redirect.createevent.failed");
                return false;
            }

            Exception? redirectError = null;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { primary.RedirectActivationToAsync(activated).AsTask().GetAwaiter().GetResult(); }
                catch (Exception ex) { redirectError = ex; }
                finally { SetEvent(doneEvent); }
            });

            int hr = CoWaitForMultipleObjects(CWMO_DEFAULT, TimeoutMs, 1, [doneEvent], out uint index);
            bool signalled = hr == 0 && index == WAIT_OBJECT_0;

            if (!signalled)
            {
                Helpers.BootLog.Write("redirect.wait.timeout", $"hr=0x{hr:X8}");
                return false;
            }
            if (redirectError != null)
            {
                // Most common cause: the primary runs at a different integrity level
                // (one launched normally, the other "Run as administrator"), so UIPI
                // blocks the cross-process COM call.
                Helpers.BootLog.WriteError("redirect", redirectError);
                return false;
            }

            Helpers.BootLog.Write("redirect.end");
            return true;
        }
        catch (Exception ex)
        {
            Helpers.BootLog.WriteError("redirect.outer", ex);
            return false;
        }
        finally
        {
            if (doneEvent != 0) CloseHandle(doneEvent);
        }
    }

    /// <summary>
    /// Asks the user whether to terminate an unresponsive Mo instance so this launch
    /// can proceed. Returns true if the field is now clear.
    /// </summary>
    private static bool OfferToKillWedgedPrimary()
    {
        const uint MB_YESNO = 0x4, MB_ICONWARNING = 0x30, MB_SETFOREGROUND = 0x10000;
        const int IDYES = 6;

        var others = Process.GetProcessesByName("Mo")
            .Where(p => p.Id != Environment.ProcessId)
            .ToArray();

        var elevationNote = BootLogElevationHint(others);
        var answer = MessageBoxW(0,
            "Mo가 이미 실행 중이지만 응답하지 않습니다.\n\n" +
            "기존 프로세스를 종료하고 새로 시작할까요?\n" +
            "(저장된 프로필과 설정은 그대로 유지됩니다.)" + elevationNote,
            "Mo", MB_YESNO | MB_ICONWARNING | MB_SETFOREGROUND);

        if (answer != IDYES)
        {
            foreach (var p in others) p.Dispose();
            return false;
        }

        foreach (var p in others)
        {
            try { p.Kill(entireProcessTree: true); p.WaitForExit(3000); }
            catch (Exception ex) { WriteEventLog($"Could not end wedged Mo process {p.Id}: {ex.Message}", EventLogEntryType.Warning); }
            finally { p.Dispose(); }
        }
        return true;
    }

    // A mixed-elevation pair is the one case Kill() may not be able to resolve, so say so.
    private static string BootLogElevationHint(Process[] others)
    {
        if (others.Length == 0)
            return "\n\n※ 실행 중인 Mo 프로세스를 찾지 못했습니다. 관리자 권한으로 실행된 인스턴스일 수 있습니다.";
        if (!Helpers.BootLog.IsElevated())
            return "\n\n※ 종료에 실패하면 Mo를 관리자 권한으로 실행해 보세요.";
        return string.Empty;
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
        var logPath = WriteCrashFile(msg);

        // A startup failure with log-file-only reporting is indistinguishable from
        // "the icon did nothing". Always tell the user something happened and where
        // to look — this is the last point at which we can still show any UI.
        const uint MB_OK = 0x0, MB_ICONERROR = 0x10, MB_SETFOREGROUND = 0x10000;
        try
        {
            MessageBoxW(0,
                "Mo를 시작하지 못했습니다.\n\n" +
                $"{ex.GetType().Name}: {ex.Message}\n\n" +
                (logPath != null ? $"자세한 내용:\n{logPath}" : "상세 로그를 기록하지 못했습니다."),
                "Mo", MB_OK | MB_ICONERROR | MB_SETFOREGROUND);
        }
        catch { }
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

    private static string? WriteCrashFile(string message)
    {
        try
        {
            var dir = Path.Combine(GetLocalFolder(), "logs");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, $"startup_crash_{DateTime.Now:yyyyMMdd_HHmmss}.log");
            File.WriteAllText(file, message);
            return file;
        }
        catch { return null; }
    }
}
