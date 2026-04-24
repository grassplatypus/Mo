using Mo.Core.DisplayConfiguration;

namespace Mo.Core.Tests;

public class DisplayTopologyTests
{
    [Fact]
    public void ComputeBoundingBox_TwoMonitors_CorrectBounds()
    {
        var monitors = new List<DisplayTopology.MonitorRect>
        {
            new(0, 0, 1920, 1080),
            new(1920, 0, 2560, 1440),
        };

        var bounds = DisplayTopology.ComputeBoundingBox(monitors);

        Assert.Equal(0, bounds.Left);
        Assert.Equal(0, bounds.Top);
        Assert.Equal(4480, bounds.Right);
        Assert.Equal(1440, bounds.Bottom);
    }

    [Fact]
    public void ComputeBoundingBox_Empty_ReturnsZero()
    {
        var bounds = DisplayTopology.ComputeBoundingBox([]);
        Assert.Equal(0, bounds.Width);
        Assert.Equal(0, bounds.Height);
    }

    [Fact]
    public void ComputeScaleFactor_FitsWithinCanvas()
    {
        var bounds = new DisplayTopology.Bounds(0, 0, 3840, 2160);
        double scale = DisplayTopology.ComputeScaleFactor(bounds, 800, 600, 20);

        double scaledWidth = 3840 * scale;
        double scaledHeight = 2160 * scale;

        Assert.True(scaledWidth <= 760); // 800 - 2*20
        Assert.True(scaledHeight <= 560); // 600 - 2*20
    }

    [Fact]
    public void TransformToCanvas_CentersOutput()
    {
        var bounds = new DisplayTopology.Bounds(0, 0, 100, 100);
        var (x, y) = DisplayTopology.TransformToCanvas(0, 0, bounds, 1.0, 200, 200);

        Assert.Equal(50, x);
        Assert.Equal(50, y);
    }

    [Fact]
    public void TransformFromCanvas_RoundTripsOrigin()
    {
        var bounds = new DisplayTopology.Bounds(-1080, 0, 2840, 1080);
        var (cx, cy) = DisplayTopology.TransformToCanvas(1000, 300, bounds, 0.25, 600, 400);
        var (mx, my) = DisplayTopology.TransformFromCanvas(cx, cy, bounds, 0.25, 600, 400);

        Assert.Equal(1000, mx);
        Assert.Equal(300, my);
    }

    [Fact]
    public void TransformFromCanvas_NegativeOrigin_RoundTrips()
    {
        var bounds = new DisplayTopology.Bounds(-1920, -1080, 1920, 1080);
        var (cx, cy) = DisplayTopology.TransformToCanvas(-500, -200, bounds, 0.1, 400, 400);
        var (mx, my) = DisplayTopology.TransformFromCanvas(cx, cy, bounds, 0.1, 400, 400);

        Assert.Equal(-500, mx);
        Assert.Equal(-200, my);
    }
}
