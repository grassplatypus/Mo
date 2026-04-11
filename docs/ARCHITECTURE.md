# Architecture

## Overview

Mo is a WinUI3 desktop application that saves and restores multi-monitor display configurations. It uses the Windows CCD (Connecting and Configuring Displays) API for querying and applying display settings.

## Solution Structure

### Mo (WinUI3 App)
The main application project using single-project MSIX packaging.

- **Models**: Data models for profiles, monitor info, settings, hotkey bindings
- **Services**: Business logic (display management, profile storage, tray, hotkeys, startup)
- **ViewModels**: MVVM view models using CommunityToolkit.Mvvm source generators
- **Views**: XAML pages (Shell, ProfileList, ProfileEditor, Settings)
- **Controls**: Custom controls (MonitorLayoutCanvas, MonitorTile)

### Mo.Core (Class Library)
Pure logic with no Win32 dependencies. Fully unit-testable.

- **MonitorMatcher**: 4-pass algorithm to match saved monitors to current monitors across sessions
- **DisplayTopology**: Coordinate math for bounding boxes and canvas scaling
- **ProfileDiffer**: Detects changes between two display configurations

### Mo.Interop (Class Library)
P/Invoke definitions for Windows CCD API. AllowUnsafeBlocks enabled.

- CCD struct definitions with LayoutKind.Explicit for union types
- CCD enum definitions
- NativeDisplayApi static class with DllImport declarations

## Key Design Decisions

### Monitor Matching Strategy
Monitors are identified across sessions using a 4-pass matching algorithm:
1. Device path (most stable, survives reboots)
2. EDID manufacturer + product code + connector instance
3. Friendly name fallback
4. Single-remaining heuristic

Adapter LUID changes on every boot and is NOT used for cross-session matching.

### Profile Storage
Individual JSON files per profile (`{id}.json`) instead of a single database. This provides:
- Atomic saves (one file per profile)
- Corruption isolation
- Easy import/export

### MSIX Packaging
MSIX guarantees clean uninstall by design:
- All files in containerized WindowsApps folder
- Registry writes are virtualized
- AppData removed on uninstall
- StartupTask extension auto-cleaned

### DPI Scaling
DPI cannot be changed via CCD API. It requires registry writes + WM_SETTINGCHANGE broadcast.
DPI restoration is opt-in due to the risk of requiring sign-out.

## Data Flow

```
User clicks "Save Current"
  → ProfileService.CaptureCurrentAsync()
    → DisplayService.GetCurrentConfiguration()
      → QueryDisplayConfig() + DisplayConfigGetDeviceInfo()
    → JSON serialization → file write

User clicks "Apply"
  → ProfileService.ApplyProfileAsync()
    → DisplayService.ApplyProfile()
      → QueryDisplayConfig(ALL_PATHS)
      → MonitorMatcher.Match()
      → Update paths/modes
      → SetDisplayConfig(VALIDATE)
      → SetDisplayConfig(APPLY | SAVE_TO_DATABASE)
```
