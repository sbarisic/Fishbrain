using System.Text.RegularExpressions;

namespace Fishbrain;

/// <summary>Conservative vetoes supplement learned execution eligibility; they never grant it.</summary>
internal static partial class ActionLanguage
{
    [GeneratedRegex("\\b(?:NOT|NEVER|DON'T|DOESN'T|DIDN'T|WON'T|WOULDN'T|CAN'T|CANNOT|COULDN'T|SHOULDN'T|CANCEL|STOP|AVOID|WITHOUT)\\b", RegexOptions.CultureInvariant)]
    private static partial Regex Negation();
    [GeneratedRegex("\\b(?:IF|SUPPOSE|SUPPOSING|IMAGIN(?:E|ING)|HYPOTHETICAL(?:LY)?|MIGHT)\\b", RegexOptions.CultureInvariant)]
    private static partial Regex Hypothetical();
    [GeneratedRegex("\\b(?:I|WE) WOULD LIKE(?: YOU)? TO\\b|\\bWOULD YOU(?: PLEASE)?\\b", RegexOptions.CultureInvariant)]
    private static partial Regex PoliteRequest();
    [GeneratedRegex("\\bWOULD\\b", RegexOptions.CultureInvariant)]
    private static partial Regex Would();
    [GeneratedRegex("\\b(?:SAID|SAYS|SAYING|WROTE|QUOTED)\\b", RegexOptions.CultureInvariant)]
    private static partial Regex Reported();

    internal static string? ExecutionVeto(string text, bool mutatesWorldState = true)
    {
        // A model-selected fragment can cut through a quotation. Veto it before
        // applying the tokenizer's complete-utterance quote validation.
        if (text.IndexOfAny(['"', '\u201c', '\u201d']) >= 0) return "QUOTED_ACTION";
        var normalized = DialogueText.Normalize(text);
        if (normalized.Contains('"') || Reported().IsMatch(normalized)) return "QUOTED_ACTION";
        if (Negation().IsMatch(normalized)) return "NEGATED_ACTION";
        if (Hypothetical().IsMatch(normalized)) return "HYPOTHETICAL_ACTION";
        // Polite request wording is not itself a conditional. This only removes a
        // veto; learned affirmative status, a plan, arguments and calibration still apply.
        // Bare WOULD in a read-only question (e.g. a price query) cannot mutate state.
        if (mutatesWorldState && Would().IsMatch(PoliteRequest().Replace(normalized, ""))) return "HYPOTHETICAL_ACTION";
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

    internal static bool ConflictingExplicitAction(string frameText, DomainToolBinding selected, DialogueDomainDefinition domain)
    {
        if (!selected.Schema.MutatesWorldState) return false;
        // Capabilities are the host's infinitive descriptions ("I CAN <capability>").
        // This narrow contradiction check never grants execution or interprets synonyms.
        static string? Verb(DomainToolBinding tool) => Tokenizer.Lex(tool.Capability)
            .Where(t => t.Kind == LexicalTokenKind.Word).Select(t => t.Text).FirstOrDefault();
        var expected = Verb(selected);
        var words = Tokenizer.Lex(DialogueText.Normalize(frameText)).Where(t => t.Kind == LexicalTokenKind.Word)
            .Select(t => t.Text).ToHashSet(StringComparer.Ordinal);
        if (expected is null || words.Contains(expected)) return false;
        return domain.Tools.Where(t => t.Schema.MutatesWorldState).Select(Verb)
            .Any(verb => verb is not null && verb != expected && words.Contains(verb));
    }
}
