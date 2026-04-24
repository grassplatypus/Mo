using Mo.Core.DisplayConfiguration;

namespace Mo.Core.Tests;

public class SnapCalculatorTests
{
    private static DisplayTopology.MonitorRect Rect(int x, int y, int w = 1920, int h = 1080)
        => new(x, y, w, h);

    [Fact]
    public void ComputeSnap_WithinTolerance_SnapsToNeighborRightEdge()
    {
        var dragging = Rect(0, 0);
        var others = new List<DisplayTopology.MonitorRect> { Rect(1920, 0) };

        // Request dragging to overlap heavily; snap should pull its left to others' left (0)
        // or its right to others' left (-1920). The smallest move wins.
        var result = SnapCalculator.ComputeSnap(dragging, requestedX: 15, requestedY: 5, others, toleranceDesktopPx: 30);

        Assert.Equal(0, result.X);
        Assert.Equal(0, result.Y);
        Assert.NotEmpty(result.Guides);
    }

    [Fact]
    public void ComputeSnap_OutsideTolerance_NoSnap()
    {
        var dragging = Rect(0, 0);
        var others = new List<DisplayTopology.MonitorRect> { Rect(5000, 5000) };

        var result = SnapCalculator.ComputeSnap(dragging, requestedX: 100, requestedY: 200, others, toleranceDesktopPx: 30);

        Assert.Equal(100, result.X);
        Assert.Equal(200, result.Y);
        Assert.Empty(result.Guides);
    }

    [Fact]
    public void WouldOverlap_SharedEdge_NotOverlap()
    {
        var a = Rect(0, 0);
        var b = Rect(1920, 0);
        Assert.False(SnapCalculator.WouldOverlap(a, new[] { b }));
    }

    [Fact]
    public void WouldOverlap_Overlapping_True()
    {
        var a = Rect(0, 0);
        var b = Rect(100, 0);
        Assert.True(SnapCalculator.WouldOverlap(a, new[] { b }));
    }

    [Fact]
    public void ResolveOverlap_PicksMinimumDisplacement()
    {
        // Target overlaps by a thin slice on the X axis (100 px) but shares the full Y.
        // Minimum-displacement push is horizontal to the right edge of the neighbor.
        var target = new DisplayTopology.MonitorRect(1820, 0, 1920, 1080);
        var others = new List<DisplayTopology.MonitorRect> { Rect(0, 0) };

        var (x, y) = SnapCalculator.ResolveOverlap(target, others);

        Assert.Equal(1920, x);
        Assert.Equal(0, y);
    }

    [Fact]
    public void ResolveOverlap_DominantHorizontalOverlap_PushesVertically()
    {
        // Target overlaps heavily on X (1820 px) but only 100 px on Y — cheaper to push vertically.
        var target = new DisplayTopology.MonitorRect(100, 0, 1920, 1080);
        var others = new List<DisplayTopology.MonitorRect> { Rect(0, 0) };

        var (x, y) = SnapCalculator.ResolveOverlap(target, others);

        Assert.Equal(100, x);
        Assert.True(y == 1080 || y == -1080);
    }

    [Fact]
    public void ResolveOverlap_NoOverlap_ReturnsOriginal()
    {
        var target = new DisplayTopology.MonitorRect(2000, 300, 1920, 1080);
        var others = new List<DisplayTopology.MonitorRect> { Rect(0, 0) };

        var (x, y) = SnapCalculator.ResolveOverlap(target, others);

        Assert.Equal(2000, x);
        Assert.Equal(300, y);
    }

    [Fact]
    public void WouldOverlap_PortraitRotatedMonitor()
    {
        // Simulates a 90°-rotated monitor whose logical dimensions are 1080x1920.
        var portrait = new DisplayTopology.MonitorRect(1920, 0, 1080, 1920);
        var landscape = Rect(0, 0);
        Assert.False(SnapCalculator.WouldOverlap(portrait, new[] { landscape }));

        var overlapping = new DisplayTopology.MonitorRect(1800, 0, 1080, 1920);
        Assert.True(SnapCalculator.WouldOverlap(overlapping, new[] { landscape }));
    }
}
