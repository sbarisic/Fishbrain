namespace Fishbrain;

internal enum InputSegment { Player, Npc, Persona, State, ApprovedFact, SessionFact, Agenda, Capability, Plan, Mask }
internal sealed record TokenSource(long? Utterance, int Start, int Length, InputSegment Segment);
internal sealed record PackedInput(int[] Tokens, int[] Segments, TokenSource[] Sources,
    DialogueUtterance[] Utterances, int[] CurrentPositions, IReadOnlyList<DialogueFact> Facts)
{
    public IReadOnlyList<DialogueAgendaEntry> Agenda { get; init; } = [];
}

/// <summary>One packer for training and inference. Roles and source spans are out-of-band metadata.</summary>
internal static class StructuredInput
{
    public static PackedInput Pack(ReplyRequest request, DialogueTokenizer tokenizer, int budget,
        IReadOnlyList<DialogueFact> memories, DialogueDomainDefinition domain)
    {
        var mandatory = new List<Part>();
        var p = request.Persona;
        mandatory.Add(Encode($"NAME {p.Name}. ROLE {p.Role}. ORIGIN {p.Origin ?? "UNKNOWN"}. HOME {p.Home ?? "UNKNOWN"}. FAMILY {p.Family ?? "UNKNOWN"}. OCCUPATION {p.Occupation ?? "UNKNOWN"}. FACTION {p.Faction ?? "UNKNOWN"}. TRAITS {string.Join(' ', p.Traits)}.", InputSegment.Persona));
        mandatory.Add(Encode($"MOOD {request.State.Mood}. RAPPORT {request.State.Rapport}. TRUST {request.State.Trust}. GOALS {string.Join(' ', request.State.ActiveGoals)}.", InputSegment.State));
        foreach (var action in request.State.PendingActions)
            mandatory.Add(Encode($"PENDING {Identifier(action.Action)} {Identifier(action.ToolSchema ?? "NONE")}. SOURCE {action.SourceUtterance?.ToString() ?? "UNKNOWN"}. {string.Join(' ', action.Arguments.Select(x => Identifier(x.Key) + " " + x.Value))}.", InputSegment.State));
        if (request.State.PendingClarification is { } clarification) mandatory.Add(Encode(clarification.Question, InputSegment.State));
        foreach (var entry in request.State.Agenda.Where(x => x.Status == AgendaStatus.Active))
            mandatory.Add(Encode($"{entry.Kind} SUBJECT {Identifier(entry.Subject)}. SOURCE {entry.SourceTurn}. {entry.Status}.", InputSegment.Agenda));
        mandatory.Add(Encode(string.Join(". ", domain.Tools.Select(x => x.Capability)) + ".", InputSegment.Capability));
        var current = request.Utterances[^1];
        var currentPart = Encode(current.Text, InputSegment.Player, current.Sequence);
        var required = mandatory.Sum(x => x.Tokens.Count) + currentPart.Tokens.Count;
        if (required > budget) throw new ArgumentException("Current utterance and required persona/state exceed the model input budget.", nameof(request));

        var facts = new List<DialogueFact>();
        var memoryParts = new List<Part>();
        foreach (var fact in memories.Take(8))
        {
            var part = Encode(FactText(fact), fact.Provenance == DialogueFactProvenance.CallerApproved ? InputSegment.ApprovedFact : InputSegment.SessionFact);
            if (required + part.Tokens.Count > budget) continue;
            required += part.Tokens.Count;
            memoryParts.Add(part);
            facts.Add(fact);
        }
        var retained = new List<DialogueUtterance>();
        var history = new List<Part>();
        foreach (var utterance in request.Utterances.Take(request.Utterances.Count - 1).Reverse())
        {
            var part = Encode(utterance.Text, utterance.Speaker == DialogueRole.Player ? InputSegment.Player : InputSegment.Npc, utterance.Sequence);
            if (required + part.Tokens.Count > budget) break;
            required += part.Tokens.Count;
            retained.Insert(0, utterance);
            history.Insert(0, part);
        }
        foreach (var summary in request.State.TopicSummaries.Reverse())
        {
            var part = Encode($"TOPIC {summary.Topic}. SOURCE {summary.SourceUtterance}.", InputSegment.State);
            if (required + part.Tokens.Count > budget) continue;
            required += part.Tokens.Count;
            history.Insert(0, part);
        }
        retained.Add(current);
        var parts = mandatory.Concat(memoryParts).Concat(history).Append(currentPart).ToArray();
        var tokens = parts.SelectMany(x => x.Tokens).ToArray();
        var sources = parts.SelectMany(x => x.Sources).ToArray();
        return new(tokens, sources.Select(x => (int)x.Segment).ToArray(), sources, retained.ToArray(),
            Enumerable.Range(0, sources.Length).Where(i => sources[i].Utterance == current.Sequence && sources[i].Length > 0).ToArray(), facts)
        { Agenda = request.State.Agenda };

        Part Encode(string text, InputSegment segment, long? sequence = null)
        {
            var normalized = DialogueText.Normalize(text);
            var part = new Part();
            part.Tokens.Add(Tokenizer.Bos);
            part.Sources.Add(new(sequence, 0, 0, segment));
            var cursor = 0;
            foreach (var lexical in Tokenizer.Lex(normalized))
            {
                var start = normalized.IndexOf(lexical.Text, cursor, StringComparison.Ordinal);
                cursor = start + lexical.Text.Length;
                var encoded = lexical.Text == "\"" ? new[] { Tokenizer.Quote } : tokenizer.Encode(lexical.Text);
                part.Tokens.AddRange(encoded);
                part.Sources.AddRange(encoded.Select(_ => new TokenSource(sequence, start, lexical.Text.Length, segment)));
            }
            part.Tokens.Add(Tokenizer.Sep);
            part.Sources.Add(new(sequence, normalized.Length, 0, segment));
            return part;
        }
    }

    internal static string FactText(DialogueFact fact) =>
        $"SUBJECT {fact.Subject}. PREDICATE {fact.Kind}. VALUE {(fact.Negated ? "NOT " : "")}{fact.Value}. SOURCE {fact.SourceUtterance}. PROVENANCE {fact.Provenance}.";
    private static string Identifier(string value) => value.Replace('_', ' ');
    private sealed class Part
    {
        public List<int> Tokens { get; } = [];
        public List<TokenSource> Sources { get; } = [];
    }
}
