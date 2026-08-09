namespace Mo.Core.DisplayConfiguration;

/// <summary>
/// Converts between the two dimension conventions the display APIs mix together.
/// </summary>
/// <remarks>
/// CCD's <c>DISPLAYCONFIG_SOURCE_MODE</c> and NVAPI's <c>PathInfo.Resolution</c> both
/// describe the source surface <em>before</em> rotation — the panel's own mode — while
/// the position stored alongside them is in post-rotation desktop coordinates. Verified
/// on a 2560x1440 panel at 270°: CCD reported source 2560x1440 at (0,0) while the GDI
/// desktop rectangle was 1440x2560, and NVAPI reported the same 2560x1440.
///
/// <c>MonitorInfo.Width/Height</c> is the desktop extent everywhere in Mo — the layout
/// canvas draws it directly — so every call into those APIs has to swap.
///
/// The swap is unconditional at 90/270. Guarding it on <c>width &gt; height</c> looks
/// safer but silently does nothing for a natively portrait panel, which is precisely the
/// case where getting it wrong writes a mode the panel does not have.
/// </remarks>
public static class RotationGeometry
{
    public static bool IsQuarterTurn(int degrees) => degrees is 90 or 270;

    /// <summary>Panel-native source mode → desktop extent.</summary>
    public static (int Width, int Height) ToDesktop(int sourceWidth, int sourceHeight, int degrees) =>
        IsQuarterTurn(degrees) ? (sourceHeight, sourceWidth) : (sourceWidth, sourceHeight);

    /// <summary>Desktop extent → panel-native source mode.</summary>
    public static (int Width, int Height) ToSource(int desktopWidth, int desktopHeight, int degrees) =>
        IsQuarterTurn(degrees) ? (desktopHeight, desktopWidth) : (desktopWidth, desktopHeight);
}
