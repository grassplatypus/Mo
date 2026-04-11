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
    public bool HdrEnabled { get; set; }

    // Raw CCD identifiers (for apply)
    public long AdapterId { get; set; }
    public uint SourceId { get; set; }
    public uint TargetId { get; set; }

    public double RefreshRateHz => RefreshRateDenominator == 0 ? 0 : (double)RefreshRateNumerator / RefreshRateDenominator;
    public string ResolutionText => $"{Width} x {Height}";
}

public enum DisplayRotation
{
    None = 0,
    Rotate90 = 90,
    Rotate180 = 180,
    Rotate270 = 270
}
