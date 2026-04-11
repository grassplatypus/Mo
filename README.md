# Mo — Monitor Profile Manager

Save and restore your multi-monitor configurations with one click.

## Features

- **Full Profile Management**: Save/restore monitor positions, rotation, refresh rate, resolution, and DPI
- **Visual Monitor Layout**: Interactive canvas showing monitor arrangement with drag-and-drop editing
- **System Tray**: Runs in the background with quick profile switching from the tray context menu
- **Global Hotkeys**: Assign keyboard shortcuts to switch profiles instantly
- **Windows Startup**: Optional auto-launch at login
- **Clean Install/Uninstall**: MSIX packaging ensures zero leftover files or registry entries

## Why Mo?

Existing tools like similar tools only manage monitor on/off states. Mo goes further by saving and restoring:

- **Monitor positions** (which monitor is left, right, above, below)
- **Rotation angles** (0°, 90°, 180°, 270°)
- **Refresh rates** per monitor
- **Resolution** per monitor
- **DPI scaling** (opt-in)

## Tech Stack

- C# / .NET 10
- WinUI 3 (Windows App SDK) with Fluent Design
- Windows CCD (Connecting and Configuring Displays) API
- MSIX single-project packaging

## Requirements

- Windows 10 version 1809 (build 17763) or later
- .NET 10 SDK (for building)

## Build

```bash
dotnet build Mo.slnx -c Debug -p:Platform=x64
dotnet test tests/Mo.Core.Tests/
```

## Project Structure

```
src/Mo/          — WinUI3 app (UI, services, MVVM)
src/Mo.Core/     — Pure logic (monitor matching, topology, diffing)
src/Mo.Interop/  — P/Invoke definitions (CCD API)
tests/           — Unit and integration tests
docs/            — Architecture and maintenance docs
```

## License

MIT
