# Maintenance Guide

## Adding a New Monitor Property to Profiles

1. Add the property to `src/Mo/Models/MonitorInfo.cs`
2. Populate it in `src/Mo/Services/DisplayService.cs` → `GetCurrentConfiguration()`
3. Apply it in `DisplayService.ApplyProfile()` (update the active path/mode accordingly)
4. Add change detection in `src/Mo.Core/DisplayConfiguration/ProfileDiffer.cs`
5. Display it in `src/Mo/Views/ProfileEditorPage.xaml` monitor details panel
6. Existing saved profiles will have the default value for the new property (backward compatible via JSON deserialization)

## Adding a New Page

1. Create `src/Mo/Views/MyPage.xaml` + `.xaml.cs`
2. Create `src/Mo/ViewModels/MyViewModel.cs` extending `ObservableObject`
3. Register the ViewModel in `App.ConfigureServices()` as Transient
4. Add a `NavigationViewItem` in `src/Mo/Views/ShellPage.xaml`
5. Add the routing case in `ShellPage.xaml.cs` → `NavView_ItemInvoked`

## Adding a New Service

1. Create `src/Mo/Services/IMyService.cs` (interface)
2. Create `src/Mo/Services/MyService.cs` (implementation)
3. Register in `App.ConfigureServices()`: `services.AddSingleton<IMyService, MyService>()`
4. Inject via constructor in ViewModels or other services

## CCD API Changes

If new display properties need P/Invoke support:

1. Add enums to `src/Mo.Interop/DisplayConfig/Enums.cs`
2. Add structs to `src/Mo.Interop/DisplayConfig/Structs.cs` (use `[FieldOffset]` for unions)
3. Add P/Invoke declarations to `src/Mo.Interop/DisplayConfig/NativeDisplayApi.cs`
4. Reference the Windows documentation: https://learn.microsoft.com/en-us/windows/win32/api/wingdi/

## Updating NuGet Packages

```bash
dotnet outdated Mo.slnx
dotnet add src/Mo/Mo.csproj package <PackageName>
```

Key packages to keep updated:
- `Microsoft.WindowsAppSDK` — WinUI3 runtime
- `CommunityToolkit.Mvvm` — MVVM source generators
- `H.NotifyIcon.WinUI` — System tray support

## Building MSIX for Distribution

1. Generate a certificate: `New-SelfSignedCertificate -Type Custom -Subject "CN=Mo Dev" ...`
2. Set `PackageCertificateThumbprint` in Mo.csproj
3. Build: `dotnet publish src/Mo/Mo.csproj -c Release -p:Platform=x64`
4. The MSIX package will be in `src/Mo/bin/x64/Release/.../AppPackages/`

## Testing

```bash
# Unit tests (pure logic, no Win32)
dotnet test tests/Mo.Core.Tests/

# Full build verification
dotnet build Mo.slnx -c Debug
```

Manual test checklist:
- [ ] Save profile with 2+ monitors
- [ ] Apply saved profile
- [ ] Tray icon appears and context menu works
- [ ] Global hotkey switches profile
- [ ] App starts minimized to tray (when configured)
- [ ] MSIX install/uninstall leaves no artifacts
