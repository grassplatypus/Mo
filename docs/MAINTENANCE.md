# Maintenance Guide

Recurring chores and the traps in them. Procedures an agent should follow live in
`.claude/rules/` and `.claude/skills/`; this file does not repeat them.

| Task | Where it is written down |
| --- | --- |
| Add a page | `.claude/rules/20-architecture.md` |
| Add a service | `.claude/rules/20-architecture.md` |
| Add a user-facing setting | `.claude/skills/add-setting/SKILL.md` |
| Add a UI string | `.claude/rules/70-localization.md` |

## Adding a monitor property to profiles

1. Add the property to `src/Mo/Models/MonitorInfo.cs` as a plain auto-property, never
   `[ObservableProperty]`.
2. Populate it in **both** `DisplayService.GetCurrentConfiguration()` and
   `GetAllConnectedMonitors()`. Captures read the second one, so a field only the first
   fills in is written to disk as its default.
3. Apply it in `DisplayService.ApplyProfile()`, and in
   `NvidiaRotationService.ApplyFullProfile()` if the NVIDIA branch owns that field.
   The vendor branch returns early on success, so anything it does not write is lost.
4. Add change detection in `Mo.Core/DisplayConfiguration/ProfileDiffer.cs`.
5. Surface it in the `ProfileEditorPage` monitor details panel, with strings in **both**
   resw bundles.
6. Existing profiles deserialize with the default value, so pick a default that means
   "as before".

A field nothing populates and nothing applies is worse than a missing feature, because
the diff view and the docs both claim it works. `DpiScale` sat that way for a long time.

If the property is a size or a position, decide explicitly whether it is pre-rotation
panel space or post-rotation desktop space, and route it through `RotationGeometry`.
Getting this wrong is silent until someone rotates a portrait panel.

## Icons

`src/Mo/Assets/AppIcon.ico` is the source of Mo's identity. It carries 16 through 256,
and the exe embeds it through `ApplicationIcon` in `Mo.csproj`; Explorer, the task bar
and Add/Remove Programs read that, not `AppWindow.SetIcon`.

Regenerate the MSIX tiles and the splash screen from it with
`.\scripts\New-AppIcons.ps1` whenever the icon changes.

Assets are included with `CopyToOutputDirectory`, not bare `Content`. `Content` alone
satisfies MSIX packaging and leaves the unpackaged publish, which is what ships, without
the file. Only `TrayIcon.ico` had the setting for a long time, so the tray icon worked
while the window and title bar had nothing to load. `BundledAssetTests` fails if an asset
stops travelling.

**Load bundled images through `ms-appx:`.** WinUI's image decoder does not accept a
filesystem path: a `file:` URI fails with `E_NETWORK_ERROR` and raises nothing you would
notice. `Helpers/AppImages` logs `appicon.loaded` or `appicon.failed` either way.

**Do not inspect an .ico with `System.Drawing.Icon`.** It mis-decodes PNG-compressed
frames into coloured noise and throws outright on the larger ones, which reads as "the
icon file is corrupt" when it is fine. Use WIC (`BitmapDecoder` from `PresentationCore`),
which is what the script does.

## CCD API additions

1. Enums → `src/Mo.Interop/DisplayConfig/Enums.cs`
2. Structs → `Structs.cs` (`[FieldOffset]` for unions)
3. P/Invoke declarations → `NativeDisplayApi.cs`
4. Reference: <https://learn.microsoft.com/en-us/windows/win32/api/wingdi/>

## NuGet updates

```bash
dotnet outdated Mo.slnx
dotnet add src/Mo/Mo.csproj package <PackageName>
```

Watch: `Microsoft.WindowsAppSDK` (WinUI3 runtime), `CommunityToolkit.Mvvm` (source
generators, where a major bump can change generated member shapes), `H.NotifyIcon.WinUI`,
`NvAPIWrapper.Net`.

## Building for distribution

**ZIP**: the .NET SDK handles this.

```bash
dotnet publish src/Mo/Mo.csproj -c Release -p:Platform=x64
```

**Installer**: `installer/Mo.iss`, built with Inno Setup. This is what ships. It
installs per user (`{autopf}` with `PrivilegesRequired=lowest`), so there is no UAC
prompt and no certificate to trust, and its uninstaller calls `Mo.exe --cleanup --quiet`
so nothing is left behind.

