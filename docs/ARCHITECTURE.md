# Architecture

How Mo fits together, for a human reading the codebase for the first time.

The *invariants*, meaning the rules a change must not break, live in `.claude/rules/` and are
deliberately not repeated here. When this document and a rule file disagree, the rule
file is right.

## Overview

Mo saves and restores multi-monitor display configurations: position, rotation, refresh
rate, resolution, scaling, plus per-monitor colour and a few desktop extras. It reads and
writes display state through the Windows CCD (Connecting and Configuring Displays) API.
Vendor driver paths (NVAPI on NVIDIA, ADL on Radeon) are retained but no longer preferred;
`.claude/rules/30-display-apis.md` records why the case for them did not survive measurement.

## Projects

### `src/Mo`: WinUI3 app

- **Models**: `DisplayProfile`, `MonitorInfo`, `AppSettings`, `HotkeyBinding`.
  Plain serializable types; see `.claude/rules/50-persistence.md` for why none of them
  use `[ObservableProperty]`.
- **Services**: see the inventory below.
- **ViewModels**: CommunityToolkit.Mvvm source generators.
- **Views**: `ShellPage`, `ProfileListPage`, `ProfileEditorPage` (split across four
  partials), `SettingsPage`, `DisplayTuningPage`.
- **Controls**: `MonitorLayoutCanvas` (drag editor), `MonitorLayoutThumbnail`
  (read-only preview), `MonitorTile`, `ApplyConfirmationDialog`, `HotkeyPicker`.
  There is no `Themes/Generic.xaml`; custom controls build their own visual tree.
- **Helpers**: `WindowHelper` (Win32 work-area enumeration), `JsonHelper`
  (`MoJsonContext`), `BootLog`, `RelativeTimeText`, `SystemInfoHelper`,
  `AnimationHelper`, `MonitorIdentityExtensions`.
- **Strings**: `en-us` and `ko-KR` resw bundles, kept key-for-key identical.

### `src/Mo.Core`: pure logic, no Win32, fully unit-testable

| Type | Role |
| --- | --- |
| `MonitorMatcher` | 4-pass match of saved monitors to present ones |
| `ProfileDiffer` | Difference between two display configurations |
| `DisplayTopology` | Bounding boxes and canvas ↔ desktop coordinate transforms |
| `SnapCalculator` | Edge snap, alignment guides, overlap push-out |
| `RotationGeometry` | `ToDesktop` / `ToSource` across the rotation boundary |
| `DpiScaling` | Scaling steps relative to recommended ↔ percentages |
| `EdidManufacturer` | EDID manufacturer ID → brand name |
| `RelativeTime`, `LegacyDescription` | Formatting, legacy description cleanup |
| `WindowPlacementValidator` | Rejects a saved window rect no monitor can host |

New logic that does not need Win32 belongs here, with tests in `tests/Mo.Core.Tests`.

### `src/Mo.Interop`: P/Invoke only (`AllowUnsafeBlocks`)

- `DisplayConfig/`: CCD structs (`LayoutKind.Explicit` for unions), enums,
  `NativeDisplayApi`, `ChangeDisplaySettingsEx`, `SendInput`.
- `Hotkey/`: `RegisterHotKey`.
- `Monitor/`: DDC/CI `MonitorConfigApi` (dxva2.dll).

## Service inventory

**Display core**

| Service | Role |
| --- | --- |
| `IDisplayService` | Query and apply configurations via CCD; HDR state |
| `IProfileService` | Load/save/delete/reorder profiles; the single apply entry point |
| `IApplyGuardService` | Capture → apply → confirm-or-revert safety net |
| `NvidiaRotationService` | Full-profile apply through NVAPI |
| `AmdRotationService`, `AdlDisplays` | Opt-in full-profile apply through ADL |
| `CursorPlaneReset` | Blanks and wakes the panels after an NVIDIA rotation |

**Monitor output**

| Service | Role |
| --- | --- |
| `IMonitorColorService` | DDC/CI brightness, contrast, RGB gain, raw VCP; WMI fallback |
| `AmdColorService` | Radeon colour path |

**Automation and shell integration**

| Service | Role |
| --- | --- |
| `IAutoSwitchService` | Watches the topology and auto-applies a matching profile |
| `IScheduleService` | Time-based profile switching (`Start`/`Stop`/`Reconfigure`) |
| `IHotkeyService` | Global hotkeys: slot digits plus next/previous |
| `ITrayService` | Tray icon and context menu, in `SortOrder` order |
| `IStartupService` | Logon registration (StartupTask / HKCU Run) |
| `INavigationService` | Page routing from the shell |

