using Mo.Core.DisplayConfiguration;

namespace Mo.Core.Tests;

public class GainReadBackTests
{
    [Fact]
    public void EveryChannelStuck_IsIgnored()
    {
        Assert.True(GainReadBack.WasIgnored((30, 50), (30, 50), (30, 50)));
    }

    [Fact]
    public void EveryChannelMoved_IsNotIgnored()
    {
        Assert.False(GainReadBack.WasIgnored((30, 30), (40, 40), (60, 60)));
    }

    [Fact]
    public void OneChannelMoved_IsNotIgnored()
    {
        Assert.False(GainReadBack.WasIgnored((30, 50), (30, 50), (30, 30)));
    }

    [Fact]
    public void WithinTolerance_CountsAsTaken()
    {
        Assert.False(GainReadBack.WasIgnored((30, 33), (30, 27), (30, 30)));
    }

    [Fact]
    public void JustOutsideTolerance_CountsAsStuck()
    {
        Assert.True(GainReadBack.WasIgnored((30, 34)));
    }

    [Fact]
    public void UnreadableChannelDropsOut()
    {
        Assert.True(GainReadBack.WasIgnored((30, 50), (30, null), (null, 50)));
    }

    [Fact]
    public void NothingComparable_IsNotIgnored()
    {
        Assert.False(GainReadBack.WasIgnored((30, null), (null, 50), (null, null)));
    }

    [Fact]
    public void NoChannels_IsNotIgnored()
    {
        Assert.False(GainReadBack.WasIgnored());
    }
}
