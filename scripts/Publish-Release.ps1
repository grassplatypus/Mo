<#
.SYNOPSIS
Builds, signs and collects Mo's distribution artifacts.

.DESCRIPTION
Produces the self-contained ZIP and a per-user installer. All through the dotnet CLI:
Visual Studio 2022 cannot build this.

.NOTES
MSIX is off by default: a self-signed package is refused at install time, so it is only
worth building for a Store submission, where Microsoft signs it.

.LINK
No line in a help block may begin with a dot. PowerShell reads it as a keyword, and one
it does not know silently discards the whole block.

.PARAMETER Configuration
Release (default) or Debug.

.PARAMETER Platform
x64 (default) or ARM64.

.PARAMETER Version
Four-part version to stamp into Package.appxmanifest, e.g. 0.21.3.0. Left alone if
omitted. Written with XmlDocument.Save, never Set-Content, which mangles the encoding.

.PARAMETER PfxPath
Sign from this .pfx instead of a certificate in the store. For CI, or a machine that has
the file but not the certificate installed.

.PARAMETER PfxPassword
Password for -PfxPath. Prompted for when omitted, so it stays out of shell history.

.PARAMETER Thumbprint
Signing certificate. Defaults to PackageCertificateThumbprint from Mo.csproj, then to
any valid certificate matching the manifest Publisher.

.PARAMETER OutputPath
Where artifacts land. Default artifacts\<Configuration>-<Platform>.

.PARAMETER Msix
Also build the MSIX. Needs a signing certificate, and the result only installs where that
certificate is trusted, so this is for Store submissions.

.PARAMETER SkipZip
Skip the ZIP.

.PARAMETER SkipInstaller
Skip the installer. Needs Inno Setup; the script says how to get it when it is missing.

.PARAMETER SkipTests
Do not run the test suites first. They run by default because a signed package built
from failing code is worse than no package.

.PARAMETER NonInteractive
Never ask. Problems that would prompt become warnings, and the run continues with
whatever the defaults were. Set this on CI.

.PARAMETER Help
Print this help and do nothing else.

.EXAMPLE
.\scripts\Publish-Release.ps1
Runs the tests, then produces the ZIP and the installer.

.EXAMPLE
.\scripts\Publish-Release.ps1 -Version 0.21.3.0 -Msix
Stamps the version and also builds a signed MSIX for the Store.
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')][string]$Configuration = 'Release',
    [ValidateSet('x64', 'ARM64')][string]$Platform = 'x64',
    [string]$Version,
    [string]$Thumbprint,
    [string]$PfxPath,
    # Object, not SecureString: PowerShell will not convert a string to one, so typing
    # -PfxPassword 'hunter2' would fail outright. Normalised below.
    [object]$PfxPassword,
    [string]$OutputPath,
    [switch]$Msix,
    [switch]$SkipZip,
    [switch]$SkipInstaller,
    [switch]$SkipTests,
    [switch]$NonInteractive,
    [switch]$Help
)

# Before anything else, so -Help works from outside the repository too.
if ($Help) { Get-Help $PSCommandPath -Detailed; return }

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'src\Mo\Mo.csproj'
$manifestPath = Join-Path $repoRoot 'src\Mo\Package.appxmanifest'

if (-not $OutputPath) { $OutputPath = Join-Path $repoRoot "artifacts\$Configuration-$Platform" }
$OutputPath = [IO.Path]::GetFullPath($OutputPath)

function Write-Step($text) {
    Write-Host ""
    Write-Host "== $text" -ForegroundColor Cyan
}

function Invoke-Checked($file, $arguments, $what) {
    & $file @arguments
    if ($LASTEXITCODE -ne 0) { throw "$what failed with exit code $LASTEXITCODE." }
}

