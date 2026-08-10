using System.Text;

namespace Fishbrain;

internal static class ConversationConditioning
{
    public static string Build(
        NpcPersona persona,
        PlayerConversationProfile playerProfile,
        NpcDialogueState state,
        DiscourseFrame? discourse,
        IReadOnlyList<DialogueUtterance> utterances,
        DiscourseResponseAction responseAction,
        IReadOnlyList<string> responseConstraints)
    {
        persona.Validate();
        playerProfile.Validate();
        state.Validate();
        if (utterances.Count == 0)
            throw new ArgumentException("Conversation conditioning requires an utterance.", nameof(utterances));

        var result = new StringBuilder();
        foreach (var utterance in utterances.Take(Math.Max(0, utterances.Count - 1)).TakeLast(6))
            Append($"{(utterance.Speaker == DialogueRole.Player ? "PLAYER" : "NPC")} {utterance.Text}");
        Append($"PERSONA NAME {persona.Name} ROLE {persona.Role} OCCUPATION {persona.Occupation ?? "UNKNOWN"}");
        if (persona.Traits.Count > 0) Append("PERSONA TRAITS " + string.Join(' ', persona.Traits));
        foreach (var fact in playerProfile.Facts.TakeLast(4)) AppendFact("APPROVED PLAYER FACT", fact);
        foreach (var fact in state.SessionFacts.TakeLast(4)) AppendFact("SESSION FACT", fact);
        Append($"RESPONSE ACTION {responseAction}");
        foreach (var constraint in responseConstraints.Take(3))
        {
            Append($"RESPONSE CONSTRAINT {constraint}");
        }
        if (discourse is { Act: not DiscourseAct.None })
        {
            Append($"DISCOURSE {discourse.Act} SUBJECT {discourse.Subject} TARGET {discourse.Target}");
            if (discourse.FactKind is { } kind && discourse.FactValueSpan is { } value)
                Append($"FACT {kind} {(discourse.Negated ? "NOT " : string.Empty)}{value.NormalizedValue}");
            if (discourse.AntecedentUtterance is { } sequence &&
                utterances.FirstOrDefault(value => value.Sequence == sequence) is { } antecedent)
                Append($"ANTECEDENT {antecedent.Speaker} {antecedent.Text}");
        }
        var current = utterances[^1];
        Append($"{(current.Speaker == DialogueRole.Player ? "PLAYER" : "NPC")} {current.Text}");
        return DialogueText.Normalize(result.ToString());

        void Append(string value)
        {
            if (result.Length > 0) result.Append(". ");
            result.Append(DialogueText.Normalize(value).TrimEnd('.', '?', '!'));
        }

        void AppendFact(string prefix, DialogueFact fact) =>
            Append($"{prefix} {fact.Kind} {(fact.Negated ? "NOT " : string.Empty)}{fact.Value}");
    }
}
