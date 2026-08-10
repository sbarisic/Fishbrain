using System.Text.RegularExpressions;

namespace Fishbrain;

internal static class DiscourseResolver
{
    private static readonly Regex Space = new("\\s+", RegexOptions.CultureInvariant);

    public static DiscourseFrame Resolve(
        ReplyRequest request,
        IReadOnlyList<DialogueUtterance> retained,
        string current,
        DiscourseFrame? learned)
    {
        var bare = current.Trim().TrimEnd('.', '?', '!');
        if (bare is "WHAT DO I DO" or "WHAT IS MY JOB" or "DO YOU REMEMBER WHAT I DO")
        {
            var occupation = request.PlayerProfile.Facts.Concat(request.State.SessionFacts).LastOrDefault(fact =>
                fact.Subject == DialogueParticipant.Player && fact.Kind == DialogueFactKind.Occupation && !fact.Negated);
            return occupation is null
                ? new DiscourseFrame(DiscourseAct.ReferBack, DialogueParticipant.Player, DialogueParticipant.Player,
                    DialogueFactKind.Occupation, null, false, null, 0.45, "NO_RETAINED_OCCUPATION")
                : new DiscourseFrame(DiscourseAct.ReferBack, DialogueParticipant.Player, DialogueParticipant.Player,
                    DialogueFactKind.Occupation, null, false, occupation.SourceUtterance, 1.0,
                    occupation.Provenance.ToString().ToUpperInvariant());
        }
        if (IsExplanationRequest(bare))
        {
            var explicitReference = bare.Contains("YOU SAID ", StringComparison.Ordinal);
            var antecedent = explicitReference
                ? ResolveExplicitAntecedent(bare, retained)
                : retained.LastOrDefault(value => value.Speaker == DialogueRole.Npc)?.Sequence;
            return new DiscourseFrame(DiscourseAct.AskExplanation, DialogueParticipant.Player,
                DialogueParticipant.Npc, null, null, false, antecedent, antecedent is null ? 0.45 : 1.0,
                antecedent is null ? explicitReference ? "AMBIGUOUS_EXPLICIT_REFERENCE" : "NO_RETAINED_NPC_UTTERANCE"
                    : explicitReference ? "MATCHED_EXPLICIT_REFERENCE" : "LATEST_NPC_UTTERANCE");
        }

        var fact = ExtractPlayerFact(bare, retained);
        if (fact is not null)
            return fact;

        if (learned is { Act: not DiscourseAct.None })
            return learned;

        return DiscourseFrame.Empty;
    }

