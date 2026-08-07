using Mo.Core.Formatting;

namespace Mo.Helpers;

/// <summary>
/// Localized wording for <see cref="RelativeTime"/> buckets. The bucketing lives in
/// Mo.Core so it can be unit-tested without a resource loader; only the phrasing is
/// here.
/// </summary>
public static class RelativeTimeText
{
    public static string Format(DateTime whenUtc) => Format(whenUtc, DateTime.UtcNow);

    public static string Format(DateTime whenUtc, DateTime nowUtc)
    {
        if (whenUtc == default) return string.Empty;

        var r = RelativeTime.Describe(whenUtc, nowUtc);
        return r.Bucket switch
        {
            RelativeTime.Bucket.JustNow => ResourceHelper.GetString("TimeJustNow"),
            RelativeTime.Bucket.Minutes => ResourceHelper.GetString("TimeMinutesAgo", r.Value),
            RelativeTime.Bucket.Hours => ResourceHelper.GetString("TimeHoursAgo", r.Value),
            RelativeTime.Bucket.Yesterday => ResourceHelper.GetString("TimeYesterday"),
            RelativeTime.Bucket.Days => ResourceHelper.GetString("TimeDaysAgo", r.Value),
            // Local time, short date: the user is comparing against their own clock.
            _ => whenUtc.ToLocalTime().ToString("d"),
        };
    }
}
