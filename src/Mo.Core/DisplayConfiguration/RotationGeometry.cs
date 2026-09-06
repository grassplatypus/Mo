namespace Mo.Core.DisplayConfiguration;

/// <summary>Converts between the two dimension conventions the display APIs mix: panel
/// source mode (pre-rotation) and desktop extent (post-rotation). The swap at 90/270 is
/// unconditional — see .claude/rules/30-display-apis.md for why.</summary>
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
