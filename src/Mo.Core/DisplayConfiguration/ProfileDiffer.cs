namespace Mo.Core.DisplayConfiguration;

public static class ProfileDiffer
{
    public enum ChangeType
    {
        None,
        PositionChanged,
        ResolutionChanged,
        RotationChanged,
        RefreshRateChanged,
        DpiChanged,
        PrimaryChanged,
        MonitorAdded,
        MonitorRemoved
    }

    public sealed record MonitorChange(int MonitorIndex, string MonitorName, ChangeType Type, string Details);

    public sealed record DiffResult(List<MonitorChange> Changes)
    {
        public bool HasChanges => Changes.Count > 0;
    }

    public sealed record MonitorSnapshot(
        string FriendlyName,
        int PositionX, int PositionY,
        int Width, int Height,
        int Rotation,
        uint RefreshRateNumerator, uint RefreshRateDenominator,
        int DpiScale,
        bool IsPrimary);

    public static DiffResult Compare(
        IReadOnlyList<MonitorSnapshot> before,
        IReadOnlyList<MonitorSnapshot> after,
        Dictionary<int, int> matching)
    {
        var changes = new List<MonitorChange>();

        foreach (var (beforeIdx, afterIdx) in matching)
        {
            var b = before[beforeIdx];
            var a = after[afterIdx];

            if (b.PositionX != a.PositionX || b.PositionY != a.PositionY)
                changes.Add(new(beforeIdx, b.FriendlyName, ChangeType.PositionChanged,
                    $"({b.PositionX},{b.PositionY}) -> ({a.PositionX},{a.PositionY})"));

            if (b.Width != a.Width || b.Height != a.Height)
                changes.Add(new(beforeIdx, b.FriendlyName, ChangeType.ResolutionChanged,
                    $"{b.Width}x{b.Height} -> {a.Width}x{a.Height}"));

            if (b.Rotation != a.Rotation)
                changes.Add(new(beforeIdx, b.FriendlyName, ChangeType.RotationChanged,
                    $"{b.Rotation} -> {a.Rotation}"));

            if (b.RefreshRateNumerator != a.RefreshRateNumerator || b.RefreshRateDenominator != a.RefreshRateDenominator)
                changes.Add(new(beforeIdx, b.FriendlyName, ChangeType.RefreshRateChanged,
                    $"{FormatHz(b.RefreshRateNumerator, b.RefreshRateDenominator)} -> {FormatHz(a.RefreshRateNumerator, a.RefreshRateDenominator)}"));

            if (b.DpiScale != a.DpiScale)
                changes.Add(new(beforeIdx, b.FriendlyName, ChangeType.DpiChanged,
                    $"{b.DpiScale}% -> {a.DpiScale}%"));

            if (b.IsPrimary != a.IsPrimary)
                changes.Add(new(beforeIdx, b.FriendlyName, ChangeType.PrimaryChanged,
                    a.IsPrimary ? "-> Primary" : "-> Not Primary"));
        }

        var unmatchedBefore = Enumerable.Range(0, before.Count).Where(i => !matching.ContainsKey(i));
        foreach (int i in unmatchedBefore)
            changes.Add(new(i, before[i].FriendlyName, ChangeType.MonitorRemoved, "Removed"));

        var matchedAfter = new HashSet<int>(matching.Values);
        var unmatchedAfter = Enumerable.Range(0, after.Count).Where(i => !matchedAfter.Contains(i));
        foreach (int i in unmatchedAfter)
            changes.Add(new(i, after[i].FriendlyName, ChangeType.MonitorAdded, "Added"));

        return new DiffResult(changes);
    }

    private static string FormatHz(uint numerator, uint denominator)
    {
        if (denominator == 0) return "?Hz";
        return $"{(double)numerator / denominator:F1}Hz";
    }
}
