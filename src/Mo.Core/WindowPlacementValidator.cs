namespace Mo.Core;

// Decides whether a remembered window rectangle is still usable. This app rearranges
// monitors for a living, so the display a window last sat on is unusually likely to be
// gone by the next launch - restoring blindly puts it off-screen and unreachable.
public static class WindowPlacementValidator
{
    public readonly record struct Rect(int X, int Y, int Width, int Height)
    {
        public int Right => X + Width;
        public int Bottom => Y + Height;
    }

    // Enough of the title bar must land on screen for the user to grab it.
    private const int MinVisibleWidth = 120;
    private const int MinVisibleHeight = 40;

    /// <summary>True when the window overlaps a work area enough to be grabbed.</summary>
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
}
