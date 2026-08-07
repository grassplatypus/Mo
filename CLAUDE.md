# Mo — Monitor Profile Manager

## Overview
WinUI3 desktop application that saves and restores multi-monitor configurations (position, rotation, refresh rate, resolution, DPI). Supports NVIDIA driver-level display management for reliable monitor activation/deactivation. Built with C# / .NET 10 / Windows App SDK.

## Tech Stack
- **Language**: C# with nullable reference types enabled
- **UI**: WinUI3 (Windows App SDK 1.8), Fluent Design with Mica backdrop
- **Architecture**: MVVM with CommunityToolkit.Mvvm source generators
- **DI**: Microsoft.Extensions.DependencyInjection
- **System Tray**: H.NotifyIcon.WinUI
- **Display API**: Windows CCD (Connecting and Configuring Displays) via P/Invoke + NVAPI (NvAPIWrapper.Net)
- **JSON**: System.Text.Json with source-generated `MoJsonContext`
- **Packaging**: Single-project MSIX
- **Minimum**: Windows 10 1809 (build 17763)

## Solution Structure
```
Mo.slnx
├── src/Mo/              → WinUI3 app (MSIX packaged)
│   ├── Models/          → DisplayProfile, MonitorInfo, AppSettings, HotkeyBinding
│   ├── Services/        → IDisplayService, IProfileService, ISettingsService, ITrayService, IHotkeyService,
│   │                      IApplyGuardService (undo safety net), IMonitorColorService (DDC/CI + VCP + WMI),
│   │                      NvidiaRotationService, AmdRotationService, IntelRotationService
│   ├── ViewModels/      → MVVM ViewModels (CommunityToolkit.Mvvm)
│   ├── Views/           → Pages (ShellPage, ProfileListPage, SettingsPage, ProfileEditorPage, DisplayTuningPage)
│   │                      ProfileEditorPage is split across partials: .xaml.cs (shell/load/localization),
│   │                      .Monitors.cs, .Extras.cs, .Persistence.cs
│   ├── Controls/        → MonitorLayoutCanvas (drag editor), MonitorLayoutThumbnail (read-only preview),
│   │                      MonitorTile, ApplyConfirmationDialog, HotkeyPicker
│   ├── Converters/      → XAML value converters
│   ├── Helpers/         → WindowHelper (+ Win32 work-area enumeration), JsonHelper (MoJsonContext),
│   │                      BootLog (startup trace), RelativeTimeText, SystemInfoHelper, AnimationHelper
│   └── Themes/          → (empty — there is no Generic.xaml; custom controls build their own visual tree)
├── src/Mo.Core/         → Pure logic (no Win32 deps, fully unit-testable)
│   ├── DisplayConfiguration/ → MonitorMatcher, ProfileDiffer, DisplayTopology, SnapCalculator,
│   │                           EdidManufacturer
│   ├── Formatting/      → RelativeTime, LegacyDescription
│   └── WindowPlacementValidator.cs
├── src/Mo.Interop/      → P/Invoke definitions (AllowUnsafeBlocks)
│   ├── DisplayConfig/   → CCD API structs, enums, NativeDisplayApi, ChangeDisplaySettingsEx, SendInput
│   ├── Hotkey/          → RegisterHotKey P/Invoke
│   └── Monitor/         → DDC/CI MonitorConfigApi
├── tests/Mo.Core.Tests/ → xUnit tests for Mo.Core
└── tests/Mo.Tests/      → Integration tests
```

## Build & Run
```bash
dotnet build Mo.slnx -c Debug -p:Platform=x64
dotnet test tests/Mo.Core.Tests/
# To run the app (unpackaged debug):
dotnet run --project src/Mo -c Debug
# MSIX packaging (Visual Studio only — .NET 10 SDK MSBuild has BuildTools.MSIX compatibility issue):
# Use VS → Project → Publish → Create App Packages
```

## Key Patterns

### CommunityToolkit.Mvvm
ViewModels use source generators:
```csharp
public partial class MyViewModel : ObservableObject
{
    [ObservableProperty] private string _name = "";
    [RelayCommand] private async Task DoSomethingAsync() { }
}
```

### DI Registration
All services registered in `App.xaml.cs`. Get them via:
```csharp
var service = App.Services.GetRequiredService<IMyService>();
```

