using Mo.Core.DisplayConfiguration;

namespace Mo.Core.Tests;

public class LayoutArrangerTests
{
    [Fact]
    public void Row_PlacesMonitorsEdgeToEdge()
    {
        var placements = LayoutArranger.Row([(2560, 1440), (1920, 1080)]);

        Assert.Equal(0, placements[0].X);
        Assert.Equal(2560, placements[1].X);
    }

    [Fact]
    public void Row_LeavesTheTallestAtTheOrigin()
    {
        var placements = LayoutArranger.Row([(2560, 1440), (1080, 1920)]);

        Assert.Equal(0, placements[1].Y);
    }

    /// <summary>The case this exists for: a QHD panel with a rotated FHD beside it. Their
    /// pixel heights differ, so crossing at the centre only lands on the centre when the
    /// centres are what line up.</summary>
    [Fact]
    public void Row_AlignsCentresNotTopEdges()
    {
        var placements = LayoutArranger.Row([(2560, 1440), (1080, 1920)]);

        int landscapeCentre = placements[0].Y + 1440 / 2;
        int portraitCentre = placements[1].Y + 1920 / 2;

        Assert.Equal(portraitCentre, landscapeCentre);

        // Concretely: the shorter one is pushed down by half the difference.
        Assert.Equal(240, placements[0].Y);
    }

    [Fact]
    public void Row_LeavesEqualHeightsFlush()
    {
        var placements = LayoutArranger.Row([(2560, 1440), (2560, 1440)]);

        Assert.Equal(0, placements[0].Y);
        Assert.Equal(0, placements[1].Y);
    }

    [Fact]
    public void Row_HandlesAnEmptyLayout()
    {
        Assert.Empty(LayoutArranger.Row([]));
    }
}
