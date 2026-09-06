# Build, test and git

## The IDE is VS Code, not Visual Studio 2022

VS 2022 **cannot build this project at all**. The .NET 10 SDK requires MSBuild 18 and
VS 2022 ships 17.14:

```
Version 10.0.400 of the .NET SDK requires at least version 18.0.0 of MSBuild.
The current available version of MSBuild is 17.14.51.32402.
```

Without a `global.json` it fails earlier and less clearly: VS silently resolves the
.NET 9 SDK and reports `NETSDK1045`. Adding a `global.json` only swaps one error for an
SDK-resolution one; it was tried and reverted.

VS Code with the **C# Dev Kit** drives the SDK's own MSBuild, so it works today.
`.vscode/` carries the build/test/release tasks and an unpackaged launch config;
`extensions.json` lists what to install. Any instruction anywhere in this repo telling
you to use Visual Studio is out of date; fix it when you see it.

## Build and run

```bash
dotnet build Mo.slnx -c Debug -p:Platform=x64 -p:AppxPackageSigningEnabled=false
dotnet test tests/Mo.Core.Tests/
dotnet test tests/Mo.Tests/ -p:Platform=x64 -p:AppxPackageSigningEnabled=false
dotnet run --project src/Mo -c Debug   # unpackaged debug
```

**`AppxPackageSigningEnabled=false` is not optional on a dev machine.** `Mo.csproj`
pins `PackageCertificateThumbprint` to `2A78B296…`; without that certificate in the
local store the build stops at `SigningCertificateThumbprintNotInStore` **after** every
assembly has already compiled. It reads like a code failure and is not one.

Requires the .NET 10 SDK. With only .NET 9 installed, every project fails restore with
`NETSDK1045` before anything is compiled.

## The build stays warning-clean

`Directory.Build.props` sets `TreatWarningsAsErrors`. It was switched on while the build
had **zero** warnings, which is the only moment that costs nothing. A warning list is
only worth reading while it is empty.

Suppression is allowed, but it has to be argued:

- `#pragma warning disable X` needs a comment next to it saying why the case is safe.
  All three in the tree today do (two RS0030, one IL2026).
- **Never add to `NoWarn`.** It silences the code everywhere, including the next real
  occurrence. `check-project-integrity.mjs` rejects any code beyond the pre-existing
  `NETSDK1233`.
- `[SuppressMessage]` is rejected outright; use a local pragma instead.
- Turning `TreatWarningsAsErrors` off, or lowering `WarningLevel`, is rejected.

`MoVersion` in `Directory.Build.props` is the release version and is set by hand. Its
three parts must match the first three of `Identity/@Version` in `Package.appxmanifest`,
or the release ships mislabelled. Checked by that hook.

**The fourth component is a build stamp and moves on its own.** `Publish-Release.ps1`
writes minutes-since-midnight there unless `-Version` overrides it, and passes the same
value as `MoVersionFull`, so the assembly, the manifest, the installer's filename and
`boot.log`'s session header all name the same build.

Before this, every build of a release carried one number. A session that produced ten
installers produced ten files called `Mo-0.22.0.0-Setup.exe`, and neither the user nor a
log could say which one was running. Two builds on different days can still collide; the
log's own timestamp settles that.

## Releases

**The release format is the Inno Setup installer** (`installer/Mo.iss`), not MSIX. It
installs per user, so it needs no elevation and no certificate the machine has to trust
first, which is what made MSIX painful to hand anyone. Its uninstaller calls
`Mo.exe --cleanup --quiet`.

```powershell
.\scripts\Publish-Release.ps1                 # tests, ZIP, installer
.\scripts\Publish-Release.ps1 -Msix           # adds the packaged build
.\scripts\New-SigningCertificate.ps1 -Trust   # only needed for -Msix
```

Both take `-Help`. `Publish-Release.ps1` also takes `-PfxPath` to sign from a file
instead of the certificate store, prompting for the password when `-PfxPassword` is
omitted so it never lands in shell history.

**No line inside a comment-based help block may start with a dot.** PowerShell reads it
as a section keyword, and one it does not recognise makes it discard the whole block:
`Get-Help` then returns only the syntax line and nothing reports an error.
`Publish-Release.ps1` had exactly this, a paragraph wrapping onto `.NET 10 SDK ...`, and
its help had never worked. `check-project-integrity.mjs` now fails the edit instead.

`Publish-Release.ps1` runs both test suites, publishes the self-contained ZIP (7-Zip if
present, `Compress-Archive` otherwise), and builds the installer. `-SkipZip`,
`-SkipInstaller` and `-SkipTests` drop a stage. With `-Msix` it also builds the package
with signing on, and refuses to finish unless the package carries the expected
certificate.

MSIX packaging goes through the `dotnet` CLI for the reason at the top of this file.
Verified end to end on 2026-08-23: `dotnet build` with
`GenerateAppxPackageOnBuild=true` produced a signed `.msixbundle`. The CLI path cannot
find `mspdbcmf.exe`, so it emits no `.appxsym` symbol package. A known gap, not a
failure.

A self-signed package will never pass `signtool verify /pa`, because the chain
terminates in an untrusted root. Check the *signer thumbprint* instead, which is what
the script does; installation works from `TrustedPeople`.

## CI/CD

- **ZIP**: `dotnet publish` with the .NET 10 SDK (trimming + R2R).
- **MSIX**: `msbuild` from VS MSBuild, for the reason above.
- **Installer**: `ISCC` on `installer/Mo.iss`, x64 only, since the script sets
  `ArchitecturesAllowed=x64compatible`. The runner image usually carries Inno Setup;
  the workflow installs it when it does not.
- **Signing**: DigiCert timestamp server, SHA-512 digest.
- **Release body**: the annotated tag's message, not GitHub's generated notes, so the
  release reads the same as `git show <tag>`. Write the tag message in Korean.
- `.github/workflows/release.yml` rewrites the manifest version with `sed -i`, because
  PowerShell `Set-Content` corrupts the XML encoding. That runs on the CI runner and
  it is not a licence to edit files with `sed` locally (`00-tooling.md`).

## Git conventions

- No `Co-Authored-By` trailer and no generated PR footer: `.claude/settings.json` sets
  `attribution.commit` and `attribution.pr` to empty strings.
- Commit or push only when asked. Branch first if on `master`.
- `.gitattributes` pins CRLF for Windows source (`*.cs`, `*.xaml`, project files) and
  LF for `*.md`, `*.json`, `*.yml`, `*.mjs`. Do not fight it with editor settings.
- `.claude/settings.local.json` and `.claude/worktrees/` stay out of the repo;
  `.claude/settings.json`, `.claude/rules/` and `.claude/hooks/` are committed.

## Tests

`Mo.Core` is the testable layer, and `tests/Mo.Core.Tests` is where new logic gets
covered. `tests/Mo.Tests` holds integration tests that touch the app project.

Run the Core tests before reporting a logic change as done; they need no display
hardware.