### Display Configuration
**CCD API** (Windows standard):
- `QueryDisplayConfig` reads all display paths and modes
- `SetDisplayConfig` applies configurations
- Monitor identity: use `DevicePath` (stable across reboots), NOT `AdapterId` (changes every boot)
- Rotation: `DISPLAYCONFIG_ROTATION` enum (Identity, Rotate90, Rotate180, Rotate270)
- CCD rotation causes known Windows mouse cursor coordinate bug

**NVAPI** (NVIDIA driver-level, preferred for NVIDIA GPUs):
- `NvidiaRotationService.ApplyFullProfile()` — complete profile apply via NVAPI
- Uses `PathInfo.GetDisplaysConfig()` / `PathInfo.SetDisplaysConfig()` for in-place modification
- **Persistence flags**: Always pass `DisplayConfigFlags.SaveToPersistence | DriverReloadAllowed`
  when calling `SetDisplaysConfig`, with a fallback to `DriverReloadAllowed` alone on failure.
  Without `SaveToPersistence` the driver reverts on reboot.
- CCD↔NVAPI display matching via GDI device name bridge (`\\.\DISPLAY1`)
- PathInfo cache for re-enabling disabled monitors
- Falls back to `displayswitch.exe /extend` for cold-start monitor activation

**CCD persistence flags**: `SetDisplayConfig` requires `SDC_SAVE_TO_DATABASE |
SDC_VIRTUAL_MODE_AWARE | SDC_PATH_PERSIST_IF_REQUIRED` for Windows 10 1903+ to
correctly persist DPI/rotation-aware layouts across reboots. Older builds reject
VIRTUAL_MODE_AWARE — retry without it on failure.

**Profile apply flow** (DisplayService.ApplyProfile):
1. Try NVAPI full profile (if NVIDIA GPU available)
2. Fallback to CCD path (topology extend → SetDisplayConfig with persistence flags)
3. Mouse unstick workaround (ClipCursor + SystemParametersInfo + SendInput)

**Reboot restore**: `App.RestoreLastAppliedProfileAsync` re-applies the profile
recorded in `AppSettings.LastAppliedProfileId` after launch. Gated by
`RestoreOnStartup`; color re-push gated by `RestoreColorOnStartup` (DDC/CI state
is *not* persisted by Windows, so color must be re-pushed every boot).

### Apply Safety Net (IApplyGuardService)
A bad profile can black out a monitor or move it off-screen, leaving the user unable
to reach Mo to undo it. `ProfileService.ApplyProfileAsync` is the single choke point
every caller goes through, and it is wired to:
1. `Capture()` the topology + DDC/CI state **before** touching hardware,
2. apply, then `ConfirmOrRevertAsync()` — a countdown dialog that rolls back on
   "Revert" or on timeout,
3. skip `LastAppliedProfileId` and `ProfileApplied` when reverted, so a rejected
   profile never returns on the next boot.

- No prompt when the before/after signature is identical (a no-op apply).
- The window is force-shown for non-`User` triggers — a hotkey apply that broke the
  desktop must still surface the dialog.
- `ApplyTrigger` (User / Hotkey / AutoSwitch / Schedule / Startup) tells the guard
  whether to surface the window. Unattended triggers pass `confirm: false` when
  `CheckCompatibility().IsFullMatch` — otherwise an unanswered countdown would revert
  every scheduled or boot-time switch.
- User-facing switches: `AppSettings.ConfirmApply`, `ApplyConfirmSeconds`.

### DDC/CI handle safety
Every DDC/CI call must go through `MonitorColorService.WithHandles` /
`WithHandleFor`, which hold the cache lock for the whole transaction. Handing a raw
`hPhysicalMonitor` back to a caller is a use-after-free:
`SystemEvents.DisplaySettingsChanged` fires on a system thread and calls
`DestroyPhysicalMonitors`, and applying a profile changes the display configuration —
raising that event — immediately before pushing colour down the same handles. Never
call WMI from inside the lambda; do it after, outside the lock.

### Radeon (ADL) rules
Verified against a real Radeon + GeForce machine; do not "simplify" these away.
- **Locate displays by `AdapterInfo.strDisplayName`** — that is the GDI name
  (`\\.\DISPLAY1`). `ADLDisplayInfo.strDisplayName` is the *EDID model name* and will
  never match one. `AdlDisplays.Resolve` does adapter-then-display in that order.
