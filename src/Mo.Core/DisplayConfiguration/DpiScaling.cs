namespace Mo.Core.DisplayConfiguration;

/// <summary>Converts between Windows' per-monitor scaling API, which counts steps away
/// from a display's recommended scale, and the percentages a person reads.</summary>
public static class DpiScaling
{
    /// <summary>The ladder Windows offers. The API indexes this; it never sees percent.</summary>
    public static readonly int[] Steps = [100, 125, 150, 175, 200, 225, 250, 300, 350, 400, 450, 500];

    public const int Default = 100;

    /// <summary>Index of the display's recommended scale. minScaleRel counts down from
    /// it, so negating gives the position in <see cref="Steps"/>.</summary>
    private static int RecommendedIndex(int minScaleRel) => -minScaleRel;

    /// <summary>Current scale as a percentage, or 100 if the relative values are out of
    /// range, which is how a driver that does not implement the call answers.</summary>
    public static int ToPercent(int minScaleRel, int currentScaleRel)
    {
        int index = RecommendedIndex(minScaleRel) + currentScaleRel;
        return index >= 0 && index < Steps.Length ? Steps[index] : Default;
    }

    /// <summary>Every scale this display will accept, low to high.</summary>
    public static IReadOnlyList<int> AvailablePercentages(int minScaleRel, int maxScaleRel)
    {
        int recommended = RecommendedIndex(minScaleRel);
        int first = Math.Max(0, recommended + minScaleRel);
        int last = Math.Min(Steps.Length - 1, recommended + maxScaleRel);
        if (last < first) return [];

        var result = new int[last - first + 1];
        Array.Copy(Steps, first, result, 0, result.Length);
        return result;
    }

    /// <summary>The relative step to write for a percentage, clamped to what the display
    /// allows. Returns false when the percentage is not on the ladder at all.</summary>
    public static bool TryToRelative(int minScaleRel, int maxScaleRel, int percent, out int relative)
    {
        relative = 0;
        int index = Array.IndexOf(Steps, percent);
        if (index < 0) return false;

        relative = Math.Clamp(index - RecommendedIndex(minScaleRel), minScaleRel, maxScaleRel);
        return true;
    }
}