    public static DiscourseFrame? ExtractNpcFact(string responseText)
    {
        if (string.IsNullOrWhiteSpace(responseText))
        {
            return null;
        }

        var text = DialogueText.Normalize(responseText).TrimEnd('.', '?', '!');
        if (text.Contains('"'))
        {
            return null;
        }

        var patterns = new (string Prefix, DialogueFactKind Kind)[]
        {
            ("MY NAME IS ", DialogueFactKind.Name),
            ("MY ROLE IS ", DialogueFactKind.Role),
            ("I WORK AS A ", DialogueFactKind.Occupation),
            ("I WORK AS AN ", DialogueFactKind.Occupation),
            ("I AM FROM ", DialogueFactKind.Origin),
            ("I LIVE IN ", DialogueFactKind.Home),
            ("I AM A ", DialogueFactKind.Role),
            ("I AM AN ", DialogueFactKind.Role)
        };
        foreach (var pattern in patterns)
        {
            if (!text.StartsWith(pattern.Prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var value = text[pattern.Prefix.Length..].Trim();
            if (value.Length is < 1 or > 128)
            {
                return null;
            }

            return new DiscourseFrame(
                DiscourseAct.Inform,
                DialogueParticipant.Npc,
                DialogueParticipant.None,
                pattern.Kind,
                new DialogueTextSpan(value, pattern.Prefix.Length, value.Length),
                false,
                null,
                1.0,
                "NPC_DIRECT_ASSERTION");
        }

        return null;
    }

    private static bool IsExplanationRequest(string text) => text is
        "WHAT DO YOU MEAN" or "WHAT DID YOU MEAN" or "WHAT ARE YOU TALKING ABOUT" or
        "WHAT WAS THAT SUPPOSED TO MEAN" or "EXPLAIN WHAT YOU MEAN" ||
        text.StartsWith("WHAT DID YOU MEAN WHEN YOU SAID ", StringComparison.Ordinal) ||
        text.StartsWith("WHEN YOU SAID ", StringComparison.Ordinal);

    private static long? ResolveExplicitAntecedent(string current, IReadOnlyList<DialogueUtterance> retained)
    {
        var marker = current.IndexOf("YOU SAID ", StringComparison.Ordinal);
        if (marker < 0)
            return null;
        var quoted = current[(marker + 9)..].Trim(' ', '\'', '"');
        var queryWords = Words(quoted);
        if (queryWords.Count == 0)
            return null;
        var matches = retained.Where(value => value.Speaker == DialogueRole.Npc)
            .Select(value => (value.Sequence, Score: Overlap(queryWords, Words(DialogueText.Normalize(value.Text)))))
            .Where(value => value.Score > 0)
            .OrderByDescending(value => value.Score)
            .ThenByDescending(value => value.Sequence)
            .ToArray();
        return matches.Length == 0 || matches.Length > 1 && matches[0].Score == matches[1].Score
            ? null
            : matches[0].Sequence;
    }

    private static DiscourseFrame? ExtractPlayerFact(string text, IReadOnlyList<DialogueUtterance> retained)
    {
        if (text.Length == 0 || text.Contains('"') || text.StartsWith("HE SAID ", StringComparison.Ordinal) ||
            text.StartsWith("SHE SAID ", StringComparison.Ordinal) ||
            text.StartsWith("THEY SAID ", StringComparison.Ordinal) ||
            text.StartsWith("ACCORDING TO ", StringComparison.Ordinal) ||
            text.Contains(" TOLD ME ", StringComparison.Ordinal) ||
            text.Contains(" SAYS THAT ", StringComparison.Ordinal))
        {
            return null;
        }

        var statement = text;
        var explicitCorrection = text.StartsWith("NO, ", StringComparison.Ordinal) ||
            text.StartsWith("ACTUALLY, ", StringComparison.Ordinal) ||
            text.Contains("CORRECT", StringComparison.Ordinal) || text.Contains("WRONG", StringComparison.Ordinal) ||
            text.Contains("FOR CLARITY", StringComparison.Ordinal) || text.Contains("DOES NOT DESCRIBE ME", StringComparison.Ordinal);
        if (explicitCorrection && text.IndexOf("I AM ", StringComparison.Ordinal) is var statementStart && statementStart > 0)
        {
            statement = text[statementStart..];
        }

        if (TryCorrectionFragment(text, retained, out var correction))
        {
            return correction;
        }

        var patterns = new (string Prefix, DialogueFactKind Kind, bool Negated)[]
        {
            ("MY NAME IS NOT ", DialogueFactKind.Name, true),
            ("MY NAME IS ", DialogueFactKind.Name, false),
            ("MY ROLE IS NOT ", DialogueFactKind.Role, true),
            ("MY ROLE IS ", DialogueFactKind.Role, false),
            ("I AM NOT FROM ", DialogueFactKind.Origin, true),
            ("I DO NOT LIVE IN ", DialogueFactKind.Home, true),
            ("MY FAMILY DOES NOT INCLUDE ", DialogueFactKind.Family, true),
            ("I AM NOT CURRENTLY ", DialogueFactKind.Activity, true),
            ("I DO NOT THINK ", DialogueFactKind.Opinion, true),
            ("I HAVE NOT EXPERIENCED ", DialogueFactKind.Experience, true),
            ("I AM NOT A ", DialogueFactKind.Occupation, true),
            ("I AM NOT AN ", DialogueFactKind.Occupation, true),
            ("I AM NOT ", DialogueFactKind.Occupation, true),
            ("I AM A ", DialogueFactKind.Occupation, false),
            ("I AM AN ", DialogueFactKind.Occupation, false),
            ("I WORK AS A ", DialogueFactKind.Occupation, false),
            ("I WORK AS AN ", DialogueFactKind.Occupation, false),
            ("I COME FROM ", DialogueFactKind.Origin, false),
            ("I AM FROM ", DialogueFactKind.Origin, false),
            ("I LIVE IN ", DialogueFactKind.Home, false),
            ("MY FAMILY INCLUDES ", DialogueFactKind.Family, false),
            ("I HAVE A SISTER NAMED ", DialogueFactKind.Family, false),
            ("I HAVE A BROTHER NAMED ", DialogueFactKind.Family, false),
            ("I AM CURRENTLY ", DialogueFactKind.Activity, false),
            ("I AM BUSY ", DialogueFactKind.Activity, false),
            ("I DO NOT PREFER ", DialogueFactKind.Preference, true),
            ("I PREFER ", DialogueFactKind.Preference, false),
            ("I DO NOT LIKE ", DialogueFactKind.Preference, true),
            ("I LIKE ", DialogueFactKind.Preference, false),
            ("I DO NOT DISLIKE ", DialogueFactKind.Dislike, true),
            ("I DISLIKE ", DialogueFactKind.Dislike, false),
            ("I HATE ", DialogueFactKind.Dislike, false),
            ("I THINK ", DialogueFactKind.Opinion, false),
            ("I BELIEVE ", DialogueFactKind.Opinion, false),
            ("I HAVE EXPERIENCED ", DialogueFactKind.Experience, false),
            ("I ONCE ", DialogueFactKind.Experience, false)
        };
        foreach (var pattern in patterns)
        {
            if (!statement.StartsWith(pattern.Prefix, StringComparison.Ordinal))
                continue;
            var value = Space.Replace(statement[pattern.Prefix.Length..].Trim(), " ");
            if (value.IndexOf(',') is var comma && comma >= 0)
            {
                value = value[..comma].Trim();
            }

            if (value.Length is < 1 or > 128)
            {
                return null;
            }

            var priorNpc = retained.LastOrDefault(item => item.Speaker == DialogueRole.Npc);
            var corrective = pattern.Negated || explicitCorrection;
            var act = explicitCorrection && !pattern.Negated
                ? DiscourseAct.Correct
                : corrective ? DiscourseAct.RejectAssumption : DiscourseAct.Inform;
            return new DiscourseFrame(act, DialogueParticipant.Player,
                corrective ? DialogueParticipant.Npc : DialogueParticipant.None,
                pattern.Kind, Span(text, value), pattern.Negated, corrective ? priorNpc?.Sequence : null, 1.0,
                pattern.Prefix.Trim());
        }

        return null;
    }

    private static bool TryCorrectionFragment(
        string text,
        IReadOnlyList<DialogueUtterance> retained,
        out DiscourseFrame? frame)
    {
        frame = null;
        var prefix = text.StartsWith("NO, ", StringComparison.Ordinal)
            ? "NO, "
            : text.StartsWith("ACTUALLY, ", StringComparison.Ordinal) ? "ACTUALLY, " : null;
        if (prefix is null)
        {
            return false;
        }

        var fragment = text[prefix.Length..].Trim();
        if (fragment.StartsWith("A ", StringComparison.Ordinal))
        {
            fragment = fragment[2..].Trim();
        }
        else if (fragment.StartsWith("AN ", StringComparison.Ordinal))
        {
            fragment = fragment[3..].Trim();
        }
        else
        {
            return false;
        }

        if (fragment.IndexOf(',') is var comma && comma >= 0)
        {
            fragment = fragment[..comma].Trim();
        }

        if (fragment.Length is < 1 or > 128)
        {
            return false;
        }

        frame = new DiscourseFrame(
            DiscourseAct.Correct,
            DialogueParticipant.Player,
            DialogueParticipant.Npc,
            DialogueFactKind.Occupation,
            Span(text, fragment),
            false,
            retained.LastOrDefault(value => value.Speaker == DialogueRole.Npc)?.Sequence,
            1.0,
            "ELLIPTICAL_OCCUPATION_CORRECTION");
        return true;
    }

    private static DialogueTextSpan Span(string source, string value)
    {
        var start = source.IndexOf(value, StringComparison.Ordinal);
        if (start < 0)
        {
            throw new InvalidOperationException("A resolved fact value must occur in the current utterance.");
        }

        return new DialogueTextSpan(value, start, value.Length);
    }

    private static HashSet<string> Words(string text) => Tokenizer.Lex(text)
        .Where(token => token.Kind == LexicalTokenKind.Word && token.Text.Length > 2)
        .Select(token => token.Text)
        .ToHashSet(StringComparer.Ordinal);

    private static int Overlap(IReadOnlySet<string> left, IReadOnlySet<string> right) =>
        left.Count(right.Contains);
}
