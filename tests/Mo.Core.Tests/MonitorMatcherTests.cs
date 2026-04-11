using Mo.Core.DisplayConfiguration;
using static Mo.Core.DisplayConfiguration.MonitorMatcher;

namespace Mo.Core.Tests;

public class MonitorMatcherTests
{
    [Fact]
    public void Match_ExactDevicePath_ReturnsFullMatch()
    {
        var profile = new List<MonitorIdentity>
        {
            new("\\\\?\\DISPLAY#DEL1234#1", 0x10AC, 0x1234, 1, "DELL U2720Q"),
            new("\\\\?\\DISPLAY#SAM5678#2", 0x4C2D, 0x5678, 2, "Samsung Odyssey"),
        };
        var current = new List<MonitorIdentity>
        {
            new("\\\\?\\DISPLAY#SAM5678#2", 0x4C2D, 0x5678, 2, "Samsung Odyssey"),
            new("\\\\?\\DISPLAY#DEL1234#1", 0x10AC, 0x1234, 1, "DELL U2720Q"),
        };

        var result = Match(profile, current);

        Assert.Equal(2, result.Matches.Count);
        Assert.Empty(result.UnmatchedProfile);
        Assert.Empty(result.UnmatchedCurrent);
        Assert.Equal(1, result.Matches[0]); // profile[0] (DELL) -> current[1] (DELL)
        Assert.Equal(0, result.Matches[1]); // profile[1] (SAM) -> current[0] (SAM)
    }

    [Fact]
    public void Match_EdidFallback_MatchesByManufacturer()
    {
        var profile = new List<MonitorIdentity>
        {
            new("old-path", 0x10AC, 0x1234, 1, "DELL U2720Q"),
        };
        var current = new List<MonitorIdentity>
        {
            new("new-path", 0x10AC, 0x1234, 1, "DELL U2720Q"),
        };

        var result = Match(profile, current);

        Assert.Single(result.Matches);
        Assert.Equal(0, result.Matches[0]);
    }

    [Fact]
    public void Match_MissingMonitor_ReportsUnmatched()
    {
        var profile = new List<MonitorIdentity>
        {
            new("path1", 0x10AC, 0x1234, 1, "DELL"),
            new("path2", 0x4C2D, 0x5678, 2, "Samsung"),
        };
        var current = new List<MonitorIdentity>
        {
            new("path1", 0x10AC, 0x1234, 1, "DELL"),
        };

        var result = Match(profile, current);

        Assert.Single(result.Matches);
        Assert.Single(result.UnmatchedProfile);
        Assert.Equal(1, result.UnmatchedProfile[0]);
    }

    [Fact]
    public void Match_SingleRemainingHeuristic_AutoMatches()
    {
        var profile = new List<MonitorIdentity>
        {
            new("path1", 0x10AC, 0x1234, 1, "DELL"),
            new("unknown", 0, 0, 0, ""),
        };
        var current = new List<MonitorIdentity>
        {
            new("path1", 0x10AC, 0x1234, 1, "DELL"),
            new("different", 0xAAAA, 0xBBBB, 1, "LG"),
        };

        var result = Match(profile, current);

        Assert.Equal(2, result.Matches.Count);
        Assert.Empty(result.UnmatchedProfile);
        Assert.Empty(result.UnmatchedCurrent);
    }

    [Fact]
    public void Match_EmptyLists_ReturnsEmpty()
    {
        var result = Match([], []);

        Assert.Empty(result.Matches);
        Assert.Empty(result.UnmatchedProfile);
        Assert.Empty(result.UnmatchedCurrent);
    }
}