# 7-Zip compresses this tree far smaller and faster than Compress-Archive, which also
# trips over long paths. Prefer a vendored copy, then PATH, then the usual install.
function Resolve-SevenZip {
    $candidates = @(
        (Join-Path $PSScriptRoot 'tools\7za.exe'),
        (Join-Path $env:ProgramFiles '7-Zip\7z.exe'),
        (Join-Path ${env:ProgramFiles(x86)} '7-Zip\7z.exe')
    )
    foreach ($name in @('7z', '7za')) {
        $onPath = Get-Command $name -ErrorAction SilentlyContinue
        if ($onPath) { $candidates = @($onPath.Source) + $candidates }
    }
    foreach ($c in $candidates) {
        if ($c -and (Test-Path $c)) { return $c }
    }
    return $null
}

function New-ZipArchive($sourceDir, $zipPath) {
    $sevenZip = Resolve-SevenZip
    if ($sevenZip) {
        Write-Host "Compressing with $sevenZip"
        & $sevenZip a -tzip -mx=9 -mmt=on -bso0 -bsp0 -- $zipPath (Join-Path $sourceDir '*')
        if ($LASTEXITCODE -eq 1) { Write-Warning '7-Zip reported non-fatal warnings.' }
        elseif ($LASTEXITCODE -ne 0) { throw "7-Zip failed with exit code $LASTEXITCODE." }
        return
    }
    Write-Warning '7-Zip not found; falling back to Compress-Archive (slower, weaker).'
    Compress-Archive -Path (Join-Path $sourceDir '*') -DestinationPath $zipPath
}

# ── SDK check ────────────────────────────────────────────────────────────────────
$sdkVersion = (& dotnet --version).Trim()
if ([int]($sdkVersion.Split('.')[0]) -lt 10) {
    throw "This project targets .NET 10 but 'dotnet --version' reports $sdkVersion. Install the .NET 10 SDK."
}

# ── Version stamp ────────────────────────────────────────────────────────────────
[xml]$manifest = Get-Content -Path $manifestPath -Raw
$publisher = $manifest.Package.Identity.Publisher

if ($Version) {
    if ($Version -notmatch '^\d+\.\d+\.\d+\.\d+$') {
        throw "Version must be four parts, e.g. 0.21.3.0 (got '$Version')."
    }
    Write-Step "Stamping version $Version"
    $manifest.Package.Identity.Version = $Version
    $manifest.Save($manifestPath)
}
else {
    # Minutes since midnight as the build stamp. Two builds of the same release version
    # are otherwise indistinguishable, in the installer's name and in the app's own logs,
    # and telling which one someone is running has cost real time.
    $props = Get-Content -Path (Join-Path $repoRoot 'Directory.Build.props') -Raw
    if ($props -notmatch '<MoVersion>([^<]+)</MoVersion>') {
        throw "Could not read MoVersion from Directory.Build.props."
    }
    $release = $Matches[1]
    $now = Get-Date
    $stamp = [int]($now.TimeOfDay.TotalMinutes)

    Write-Step "Stamping build $release.$stamp"
    $manifest.Package.Identity.Version = "$release.$stamp"
    $manifest.Save($manifestPath)
}
$packageVersion = $manifest.Package.Identity.Version

# ── Certificate ──────────────────────────────────────────────────────────────────

# MSBuild's MSIX packaging signs only from the certificate store, by thumbprint. A
# password-protected key file gets APPX0105 and it quietly signs with something else.
# So the job here is to get the right certificate into the store and trusted.

<#
.SYNOPSIS
Asks a yes/no question, or returns $Default when nobody is there to answer.
#>
function Read-YesNo {
    param([string]$Question, [switch]$Default)

    if (-not $script:CanAsk) {
        Write-Warning "$Question -> $(if ($Default) { 'yes' } else { 'no' }) (not asking)"
        return [bool]$Default
    }

    $hint = if ($Default) { '[Y/n]' } else { '[y/N]' }
    while ($true) {
        $answer = (Read-Host "$Question $hint").Trim().ToLowerInvariant()
        if ($answer -eq '') { return [bool]$Default }
        if ($answer -in 'y', 'yes') { return $true }
        if ($answer -in 'n', 'no') { return $false }
    }
}

