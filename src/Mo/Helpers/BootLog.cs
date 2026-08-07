using System.Diagnostics;
using System.Text;

namespace Mo.Helpers;

// Append-only startup trace at %LOCALAPPDATA%\Mo\logs\boot.log.
//
// WinUI3 startup failures are frequently silent: the dispatcher keeps running with no
// window, so nothing is shown and no handler fires. The trace is what identifies where
// it stopped. Dependency-free (no DI, settings or WinRT) so it works from the first
// instruction of Main.
public static class BootLog
{
    private static readonly object Gate = new();
    private static readonly int Pid = Environment.ProcessId;
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static string? _path;

    private const long MaxBytes = 256 * 1024;

    /// <summary>
    /// Writes a session header. Call once, first thing in Main. Appends rather than
    /// truncates — a redirecting instance runs alongside the primary and would
    /// otherwise erase the primary's trace.
    /// </summary>
    public static void BeginSession(string version)
    {
        try
        {
            var path = Path();
            if (path == null) return;

            // Roll rather than grow unbounded.
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
            // Not ApplicationData.Current — it throws unpackaged, the case this logger
            // most needs to work in.
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
