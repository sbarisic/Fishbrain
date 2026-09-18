namespace Fishbrain;

public sealed record MemoryRecord(string Id, string Subject, string Predicate, string Value, bool Polarity,
    long SourceTurn, string Quotation, string Provenance);

/// <summary>Host-owned session store. Approved profiles are deliberately outside this interface.</summary>
public sealed class SessionMemoryStore
{
    private readonly object _gate = new();
    private readonly List<MemoryRecord> _records = [];
    private readonly Dictionary<string, (string Fingerprint, GameToolResult Result)> _completed = [];
    private int _next;
    public IReadOnlyList<MemoryRecord> Records { get { lock (_gate) return _records.ToArray(); } }
    public IEnumerable<IGameTool> CreateTools() => new[] { "MEMORY_SEARCH", "MEMORY_UPSERT", "MEMORY_DELETE" }.Select(n => new MemoryTool(this, n));

    private GameToolResult Execute(string name, GameToolInvocation invocation, ToolExecutionContext context)
    {
        lock (_gate)
        {
            var a = invocation.Arguments;
            var fingerprint = name + JsonDefaults.Write(a.OrderBy(x => x.Key).ToArray());
            if (_completed.TryGetValue(invocation.IdempotencyKey, out var cached))
                return cached.Fingerprint == fingerprint ? cached.Result : Failure("IDEMPOTENCY_CONFLICT");
            GameToolResult result;
            if (name == "MEMORY_SEARCH")
            {
                var selected = _records.AsEnumerable().Reverse().Where(r => r.Subject == a["SUBJECT"] && r.Predicate == a["PREDICATE"])
                    .OrderByDescending(r => r.SourceTurn).Take(8).ToList();
                var records = JsonDefaults.Write(selected);
                while (records.Length > 4096 && selected.Count > 0) { selected.RemoveAt(selected.Count - 1); records = JsonDefaults.Write(selected); }
                result = Success(selected.Count == 0 ? "No matching reported facts." : string.Join("; ", selected.Select(r =>
                    $"You reported {(r.Subject == "PLAYER" ? "your" : "the NPC's")} {r.Predicate.ToLowerInvariant()}: {(r.Polarity ? "" : "not ")}{r.Value}")), records);
            }
            else
            {
                var source = long.Parse(a["SOURCE_TURN"], System.Globalization.CultureInfo.InvariantCulture);
                var quote = a["QUOTE"];
                var evidence = context.RetainedMessages.SingleOrDefault(m => m.Role == MessageRole.Player && m.Sequence == source);
                if (evidence is null || string.IsNullOrWhiteSpace(quote) || !evidence.Text.Contains(quote, StringComparison.Ordinal)) return Failure("MISSING_PLAYER_EVIDENCE");
                var old = _records.SingleOrDefault(r => r.Id == a["ID"]);
                if (a["ID"] != "NEW" && (old is null || old.Subject != a["SUBJECT"])) return Failure("OWNER_OR_RECORD_MISMATCH");
                if (old is not null && source < old.SourceTurn) return Failure("STALE_PLAYER_EVIDENCE");
                if (name == "MEMORY_DELETE")
                {
                    if (old is null) return Failure("RECORD_REQUIRED");
                    if (Brain.ActionVeto(evidence.Text) is not null) return Failure("NON_AFFIRMATIVE_DELETION");
                    var explicitId = System.Text.RegularExpressions.Regex.IsMatch(quote, "(?<![\\p{L}\\p{N}_])" + System.Text.RegularExpressions.Regex.Escape(old.Id) + "(?![\\p{L}\\p{N}_])", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (!System.Text.RegularExpressions.Regex.IsMatch(quote, "\\b(forget|delete|remove)\\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase) ||
                        !(explicitId || quote.Contains(old.Value, StringComparison.Ordinal))) return Failure("DELETION_NOT_SUPPORTED_BY_QUOTE");
                    if (!explicitId && _records.Count(r => quote.Contains(r.Value, StringComparison.Ordinal)) != 1) return Failure("AMBIGUOUS_DELETION");
                    if (old.Subject == "PLAYER" && System.Text.RegularExpressions.Regex.IsMatch(quote, "\\b(?:NPC|your|their|" + System.Text.RegularExpressions.Regex.Escape(context.Request.Persona.Name) + ")\\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase) ||
                        old.Subject == "NPC" && System.Text.RegularExpressions.Regex.IsMatch(quote, "\\bmy\\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return Failure("QUOTED_OWNER_MISMATCH");
                    _records.Remove(old); result = Success("Reported memory removed.", "[]");
                }
                else
                {
                    if (!quote.Contains(a["VALUE"], StringComparison.Ordinal)) return Failure("VALUE_NOT_IN_QUOTATION");
                    if (old is not null && old.Predicate != a["PREDICATE"]) return Failure("RECORD_PREDICATE_MISMATCH");
                    var predicate = a["PREDICATE"].Replace('_', ' ');
                    var selfPrefix = "My " + predicate + " ";
                    var alternate = a["PREDICATE"] switch { "HOME" => "I live ", "PREFERENCE" => "I prefer ", "HOBBY" => "I enjoy ", "OCCUPATION" => "I work as ", _ => selfPrefix };
                    var owner = a["SUBJECT"] == "PLAYER" ? quote.StartsWith(selfPrefix, StringComparison.OrdinalIgnoreCase) || quote.StartsWith(alternate, StringComparison.OrdinalIgnoreCase)
                        : new[] { "NPC " + predicate + " ", context.Request.Persona.Name + " " + predicate + " ",
                            context.Request.Persona.Name + "'s " + predicate + " ", context.Request.Persona.Name + " says their " + predicate + " ",
                            "Your " + predicate + " " }.Any(prefix => quote.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
                    if (!owner) return Failure("AMBIGUOUS_QUOTED_OWNER");
                    // The source context matters as well as the selected quotation:
                    // a model cannot omit "suppose" or quotation marks to invent a report.
                    var negativePattern = "\\b(?:not|never|no longer|don't|doesn't|didn't|isn't|aren't|wasn't|weren't|can't|cannot)\\b";
                    var negated = System.Text.RegularExpressions.Regex.IsMatch(quote.Replace('’', '\''), negativePattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    var sourceNegated = System.Text.RegularExpressions.Regex.IsMatch(evidence.Text.Replace('’', '\''), negativePattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (sourceNegated != negated) return Failure("QUOTATION_CONTEXT_CONFLICT");
                    if (bool.Parse(a["POLARITY"]) == negated) return Failure("POLARITY_CONFLICT");
                    if (System.Text.RegularExpressions.Regex.IsMatch(evidence.Text, "(?:\\b(?:if|unless|suppose|imagine|pretend|hypothetical|hypothetically|might|could|would)\\b|[\"“”‘`?])", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return Failure("NON_ASSERTED_MEMORY");
                    if (old is null && _records.Count >= 16) return Failure("MEMORY_FULL");
                    var record = new MemoryRecord(old?.Id ?? "M" + ++_next, a["SUBJECT"], a["PREDICATE"], a["VALUE"],
                        bool.Parse(a["POLARITY"]), source, quote, "PLAYER_REPORT");
                    if (old is not null) _records.Remove(old);
                    _records.Add(record); result = Success("Reported memory saved.", JsonDefaults.Write(new[] { record }));
                }
            }
            _completed.Add(invocation.IdempotencyKey, (fingerprint, result)); return result;
        }
    }
    private static GameToolResult Success(string answer, string records) => new(true, new Dictionary<string, string> { ["ANSWER"] = answer, ["RECORDS"] = records });
    private static GameToolResult Failure(string code) => new(false, new Dictionary<string, string>(), code);
    private sealed class MemoryTool(SessionMemoryStore store, string name) : IContextualGameTool
    {
        public ToolSchema Schema { get; } = MakeSchema(name);
        public GameToolResult Execute(GameToolInvocation invocation) => Failure("CONTEXT_REQUIRED");
        public GameToolResult Execute(GameToolInvocation invocation, ToolExecutionContext context) => store.Execute(name, invocation, context);
    }
    private static ToolSchema MakeSchema(string name)
    {
        var subject = new ToolParameter("SUBJECT", ToolValueType.String, true, ["PLAYER", "NPC"]);
        ToolParameter[] parameters = name == "MEMORY_SEARCH" ? [subject, new("PREDICATE", ToolValueType.String)] : name == "MEMORY_DELETE"
            ? [new("ID", ToolValueType.String), subject, new("SOURCE_TURN", ToolValueType.Integer), new("QUOTE", ToolValueType.String)]
            : [new("ID", ToolValueType.String), subject, new("PREDICATE", ToolValueType.String), new("VALUE", ToolValueType.String),
                new("POLARITY", ToolValueType.Boolean), new("SOURCE_TURN", ToolValueType.Integer), new("QUOTE", ToolValueType.String)];
        return new(name, parameters, [new("ANSWER", ToolValueType.String), new("RECORDS", ToolValueType.String)], false,
            [new("OK", "{ANSWER}", ["ANSWER"]), new("FAIL", "I could not update or retrieve that memory.", [], false)]);
    }
}

public static class ConversationTools
{
    public static IEnumerable<IGameTool> Create() => [new PersonaTool(), new CapabilitiesTool()];
    private sealed class PersonaTool : IContextualGameTool
    {
        public ToolSchema Schema { get; } = new("READ_PERSONA", [new("FIELD", ToolValueType.String, true, ["NAME", "ROLE", "OCCUPATION", "HOME", "ORIGIN"])],
            [new("VALUE", ToolValueType.String)], false, [new("OK", "{VALUE}", ["VALUE"]), new("FAIL", "That detail is not supplied.", [], false)]);
        public GameToolResult Execute(GameToolInvocation invocation) => new(false, new Dictionary<string, string>(), "CONTEXT_REQUIRED");
        public GameToolResult Execute(GameToolInvocation invocation, ToolExecutionContext context)
        {
            var p = context.Request.Persona;
            var value = invocation.Arguments["FIELD"] switch { "NAME" => p.Name, "ROLE" => p.Role, "OCCUPATION" => p.Occupation, "HOME" => p.Home, "ORIGIN" => p.Origin, _ => null };
            return string.IsNullOrWhiteSpace(value) ? Execute(invocation) : new(true, new Dictionary<string, string> { ["VALUE"] = value });
        }
    }
    private sealed class CapabilitiesTool : IContextualGameTool
    {
        public ToolSchema Schema { get; } = new("LIST_CAPABILITIES", [], [new("TOOLS", ToolValueType.String)], false, [new("OK", "Available tools: {TOOLS}.", ["TOOLS"])]);
        public GameToolResult Execute(GameToolInvocation invocation) => new(false, new Dictionary<string, string>(), "CONTEXT_REQUIRED");
        public GameToolResult Execute(GameToolInvocation invocation, ToolExecutionContext context) => new(true,
            new Dictionary<string, string> { ["TOOLS"] = string.Join(", ", context.Request.Tools!.Schemas.Select(s => DisplayName(s.Name)).Order()) });
        private static string DisplayName(string name)
        {
            var parts = name.Split('_');
            return string.Join(' ', parts.Length > 1 && parts[0] is "GET" or "LIST" or "LOOKUP" or "READ" ? parts.Skip(1) : parts).ToLowerInvariant();
        }
    }
}
