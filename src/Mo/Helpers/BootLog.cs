using System.Diagnostics;
using System.Text;

namespace Mo.Helpers;

/// <summary>
/// Append-only startup trace written to <c>%LOCALAPPDATA%\Mo\logs\boot.log</c>.
///
/// Startup failures in a WinUI3 app are frequently *silent*: the process stays
/// alive with a running dispatcher but never creates a window, so the user sees
/// nothing and no exception handler ever runs. A boot trace is the only way to
/// tell "died before Main" from "hung in the single-instance redirect" from
/// "MainWindow ctor threw" after the fact.
///
/// Deliberately dependency-free (no DI, no settings, no WinRT) so it works at
/// the very first instruction of Main, before anything else is initialized.
/// </summary>
public static class BootLog
{
    private static readonly object Gate = new();
    private static readonly int Pid = Environment.ProcessId;
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static string? _path;

    private const long MaxBytes = 256 * 1024;

    /// <summary>Writes a session header. Call once, first thing in Main.</summary>
    /// <remarks>
    /// Appends rather than truncates: a secondary (redirecting) instance runs
    /// concurrently with the primary, and truncating here would erase the primary's
    /// trace — destroying exactly the evidence needed to diagnose a hung primary.
    /// </remarks>
    public static void BeginSession(string version)
    {
        try
        {
            var path = Path();
            if (path == null) return;

            // Roll rather than grow unbounded; one previous file is kept for comparison.
            try
            {
                var info = new FileInfo(path);
                if (info.Exists && info.Length > MaxBytes)
                    File.Move(path, path + ".prev", overwrite: true);
            }
            catch { }

            lock (Gate)
                File.AppendAllText(path,
                    $"\n=== Mo boot trace ===\nversion : {version}\npid     : {Pid}\nstarted : {DateTime.Now:O}\n" +
                    $"os      : {Environment.OSVersion}\nclr     : {Environment.Version}\n" +
                    $"exe     : {Environment.ProcessPath}\nelevated: {IsElevated()}\n\n",
                    Encoding.UTF8);
        }
        catch { }
    }

    public static void Write(string step, string? detail = null)
    {
        try
        {
            var path = Path();
            if (path == null) return;

            var line = detail == null
                ? $"[pid {Pid,6}] [{Clock.ElapsedMilliseconds,7} ms] {step}\n"
                : $"[pid {Pid,6}] [{Clock.ElapsedMilliseconds,7} ms] {step} — {detail}\n";

            lock (Gate) File.AppendAllText(path, line, Encoding.UTF8);
        }
        catch { }
    }

    public static void WriteError(string step, Exception ex) =>
        Write(step + " FAILED", ex.GetType().Name + ": " + ex.Message);

    public static bool IsElevated()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(identity)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    private static string? Path()
    {
        if (_path != null) return _path;
        try
        {
            // Never touch ApplicationData.Current here — it throws for unpackaged
            // launches, and this logger must work in exactly that case.
            var dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Mo", "logs");
            Directory.CreateDirectory(dir);
            _path = System.IO.Path.Combine(dir, "boot.log");
            return _path;
        }
        catch { return null; }
    }
}
