# Safety invariants

These exist because breaking them leaves the user unable to reach the app, or crashes
it on a background thread. Do not relax them for convenience.

## Apply safety net (`IApplyGuardService`)

A bad profile can black out a monitor or move it off-screen, leaving the user unable to
reach Mo to undo it. `ProfileService.ApplyProfileAsync` is the single choke point every
caller goes through, and it is wired to:

1. `Capture()` the topology + DDC/CI state **before** touching hardware,
2. apply, then `ConfirmOrRevertAsync()`, a countdown dialog that rolls back on
   "Revert" or on timeout,
3. skip `LastAppliedProfileId` and `ProfileApplied` when reverted, so a rejected
   profile never returns on the next boot.

Rules:

- No prompt when the before/after signature is identical (a no-op apply).
- The window is force-shown for non-`User` triggers: a hotkey apply that broke the
  desktop must still surface the dialog.
- `ApplyTrigger` (User / Hotkey / AutoSwitch / Schedule / Startup) tells the guard
  whether to surface the window. Unattended triggers pass `confirm: false` when
  `CheckCompatibility().IsFullMatch`. Otherwise an unanswered countdown would revert
  every scheduled or boot-time switch.
- User-facing switch: `AppSettings.ConfirmApply`. The countdown is a fixed 15 seconds
  (`ApplyGuardService.CountdownSeconds`); it was a setting and is not one any more.

## A profile lists every connected monitor, and colour is addressed by identity

`CaptureCurrentAsync` records `GetAllConnectedMonitors`, not `GetCurrentConfiguration`.
A profile can only say "leave this one off" about a monitor it names, so capturing just
the active ones made switching a monitor off impossible to express, whatever the editor
did. The all-paths read therefore has to fill in position, primary and the GDI name too;
anything it leaves at a default is written to disk and applied.

`ApplyGuardService.Capture` still reads active paths only, and must. It is the snapshot a
revert restores, so it has to describe the desktop as it stands rather than give
switched-off monitors a placeholder mode at the origin. `ConnectedMonitorFidelityTests`
pins the two reads together on the fields they share.

Colour and scaling are matched to monitors through `MonitorMatcher.IsSameMonitor` against
the live configuration. Never by list position, which any switched-off monitor shifts,
and never by a stored GDI name, which Windows renumbers across reboots.

**A profile describes the whole desktop.** A monitor it switches off and a monitor it
never mentions both end up off. There is no per-profile setting for this any more:
`UnmatchedMonitorAction` existed, defaulted to `Keep`, and made the result of an apply
depend on a switch most people never found. One reading, applied everywhere, is what
makes an apply predictable.

Do not "repair" an old profile by quietly adding its missing monitors as disabled either.
It already means that. The editor lists connected monitors in its inventory so the user
can add one and give it a position, and adding is their decision.

## DDC/CI handle safety

Every DDC/CI call must go through `MonitorColorService.WithHandles` / `WithHandleFor`,
which hold the cache lock for the whole transaction.

Handing a raw `hPhysicalMonitor` back to a caller is a use-after-free:
`SystemEvents.DisplaySettingsChanged` fires on a system thread and calls
`DestroyPhysicalMonitors`, and applying a profile changes the display configuration,
raising that event, immediately before pushing colour down the same handles.

Never call WMI from inside the lambda; do it after, outside the lock.

## Uninstall leaves nothing

`Services/AppDataCleanup` removes `%LOCALAPPDATA%\Mo` and the HKCU Run entry. Reachable
from Settings → "Remove Mo's data" and from `Mo.exe --cleanup [--quiet]` for
uninstallers. The packaged LocalFolder is Windows' to remove and is left alone.

Do not reintroduce Windows event-log writes. Earlier builds of Mo wrote to the Windows
event log; `EventLog.CreateEventSource` puts a key under
`HKLM\SYSTEM\CurrentControlSet\Services\EventLog\Application\Mo` that needs admin to
create *and* to remove, and survives uninstalling the app.

BootLog covers the same diagnostic need with nothing left behind. There is no
`EventLog` reference in the tree today, and `BannedSymbols.txt` now makes putting one
back a build error rather than a convention.

## What is enforced mechanically

| Invariant | Enforced by |
| --- | --- |
| Every apply goes through `ProfileService.ApplyProfileAsync` | `check-code-invariants.mjs` |
| DDC/CI calls stay inside `MonitorColorService` | `check-code-invariants.mjs` |
| No `[ObservableProperty]` on a serialized model | `check-code-invariants.mjs` |
| No blocking on a task; no `EventLog` | `BannedSymbols.txt` (RS0030, build error) |

The hooks judge whole files, and the tree is clean, so any report is something the
current change introduced.
