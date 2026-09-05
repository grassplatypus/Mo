# Mo, Monitor Profile Manager

WinUI3 desktop app that saves and restores multi-monitor configurations (position,
rotation, refresh rate, resolution, DPI), with NVIDIA/AMD driver-level display
management for reliable monitor activation. C# / .NET 10 / Windows App SDK.

## Non-negotiables

1. **Files are read and written with `Read` / `Write` / `Edit` only.** No `sed -i`, no
   `> file`, no heredoc-fed scripts, no `python -c "…replace…"`. A `PreToolUse` hook
   denies these; see `.claude/rules/00-tooling.md`.
2. **Comments are at most 3 consecutive lines.** A `PostToolUse` hook fails the edit
   otherwise; see `.claude/rules/10-code-style.md`.
3. **`Task.Wait()` / `.Result` / `GetAwaiter().GetResult()` are build errors.** They
   deadlock the dispatcher before any window exists.
4. **`[ObservableProperty]` must never appear on a serialized model.** The property
   vanishes from the JSON contract and profiles deserialize blank.
5. **Every profile apply goes through `ProfileService.ApplyProfileAsync`**, which owns
   the capture/confirm/revert safety net.

## Tech stack

- **UI**: WinUI3 (Windows App SDK 1.8), Fluent Design with Mica backdrop
- **Architecture**: MVVM with CommunityToolkit.Mvvm source generators
- **DI**: Microsoft.Extensions.DependencyInjection
- **System tray**: H.NotifyIcon.WinUI
- **Display API**: Windows CCD via P/Invoke + NVAPI (NvAPIWrapper.Net) + ADL
- **JSON**: System.Text.Json with source-generated `MoJsonContext`
- **Packaging**: single-project MSIX. **Minimum**: Windows 10 1809 (build 17763)

## Solution structure

```
Mo.slnx
├── src/Mo/              → WinUI3 app (MSIX packaged)
│   ├── Models/          → DisplayProfile, MonitorInfo, AppSettings, HotkeyBinding
│   ├── Services/        → ~30 services; inventory in docs/ARCHITECTURE.md
│   ├── ViewModels/      → MVVM ViewModels (CommunityToolkit.Mvvm)
│   ├── Views/           → ShellPage, ProfileListPage, SettingsPage, ProfileEditorPage,
│   │                      DisplayTuningPage
│   ├── Controls/        → MonitorLayoutCanvas, MonitorLayoutThumbnail, MonitorTile,
│   │                      ApplyConfirmationDialog, HotkeyPicker
│   ├── Converters/      → XAML value converters
│   └── Helpers/         → WindowHelper, JsonHelper, BootLog, RelativeTimeText,
│                          SystemInfoHelper, AnimationHelper
├── src/Mo.Core/         → Pure logic, no Win32 deps, fully unit-testable
│   ├── DisplayConfiguration/ → MonitorMatcher, ProfileDiffer, DisplayTopology,
│   │                           SnapCalculator, RotationGeometry, EdidManufacturer
│   ├── Formatting/      → RelativeTime, LegacyDescription
│   └── WindowPlacementValidator.cs
├── src/Mo.Interop/      → P/Invoke definitions (AllowUnsafeBlocks)
│   ├── DisplayConfig/   → CCD structs, enums, NativeDisplayApi, ChangeDisplaySettingsEx
│   ├── Hotkey/          → RegisterHotKey P/Invoke
│   └── Monitor/         → DDC/CI MonitorConfigApi
├── tests/Mo.Core.Tests/ → xUnit tests for Mo.Core
└── tests/Mo.Tests/      → Integration tests
```

## Rules

Detailed rules live in `.claude/rules/` and are imported below. Edit the rule file, not
this index, when a rule changes.

@.claude/rules/00-tooling.md
@.claude/rules/10-code-style.md
@.claude/rules/20-architecture.md
@.claude/rules/30-display-apis.md
@.claude/rules/40-safety-invariants.md
@.claude/rules/50-persistence.md
@.claude/rules/60-build-and-git.md
@.claude/rules/70-localization.md
@.claude/rules/80-ui-responsiveness.md
