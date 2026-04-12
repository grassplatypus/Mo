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
│   ├── Services/        → IDisplayService, IProfileService, ISettingsService, ITrayService, IHotkeyService
│   │                      NvidiaRotationService, AmdRotationService, IntelRotationService
│   ├── ViewModels/      → MVVM ViewModels (CommunityToolkit.Mvvm)
│   ├── Views/           → Pages (ShellPage, ProfileListPage, SettingsPage, ProfileEditorPage)
│   ├── Controls/        → Custom controls (MonitorLayoutCanvas, MonitorTile, ProfileCard)
│   ├── Converters/      → XAML value converters
│   ├── Helpers/         → WindowHelper, JsonHelper (MoJsonContext), SystemInfoHelper, AnimationHelper
│   └── Themes/          → XAML style resources
├── src/Mo.Core/         → Pure logic (no Win32 deps, fully unit-testable)
│   └── DisplayConfiguration/ → MonitorMatcher, ProfileDiffer, DisplayTopology
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
- CCD↔NVAPI display matching via GDI device name bridge (`\\.\DISPLAY1`)
- PathInfo cache for re-enabling disabled monitors
- Falls back to `displayswitch.exe /extend` for cold-start monitor activation

**Profile apply flow** (DisplayService.ApplyProfile):
1. Try NVAPI full profile (if NVIDIA GPU available)
2. Fallback to CCD path (topology extend → SetDisplayConfig)
3. Mouse unstick workaround (ClipCursor + SystemParametersInfo + SendInput)

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