- **Filter on `iVendorID == 1002`** (decimal, not `0x1002`). ADL enumerates non-AMD
  adapters too — the probe machine listed four `NVIDIA GeForce RTX 5080` adapters on
  `\\.\DISPLAY1..4` next to Radeon ones on `\\.\DISPLAY5..9`. An adapter count is
  therefore not a test for "has AMD"; use `AdlDisplays.HasAmdAdapter`.
- ADL functions are `__cdecl`; the **allocation callback is `__stdcall`**
  (`ADL_MAIN_MALLOC_CALLBACK`). Declaring it `Cdecl` corrupts the stack on every ADL
  allocation.
- Buffers from `ADL2_Display_*_Get` are allocated through our callback and owned by us
  (`Marshal.FreeHGlobal`); the `AdapterInfo` buffer is ours to allocate instead.
- `ADL2_Display_Modes_Set` changes modes only. Topology (enabling/disabling outputs)
  would need `DisplayMapConfig_Set`, so `TryApplyAmdFullProfile` bails to CCD whenever
  a profile disables a monitor.
- The AMD full-profile path is opt-in (`RotationMethod.AmdDriver`) and **verifies by
  reading the configuration back**; a mismatch returns false so CCD corrects it. Whether
  ADL wants pre- or post-rotation `iXRes/iYRes` is undocumented, and that read-back is
  what keeps the guess from mattering.

### Uninstall leaves nothing
`Services/AppDataCleanup` removes `%LOCALAPPDATA%\Mo` and the HKCU Run entry. Reachable
from Settings → "Remove Mo's data" and from `Mo.exe --cleanup [--quiet]` for
uninstallers. The packaged LocalFolder is Windows' to remove and is left alone. Do not
reintroduce Windows event-log writes: `EventLog.CreateEventSource` puts a key under
HKLM that needs admin to create *and* to remove, and survives uninstall. BootLog covers
the same need with nothing left behind.

### Startup Diagnostics
`Helpers/BootLog` writes `%LOCALAPPDATA%/Mo/logs/boot.log` with a timestamped step
trace. WinUI3 startup failures are frequently *silent* — if `OnLaunched` throws, the
handler marks it handled and the dispatcher keeps running with no window, leaving a
live process that also holds the single-instance key. The last line in boot.log
identifies where it stopped. `App.App_UnhandledException` refuses to swallow anything
thrown before `MainWindow` is activated: it reports and exits so the key is released.

### Color Control
- **DDC/CI** via `IMonitorColorService` using dxva2.dll — brightness, contrast,
  RGB gain, plus raw VCP Get/Set (color-temperature preset code 0x14, etc.).
- **WMI fallback** for laptop internal display brightness (`WmiMonitorBrightness`).
- **HDR toggle** via `IDisplayService.GetHdrState` / `SetHdrEnabled` using CCD
  `DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO` / `SET_ADVANCED_COLOR_STATE`.
- **Manufacturer** resolved from EDID manufacturer ID via `EdidManufacturer`
  (`GSM→LG`, `SAM→Samsung`, `DEL→Dell`, …). Tries both byte orders.
- Real-time UI: `DisplayTuningPage` — 60 ms throttled slider changes go straight
  to `ApplyToMonitor` without touching any saved profile.

### Profile Editor Layout
- `MonitorLayoutCanvas` handles drag + snap + overlap resolution. On release,
  inverse transform (`DisplayTopology.TransformFromCanvas`) writes back to
  `MonitorInfo.PositionX/Y` and raises `MonitorPositionChanged`.
- `SnapCalculator` (Mo.Core): edge snap with configurable tolerance (default
  30 desktop px), alignment guide collection, and minimum-displacement
  overlap push-out.
- Rotation changes swap `Width/Height` when transitioning between landscape
  (0°/180°) and portrait (90°/270°).

### JSON Serialization
Uses source-generated `MoJsonContext` for trimming safety:
```csharp
JsonSerializer.Serialize(profile, MoJsonContext.Default.DisplayProfile);
JsonSerializer.Deserialize(json, MoJsonContext.Default.AppSettings);
```

**Never use `[ObservableProperty]` on a serialized model.** `MoJsonContext` and the
CommunityToolkit MVVM generator both run against the *same original compilation*, so
System.Text.Json sees `private string _name` and never the `Name` property MVVM emits
afterward. The member silently vanishes from the JSON contract and profiles
deserialize blank. `DisplayProfile` therefore derives from `ObservableObject` but
writes its properties by hand with `SetProperty`. `ProfileService.EnsureRoundTrips`
verifies each save round-trips and throws rather than overwriting a good file.

