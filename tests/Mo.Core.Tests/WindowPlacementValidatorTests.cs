using Mo.Core;
using static Mo.Core.WindowPlacementValidator;

namespace Mo.Core.Tests;

public class WindowPlacementValidatorTests
{
    // A typical two-monitor desktop: 1920x1080 primary, a second panel to its left.
    private static readonly Rect Primary = new(0, 0, 1920, 1040);
    private static readonly Rect Secondary = new(-2560, 0, 2560, 1400);
    private static readonly IReadOnlyList<Rect> TwoMonitors = [Primary, Secondary];
    private static readonly IReadOnlyList<Rect> OnlyPrimary = [Primary];

    [Fact]
    public void WindowFullyOnAMonitorIsReachable()
    {
        Assert.True(IsReachable(new Rect(100, 100, 1000, 680), TwoMonitors));
    }

    [Fact]
    public void WindowOnTheSecondMonitorIsReachable()
    {
        Assert.True(IsReachable(new Rect(-2000, 200, 1000, 680), TwoMonitors));
    }

    // The case this exists for: the profile that owned that monitor got unplugged.
    [Fact]
    public void WindowOnAMonitorThatIsGoneIsNotReachable()
    {
        Assert.False(IsReachable(new Rect(-2000, 200, 1000, 680), OnlyPrimary));
    }

    [Fact]
    public void PartlyOffScreenButGrabbableIsReachable()
    {
        // Hangs off the right edge, leaving ~200px of title bar visible.
        Assert.True(IsReachable(new Rect(1720, 50, 1000, 680), OnlyPrimary));
    }

    [Fact]
    public void SliverTooSmallToGrabIsNotReachable()
    {
        // Only 10px of the window remains on screen.
        Assert.False(IsReachable(new Rect(1910, 50, 1000, 680), OnlyPrimary));
    }

    [Fact]
    public void WindowAboveTheTopEdgeIsNotReachable()
    {
        Assert.False(IsReachable(new Rect(100, -900, 1000, 680), OnlyPrimary));
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(100, 0, 100)]
    [InlineData(100, 100, 0)]
    public void DegenerateSizesAreNotReachable(int _, int w, int h)
    {
        Assert.False(IsReachable(new Rect(10, 10, w, h), OnlyPrimary));
    }

    [Fact]
    public void NoMonitorsMeansNothingIsReachable()
    {
        Assert.False(IsReachable(new Rect(0, 0, 800, 600), []));
    }
}
