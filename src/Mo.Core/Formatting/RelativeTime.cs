namespace Mo.Core.Formatting;

// Picks the coarse bucket a timestamp falls into. Culture-free: the caller supplies
// the localized wording, this only decides which wording applies.
public static class RelativeTime
{
    public enum Bucket
    {
        JustNow,
        Minutes,
        Hours,
        Yesterday,
        Days,
        /// <summary>Older than a week — show an absolute date instead.</summary>
        Absolute,
    }

    public sealed record Result(Bucket Bucket, int Value);

    /// <param name="whenUtc">The timestamp, in UTC.</param>
    /// <param name="nowUtc">Current time, in UTC. Passed in so this stays testable.</param>
    public static Result Describe(DateTime whenUtc, DateTime nowUtc)
    {
        var delta = nowUtc - whenUtc;

        // Clock skew can put the timestamp in the future.
        if (delta < TimeSpan.Zero) return new Result(Bucket.JustNow, 0);

        if (delta < TimeSpan.FromMinutes(1)) return new Result(Bucket.JustNow, 0);
        if (delta < TimeSpan.FromHours(1)) return new Result(Bucket.Minutes, (int)delta.TotalMinutes);
        if (delta < TimeSpan.FromHours(24)) return new Result(Bucket.Hours, (int)delta.TotalHours);
        if (delta < TimeSpan.FromHours(48)) return new Result(Bucket.Yesterday, 1);
        if (delta < TimeSpan.FromDays(7)) return new Result(Bucket.Days, (int)delta.TotalDays);

        return new Result(Bucket.Absolute, 0);
    }
}
