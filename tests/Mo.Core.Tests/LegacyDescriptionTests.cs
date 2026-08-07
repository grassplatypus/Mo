using Mo.Core.Formatting;

namespace Mo.Core.Tests;

public class LegacyDescriptionTests
{
    [Theory]
    // Exactly the shapes 0.20.3 and earlier wrote, including the Korean-locale
    // timestamp a ko-KR machine produced.
    [InlineData("1 monitor(s) — 2026-04-12 오전 3:38")]
    [InlineData("2 monitor(s) — 2026-04-12 오후 3:07")]
    [InlineData("3 monitor(s) — 4/12/2026 3:07 PM")]
    [InlineData("1 monitor(s)")]
    [InlineData("  2 monitor(s)  ")]
    public void RecognisesGeneratedText(string description)
    {
        Assert.True(LegacyDescription.IsGenerated(description));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    // A note the user actually typed must survive, even when it mentions monitors.
    [InlineData("Work setup — 2 monitors, left one rotated")]
    [InlineData("모니터 2개 구성")]
    [InlineData("monitor(s)")]
    [InlineData("Gaming")]
    public void LeavesUserWrittenNotesAlone(string? description)
    {
        Assert.False(LegacyDescription.IsGenerated(description));
    }
}
