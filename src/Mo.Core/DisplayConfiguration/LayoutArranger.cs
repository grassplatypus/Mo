namespace Mo.Core.DisplayConfiguration;

/// <summary>Arranges monitors into a row.</summary>
/// <remarks>Windows maps the cursor between monitors by absolute pixel coordinate, with
/// no notion of proportion, so where the pointer lands after crossing an edge is decided
/// entirely by how the rectangles are placed.</remarks>
public static class LayoutArranger
{
    public readonly record struct Placement(int X, int Y);

    /// <summary>Left to right, edges touching, vertical centres aligned.</summary>
    /// <remarks>Centres rather than top edges: two panels of the same physical height but
    /// different pixel heights (1440 beside a rotated 1920) only correspond at their
    /// centres, so aligning tops puts the crossing point near neither.</remarks>
    public static IReadOnlyList<Placement> Row(IReadOnlyList<(int Width, int Height)> monitors)
    {
        if (monitors.Count == 0) return [];

        int tallest = monitors.Max(m => m.Height);
        var placements = new Placement[monitors.Count];

        int x = 0;
        for (int i = 0; i < monitors.Count; i++)
        {
            // The tallest keeps y = 0 and the rest hang off its centre line, so the
            // arrangement never drifts away from the origin.
            int y = (tallest - monitors[i].Height) / 2;
            placements[i] = new Placement(x, y);
            x += monitors[i].Width;
        }

        return placements;
    }
}
