using System.Collections.Generic;

namespace Mo.Models;

public sealed class MonitorInfo
{
    // Identity (for matching across sessions)
    public string DevicePath { get; set; } = string.Empty;
    public string FriendlyName { get; set; } = string.Empty;
    public ushort EdidManufacturerId { get; set; }
    public ushort EdidProductCodeId { get; set; }
    public uint ConnectorInstance { get; set; }

    // Configuration
    public int PositionX { get; set; }
    public int PositionY { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public DisplayRotation Rotation { get; set; } = DisplayRotation.None;
    public uint RefreshRateNumerator { get; set; }
    public uint RefreshRateDenominator { get; set; }
    public int DpiScale { get; set; } = 100;
    public bool IsPrimary { get; set; }
    public bool IsEnabled { get; set; } = true;
    public bool HdrEnabled { get; set; }

    // Color & Brightness (DDC/CI via dxva2)
    public MonitorColorSettings? ColorSettings { get; set; }

    // Raw CCD identifiers (for apply)
    public long AdapterId { get; set; }
    public uint SourceId { get; set; }
    public uint TargetId { get; set; }

    public double RefreshRateHz => RefreshRateDenominator == 0 ? 0 : (double)RefreshRateNumerator / RefreshRateDenominator;
    public string ResolutionText => $"{Width} x {Height}";
}

public sealed class MonitorColorSettings
{
    // DDC/CI values (0-100 range from monitor)
    public int? Brightness { get; set; }
    public int? Contrast { get; set; }

    // RGB Drive (gain) — per-channel color adjustment (0-100)
    public int? RedGain { get; set; }
    public int? GreenGain { get; set; }
    public int? BlueGain { get; set; }

    public bool HasValues => Brightness.HasValue || Contrast.HasValue ||
                             RedGain.HasValue || GreenGain.HasValue || BlueGain.HasValue;
}

/// <summary>
/// Per-monitor DDC/CI capability flags. Not serialized — detected at runtime.
/// </summary>
public sealed class MonitorColorCapabilities
{
    public bool SupportsBrightness { get; set; }
    public bool SupportsContrast { get; set; }
    public bool SupportsRedGain { get; set; }
    public bool SupportsGreenGain { get; set; }
    public bool SupportsBlueGain { get; set; }

    /// <summary>Laptop internal display brightness via WMI (not DDC/CI)</summary>
    public bool SupportsWmiBrightness { get; set; }

    public bool SupportsAny => SupportsBrightness || SupportsContrast ||
                                SupportsRedGain || SupportsGreenGain || SupportsBlueGain ||
                                SupportsWmiBrightness;
}

public enum DisplayRotation
{
    None = 0,
    Rotate90 = 90,
    Rotate180 = 180,
    Rotate270 = 270
}
