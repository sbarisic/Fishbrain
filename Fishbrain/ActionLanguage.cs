using System.Text.RegularExpressions;

namespace Fishbrain;

/// <summary>Conservative vetoes supplement learned execution eligibility; they never grant it.</summary>
internal static partial class ActionLanguage
{
    [GeneratedRegex("\\b(?:NOT|NEVER|DON'T|DOESN'T|DIDN'T|CANCEL|STOP|AVOID|WITHOUT)\\b", RegexOptions.CultureInvariant)]
    private static partial Regex Negation();
    [GeneratedRegex("\\b(?:IF|SUPPOSE|SUPPOSING|IMAGINE|HYPOTHETICALLY|WOULD|MIGHT)\\b", RegexOptions.CultureInvariant)]
    private static partial Regex Hypothetical();
    [GeneratedRegex("\\b(?:SAID|SAYS|SAYING|WROTE|QUOTED)\\b", RegexOptions.CultureInvariant)]
    private static partial Regex Reported();

    internal static string? ExecutionVeto(string text)
    {
        var normalized = DialogueText.Normalize(text);
        if (normalized.Contains('"') || Reported().IsMatch(normalized)) return "QUOTED_ACTION";
        if (Negation().IsMatch(normalized)) return "NEGATED_ACTION";
        if (Hypothetical().IsMatch(normalized)) return "HYPOTHETICAL_ACTION";
        return null;
    }
}
