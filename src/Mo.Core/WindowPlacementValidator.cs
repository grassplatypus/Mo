namespace Mo.Core;

/// <summary>
/// Decides whether a remembered window rectangle is still usable.
/// </summary>
/// <remarks>
/// Mo's whole job is rearranging monitors, so the display a window was last placed on
/// is unusually likely to be gone, moved, or resized by the next launch. Restoring a
/// saved rectangle blindly can put the window entirely off-screen, where it is running
/// but unreachable.
/// </remarks>
public static class WindowPlacementValidator
{
    public readonly record struct Rect(int X, int Y, int Width, int Height)
    {
        public int Right => X + Width;
        public int Bottom => Y + Height;
    }

    /// <summary>
    /// Enough of the title bar must land inside a work area for the user to grab it.
    /// </summary>
    private const int MinVisibleWidth = 120;
    private const int MinVisibleHeight = 40;

    /// <summary>
    /// True when <paramref name="placement"/> overlaps some work area by enough for the
    /// user to see and drag the window.
    /// </summary>
    public static bool IsReachable(Rect placement, IReadOnlyList<Rect> workAreas)
    {
        if (placement.Width <= 0 || placement.Height <= 0) return false;
        if (workAreas.Count == 0) return false;

        foreach (var area in workAreas)
        {
            int overlapW = Math.Min(placement.Right, area.Right) - Math.Max(placement.X, area.X);
            int overlapH = Math.Min(placement.Bottom, area.Bottom) - Math.Max(placement.Y, area.Y);

            if (overlapW >= Math.Min(MinVisibleWidth, placement.Width) &&
                overlapH >= Math.Min(MinVisibleHeight, placement.Height))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Clamps a window to fit inside a work area, shrinking it only if it is larger
    /// than the area itself. Used when the saved size came from a bigger monitor.
    /// </summary>
    public static Rect ClampInto(Rect placement, Rect workArea)
    {
        int width = Math.Min(placement.Width, workArea.Width);
        int height = Math.Min(placement.Height, workArea.Height);

        int x = Math.Clamp(placement.X, workArea.X, Math.Max(workArea.X, workArea.Right - width));
        int y = Math.Clamp(placement.Y, workArea.Y, Math.Max(workArea.Y, workArea.Bottom - height));

        return new Rect(x, y, width, height);
    }
}
