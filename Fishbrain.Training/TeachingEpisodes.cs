using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Fishbrain;

/// <summary>Authoring-only gold replay. Uses the same reducer and typed tools as inference.</summary>
internal static class TeachingEpisodes
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseUpper) }
    };

    internal static void Prepare(string input, string output)
    {
        if (Directory.Exists(output)) throw new ArgumentException("Use a new teaching corpus directory.");
        Directory.CreateDirectory(output);
        foreach (var split in new[] { "train", "validation", "test" })
        {
            var rows = File.ReadLines(Path.Combine(input, split + ".jsonl"))
                .Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => JsonNode.Parse(x)!.AsObject()).ToArray();
            using var writer = new StreamWriter(Path.Combine(output, split + ".jsonl"));
            foreach (var episode in rows.GroupBy(r => r["training"]!["episodeId"]!.GetValue<string>()))
            {
                var state = NpcDialogueState.Initial;
                var history = new List<DialogueUtterance>();
                var tools = DemoGameTools.CreateMerchant();
                var sequenceOffset = episode.First()["episodeSequenceOffset"]?.GetValue<long>() ?? 0;
                foreach (var row in episode)
                {
                    var text = row["turns"]!.AsArray()[^1]!["text"]!.GetValue<string>();
                    var current = new DialogueUtterance(sequenceOffset + history.Count, DialogueRole.Player, text);
                    history.Add(current);
                    var p = Read<StructuredPerception>(row["structuredPerception"]!);
                    var gold = Read<ContextualSupervision>(row["contextual"]!);
                    var persona = Read<NpcPersona>(row["persona"]!);
                    var frames = gold.Frames!;
                    var relevant = new List<DialogueFact>();
                    for (var i = 0; i < frames.Length; i++)
                    {
                        if (frames[i].Fact is not { FactKind: { } kind } fact || fact.Act is not (DiscourseAct.Correct or DiscourseAct.ReferBack)) continue;
                        var owner = fact.Act == DiscourseAct.ReferBack ? fact.Target : fact.Subject;
                        var matches = state.SessionFacts.Where(f => f.Subject == owner && f.Kind == kind).ToArray();
                        relevant.AddRange(matches);
                        var source = matches.LastOrDefault()?.SourceUtterance;
                        frames[i] = frames[i] with { Antecedent = source, Fact = fact with { AntecedentUtterance = source } };
                        if (frames.Length == 1) p = p with { Discourse = frames[i].Fact };
                    }
                    gold = gold with { Frames = frames, RelevantFacts = relevant.Distinct().Take(8).ToArray() };
                    gold.Validate(text);
                    row["turns"] = Node(history);
                    row["input"] = DialogueText.Normalize(string.Join(' ', history.Select(u => $"{u.Speaker} {u.Text}")));
                    row["initialDialogueState"] = Node(state);
                    row["structuredPerception"] = Node(p);
                    row["contextual"] = Node(gold);
                    string? selectedTool = null;
                    GameToolResult? result = null;
                    var reply = row["response"]?.GetValue<string>() ?? "";
                    var pending = state.PendingActions.ToList();
                    foreach (var act in gold.Plan!)
                    {
                        if (act.Act != DialogueResponseAct.ExecuteTool || act.FrameIndex is not { } index) continue;
                        var f = frames[index];
                        if (f.ToolName is null || !tools.TryGet(f.ToolName, out var tool)) throw new InvalidDataException("Gold tool is unavailable.");
                        if (f.Status != ActionStatus.Affirmative && (f.Status != ActionStatus.Question || tool.Schema.MutatesWorldState) ||
                            ActionLanguage.ExecutionVeto(ActionLanguage.SurroundingSentence(text, f.Start, f.Length), tool.Schema.MutatesWorldState) is not null)
                            throw new InvalidDataException("Gold plan tried to execute a non-executable clause: " + text);
                        var binding = DemoDialogueDomains.Merchant.Tools.Single(t => t.Schema.Name == f.ToolName);
                        if (ActionLanguage.ConflictingExplicitAction(text.Substring(f.Start, f.Length), binding, DemoDialogueDomains.Merchant))
                            throw new InvalidDataException("Gold tool conflicts with an explicit action: " + text);
                        var arguments = new Dictionary<string, string>();
                        foreach (var parameter in tool.Schema.Parameters)
                        {
                            var values = f.Arguments.Where(s => s.Type == binding.Parameters[parameter.Name]).Select(s => s.Value).Distinct().ToArray();
                            if (values.Length != 1) throw new InvalidDataException("Gold tool arguments must be explicit and unambiguous.");
                            arguments[parameter.Name] = binding.Parameters[parameter.Name] == SlotType.Quantity
                                ? Brain.NormalizeQuantity(values[0]) : DemoDialogueDomains.Merchant.CanonicalEntity(values[0]);
                        }
                        if (selectedTool is not null)
                        {
                            pending.Add(new("EXECUTE_TOOL", f.ToolName, arguments) { SourceUtterance = current.Sequence });
                            continue;
                        }
                        selectedTool = f.ToolName;
                        result = GameToolRegistry.InvokeValidated(tool, new(f.ToolName, arguments,
                            GameToolRegistry.IdempotencyKey(episode.Key, current.Sequence.ToString())));
                        reply = GameToolRegistry.Render(tool.Schema, result);
                    }
                    if (selectedTool is null && Brain.TryRenderPersona(p.KnowledgeTarget, persona, tools, out var identity, out _)) reply = identity;
                    if (selectedTool is null && gold.Plan.Any(a => a.Act == DialogueResponseAct.Clarify)) reply = "COULD YOU EXPLAIN WHAT YOU MEAN?";
                    if (reply.Length == 0 && frames.FirstOrDefault(f => f.Fact?.Act == DiscourseAct.ReferBack)?.Fact is { } remembered)
                        reply = Brain.RecallMemory(remembered, relevant.Select(f => new MemorySelection(f, 1)).ToArray());
                    if (reply.Length == 0) reply = "I AM LISTENING.";
                    var plan = new TurnPlan(p.Policy, selectedTool, null, p.KnowledgeTarget, pending.Take(3).ToArray(),
                        p.Policy == ResponsePolicy.Clarify ? reply : null, []);
                    var next = DialogueStateReducer.Apply(state, PlayerConversationProfile.Empty, current, current.Sequence + 1,
                        p, plan, result, reply, null);
                    next = next with
                    {
                        SessionFacts = DialogueStateReducer.ReduceFrameFacts(state.SessionFacts, frames, current.Sequence),
                        Agenda = DialogueStateReducer.ReduceAgendaPlan(state.Agenda, gold.Agenda ?? [], gold.Plan!, frames, selectedTool, result)
                    };
                    next.Validate();
                    row["factDelta"] = Node(next.SessionFacts);
                    row["contextual"]!["agenda"] = Node(next.Agenda);
                    writer.WriteLine(row.ToJsonString(Json));
                    history.Add(new(current.Sequence + 1, DialogueRole.Npc, reply));
                    state = next;
                }
            }
            Console.WriteLine($"GOLD REPLAY {split} {rows.Length}");
        }
    }

    private static T Read<T>(JsonNode value) => value.Deserialize<T>(Json) ?? throw new InvalidDataException("Missing gold annotation.");
    private static JsonNode Node<T>(T value) => JsonSerializer.SerializeToNode(value, Json)!;
}
