namespace Mo.Core.DisplayConfiguration;

/// <summary>Decides whether a monitor took the colour gains written to it.</summary>
/// <remarks>A panel can accept a DDC/CI write, report success, and change nothing.</remarks>
public static class GainReadBack
{
    /// <summary>Percent slack between the value written and the value read back.</summary>
    public const int TolerancePercent = 3;

    /// <summary>True when every comparable channel came back at a different value.</summary>
    /// <param name="channels">Written and read-back percent per channel. A channel
    /// missing either half is no evidence and drops out.</param>
    /// <returns>False when nothing is left to compare, rather than a guess.</returns>
    public static bool WasIgnored(params (int? Sent, int? ReadBack)[] channels)
    {
        int comparable = 0;
        int missed = 0;

        foreach (var (sent, readBack) in channels)
        {
            if (sent is not int s || readBack is not int r) continue;
            comparable++;
            if (Math.Abs(s - r) > TolerancePercent) missed++;
        }

        return comparable > 0 && missed == comparable;
    }
}
