using Mo.Core.DisplayConfiguration;
using static Mo.Core.DisplayConfiguration.ProfileDiffer;

namespace Mo.Core.Tests;

public class ProfileDifferTests
{
    private static MonitorSnapshot Monitor(
        string name = "DELL U2720Q",
        int x = 0, int y = 0,
        int width = 3840, int height = 2160,
        int rotation = 0,
        uint num = 60, uint den = 1,
        int dpi = 100,
        bool primary = true)
        => new(name, x, y, width, height, rotation, num, den, dpi, primary);

    /// <summary>Pairs index i in `before` with index i in `after`.</summary>
    private static Dictionary<int, int> Aligned(int count) =>
        Enumerable.Range(0, count).ToDictionary(i => i, i => i);

    [Fact]
    public void IdenticalSnapshotsProduceNoChanges()
    {
        var result = Compare([Monitor()], [Monitor()], Aligned(1));

        Assert.False(result.HasChanges);
        Assert.Empty(result.Changes);
    }

    [Fact]
    public void MovingAMonitorReportsPositionWithBothCoordinates()
    {
        var result = Compare([Monitor(x: 0, y: 0)], [Monitor(x: -1920, y: 120)], Aligned(1));

        var change = Assert.Single(result.Changes);
        Assert.Equal(ChangeType.PositionChanged, change.Type);
        Assert.Equal("(0,0) -> (-1920,120)", change.Details);
    }

    [Fact]
    public void ResolutionChangeIsReported()
    {
        var result = Compare([Monitor(width: 3840, height: 2160)],
                             [Monitor(width: 2560, height: 1440)], Aligned(1));

        var change = Assert.Single(result.Changes);
        Assert.Equal(ChangeType.ResolutionChanged, change.Type);
        Assert.Equal("3840x2160 -> 2560x1440", change.Details);
    }

    [Fact]
    public void RotationChangeIsReported()
    {
        var result = Compare([Monitor(rotation: 0)], [Monitor(rotation: 90)], Aligned(1));

        Assert.Equal(ChangeType.RotationChanged, Assert.Single(result.Changes).Type);
    }

    // 59.94 Hz is stored as 60000/1001, which is why the ratio is compared rather
    // than a rounded integer.
    [Fact]
    public void RefreshRateChangeFormatsAsHz()
    {
        var result = Compare([Monitor(num: 60000, den: 1001)],
                             [Monitor(num: 144, den: 1)], Aligned(1));

        var change = Assert.Single(result.Changes);
        Assert.Equal(ChangeType.RefreshRateChanged, change.Type);
        Assert.Equal("59.9Hz -> 144.0Hz", change.Details);
    }

    [Fact]
    public void ZeroDenominatorDegradesToAQuestionMarkRatherThanDividingByZero()
    {
        var result = Compare([Monitor(num: 60, den: 0)], [Monitor(num: 60, den: 1)], Aligned(1));

        Assert.Equal("?Hz -> 60.0Hz", Assert.Single(result.Changes).Details);
    }

    [Fact]
    public void DpiChangeIsReportedAsPercentages()
    {
        var result = Compare([Monitor(dpi: 100)], [Monitor(dpi: 150)], Aligned(1));

        var change = Assert.Single(result.Changes);
        Assert.Equal(ChangeType.DpiChanged, change.Type);
        Assert.Equal("100% -> 150%", change.Details);
    }

    [Fact]
    public void GainingPrimaryAndLosingItAreDistinguished()
    {
        var promoted = Compare([Monitor(primary: false)], [Monitor(primary: true)], Aligned(1));
        Assert.Equal("-> Primary", Assert.Single(promoted.Changes).Details);

        var demoted = Compare([Monitor(primary: true)], [Monitor(primary: false)], Aligned(1));
        Assert.Equal("-> Not Primary", Assert.Single(demoted.Changes).Details);
    }

    [Fact]
    public void SeveralDifferencesOnOneMonitorAreAllReported()
    {
        var before = Monitor(x: 0, width: 3840, height: 2160, rotation: 0, dpi: 100);
        var after = Monitor(x: 1920, width: 2560, height: 1440, rotation: 270, dpi: 125);

        var result = Compare([before], [after], Aligned(1));

        Assert.Equal(4, result.Changes.Count);
        Assert.Contains(result.Changes, c => c.Type == ChangeType.PositionChanged);
        Assert.Contains(result.Changes, c => c.Type == ChangeType.ResolutionChanged);
        Assert.Contains(result.Changes, c => c.Type == ChangeType.RotationChanged);
        Assert.Contains(result.Changes, c => c.Type == ChangeType.DpiChanged);
    }

    // An unmatched "before" monitor is one the profile expects but the machine no
    // longer has — undocking a laptop, for example.
    [Fact]
    public void UnmatchedBeforeMonitorIsReportedAsRemoved()
    {
        var result = Compare(
            [Monitor(name: "Built-in"), Monitor(name: "Dock LG")],
            [Monitor(name: "Built-in")],
            new Dictionary<int, int> { [0] = 0 });

        var change = Assert.Single(result.Changes);
        Assert.Equal(ChangeType.MonitorRemoved, change.Type);
        Assert.Equal("Dock LG", change.MonitorName);
        Assert.Equal(1, change.MonitorIndex);
    }

    [Fact]
    public void UnmatchedAfterMonitorIsReportedAsAdded()
    {
        var result = Compare(
            [Monitor(name: "Built-in")],
            [Monitor(name: "Built-in"), Monitor(name: "New Panel")],
            new Dictionary<int, int> { [0] = 0 });

        var change = Assert.Single(result.Changes);
        Assert.Equal(ChangeType.MonitorAdded, change.Type);
        Assert.Equal("New Panel", change.MonitorName);
        Assert.Equal(1, change.MonitorIndex);
    }

    [Fact]
    public void MonitorsCanBeMatchedOutOfOrder()
    {
        // The matcher pairs by identity, not position, so index 0 may map to index 1.
        var before = new[] { Monitor(name: "A", x: 0), Monitor(name: "B", x: 1920) };
        var after = new[] { Monitor(name: "B", x: 1920), Monitor(name: "A", x: 0) };

        var result = Compare(before, after, new Dictionary<int, int> { [0] = 1, [1] = 0 });

        Assert.False(result.HasChanges);
    }

    [Fact]
    public void SwappingTwoMonitorPositionsReportsBoth()
    {
        var before = new[] { Monitor(name: "A", x: 0), Monitor(name: "B", x: 1920) };
        var after = new[] { Monitor(name: "A", x: 1920), Monitor(name: "B", x: 0) };

        var result = Compare(before, after, Aligned(2));

        Assert.Equal(2, result.Changes.Count);
        Assert.All(result.Changes, c => Assert.Equal(ChangeType.PositionChanged, c.Type));
        Assert.Contains(result.Changes, c => c.MonitorName == "A");
        Assert.Contains(result.Changes, c => c.MonitorName == "B");
    }

    [Fact]
    public void EmptySnapshotsProduceNoChanges()
    {
        var result = Compare([], [], []);

        Assert.False(result.HasChanges);
    }

    [Fact]
    public void EverythingRemovedAndEverythingAddedWhenNothingMatches()
    {
        var result = Compare(
            [Monitor(name: "Old")],
            [Monitor(name: "New")],
            []);

        Assert.Equal(2, result.Changes.Count);
        Assert.Contains(result.Changes, c => c is { Type: ChangeType.MonitorRemoved, MonitorName: "Old" });
        Assert.Contains(result.Changes, c => c is { Type: ChangeType.MonitorAdded, MonitorName: "New" });
    }
}
