# Mo — Monitor Profile Manager

[![CI](https://github.com/grassplatypus/Mo/actions/workflows/ci.yml/badge.svg)](https://github.com/grassplatypus/Mo/actions/workflows/ci.yml)

Save and restore your multi-monitor configurations — positions, rotation, refresh rate, resolution, brightness, color, audio, wallpaper, and more — with one click.

<p align="center">
  <strong>WinUI 3</strong> · <strong>Fluent Design</strong> · <strong>System Tray</strong> · <strong>한국어/English</strong>
</p>

## Features

- **Full Profile Management** — Save/restore monitor positions, rotation, refresh rate, resolution, DPI
- **Brightness & Color** — Per-monitor brightness, contrast, RGB gain via DDC/CI; WMI fallback for laptops
- **Audio Output** — Switch default audio device per profile
- **Wallpaper** — Static wallpaper + WallpaperEngine / Lively Wallpaper support
- **Auto-Switch** — Automatically apply profile when monitor configuration changes
- **Schedule** — Time + day-of-week based auto-switching
- **System Tray** — Background operation with quick profile switching
- **Global Hotkeys** — Keyboard shortcuts per profile
- **Apply Confirmation** — 15-second countdown timer with auto-revert
- **Export / Import** — Share profiles as `.moprofile` files
- **Dark / Light / System Theme** — Fluent Design with Mica backdrop
- **Localization** — English and Korean (한국어)
- **Auto Update** — Checks GitHub Releases for new versions
- **Error Reporting** — Structured YAML reports with full hardware info, optimized for LLM analysis
- **Clean Uninstall** — MSIX packaging leaves zero leftover files

## Screenshots

*Coming soon*

## Requirements

- Windows 10 version 1809 (build 17763) or later
- .NET 10 SDK (for building from source)

## Installation

### From Releases

Download the latest `.zip` from [Releases](https://github.com/grassplatypus/Mo/releases) and run `Mo.exe`.

### Build from Source

```bash
git clone https://github.com/grassplatypus/Mo.git
cd Mo
dotnet build Mo.slnx -c Debug -p:Platform=x64
dotnet test tests/Mo.Core.Tests/
```

## Tech Stack

- **Language**: C# / .NET 10
- **UI**: WinUI 3 (Windows App SDK) with Fluent Design
- **Architecture**: MVVM (CommunityToolkit.Mvvm)
- **Display API**: Windows CCD (Connecting and Configuring Displays)
- **Monitor Control**: DDC/CI via dxva2.dll + WMI fallback
- **System Tray**: H.NotifyIcon.WinUI
- **Packaging**: Single-project MSIX

## Project Structure

```
src/Mo/              WinUI3 app (UI, services, MVVM)
src/Mo.Core/         Pure logic (monitor matching, topology, diffing)
src/Mo.Interop/      P/Invoke (CCD API, DDC/CI)
tests/Mo.Core.Tests/ Unit tests
docs/                Architecture and maintenance docs
```

## Contributing

Contributions are welcome! See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) and [docs/MAINTENANCE.md](docs/MAINTENANCE.md) for project structure and development guides.

## License

MIT
