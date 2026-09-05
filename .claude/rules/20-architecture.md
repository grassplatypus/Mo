# Architecture

## Layering

- `src/Mo`: WinUI3 app. Only this project may touch Win32/WinRT.
- `src/Mo.Core`: pure logic, no Win32 dependency, fully unit-testable.
- `src/Mo.Interop`: P/Invoke definitions only (`AllowUnsafeBlocks`).

New logic that can be expressed without Win32 belongs in `Mo.Core` with tests in
`tests/Mo.Core.Tests`.

## CommunityToolkit.Mvvm

ViewModels use source generators:

```csharp
public partial class MyViewModel : ObservableObject
{
    [ObservableProperty] private string _name = "";
    [RelayCommand] private async Task DoSomethingAsync() { }
}
```

The one exception is serialized models; see `10-code-style.md`.

## Dependency injection

All services are registered in `App.xaml.cs`. Resolve with
`App.Services.GetRequiredService<IMyService>()`.

## Adding a page

1. `Views/MyPage.xaml` + `Views/MyPage.xaml.cs`
2. `ViewModels/MyViewModel.cs` extending `ObservableObject`
3. Register the ViewModel in `App.ConfigureServices()`
4. Navigation entry in `ShellPage.xaml` `NavigationView.MenuItems`
5. Case in `ShellPage.xaml.cs` `NavView_ItemInvoked`

## Adding a service

1. `Services/IMyService.cs` (interface)
2. `Services/MyService.cs` (implementation)
3. `services.AddSingleton<IMyService, MyService>()` in `App.ConfigureServices()`

## Profile editor layout

- `MonitorLayoutCanvas` handles drag + snap + overlap resolution. On release, the
  inverse transform (`DisplayTopology.TransformFromCanvas`) writes back to
  `MonitorInfo.PositionX/Y` and raises `MonitorPositionChanged`.
- `SnapCalculator` (Mo.Core): edge snap with configurable tolerance (default 30
  desktop px), alignment guide collection, minimum-displacement overlap push-out.
- Arrow keys nudge the selected tile through the same snap and adjacency path as a drag.
  Steps are 20 desktop px, 200 with Shift, 1 with Ctrl: one pixel is invisible on a
  canvas that fits a multi-thousand-pixel desktop into a few hundred.
- `LayoutArranger.Row` (Mo.Core) aligns **centres**, not top edges. Windows maps the
  cursor across a monitor boundary by absolute pixel coordinate, so a 1440-tall panel
  beside a rotated 1920-tall one corresponds physically only where the centres meet.
- Rotation changes swap `Width/Height` between landscape (0°/180°) and portrait
  (90°/270°).
- `ProfileEditorPage` is split across partials: `.xaml.cs` (shell/load/localization),
  `.Monitors.cs`, `.Extras.cs`, `.Persistence.cs`.

There is no `Themes/Generic.xaml`; custom controls build their own visual tree.

## ComboBox selection is wired by hand, not with x:Bind

Every `ComboBox` on `SettingsPage` (theme, language, rotation method) sets its selection
in `Loaded` and writes back on `SelectionChanged`. Do not "simplify" this to
`x:Bind SelectedValue` / `SelectedValuePath`.

That binding loses a race in WinUI 3: when `SelectedValue` is evaluated before
`SelectedValuePath` has finished binding, the initial lookup falls through to null. The
combo renders blank *and* the TwoWay listener pushes that null back into the source.
For a `string` that is merely wrong; for a `RotationMethod` enum the unbox of null
throws `NullReferenceException` through `CastHelpers.Unbox`, which is what killed
0.20.1 first-launches for NVIDIA and AMD users.

By `Loaded`, both `ItemsSource` and the items are fully realized, so assigning
`SelectedItem` is safe. Anything written straight to the settings store to avoid
re-entering a cached page (see `App.MaybeOfferDriverRotationAsync`) exists for the same
reason.

## Startup diagnostics

`Helpers/BootLog` writes `%LOCALAPPDATA%/Mo/logs/boot.log` with a timestamped step
trace. WinUI3 startup failures are frequently *silent*: if `OnLaunched` throws, the
handler marks it handled and the dispatcher keeps running with no window, leaving a
live process that also holds the single-instance key.

The last line in `boot.log` identifies where it stopped.
`App.App_UnhandledException` refuses to swallow anything thrown before `MainWindow` is
activated. It reports and exits so the key is released.
