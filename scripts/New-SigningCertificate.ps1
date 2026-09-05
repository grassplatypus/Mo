<#
.SYNOPSIS
Creates the self-signed code-signing certificate Mo's MSIX build needs.

.DESCRIPTION
MSIX only accepts a certificate whose subject matches Package.appxmanifest's Publisher
exactly, so this reads that Publisher and issues a code-signing certificate for it.

.PARAMETER Years
Lifetime of the certificate. Default 3.

.PARAMETER ExportPath
Also export a password-protected .pfx here, for CI or another machine.

.PARAMETER Password
Password for -ExportPath. Prompted for when omitted, so it stays out of shell history.

.PARAMETER Trust
Install a copy into TrustedPeople so the signed package can actually be installed.
Without this, Windows refuses the sideload with "publisher not trusted".

.PARAMETER MachineWide
With -Trust, install for every user (LocalMachine). Requires an elevated shell.

.PARAMETER UpdateProject
Rewrite PackageCertificateThumbprint in src/Mo/Mo.csproj to the new thumbprint.

.PARAMETER Force
Create a new certificate even though one already matches the Publisher.

.PARAMETER Help
Print this help and do nothing else.

.EXAMPLE
.\scripts\New-SigningCertificate.ps1 -Trust -UpdateProject
Creates it, trusts it for this user, and points the project at it.

.EXAMPLE
.\scripts\New-SigningCertificate.ps1 -ExportPath .\Mo-dev.pfx -Password 'hunter2'
Creates it and exports a .pfx for another machine.
#>
[CmdletBinding()]
param(
    [int]$Years = 3,
    [string]$ExportPath,
    # Object, not SecureString: PowerShell will not convert a string to one, so typing
    # -Password 'hunter2' would fail outright. Normalised below.
    [object]$Password,
    [switch]$Trust,
    [switch]$MachineWide,
    [switch]$UpdateProject,
    [switch]$Force,
    [switch]$Help
)

# Before anything else, so -Help works from outside the repository too.
if ($Help) { Get-Help $PSCommandPath -Detailed; return }

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$manifestPath = Join-Path $repoRoot 'src\Mo\Package.appxmanifest'
$projectPath = Join-Path $repoRoot 'src\Mo\Mo.csproj'

if (-not (Test-Path $manifestPath)) {
    throw "Package.appxmanifest not found at $manifestPath. Run this from the Mo repository."
}

# The subject is not a free choice: MSIX validates it against the manifest Publisher.
[xml]$manifest = Get-Content -Path $manifestPath -Raw
$subject = $manifest.Package.Identity.Publisher
if ([string]::IsNullOrWhiteSpace($subject)) {
    throw "Could not read Identity/@Publisher from $manifestPath."
}
Write-Host "Publisher from manifest: $subject" -ForegroundColor Cyan

$personalStore = 'Cert:\CurrentUser\My'

<#
.SYNOPSIS
Resolves the on-disk private key file backing a certificate, or $null.
.DESCRIPTION
The certificate lives in the registry, but its private key is a real file, and losing it
means the thumbprint pinned in Mo.csproj signs nothing.
#>
function Get-PrivateKeyFile {
    param($Certificate)

    $unique = $null
    try {
        $rsa = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($Certificate)
        if ($rsa -and $rsa.GetType().Name -eq 'RSACng') { $unique = $rsa.Key.UniqueName }
    }
    catch { }

    if (-not $unique) {
        try { $unique = $Certificate.PrivateKey.CspKeyContainerInfo.UniqueKeyContainerName } catch { }
    }
    if (-not $unique) { return $null }

    # CNG keeps the file directly under Crypto\Keys; CAPI nests it under a SID folder.
    $direct = Join-Path $env:APPDATA "Microsoft\Crypto\Keys\$unique"
    if (Test-Path -LiteralPath $direct) { return $direct }

    foreach ($root in @("$env:APPDATA\Microsoft\Crypto\Keys", "$env:APPDATA\Microsoft\Crypto\RSA")) {
        if (-not (Test-Path -LiteralPath $root)) { continue }
        $leaf = Split-Path -Leaf $unique
        $hit = Get-ChildItem -LiteralPath $root -Recurse -File -Filter $leaf -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if ($hit) { return $hit.FullName }
    }
    return $null
}

