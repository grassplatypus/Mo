<#
.SYNOPSIS
Renders the MSIX tile and splash images from Mo's own icon.

.DESCRIPTION
The tile assets shipped as the WinUI template's grey placeholders. This regenerates them
from src\Mo\Assets\AppIcon.ico. Run it whenever that icon changes.

.NOTES
Output names carry the MRT qualifiers the manifest already references. The exe icon is
separate: it comes from the ApplicationIcon property in Mo.csproj.

.PARAMETER IconPath
Source icon. Defaults to src\Mo\Assets\AppIcon.ico.

.PARAMETER OutputDir
Where the PNGs land. Defaults to src\Mo\Assets.

.PARAMETER Help
Prints this help and exits.

.EXAMPLE
.\scripts\New-AppIcons.ps1
#>
[CmdletBinding()]
param(
    [string]$IconPath,
    [string]$OutputDir,
    [switch]$Help
)

if ($Help) { Get-Help $PSCommandPath -Detailed; return }

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName WindowsBase

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $IconPath)  { $IconPath  = Join-Path $repoRoot 'src\Mo\Assets\AppIcon.ico' }
if (-not $OutputDir) { $OutputDir = Join-Path $repoRoot 'src\Mo\Assets' }

if (-not (Test-Path $IconPath)) { throw "Icon not found: $IconPath" }
if (-not (Test-Path $OutputDir)) { throw "Output directory not found: $OutputDir" }

# Square tiles are the logo edge to edge; the wide tile and the splash screen are the
# logo centred on transparency, at a fraction of the shorter side.
$targets = @(
    # No MRT qualifier in the name, so ms-appx resolves it verbatim. The UI loads this
    # rather than the .ico, whose frame the decoder picks for itself.
    @{ Name = 'AppIcon.png';                                       W = 256;  H = 256; Fill = 1.0 }
    @{ Name = 'Square44x44Logo.scale-200.png';                     W = 88;   H = 88;  Fill = 1.0 }
    @{ Name = 'Square44x44Logo.targetsize-24_altform-unplated.png'; W = 24;   H = 24;  Fill = 1.0 }
    @{ Name = 'Square150x150Logo.scale-200.png';                   W = 300;  H = 300; Fill = 1.0 }
    @{ Name = 'StoreLogo.png';                                     W = 50;   H = 50;  Fill = 1.0 }
    @{ Name = 'LockScreenLogo.scale-200.png';                      W = 48;   H = 48;  Fill = 1.0 }
    @{ Name = 'Wide310x150Logo.scale-200.png';                     W = 620;  H = 310; Fill = 0.62 }
    @{ Name = 'SplashScreen.scale-200.png';                        W = 1240; H = 600; Fill = 0.40 }
)

function Get-LargestFrame {
    param([string]$Path)

    # WIC, not GDI+. System.Drawing.Icon mis-decodes PNG-compressed ICO frames into
    # noise and throws outright on the larger ones, which is exactly the shape of this
    # file: it carries 16 through 256 and only the small frames survive that path.
    $decoder = [System.Windows.Media.Imaging.BitmapDecoder]::Create(
        [Uri]$Path,
        [System.Windows.Media.Imaging.BitmapCreateOptions]::PreservePixelFormat,
        [System.Windows.Media.Imaging.BitmapCacheOption]::OnLoad)

    $best = $null
    foreach ($f in $decoder.Frames) {
        if ($null -eq $best -or $f.PixelWidth -gt $best.PixelWidth) { $best = $f }
    }

    # Hand the WIC frame to System.Drawing through a PNG in memory, so the drawing code
    # below stays ordinary GDI+.
    $encoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($best))
    $ms = New-Object System.IO.MemoryStream
    $encoder.Save($ms)
    $ms.Position = 0
    return [System.Drawing.Image]::FromStream($ms)
}

$source = Get-LargestFrame -Path (Resolve-Path $IconPath).Path
Write-Host "Source: $IconPath ($($source.Width)x$($source.Height))"

try {
    foreach ($t in $targets) {
        $canvas = New-Object System.Drawing.Bitmap($t.W, $t.H, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $g = [System.Drawing.Graphics]::FromImage($canvas)
        try {
            $g.Clear([System.Drawing.Color]::Transparent)
            $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $g.PixelOffsetMode  = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $g.SmoothingMode    = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality

            $side = [Math]::Round([Math]::Min($t.W, $t.H) * $t.Fill)
            $x = [Math]::Round(($t.W - $side) / 2)
            $y = [Math]::Round(($t.H - $side) / 2)
            $g.DrawImage($source, $x, $y, $side, $side)
        }
        finally { $g.Dispose() }

        $path = Join-Path $OutputDir $t.Name
        $canvas.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
        $canvas.Dispose()
        Write-Host ("  {0,-52} {1}x{2}" -f $t.Name, $t.W, $t.H)
    }
}
finally { $source.Dispose() }

Write-Host "Done."