**MSIX** is opt-in and no longer the release format. It needs a signing certificate the
machine already trusts, which is the whole reason the installer replaced it. Build it
with `Publish-Release.ps1 -Msix`; use the `dotnet` CLI rather than Visual Studio (see the
IDE note below). The CLI cannot find `mspdbcmf.exe`, so it produces no `.appxsym`.

Signing and the `PackageCertificateThumbprint` trap are covered in
`.claude/rules/60-build-and-git.md`. For a plain build, pass
`-p:AppxPackageSigningEnabled=false`.

## Release scripts

```powershell
.\scripts\Publish-Release.ps1 -Version 0.21.3.0  # tests, ZIP, installer
```

Both scripts take `-Help`, and ask before anything with a lasting effect unless
`-NonInteractive` is passed.

`Publish-Release.ps1` runs both test suites, publishes the self-contained ZIP (7-Zip when
installed, `Compress-Archive` otherwise), and builds the installer. `-SkipZip`,
`-SkipInstaller` and `-SkipTests` drop a stage; `-Msix` adds the packaged build.
`-Version` stamps the manifest through `XmlDocument.Save`, the only writer that preserves
the XML encoding. Artifacts land in `artifacts\<Configuration>-<Platform>\`.

`New-SigningCertificate.ps1` is only needed for the MSIX path. It reads
`Identity/@Publisher` out of `Package.appxmanifest`, since MSIX only accepts a
certificate whose subject matches it exactly, and prints where everything lives: the
store entry, the private key file, and a `.cer` under `artifacts/`.

It also reports whether the certificate is in `CurrentUser\TrustedPeople` and
`LocalMachine\TrustedPeople`. **If an MSIX install is refused, read those two lines
first**, which is almost always the reason. `-Trust` fixes the first, `-Trust
-MachineWide` the second from an elevated shell.

`-ExportPath` produces a `.pfx` for CI, asking for the password if `-Password` is
omitted. `-UpdateProject` rewrites the thumbprint in `Mo.csproj`; that value is
machine-specific, so think before committing it. On the publish side, `-PfxPath` signs
from a file instead of the store and asks for the password when `-PfxPassword` is
omitted. A wrong password is caught by opening the file up front, not several minutes
later inside MSBuild.

## Development environment

Visual Studio 2022 cannot build this project; use **VS Code with the C# Dev Kit**. The
reason and the exact errors are in `.claude/rules/60-build-and-git.md`.

Open the repo and accept the recommended extensions from `.vscode/extensions.json`; the
workspace ships:

| | |
| --- | --- |
| `Ctrl+Shift+B` | build (x64 Debug, signing off) |
| `F5` | launch the unpackaged app with the debugger attached |
| Task **test: all** | both suites |
| Task **release: signed package** | `Publish-Release.ps1` (ZIP + installer) |
| Task **release: create signing certificate** | one-off per machine |

Rider works too. Adding a `global.json` to make VS 2022 cooperate does not work; it was
tried and reverted.

## Testing

```bash
dotnet test tests/Mo.Core.Tests/          # pure logic, no display hardware needed
dotnet build Mo.slnx -c Debug -p:Platform=x64 -p:AppxPackageSigningEnabled=false
```

Manual checklist, the parts no unit test reaches:

- [ ] Save a profile with 2+ monitors, apply it after changing the layout
- [ ] Apply a profile that rotates a panel; confirm position and size both survive
- [ ] Switch a monitor off in the editor, apply, and confirm the panel actually goes dark
- [ ] Change resolution and refresh rate in the editor; both survive the apply
- [ ] Apply, then let the confirmation countdown expire: displays roll back and the
      profile does **not** become `LastAppliedProfileId`
- [ ] Reboot with `RestoreOnStartup` on; layout and colour both return
- [ ] Tray menu order matches the list order; slot hotkeys hit the right profiles
- [ ] Switch language to `ko-KR` and back; no English leaks into the Korean UI
- [ ] Uninstall through Add/Remove Programs leaves nothing behind, and so does
      `Mo.exe --cleanup` for the ZIP

## Diagnostics

`%LOCALAPPDATA%/Mo/logs/`:

| File | Use |
| --- | --- |
| `boot.log` | Timestamped startup trace; the last line is where startup stopped |
| `crash_*.log`, `startup_crash_*.log` | Unhandled exceptions |
| `nvapi_debug.log` | Every field the NVAPI branch wrote |

A WinUI3 startup failure is often silent, leaving a live process with no window.
`boot.log` is the first thing to read when that happens.
