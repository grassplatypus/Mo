using System.Diagnostics.CodeAnalysis;

namespace Mo.Core.DisplayConfiguration;

public static class MonitorMatcher
{
    public sealed record MonitorIdentity(
        string DevicePath,
        ushort EdidManufacturerId,
        ushort EdidProductCodeId,
        uint ConnectorInstance,
        string FriendlyName);

    public sealed record MatchResult(
        Dictionary<int, int> Matches,
        List<int> UnmatchedProfile,
        List<int> UnmatchedCurrent);

    public static MatchResult Match(
        IReadOnlyList<MonitorIdentity> profileMonitors,
        IReadOnlyList<MonitorIdentity> currentMonitors)
    {
        var matches = new Dictionary<int, int>();
        var usedCurrent = new HashSet<int>();

        // Pass 1: Exact device path match
        for (int p = 0; p < profileMonitors.Count; p++)
        {
            if (matches.ContainsKey(p)) continue;
            for (int c = 0; c < currentMonitors.Count; c++)
            {
                if (usedCurrent.Contains(c)) continue;
                if (!string.IsNullOrEmpty(profileMonitors[p].DevicePath) &&
                    string.Equals(profileMonitors[p].DevicePath, currentMonitors[c].DevicePath, StringComparison.OrdinalIgnoreCase))
                {
                    matches[p] = c;
                    usedCurrent.Add(c);
                    break;
                }
            }
        }

        // Pass 2: EDID manufacturer + product code + connector instance
        for (int p = 0; p < profileMonitors.Count; p++)
        {
            if (matches.ContainsKey(p)) continue;
            for (int c = 0; c < currentMonitors.Count; c++)
            {
                if (usedCurrent.Contains(c)) continue;
                if (profileMonitors[p].EdidManufacturerId != 0 &&
                    profileMonitors[p].EdidManufacturerId == currentMonitors[c].EdidManufacturerId &&
                    profileMonitors[p].EdidProductCodeId == currentMonitors[c].EdidProductCodeId &&
                    profileMonitors[p].ConnectorInstance == currentMonitors[c].ConnectorInstance)
                {
                    matches[p] = c;
                    usedCurrent.Add(c);
                    break;
                }
            }
        }

        // Pass 3: Friendly name match
        for (int p = 0; p < profileMonitors.Count; p++)
        {
            if (matches.ContainsKey(p)) continue;
            for (int c = 0; c < currentMonitors.Count; c++)
            {
                if (usedCurrent.Contains(c)) continue;
                if (!string.IsNullOrEmpty(profileMonitors[p].FriendlyName) &&
                    string.Equals(profileMonitors[p].FriendlyName, currentMonitors[c].FriendlyName, StringComparison.OrdinalIgnoreCase))
                {
                    matches[p] = c;
                    usedCurrent.Add(c);
                    break;
                }
            }
        }

        // Pass 4: If exactly one remains on each side, match them
        var unmatchedProfile = Enumerable.Range(0, profileMonitors.Count).Where(p => !matches.ContainsKey(p)).ToList();
        var unmatchedCurrent = Enumerable.Range(0, currentMonitors.Count).Where(c => !usedCurrent.Contains(c)).ToList();

        if (unmatchedProfile.Count == 1 && unmatchedCurrent.Count == 1)
        {
            matches[unmatchedProfile[0]] = unmatchedCurrent[0];
            unmatchedProfile.Clear();
            unmatchedCurrent.Clear();
        }

        return new MatchResult(matches, unmatchedProfile, unmatchedCurrent);
    }
}
