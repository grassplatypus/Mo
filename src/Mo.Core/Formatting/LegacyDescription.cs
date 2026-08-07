using System.Text.RegularExpressions;

namespace Mo.Core.Formatting;

/// <summary>
/// Recognises the auto-generated profile descriptions written by Mo 0.20.3 and
/// earlier, so they can be cleared instead of being mistaken for the user's own note.
/// </summary>
/// <remarks>
/// Those builds stored strings like <c>"2 monitor(s) — 2026-04-12 오후 3:07"</c> in the
/// profile JSON and overwrote them whenever the monitor set changed. They were English
/// regardless of UI language and went stale on edit, so the field is now the user's
/// note only. Existing files still contain the generated text; this tells the two
/// apart on load.
/// </remarks>
public static partial class LegacyDescription
{
    // "<n> monitor(s)" optionally followed by an em-dash and the capture timestamp,
    // which was formatted with the *machine's* locale — hence the permissive tail.
    [GeneratedRegex(@"^\s*\d+\s+monitor\(s\)\s*(—.*)?$", RegexOptions.CultureInvariant)]
    private static partial Regex GeneratedPattern();

    public static bool IsGenerated(string? description) =>
        !string.IsNullOrWhiteSpace(description) && GeneratedPattern().IsMatch(description);
}
