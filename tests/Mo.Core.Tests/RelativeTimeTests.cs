using Mo.Core.Formatting;
using static Mo.Core.Formatting.RelativeTime;

namespace Mo.Core.Tests;

public class RelativeTimeTests
{
    private static readonly DateTime Now = new(2026, 8, 7, 12, 0, 0, DateTimeKind.Utc);

    private static Result At(TimeSpan ago) => Describe(Now - ago, Now);

    [Fact]
    public void UnderAMinuteIsJustNow()
    {
        Assert.Equal(Bucket.JustNow, At(TimeSpan.Zero).Bucket);
        Assert.Equal(Bucket.JustNow, At(TimeSpan.FromSeconds(59)).Bucket);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(30, 30)]
    [InlineData(59, 59)]
    public void UnderAnHourReportsMinutes(int minutesAgo, int expected)
    {
        var r = At(TimeSpan.FromMinutes(minutesAgo));
        Assert.Equal(Bucket.Minutes, r.Bucket);
        Assert.Equal(expected, r.Value);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(12)]
    [InlineData(23)]
    public void UnderADayReportsHours(int hoursAgo)
    {
        var r = At(TimeSpan.FromHours(hoursAgo));
        Assert.Equal(Bucket.Hours, r.Bucket);
        Assert.Equal(hoursAgo, r.Value);
    }

    [Fact]
    public void TwentyFourToFortyEightHoursIsYesterday()
    {
        Assert.Equal(Bucket.Yesterday, At(TimeSpan.FromHours(24)).Bucket);
        Assert.Equal(Bucket.Yesterday, At(TimeSpan.FromHours(47)).Bucket);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(6)]
    public void UnderAWeekReportsDays(int daysAgo)
    {
        var r = At(TimeSpan.FromDays(daysAgo));
        Assert.Equal(Bucket.Days, r.Bucket);
        Assert.Equal(daysAgo, r.Value);
    }

    [Fact]
    public void AWeekOrMoreFallsBackToAnAbsoluteDate()
    {
        Assert.Equal(Bucket.Absolute, At(TimeSpan.FromDays(7)).Bucket);
        Assert.Equal(Bucket.Absolute, At(TimeSpan.FromDays(365)).Bucket);
    }

    // Clock skew, or a profile file written by a machine running slightly ahead.
    [Fact]
    public void FutureTimestampsDegradeToJustNowRatherThanNegatives()
    {
        var r = Describe(Now + TimeSpan.FromHours(3), Now);
        Assert.Equal(Bucket.JustNow, r.Bucket);
        Assert.Equal(0, r.Value);
    }
}
