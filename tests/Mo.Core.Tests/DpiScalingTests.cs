using Mo.Core.DisplayConfiguration;

namespace Mo.Core.Tests;

/// <summary>The scaling API counts steps away from a display's recommended scale, so
/// every conversion here hinges on reading minScaleRel as that offset.</summary>
public class DpiScalingTests
{
    // A display recommending 150% with 100%..300% available: 150 sits at index 2, so
    // minScaleRel is -2 and maxScaleRel is 5.
    private const int Min = -2;
    private const int Max = 5;

    [Fact]
    public void ToPercent_ZeroIsTheRecommendedScale()
    {
        Assert.Equal(150, DpiScaling.ToPercent(Min, 0));
    }

    [Theory]
    [InlineData(-2, 100)]
    [InlineData(-1, 125)]
    [InlineData(1, 175)]
    [InlineData(5, 300)]
    public void ToPercent_WalksTheLadder(int relative, int expected)
    {
        Assert.Equal(expected, DpiScaling.ToPercent(Min, relative));
    }

    /// <summary>A driver that does not implement the call answers with values that index
    /// off the ladder. That has to read as 100%, not throw.</summary>
    [Theory]
    [InlineData(0, -1)]
    [InlineData(0, 99)]
    [InlineData(-2, 40)]
    public void ToPercent_OutOfRangeFallsBackToDefault(int min, int relative)
    {
        Assert.Equal(100, DpiScaling.ToPercent(min, relative));
    }

    [Fact]
    public void AvailablePercentages_SpansMinToMaxInclusive()
    {
        Assert.Equal([100, 125, 150, 175, 200, 225, 250, 300], DpiScaling.AvailablePercentages(Min, Max));
    }

    [Fact]
    public void AvailablePercentages_ClampsToTheLadder()
    {
        // Recommended at 100% with nothing below it, and a max past the top of the list.
        var available = DpiScaling.AvailablePercentages(0, 99);

        Assert.Equal(DpiScaling.Steps, available);
    }

    [Fact]
    public void TryToRelative_IsTheInverseOfToPercent()
    {
        foreach (int percent in DpiScaling.AvailablePercentages(Min, Max))
        {
            Assert.True(DpiScaling.TryToRelative(Min, Max, percent, out int relative));
            Assert.Equal(percent, DpiScaling.ToPercent(Min, relative));
        }
    }

    [Fact]
    public void TryToRelative_RejectsAPercentageNotOnTheLadder()
    {
        Assert.False(DpiScaling.TryToRelative(Min, Max, 137, out _));
    }

    /// <summary>A profile captured on a display that allowed 400% must not write a step
    /// past what this display accepts.</summary>
    [Fact]
    public void TryToRelative_ClampsToWhatTheDisplayAllows()
    {
        Assert.True(DpiScaling.TryToRelative(Min, Max, 500, out int relative));
        Assert.Equal(Max, relative);
    }
}
