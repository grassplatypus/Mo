using System.Runtime.InteropServices;
using Mo.Helpers;
using Mo.Interop.DisplayConfig;

namespace Mo.Services;

/// <summary>Blanks the displays and wakes them, which is the only thing found to make
/// the GPU reseat a rotated cursor plane. Everything cheaper was measured and ruled out
/// — see .claude/rules/30-display-apis.md before replacing this.</summary>
public static class CursorPlaneReset
{
    /// <summary>How long to hold the displays off. The panel's own power-on latency
    /// dominates the visible blank, so this only has to be long enough for the driver
    /// to act on the request.</summary>
    private const int BlankMilliseconds = 400;

    /// <summary>Per window, not per broadcast. Keep it small: a session with twenty
    /// top-level windows multiplies this, and one slow window must not stall an apply.
    /// </summary>
    private const uint WindowTimeoutMs = 150;

    /// <summary>Settle time after the wake request. DPMS does not remove a monitor from
    /// Windows' view, so there is nothing to poll: this is a budget sized from observed
    /// panel latency, not a measurement.</summary>
    private const int WakeSettleMs = 1500;

    private static readonly object Gate = new();
    private static bool _running;

    /// <summary>Returns once the displays are back, or once the wait budget runs out.
    /// Never returns with the panels knowingly left dark.</summary>
    public static void Run()
    {
        lock (Gate)
        {
            // Two applies overlap easily: a hotkey during an auto-switch, or the guard
            // reverting while the user applies again. Interleaved off/on strands them.
            if (_running) { BootLog.Write("cursorplane.reset.skipped", "already running"); return; }
            _running = true;
        }

        bool blanked = false;
        try
        {
            blanked = Signal(NativeDisplayApi.MONITOR_POWER_OFF);
            Thread.Sleep(BlankMilliseconds);
        }
        catch (Exception ex) { BootLog.WriteError("cursorplane.reset", ex); }
        finally
        {
            // Unconditional: the broadcast result says nothing about which windows
            // already forwarded the OFF, so treating it as a delivery flag can leave
            // every panel dark.
            try { Wake(blanked); } catch (Exception ex) { BootLog.WriteError("cursorplane.wake", ex); }
            lock (Gate) _running = false;
        }
    }

    private static void Wake(bool blanked)
    {
        Signal(NativeDisplayApi.MONITOR_POWER_ON);

        // SC_MONITORPOWER -1 is advisory on modern Windows; input is what actually
        // relights the panels. SendInput fails while a higher-integrity process holds
        // the foreground, so a failure here is worth a log line.
        if (!Nudge()) BootLog.Write("cursorplane.wake.nudge-failed", $"win32={Marshal.GetLastWin32Error()}");

        // Give the panels their sync time before the caller opens a countdown on them.
        Thread.Sleep(WakeSettleMs);

        // Whether the off broadcast was accepted, so a report of "it never blanked" can
        // be told apart from the reset not running at all. A refusal here means no panel
        // went dark and the cursor plane was never reseated.
        BootLog.Write("cursorplane.reset",
            $"blank {BlankMilliseconds} ms, settle {WakeSettleMs} ms, offAccepted={blanked}");
    }

    private static bool Signal(int state) =>
        NativeDisplayApi.SendMessageTimeout(
            NativeDisplayApi.HWND_BROADCAST,
            NativeDisplayApi.WM_SYSCOMMAND,
            NativeDisplayApi.SC_MONITORPOWER,
            state,
            NativeDisplayApi.SMTO_ABORTIFHUNG,
            WindowTimeoutMs,
            out _) != IntPtr.Zero;

    private static bool Nudge()
    {
        var input = new NativeDisplayApi.INPUT
        {
            type = NativeDisplayApi.INPUT_MOUSE,
            mi = new NativeDisplayApi.MOUSEINPUT
            {
                dx = 8, dy = 8,
                dwFlags = NativeDisplayApi.MOUSEEVENTF_MOVE,
            }
        };
        int size = Marshal.SizeOf<NativeDisplayApi.INPUT>();
        if (NativeDisplayApi.SendInput(1, [input], size) == 0) return false;

        Thread.Sleep(60);
        input.mi.dx = -8; input.mi.dy = -8;
        return NativeDisplayApi.SendInput(1, [input], size) != 0;
    }
}
