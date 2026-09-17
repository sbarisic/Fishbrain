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
        // A model-selected fragment can cut through a quotation. Veto it before
        // applying the tokenizer's complete-utterance quote validation.
        if (text.IndexOfAny(['"', '\u201c', '\u201d']) >= 0) return "QUOTED_ACTION";
        var normalized = DialogueText.Normalize(text);
        if (normalized.Contains('"') || Reported().IsMatch(normalized)) return "QUOTED_ACTION";
        if (Negation().IsMatch(normalized)) return "NEGATED_ACTION";
        if (Hypothetical().IsMatch(normalized)) return "HYPOTHETICAL_ACTION";
        return null;
    }

    internal static string SurroundingSentence(string normalized, int start, int length)
    {
        var begin = 0;
        var end = start + length;
        var quoted = false;
        for (var i = 0; i < normalized.Length; i++)
        {
            if (normalized[i] == '"') quoted = !quoted;
            if (quoted || !".?!;".Contains(normalized[i])) continue;
            if (i < start) begin = i + 1;
            else if (i >= end - 1) return normalized[begin..(i + 1)];
        }
        return normalized[begin..];
    }
}
