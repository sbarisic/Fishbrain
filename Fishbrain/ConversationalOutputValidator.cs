using System.Text.RegularExpressions;

namespace Fishbrain;

internal static partial class ConversationalOutputValidator
{
    [GeneratedRegex(@"\b[0-9]+\s+(?:GOLD|CREDITS|COINS|ITEMS?)\b", RegexOptions.CultureInvariant)]
    private static partial Regex AuthoritativeQuantityPattern();

    public static bool IsSafe(
        string text,
        NpcPersona persona,
        IReadOnlyList<DialogueFact> sessionFacts,
        out string? reason)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 256 || !DialogueText.IsCanonical(text))
            return Fail("INVALID_GENERATED_TEXT", out reason);
        if (AuthoritativeQuantityPattern().IsMatch(text) || ContainsAny(text,
                "YOU BOUGHT", "YOU SOLD", "YOUR BALANCE", "YOUR INVENTORY", "YOU NOW OWN",
                "YOU OWN THE", "YOU HAVE PERMISSION", "YOU ARE AUTHORIZED", "YOUR QUEST IS",
                "I COMPLETED THE QUEST", "THE QUEST IS COMPLETE", "I MOVED YOU", "I CHANGED YOUR"))
            return Fail("GENERATED_AUTHORITATIVE_CLAIM", out reason);
        if (ContainsAny(text, "MY NAME IS", "I AM FROM", "MY HOME IS", "MY FAMILY IS", "MY FACTION IS"))
            return Fail("GENERATED_PERSONA_FACT", out reason);
        if (text.Contains("I AM A ", StringComparison.Ordinal) &&
            !text.Contains(persona.Role, StringComparison.Ordinal) &&
            !(persona.Occupation is { } occupation && text.Contains(occupation, StringComparison.Ordinal)))
            return Fail("GENERATED_PERSONA_CONTRADICTION", out reason);
        var attributesSessionClaim = ContainsAny(text,
            "YOU SAID", "YOU TOLD ME", "YOU MENTIONED", "AS YOU SAY", "I REMEMBER");
        if (!attributesSessionClaim && sessionFacts.Any(fact =>
                fact.Subject == DialogueParticipant.Player &&
                !fact.Negated &&
                text.Contains(fact.Value, StringComparison.Ordinal)))
            return Fail("GENERATED_UNVERIFIED_SESSION_CLAIM", out reason);
        reason = null;
        return true;
    }

    private static bool ContainsAny(string text, params string[] values) =>
        values.Any(value => text.Contains(value, StringComparison.Ordinal));

    private static bool Fail(string value, out string? reason)
    {
        reason = value;
        return false;
    }
}