<#
.SYNOPSIS
Opens a .pfx, asking for the password and asking again when it is wrong.
.DESCRIPTION
Order: the password given, then the .cred beside the file, then the keyboard. A .cred is
DPAPI-wrapped, so it only opens for the user and machine that wrote it.
#>
function Open-Pfx {
    param([string]$Path, $Given)

    $credFile = "$Path.cred"
    $secure = $null

    if ($Given -is [SecureString]) { $secure = $Given }
    elseif ($Given -is [string] -and $Given.Length -gt 0) {
        Write-Warning "A password given as text is in your shell history. Omit it to be asked instead."
        $secure = ConvertTo-SecureString -String $Given -AsPlainText -Force
    }
    elseif (Test-Path -LiteralPath $credFile) {
        try {
            $secure = Get-Content -LiteralPath $credFile -Raw | ConvertTo-SecureString
            Write-Host "  password from $(Split-Path -Leaf $credFile)"
        }
        catch { Write-Warning "$credFile does not open here; it is tied to one user and machine." }
    }

    for ($attempt = 1; $attempt -le 3; $attempt++) {
        if (-not $secure -or $secure.Length -eq 0) {
            if (-not $script:CanAsk) {
                throw "$Path needs a password. Pass -PfxPassword, or leave a $credFile beside it."
            }
            $secure = Read-Host -Prompt "  Password for $(Split-Path -Leaf $Path)" -AsSecureString
        }

        try {
            return @{
                Certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new(
                    $Path, $secure,
                    [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet)
                Password = $secure
            }
        }
        catch {
            $secure = $null
            if (-not $script:CanAsk -or $attempt -eq 3) {
                throw "Could not open $Path. Wrong password, or the file is damaged."
            }
            Write-Warning "  That password did not open it. Try again."
        }
    }
}

<#
.SYNOPSIS
Copies a certificate into TrustedPeople, which is what Windows checks before it lets an
MSIX install.
#>
function Add-ToTrustedPeople {
    param($Certificate)

    $temp = Join-Path ([IO.Path]::GetTempPath()) ("mo-trust-" + [guid]::NewGuid().ToString('N') + '.cer')
    try {
        Export-Certificate -Cert $Certificate -FilePath $temp -Type CERT | Out-Null
        Import-Certificate -FilePath $temp -CertStoreLocation 'Cert:\CurrentUser\TrustedPeople' | Out-Null
        Write-Host "Trusted in Cert:\CurrentUser\TrustedPeople" -ForegroundColor Green
    }
    finally { if (Test-Path $temp) { Remove-Item $temp -Force } }
}

$signFromPfx = $false
$cert = $null

# A .pfx in the repository root is the portable option: it is gitignored, it carries its
# own key, and it does not depend on a thumbprint that only exists on one machine.
if ($Msix -and -not $PfxPath) {
    $rootPfx = Join-Path $repoRoot 'Mo.pfx'
    if ((Test-Path -LiteralPath $rootPfx) -and (Read-YesNo "Sign with Mo.pfx from the repository root?" -Default)) {
        $PfxPath = $rootPfx
    }
}
if ($Msix -and $PfxPath) {
    # A .pfx carries its own key, so nothing needs to be in the certificate store: this
    # is the path for CI, or a machine that has the file but not the certificate.

    # Rooted check first: Join-Path concatenates an absolute path instead of resolving
    # to it, and the two-argument GetFullPath does not exist on Windows PowerShell 5.1.
    if (-not [IO.Path]::IsPathRooted($PfxPath)) { $PfxPath = Join-Path (Get-Location).Path $PfxPath }
    $PfxPath = [IO.Path]::GetFullPath($PfxPath)
    if (-not (Test-Path -LiteralPath $PfxPath)) { throw "No .pfx at $PfxPath." }

    # A .cred beside the .pfx holds the password through DPAPI, so it only opens for the
    # same user on the same machine. It saves typing without putting the password on disk
    # in the clear; *.cred is gitignored with everything else.
    $credFile = "$PfxPath.cred"
    if (-not $PfxPassword -and (Test-Path -LiteralPath $credFile)) {
        try {
            $PfxPassword = Get-Content -LiteralPath $credFile -Raw | ConvertTo-SecureString
            Write-Host "Password read from $(Split-Path -Leaf $credFile)"
        }
        catch { Write-Warning "$credFile could not be read here. It only opens for the user and machine that wrote it." }
    }

    if ($PfxPassword -is [SecureString]) {
        $secure = $PfxPassword
    }
    elseif ($PfxPassword -is [string] -and $PfxPassword.Length -gt 0) {
        Write-Warning "A -PfxPassword given as text is in your shell history. Omit it to be asked instead."
        $secure = ConvertTo-SecureString -String $PfxPassword -AsPlainText -Force
    }
    else {
        $secure = Read-Host -Prompt "Password for $(Split-Path -Leaf $PfxPath)" -AsSecureString
    }
    if (-not $secure -or $secure.Length -eq 0) { throw "The .pfx password cannot be empty." }
    $PfxPassword = $secure

    # Opening it here turns a wrong password into a clear message now, rather than an
    # MSBuild signing error several minutes into the build.
    try {
        $cert = [Security.Cryptography.X509Certificates.X509Certificate2]::new(
            $PfxPath, $PfxPassword,
            [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet)
    }
    catch { throw "Could not open $PfxPath. Wrong password, or the file is damaged." }

    $Thumbprint = $cert.Thumbprint
    if ($cert.Subject -ne $publisher) {
        throw "The .pfx is for '$($cert.Subject)' but the manifest Publisher is '$publisher'."
    }

    # MSBuild's MSIX packaging cannot open a password-protected key file: it answers
    # APPX0105 and silently signs with whatever is in the store instead. So the .pfx is
    # a portable source, and the store is the mechanism. Import, then sign by thumbprint.
    if (-not (Test-Path "Cert:\CurrentUser\My\$Thumbprint")) {
        Import-PfxCertificate -FilePath $PfxPath -Password $PfxPassword `
            -CertStoreLocation 'Cert:\CurrentUser\My' | Out-Null
        Write-Host "Imported $Thumbprint into Cert:\CurrentUser\My"
    }
    $cert = Get-Item "Cert:\CurrentUser\My\$Thumbprint"
    Write-Host "Signing with $Thumbprint, from $(Split-Path -Leaf $PfxPath)" -ForegroundColor Green
}
elseif ($Msix) {
    if (-not $Thumbprint) {
        $projectText = Get-Content -Path $project -Raw
        if ($projectText -match '<PackageCertificateThumbprint>([^<]+)</PackageCertificateThumbprint>') {
            $Thumbprint = $Matches[1].Trim()
        }
    }

    $pinned = $Thumbprint
    $cert = $null
    if ($Thumbprint) {
        $cert = Get-ChildItem Cert:\CurrentUser\My |
            Where-Object { $_.Thumbprint -eq $Thumbprint } | Select-Object -First 1
    }

    # The pinned thumbprint is machine-specific, so on any other machine it names a
    # certificate that is not here. MSBuild answers that by quietly signing with a
    # throwaway it generates, and the package then refuses to install.
    if ($pinned -and -not $cert) {
        Write-Warning "Mo.csproj pins $pinned, which is not in Cert:\CurrentUser\My."
        $candidates = @(Get-ChildItem Cert:\CurrentUser\My |
            Where-Object { $_.Subject -eq $publisher -and $_.NotAfter -gt (Get-Date) })

        if ($candidates.Count -gt 0) {
            $pick = $candidates[0]
            Write-Host "  A usable one is here: $($pick.Thumbprint)"
            if (Read-YesNo "Sign with that instead?" -Default) {
                $cert = $pick
                if (Read-YesNo "Point Mo.csproj at it as well?") {
                    $text = Get-Content -Path $project -Raw
                    $pattern = '(?<open><PackageCertificateThumbprint>)[^<]*(?<close></PackageCertificateThumbprint>)'
                    $text = [regex]::Replace($text, $pattern, "`${open}$($cert.Thumbprint)`${close}")
                    [IO.File]::WriteAllText($project, $text, (New-Object Text.UTF8Encoding($true)))
                    Write-Host "  Mo.csproj now pins $($cert.Thumbprint)" -ForegroundColor Green
                }
            }
        }
    }

    if (-not $cert) {
        $cert = Get-ChildItem Cert:\CurrentUser\My |
            Where-Object { $_.Subject -eq $publisher -and $_.NotAfter -gt (Get-Date) } |
            Select-Object -First 1
    }
    if (-not $cert) {
        throw @"
No signing certificate for publisher '$publisher'.
Create one first:  .\scripts\New-SigningCertificate.ps1 -Trust -UpdateProject
Or drop -Msix: the ZIP and installer need no certificate at all.
"@
    }
    $Thumbprint = $cert.Thumbprint
    Write-Host "Signing with $Thumbprint ($($cert.Subject))" -ForegroundColor Green
}

if ($Msix) {
    if ($cert.NotAfter -lt (Get-Date).AddDays(30)) {
        Write-Warning "Certificate expires $($cert.NotAfter.ToString('yyyy-MM-dd'))."
    }

    # Trust is what decides whether the package can be installed, so settle it before
    # spending minutes on a build whose output nobody can use.
    $trusted = (Test-Path "Cert:\CurrentUser\TrustedPeople\$Thumbprint") -or
               (Test-Path "Cert:\LocalMachine\TrustedPeople\$Thumbprint")
    if (-not $trusted) {
        Write-Warning "$Thumbprint is not in TrustedPeople, so installing the package will be refused."
        if (Read-YesNo "Trust it for this user now?" -Default) { Add-ToTrustedPeople -Certificate $cert }
    }
}

# ── Tests ────────────────────────────────────────────────────────────────────────
if (-not $SkipTests) {
    Write-Step 'Running tests'
    Invoke-Checked 'dotnet' @('test', (Join-Path $repoRoot 'tests\Mo.Core.Tests'),
        '--nologo', '-v', 'quiet') 'Mo.Core.Tests'
    Invoke-Checked 'dotnet' @('test', (Join-Path $repoRoot 'tests\Mo.Tests'),
        "-p:Platform=$Platform", '-p:AppxPackageSigningEnabled=false',
        '--nologo', '-v', 'quiet') 'Mo.Tests'
}

New-Item -ItemType Directory -Force -Path $OutputPath | Out-Null

# ── Publish ──────────────────────────────────────────────────────────────────────

# One publish feeds both the ZIP and the installer; they ship identical bits that way.
$publishDir = Join-Path $repoRoot "src\Mo\bin\$Platform\$Configuration\publish"
if (-not $SkipZip -or -not $SkipInstaller) {
    Write-Step 'Publishing self-contained build'
    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

    # The same stamp goes into the assembly, so boot.log names the exact build that
    # wrote it rather than the release version every build of it shares.
    Invoke-Checked 'dotnet' @('publish', $project, '-c', $Configuration,
        "-p:Platform=$Platform", "-p:PublishProfile=win-$Platform.pubxml",
        "-p:MoVersionFull=$packageVersion",
        '-p:AppxPackageSigningEnabled=false', '-p:GenerateAppxPackageOnBuild=false',
        '-o', $publishDir, '--nologo') 'dotnet publish'
}

# ── ZIP ──────────────────────────────────────────────────────────────────────────
if (-not $SkipZip) {
    Write-Step 'Packing the ZIP'
    $zipPath = Join-Path $OutputPath "Mo-$packageVersion-$Platform.zip"
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    New-ZipArchive $publishDir $zipPath
    Write-Host "ZIP       -> $zipPath" -ForegroundColor Green
}

# ── Installer ────────────────────────────────────────────────────────────────────
if (-not $SkipInstaller) {
    Write-Step 'Building the installer'

    # winget installs per-user under LOCALAPPDATA, the .exe installer under Program
    # Files, so ask the uninstall keys as well as guessing the usual folders.
    $isccCandidates = foreach ($base in "${env:ProgramFiles(x86)}", $env:ProgramFiles, "$env:LOCALAPPDATA\Programs") {
        foreach ($v in 6, 7) { Join-Path $base "Inno Setup $v\ISCC.exe" }
    }
    foreach ($hive in 'HKLM:\SOFTWARE\WOW6432Node', 'HKLM:\SOFTWARE', 'HKCU:\SOFTWARE') {
        foreach ($v in 6, 7) {
            $key = "$hive\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup ${v}_is1"
            if (Test-Path $key) {
                $loc = (Get-ItemProperty $key -ErrorAction SilentlyContinue).InstallLocation
                if ($loc) { $isccCandidates += (Join-Path $loc 'ISCC.exe') }
            }
        }
    }
    $iscc = $isccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1

    if (-not $iscc) {
        Write-Warning "Inno Setup is not installed, so no installer was built."
        Write-Host "  Install it:  winget install JRSoftware.InnoSetup"
        Write-Host "  Or skip it:  -SkipInstaller"
    }
    else {
        Invoke-Checked $iscc @(
            "/DMoVersion=$packageVersion",
            "/DMoSourceDir=$publishDir",
            "/DMoOutputDir=$OutputPath",
            (Join-Path $repoRoot 'installer\Mo.iss')) 'Inno Setup'

        $setup = Join-Path $OutputPath "Mo-$packageVersion-Setup.exe"
        if (-not (Test-Path $setup)) { throw "Inno Setup reported success but produced no $setup." }
        Write-Host "Installer -> $setup" -ForegroundColor Green
    }
}

# ── MSIX ─────────────────────────────────────────────────────────────────────────
if ($Msix) {
    Write-Step 'Building signed MSIX'

    $appxDir = Join-Path $repoRoot 'src\Mo\AppPackages'
    if (Test-Path $appxDir) { Remove-Item $appxDir -Recurse -Force }

    # Always by thumbprint. PackageCertificateKeyFile cannot open a protected .pfx, so
    # the certificate section imported it into the store and handed us the thumbprint.
    Invoke-Checked 'dotnet' @('build', $project, '-c', $Configuration,
        "-p:Platform=$Platform",
        '-p:GenerateAppxPackageOnBuild=true', '-p:AppxPackageSigningEnabled=true',
        "-p:PackageCertificateThumbprint=$Thumbprint",
        '-p:AppxPackageSigningTimestampServerUrl=http://timestamp.digicert.com',
        '-p:AppxPackageSigningTimestampDigestAlgorithm=SHA512',
        '--nologo', '-v', 'minimal') 'MSIX packaging'

    $packages = @(Get-ChildItem -Path $appxDir -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.Extension -in '.msix', '.msixbundle' })
    if ($packages.Count -eq 0) { throw "The build succeeded but produced no package under $appxDir." }
    foreach ($p in $packages) {
        Copy-Item $p.FullName -Destination $OutputPath -Force
        Write-Host "MSIX -> $(Join-Path $OutputPath $p.Name)" -ForegroundColor Green
    }

    # A self-signed package cannot chain to a trusted root, so check the signer rather
    # than the chain: what matters is that our certificate is the one on the package.
    Write-Step 'Checking signature'
    foreach ($p in $packages) {
        $sig = Get-AuthenticodeSignature -FilePath (Join-Path $OutputPath $p.Name)
        if (-not $sig.SignerCertificate) { throw "$($p.Name) is not signed at all." }
        if ($sig.SignerCertificate.Thumbprint -ne $Thumbprint) {
            throw "$($p.Name) is signed by $($sig.SignerCertificate.Thumbprint), expected $Thumbprint."
        }
        Write-Host "signed by $($sig.SignerCertificate.Subject) [$($sig.Status)]" -ForegroundColor Green
    }
    if ($cert.Issuer -eq $cert.Subject) {
        Write-Host "Self-signed: install the certificate (New-SigningCertificate.ps1 -Trust) on any machine that installs this package." -ForegroundColor Yellow
    }
}

Write-Step "Done — $packageVersion ($Configuration/$Platform)"
Get-ChildItem $OutputPath | Select-Object Name, @{ N = 'MB'; E = { [math]::Round($_.Length / 1MB, 1) } } |
    Format-Table -AutoSize | Out-String | Write-Host
