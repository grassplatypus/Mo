using System.Threading;

namespace Mo.Services;

/// <summary>Lets an installer ask a running Mo to exit, and see that it is still
/// there.</summary>
/// <remarks>Restart Manager has only `WM_CLOSE`, which is what the close button sends
/// and which Mo answers by hiding to the tray. See installer/Mo.iss, which opens both
/// handles by name.</remarks>
public static class ShutdownSignal
{
    // Session-local names, matched verbatim in installer/Mo.iss. Both are per-user by
    // nature: Mo installs per user and never elevates, so no Global\ prefix.
    public const string QuitEventName = "MoAppQuitRequest";
    public const string RunningMutexName = "MoAppRunning";

    private static EventWaitHandle? _quitRequest;
    private static Mutex? _running;
    private static RegisteredWaitHandle? _registration;

    /// <summary>Publishes both handles and starts listening. Failure is not fatal: it
    /// costs a smoother upgrade, not a working app, so it never blocks startup.</summary>
    public static void Start()
    {
        try
        {
            _running = new Mutex(initiallyOwned: true, RunningMutexName, out _);
        }
        catch (Exception ex) { Helpers.BootLog.WriteError("shutdownsignal.mutex", ex); }

        try
        {
            _quitRequest = new EventWaitHandle(false, EventResetMode.ManualReset, QuitEventName, out _);

            // A pool wait rather than a thread: nothing is running until the event fires.
            _registration = ThreadPool.RegisterWaitForSingleObject(
                _quitRequest, OnQuitRequested, null, Timeout.Infinite, executeOnlyOnce: true);

            Helpers.BootLog.Write("shutdownsignal.listening");
        }
        catch (Exception ex) { Helpers.BootLog.WriteError("shutdownsignal.event", ex); }
    }

    private static void OnQuitRequested(object? state, bool timedOut)
    {
        Helpers.BootLog.Write("shutdownsignal.quit", "installer asked Mo to exit");
        App.RequestExit();
    }

    /// <summary>Releases the presence mutex so an installer stops waiting. Called on the
    /// way out; process teardown would do it anyway, but not before the wait times out.</summary>
    public static void Stop()
    {
        try { _registration?.Unregister(null); } catch { }
        try { _running?.ReleaseMutex(); } catch { }
        try { _running?.Dispose(); } catch { }
        try { _quitRequest?.Dispose(); } catch { }
        _registration = null;
        _running = null;
        _quitRequest = null;
    }
}
