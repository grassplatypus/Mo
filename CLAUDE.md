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
│   │                      IMonitorColorService (DDC/CI + VCP + WMI), NvidiaRotationService,
│   │                      AmdRotationService, IntelRotationService
│   ├── ViewModels/      → MVVM ViewModels (CommunityToolkit.Mvvm)
│   ├── Views/           → Pages (ShellPage, ProfileListPage, SettingsPage, ProfileEditorPage, DisplayTuningPage)
│   ├── Controls/        → Custom controls (MonitorLayoutCanvas, MonitorTile, ProfileCard)
│   ├── Converters/      → XAML value converters
│   ├── Helpers/         → WindowHelper, JsonHelper (MoJsonContext), SystemInfoHelper, AnimationHelper
│   └── Themes/          → XAML style resources
├── src/Mo.Core/         → Pure logic (no Win32 deps, fully unit-testable)
│   └── DisplayConfiguration/ → MonitorMatcher, ProfileDiffer, DisplayTopology, SnapCalculator,
│                               EdidManufacturer
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

### Profile Storage
- Individual JSON files per profile in `ApplicationData.Current.LocalFolder/profiles/`
- Settings in `ApplicationData.Current.LocalFolder/settings.json`
- NVAPI debug log in `%LOCALAPPDATA%/Mo/logs/nvapi_debug.log`

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
