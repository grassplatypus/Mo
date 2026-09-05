using System.Text.RegularExpressions;

namespace Mo.Core.Formatting;

/// <summary>Recognises the auto-generated descriptions Mo 0.20.3 and earlier wrote into
/// profile JSON (e.g. <c>"2 monitor(s) — 2026-04-12 오후 3:07"</c>), so they are cleared
/// rather than mistaken for the user's note. See .claude/rules/50-persistence.md.</summary>
public static partial class LegacyDescription
{
    // "<n> monitor(s)" optionally followed by an em-dash and the capture timestamp,
    // which was formatted with the *machine's* locale — hence the permissive tail.
    [GeneratedRegex(@"^\s*\d+\s+monitor\(s\)\s*(—.*)?$", RegexOptions.CultureInvariant)]
    private static partial Regex GeneratedPattern();

    public static bool IsGenerated(string? description) =>
        !string.IsNullOrWhiteSpace(description) && GeneratedPattern().IsMatch(description);
}