$existing = @(Get-ChildItem $personalStore |
    Where-Object { $_.Subject -eq $subject -and $_.NotAfter -gt (Get-Date) })

if ($existing.Count -gt 0 -and -not $Force) {
    Write-Host "A valid certificate for this publisher already exists:" -ForegroundColor Yellow
    foreach ($found in $existing) {
        Write-Host "  $personalStore\$($found.Thumbprint)"
        Write-Host "    expires $($found.NotAfter.ToString('yyyy-MM-dd'))"
    }
    Write-Host "Re-run with -Force to create another one." -ForegroundColor Yellow
    $cert = $existing[0]
}
else {
    $cert = New-SelfSignedCertificate `
        -Type Custom `
        -Subject $subject `
        -KeyUsage DigitalSignature `
        -KeyAlgorithm RSA `
        -KeyLength 2048 `
        -FriendlyName "Mo development signing ($subject)" `
        -CertStoreLocation 'Cert:\CurrentUser\My' `
        -NotAfter (Get-Date).AddYears($Years) `
        -TextExtension @(
            '2.5.29.37={text}1.3.6.1.5.5.7.3.3',
            '2.5.29.19={text}Subject Type:End Entity'
        )
    Write-Host "Created certificate." -ForegroundColor Green
}

$artifacts = Join-Path $repoRoot 'artifacts'
if (-not (Test-Path $artifacts)) { New-Item -ItemType Directory -Path $artifacts | Out-Null }
$cerPath = Join-Path $artifacts "Mo-signing-$($cert.Thumbprint).cer"
Export-Certificate -Cert $cert -FilePath $cerPath -Type CERT | Out-Null

$keyFile = Get-PrivateKeyFile -Certificate $cert

Write-Host ""
Write-Host "  Thumbprint  : $($cert.Thumbprint)"
Write-Host "  Subject     : $($cert.Subject)"
Write-Host "  Expires     : $($cert.NotAfter.ToString('yyyy-MM-dd'))"
Write-Host "  Store       : $personalStore\$($cert.Thumbprint)"
Write-Host "  Private key : $(if ($keyFile) { $keyFile } else { '(not resolvable)' })"
Write-Host "  Public cert : $cerPath"
Write-Host "  Inspect     : certmgr.msc, or Get-Item '$personalStore\$($cert.Thumbprint)'"
Write-Host ""

if ($Trust) {
    $store = 'Cert:\CurrentUser\TrustedPeople'
    if ($MachineWide) {
        $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
        $principal = New-Object Security.Principal.WindowsPrincipal($identity)
        if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
            throw "-MachineWide needs an elevated shell. Re-run as administrator, or drop -MachineWide."
        }
        $store = 'Cert:\LocalMachine\TrustedPeople'
    }
    $temp = Join-Path ([IO.Path]::GetTempPath()) ("mo-trust-" + [guid]::NewGuid().ToString('N') + '.cer')
    try {
        Export-Certificate -Cert $cert -FilePath $temp -Type CERT | Out-Null
        Import-Certificate -FilePath $temp -CertStoreLocation $store | Out-Null
        Write-Host "Trusted copy at $store\$($cert.Thumbprint)" -ForegroundColor Green
    }
    finally {
        if (Test-Path $temp) { Remove-Item $temp -Force }
    }
}

if ($ExportPath) {
    if ($Password -is [SecureString]) {
        $secure = $Password
    }
    elseif ($Password -is [string] -and $Password.Length -gt 0) {
        Write-Warning "A -Password given as text is in your shell history. Omit it to be asked instead."
        $secure = ConvertTo-SecureString -String $Password -AsPlainText -Force
    }
    else {
        $secure = Read-Host -Prompt 'Password for the .pfx' -AsSecureString
    }
    if (-not $secure -or $secure.Length -eq 0) { throw "The .pfx password cannot be empty." }
    $Password = $secure

    # Rooted check first: Join-Path concatenates an absolute -ExportPath instead of
    # resolving to it, and two-argument GetFullPath is not on Windows PowerShell 5.1.
    $full = $ExportPath
    if (-not [IO.Path]::IsPathRooted($full)) { $full = Join-Path (Get-Location).Path $full }
    $full = [IO.Path]::GetFullPath($full)
    Export-PfxCertificate -Cert $cert -FilePath $full -Password $Password | Out-Null
    Write-Host "Exported PFX to $full" -ForegroundColor Green

    # A .cred next to it lets Publish-Release.ps1 sign without asking. DPAPI ties it to
    # this user on this machine, so copying it elsewhere gains an attacker nothing.
    ConvertFrom-SecureString -SecureString $Password | Set-Content -LiteralPath "$full.cred" -NoNewline
    Write-Host "  Password saved to $full.cred (this user, this machine only)"

    if ((Split-Path -Parent $full) -eq $repoRoot -and (Split-Path -Leaf $full) -eq 'Mo.pfx') {
        Write-Host "  Publish-Release.ps1 will offer to use it automatically."
    }
    else {
        Write-Host "  Sign with it:  .\scripts\Publish-Release.ps1 -PfxPath '$full'"
    }
    Write-Host "Holds the private key. Keep it out of the repository; *.pfx is gitignored." -ForegroundColor Yellow
}

if ($UpdateProject) {
    if (-not (Test-Path $projectPath)) { throw "Project not found at $projectPath." }
    $text = Get-Content -Path $projectPath -Raw
    $pattern = '(?<open><PackageCertificateThumbprint>)[^<]*(?<close></PackageCertificateThumbprint>)'
    if ($text -notmatch $pattern) {
        throw "No <PackageCertificateThumbprint> element in $projectPath."
    }
    $updated = [regex]::Replace($text, $pattern, "`${open}$($cert.Thumbprint)`${close}")
    [IO.File]::WriteAllText($projectPath, $updated, (New-Object Text.UTF8Encoding($true)))
    Write-Host "Updated PackageCertificateThumbprint in Mo.csproj" -ForegroundColor Green
    Write-Host "That thumbprint is machine-specific — think before committing it." -ForegroundColor Yellow
}

# Why an MSIX install gets refused: the signer has to be in TrustedPeople, and which of
# the two stores counts depends on whether the install is per-user or for everyone.
$trustedUser = Test-Path "Cert:\CurrentUser\TrustedPeople\$($cert.Thumbprint)"
$trustedMachine = Test-Path "Cert:\LocalMachine\TrustedPeople\$($cert.Thumbprint)"

Write-Host "Trusted for install:" -ForegroundColor Cyan
Write-Host "  CurrentUser\TrustedPeople  : $(if ($trustedUser) { 'yes' } else { 'NO' })"
Write-Host "  LocalMachine\TrustedPeople : $(if ($trustedMachine) { 'yes' } else { 'NO' })"

if (-not $trustedUser -and -not $trustedMachine) {
    Write-Host ""
    Write-Host "Installing the package will be refused until one of those says yes." -ForegroundColor Yellow
    Write-Host "  This user:   .\scripts\New-SigningCertificate.ps1 -Trust"
    Write-Host "  Every user:  .\scripts\New-SigningCertificate.ps1 -Trust -MachineWide   (elevated shell)"
    Write-Host "  By hand:     double-click $cerPath, then pick Trusted People as the store."
}

Write-Host ""
Write-Host "Next: .\scripts\Publish-Release.ps1" -ForegroundColor Cyan
