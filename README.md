# Mo, Monitor Profile Manager

[![CI](https://github.com/grassplatypus/Mo/actions/workflows/ci.yml/badge.svg)](https://github.com/grassplatypus/Mo/actions/workflows/ci.yml)

Save and restore your multi-monitor setup in one click: positions, rotation, refresh rate, resolution, scaling, brightness, color, audio, wallpaper, and more.

<p align="center">
  <strong>WinUI 3</strong> · <strong>Fluent Design</strong> · <strong>System Tray</strong> · <strong>한국어/English</strong>
</p>

## Features

- **Full profile management**: save and restore monitor positions, rotation, refresh rate, resolution and scaling
- **Brightness and color**: per-monitor brightness, contrast and RGB gain over DDC/CI, with a WMI fallback for laptops
- **Audio output**: switch the default audio device per profile
- **Wallpaper**: static wallpaper, plus Wallpaper Engine and Lively Wallpaper
- **Auto-switch**: apply a profile automatically when the monitor setup changes
- **Schedule**: switch by time and day of week
- **System tray**: runs in the background with quick profile switching
- **Global hotkeys**: a keyboard shortcut per profile
- **Apply confirmation**: a 15-second countdown that rolls back on its own
- **Export and import**: share profiles as `.moprofile` files
- **Dark, light and system theme**: Fluent Design with a Mica backdrop
- **Localization**: English and Korean (한국어)
- **Auto update**: checks GitHub Releases for new versions
- **Error reporting**: structured YAML reports with full hardware detail
- **Clean uninstall**: removing Mo takes its settings and profiles with it

## Screenshots

*Coming soon*

## Requirements

- Windows 10 version 1809 (build 17763) or later
- .NET 10 SDK (for building from source)

## Installation

### From Releases

Download `Mo-<version>-Setup.exe` from [Releases](https://github.com/grassplatypus/Mo/releases) and run it. It installs for the current user, so there is no admin prompt.

Prefer no installer? The `.zip` from the same page is self-contained: unpack it and run `Mo.exe`.

### Build from Source

```bash
git clone https://github.com/grassplatypus/Mo.git
cd Mo
dotnet build Mo.slnx -c Debug -p:Platform=x64 -p:AppxPackageSigningEnabled=false
dotnet test tests/Mo.Core.Tests/
```

Use **VS Code with the C# Dev Kit**. The repo ships build, test and release tasks in
`.vscode/`, plus `F5` debugging. Visual Studio 2022 cannot build this project: the .NET
10 SDK requires MSBuild 18 and VS 2022 ships 17.14.

## Tech Stack

- **Language**: C# / .NET 10
- **UI**: WinUI 3 (Windows App SDK) with Fluent Design
- **Architecture**: MVVM (CommunityToolkit.Mvvm)
- **Display API**: Windows CCD (Connecting and Configuring Displays), with NVAPI
  (NVIDIA) and ADL (Radeon) driver paths for reliable monitor activation
- **Monitor Control**: DDC/CI via dxva2.dll + WMI fallback
- **System Tray**: H.NotifyIcon.WinUI
- **Packaging**: Per-user installer (Inno Setup); MSIX available as an opt-in build

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

The invariants a change must not break (display-driver persistence flags, rotation
coordinate spaces, DDC/CI handle lifetime, the JSON contract) are written up in
[`.claude/rules/`](.claude/rules/) and indexed from [CLAUDE.md](CLAUDE.md). They are
worth reading before touching the display stack, whether you are a person or an agent.

## License

MIT