### Blocking calls are a build error
`BannedSymbols.txt` + `Microsoft.CodeAnalysis.BannedApiAnalyzers` fail the build
(RS0030 via `WarningsAsErrors`) on `Task.Wait()`, `.Result`, and
`GetAwaiter().GetResult()`. `Program.Main` installs a
`DispatcherQueueSynchronizationContext`, so blocking the UI thread on a task whose
continuation posts back to it deadlocks before any window exists — with no exception
and no crash log. Suppress locally with `#pragma warning disable RS0030` *only* with
a comment establishing the call is off the UI thread.

### Profile Ordering
`DisplayProfile.SortOrder` is the user's own ordering, and it is load-bearing: the slot
hotkeys bind `<modifier>+1..9` to `Profiles[0..8]`, the next/previous hotkeys cycle in
this order, and the tray menu follows it. Before it existed the order was
`Directory.GetFiles` order — GUID filenames — so shortcuts pointed at arbitrary
profiles. Reorder via drag or the profile menu's Move up/Move down (drag alone is not
enough: it competes with click-to-open and gives keyboard users no path). Order changes
go through `PersistOrderAsync`, which does **not** touch `ModifiedAt`, then call
`App.RegisterAllHotkeys()` to rebind the slots.

### Runtime-only profile state
`IsActive` and `IsAvailable` are `[JsonIgnore]` and describe the machine right now, not
the profile. `ProfileListViewModel` maintains both — `IsActive` from `ProfileApplied`
plus `LastAppliedProfileId` on cold start, `IsAvailable` from `CheckCompatibility`,
refreshed on `SystemEvents.DisplaySettingsChanged` rather than polled.

### Profile Storage
- Individual JSON files per profile in `ApplicationData.Current.LocalFolder/profiles/`
  (unpackaged builds fall back to `%LOCALAPPDATA%/Mo/`)
- Settings in `ApplicationData.Current.LocalFolder/settings.json`
- Both write via temp file + atomic replace so an interrupted write can't truncate them
- `DisplayProfile.Description` is the **user's own note only**. The monitor count and
  last-modified time are derived at render time; older builds baked untranslatable
  English into the field, and `LegacyDescription.IsGenerated` clears those on load.
- Logs in `%LOCALAPPDATA%/Mo/logs/`: `boot.log` (startup trace), `crash_*.log`,
  `startup_crash_*.log`, `nvapi_debug.log`

### Window Placement
`AppSettings.WindowPlacement` is restored on first activation, not in the constructor:
Windows applies its own default placement when a window is first shown and discards a
position set beforehand (the size survives). `WindowPlacementValidator` (Mo.Core)
rejects a saved rectangle that no longer lands on any monitor — this app rearranges
displays for a living, so that happens often. Work areas come from
`WindowHelper.GetWorkAreas()` (Win32 `EnumDisplayMonitors`); `DisplayArea.FindAll()`
throws `InvalidCastException` in this app's self-contained/unpackaged configuration.

## Code Style
- File-scoped namespaces
- `sealed` on classes not designed for inheritance
- Nullable reference types enabled everywhere
- Private fields: `_camelCase`
- Use `string.Empty` not `""`

## Adding a New Page
1. Create `Views/MyPage.xaml` + `Views/MyPage.xaml.cs`
2. Create `ViewModels/MyViewModel.cs` extending `ObservableObject`
3. Register ViewModel in `App.ConfigureServices()`
4. Add navigation entry in `ShellPage.xaml` NavigationView.MenuItems
5. Add case in `ShellPage.xaml.cs` NavView_ItemInvoked

## Adding a New Service
1. Create `Services/IMyService.cs` (interface)
2. Create `Services/MyService.cs` (implementation)
3. Register in `App.ConfigureServices()`: `services.AddSingleton<IMyService, MyService>()`

## CI/CD Notes
- **ZIP**: `dotnet publish` with .NET 10 SDK (trimming + R2R)
- **MSIX**: `msbuild` with VS MSBuild (dotnet CLI MSBuild has BuildTools.MSIX .NET 10 compatibility issue)
- **Signing**: DigiCert timestamp server, SHA-512 digest
- MSIX version set via `sed` in manifest (PowerShell Set-Content corrupts XML encoding)