**Desktop extras**

| Service | Role |
| --- | --- |
| `IWallpaperService`, `ILiveWallpaperService` | Wallpaper per profile; live-wallpaper providers |
| `IAudioService` | Default audio device per profile |

**Housekeeping**

| Service | Role |
| --- | --- |
| `ISettingsService` | `AppSettings` load/save |
| `ExportImportService` | Profile import/export |
| `IUpdateService` | Update check (version + download URL) |
| `SystemInfoService` | Machine/GPU information for the UI |
| `AppDataCleanup` | `%LOCALAPPDATA%\Mo` + Run-key removal for uninstallers |

All are registered in `App.xaml.cs` and resolved with
`App.Services.GetRequiredService<T>()`.

## Key design decisions

### Monitor identity

Monitors are matched across sessions by, in order: device path (survives reboots), EDID
manufacturer + product code + connector instance, friendly name, then a
single-remaining heuristic. The adapter LUID changes on every boot and is never used.

### CCD does the work; the vendor paths are opt-in

The apply order is NVAPI full profile, then CCD, then the cursor unstick. NVAPI and the
AMD path (`RotationMethod.AmdDriver`) are both opt-in and neither is offered in the UI.
The reason they existed, that CCD could not re-activate a dropped monitor, did not
survive measurement: CCD does it first try once the call is well formed.

A vendor branch that succeeds owns the *whole* apply, geometry included. The consequences
of getting that wrong are in `.claude/rules/30-display-apis.md`.

Rotation leaves the GPU drawing the mouse cursor at the *previous* rotation. No mode-set
of any kind clears it; only a display power cycle does, which is what `CursorPlaneReset`
performs. It is off by default, since it blanks every panel and the fault has only ever
been seen on NVIDIA.

### One JSON file per profile

`{id}.json` rather than a single database, giving atomic saves, corruption isolation,
and trivial import/export. Writes go through temp file + atomic replace, and
`ProfileService.EnsureRoundTrips` refuses to overwrite a good file with one that does
not deserialize back.

### The apply is guarded

A bad profile can black out a monitor or move it off-screen, leaving the user unable to
reach Mo to undo it. Every apply therefore funnels through
`ProfileService.ApplyProfileAsync`, which captures state first and runs a countdown
dialog that rolls back on "Revert" or on timeout. Windows guards its own display
changes the same way.

### Per-monitor scaling

Scaling goes through two undocumented CCD device-info calls, the same pair the Settings
app uses. They speak in steps relative to the display's recommended scale, so `Mo.Core`'s
`DpiScaling` converts them to the percentages a person reads. It takes effect
immediately, with no sign-out. See `.claude/rules/30-display-apis.md`.

### Clean uninstall

The installer's uninstaller calls `Mo.exe --cleanup --quiet`, and `AppDataCleanup` is
also reachable from Settings, so the ZIP build has the same guarantee. Nothing is ever
written to HKLM; see `.claude/rules/40-safety-invariants.md`.

## Data flow

```
Save Current
  → ProfileService.CaptureCurrentAsync(name)
    → DisplayService.GetCurrentConfiguration()
      → QueryDisplayConfig() + DisplayConfigGetDeviceInfo()
    → MoJsonContext serialize → temp file → atomic replace

Apply
  → ProfileService.ApplyProfileAsync(profile, trigger)
    → IApplyGuardService.Capture()              topology + DDC/CI state
    → DisplayService.ApplyProfile()
        ├── NvidiaRotationService.ApplyFullProfile()      position + resolution
        │                                                 + rotation + primary
        └── CCD fallback
              → QueryDisplayConfig(ALL_PATHS)
              → MonitorMatcher.Match()
              → update paths / modes
              → SetDisplayConfig(VALIDATE)
              → SetDisplayConfig(APPLY | SAVE_TO_DATABASE
                                 | VIRTUAL_MODE_AWARE | PATH_PERSIST_IF_REQUIRED)
        → UnstickCursor (ClipCursor release + SendInput nudge)
        → CursorPlaneReset (blank/wake) when a panel rotated
    → IApplyGuardService.ConfirmOrRevertAsync()  countdown, revert on timeout
    → on confirm: LastAppliedProfileId, ProfileApplied
```

Startup replays the last applied profile when `RestoreOnStartup` is set; colour is
re-pushed separately under `RestoreColorOnStartup`, because Windows does not persist
DDC/CI state across a boot.
