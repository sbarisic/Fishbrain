using Fishbrain;
using Fishbrain.Neural;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

Console.InputEncoding = Encoding.UTF8;
Console.OutputEncoding = new UTF8Encoding(false);

try
{
    if (args.Length == 0) throw new ArgumentException("Commands: chat MODEL [TRACE.jsonl], inspect MODEL, schemas OUTPUT.json, evaluate MODEL SUITE OUTPUT.jsonl, benchmark MODEL OUTPUT.json, compile-trajectories PLAN.jsonl OUTPUT.jsonl, pack-corpus ROOT OUTPUT.jsonl, reference MODEL INPUT.json OUTPUT.json");
    if (args[0] == "evaluate") { CausalEvaluation.Run(args[1], args[2], args[3]); return; }
    if (args[0] == "benchmark") { CausalEvaluation.Benchmark(args[1], args[2]); return; }
    if (args[0] == "schemas")
    {
        var tools = Registry(new(), new()); File.WriteAllText(args[1], JsonDefaults.Write(tools.Schemas));
        return;
    }
    if (args[0] == "compile-trajectories")
    {
        using var output = new StreamWriter(args[2]);
        foreach (var line in File.ReadLines(args[1]))
        {
            var episode = JsonDefaults.Read<TeachingEpisode>(line); var w = new DemoWorldState(); var m = new SessionMemoryStore(); var r = Registry(w, m);
            var messages = new List<ChatMessage>(); var targets = new List<object>(); var playerSequences = new List<long>();
            foreach (var step in episode.Steps)
            {
                messages.Add(new(MessageRole.Player, step.Player, messages.Count));
                playerSequences.Add(messages[^1].Sequence);
                if ((step.RequiredSteps ?? []).Any(i => i < 0 || i >= playerSequences.Count))
                    throw new InvalidDataException("Required history must reference an existing player step.");
                var requiredSequences = (step.RequiredSteps ?? []).Select(i => playerSequences[i]).ToArray();
                var teacherCalls = step.Calls ?? (step.Call is null ? [] : [step.Call]);
                if (teacherCalls.Length > 0)
                {
                    var sourceSequence = messages.Count - 1; var requestMessages = messages.ToArray(); var clauses = new List<string>(); var mutations = 0;
                    if (teacherCalls.Length > 4) throw new InvalidDataException("Teacher tool budget exceeded.");
                    foreach (var call in teacherCalls)
                    {
                    if (!r.TryGet(call.Name, out var tool)) throw new InvalidDataException("Unknown teacher tool.");
                    var arguments = call.Arguments.ToDictionary(a => a.Key, a => a.Value.ValueKind == JsonValueKind.String ? a.Value.GetString()! : a.Value.GetRawText());
                    if (arguments.TryGetValue("SOURCE_TURN", out var turn) && turn == "CURRENT") arguments["SOURCE_TURN"] = sourceSequence.ToString();
                    var request = new ReplyRequest(episode.Id, sourceSequence.ToString(), requestMessages, NpcPersona.Default, Tools: r);
                    var invocation = new GameToolInvocation(call.Name, arguments, episode.Id + ":" + messages.Count);
                    if (tool.Schema.MutatesWorldState && (Brain.ActionVeto(step.Player) is not null || !DemoAuthorization.Allow(request, invocation)))
                        throw new InvalidDataException("Teacher supplied an unauthorized action.");
                    if (tool.Schema.MutatesWorldState && ++mutations > 1) throw new InvalidDataException("Teacher mutation budget exceeded.");
                    var protocol = ProtocolText.Call(call.Name, arguments, tool.Schema);
                    targets.Add(new { history = messages.ToArray(), target = protocol, authoritative = false, requiredSequences });
                    messages.Add(new(MessageRole.AssistantToolCall, protocol, messages.Count, call.Name));
                    var result = GameToolRegistry.InvokeValidated(tool, invocation, new(request, request.Messages.ToArray()));
                    if (!result.Success && !step.AllowFailure) throw new InvalidDataException($"Teacher tool failed: {episode.Id}: {result.ErrorCode}");
                    messages.Add(new(MessageRole.ToolResult, JsonDefaults.Write(new { name = call.Name, result }), messages.Count, call.Name));
                    clauses.Add(GameToolRegistry.Render(tool.Schema, result));
                    }
                    var text = string.Join(" ", clauses);
                    targets.Add(new { history = messages.ToArray(), target = JsonDefaults.Write(new { type = "text", text }), authoritative = true, requiredSequences });
                    messages.Add(new(MessageRole.Assistant, text, messages.Count));
                }
                else
                {
                    var text = step.Text ?? throw new InvalidDataException("Missing teacher reply.");
                    targets.Add(new { history = messages.ToArray(), target = JsonDefaults.Write(new { type = "text", text }), authoritative = false, requiredSequences });
                    messages.Add(new(MessageRole.Assistant, text, messages.Count));
                }
            }
            output.WriteLine(JsonDefaults.Write(new { episode.Id, episode.Family, episode.Split, episode.Pool, messages, targets,
                provenance = new { license = "PROJECT-OWNED", author = "Fishbrain contributors and agent", augmentation = false } }));
        }
        return;
    }
    if (args[0] == "pack-corpus")
    {
        var root = args[1]; var tokenizer = new ByteBpe(JsonDefaults.Read<BpeDefinition>(File.ReadAllText(Path.Combine(root, "prepared/tokenizer.json"))));
        var registry = Registry(new(), new()); var config = new CausalConfig(); var accepted = 0; var excluded = new Dictionary<string, int>();
        using var output = new StreamWriter(args[2]);
        foreach (var line in File.ReadLines(Path.Combine(root, "prepared/episodes.jsonl")))
        {
            using var doc = JsonDocument.Parse(line); var e = doc.RootElement;
            var id = e.GetProperty("id").GetString(); var family = e.GetProperty("family").GetString(); var split = e.GetProperty("split").GetString(); var pool = e.GetProperty("pool").GetString();
            var targets = new List<(ChatMessage[] History, string Target, long[] Required)>();
            if (e.TryGetProperty("targets", out var supplied))
                foreach (var t in supplied.EnumerateArray())
                {
                    var h = JsonDefaults.Read<ChatMessage[]>(t.GetProperty("history").GetRawText());
                    // Unannotated legacy conversations are conservative: no silent loss of their context.
                    targets.Add((h, t.GetProperty("target").GetString()!, t.TryGetProperty("requiredSequences", out var required)
                        ? JsonDefaults.Read<long[]>(required.GetRawText()) : h.Where(m => m.Role == MessageRole.Player).Select(m => m.Sequence).ToArray()));
                }
            else
            {
                var messages = JsonDefaults.Read<ChatMessage[]>(e.GetProperty("messages").GetRawText());
                targets.Add((messages[..^1], JsonDefaults.Write(new { type = "text", text = messages[^1].Text }),
                    messages.Where(m => m.Role == MessageRole.Player).Select(m => m.Sequence).ToArray()));
            }
            foreach (var (history, target, required) in targets)
            {
                try
                {
                    var current = Array.FindLastIndex(history, m => m.Role == MessageRole.Player);
                    if (current < 0) throw new ArgumentException("NO_CURRENT_PLAYER");
                    var request = new ReplyRequest(id!, current.ToString(), history[..(current + 1)], NpcPersona.Default, Tools: registry);
                    var packed = PromptPacker.Pack(tokenizer, config, request, history[(current + 1)..]);
                    if (required.Any(sequence => !packed.Retained.Any(m => m.Role == MessageRole.Player && m.Sequence == sequence)))
                        throw new ArgumentException("REQUIRED_HISTORY_EVICTED");
                    var protocol = ProtocolGrammar.Parse(target, registry); var answer = tokenizer.Encode(target).Append(ByteBpe.Eos).ToArray();
                    if (answer.Length > 256) throw new ArgumentException("PROTOCOL_TOO_LONG");
                    if (protocol.Type == "text" && (protocol.Text!.Length > 256 || tokenizer.Encode(protocol.Text).Length > 64)) throw new ArgumentException("RESPONSE_TOO_LONG");
                    output.WriteLine(JsonDefaults.Write(new { id, family, split, pool, tokens = packed.Tokens.Concat(answer).ToArray(), promptLength = packed.Tokens.Length,
                        historyTurns = packed.Retained.Count, retainedSequences = packed.Retained.Select(m => m.Sequence),
                        requiredSequences = required, promptFormat = PromptPacker.Format, target })); accepted++;
                }
                catch (ArgumentException ex) { var reason = ex.Message; excluded[reason] = excluded.GetValueOrDefault(reason) + 1; }
            }
        }
        File.WriteAllText(args[2] + ".audit.json", JsonDefaults.Write(new { accepted, excluded })); return;
    }
    if (args[0] is "reference" or "forward")
    {
        var loaded = CausalArtifact.Load(args[1]); var input = JsonDefaults.Read<ReferenceInput>(File.ReadAllText(args[2]));
        var gradients = args[0] == "reference";
        var graph = new TensorGraph(gradients); var p = loaded.Model.Parameters(gradients); var logits = loaded.Model.Forward(graph, p, input.Tokens);
        var loss = 0f;
        if (gradients)
        {
            for (var row = 0; row < input.Targets.Length; row++) loss += graph.CrossEntropy(logits, row, input.Targets[row], 1f / input.Targets.Length);
            graph.Backward();
        }
        using var cache = new CausalNetwork.Session(loaded.Model); var cached = cache.Prefill(input.Tokens[..1]);
        foreach (var id in input.Tokens.Skip(1)) cached = cache.Next(id);
        using var promptCache = new CausalNetwork.Session(loaded.Model);
        var prefilled = promptCache.Prefill(input.Tokens);
        File.WriteAllText(args[3], JsonDefaults.Write(new { logits = logits.Data, loss, cached, prefilled,
            gradients = p.ToDictionary(k => k.Key, k => k.Value.Gradient), encodings = input.Texts.Select(t => loaded.Tokenizer.Encode(t)).ToArray(),
            roundTrips = input.Texts.Select(t => loaded.Tokenizer.Decode(loaded.Tokenizer.Encode(t))).ToArray() })); return;
    }
    if (args[0] == "inspect")
    {
        var a = CausalArtifact.Load(args[1]); Console.WriteLine(JsonDefaults.Write(new { a.Header.Config, a.Header.Updates, a.Header.Phase, a.Model.ParameterCount, a.Header.TrainingFingerprint })); return;
    }
    if (args[0] != "chat") throw new ArgumentException("Unknown command.");
    {
    var world = new DemoWorldState(); var memory = new SessionMemoryStore(); var registry = Registry(world, memory);
    var brain = Brain.Load(args[1], registry, authorize: DemoAuthorization.Allow); var history = new List<ChatMessage>(); var conversation = Guid.NewGuid().ToString("N");
    using var trace = args.Length > 2 ? new StreamWriter(args[2], append: true) { AutoFlush = true } : null;
    Console.WriteLine("Enter dialogue or an empty line to quit.");
    while (true)
    {
        Console.Write("> "); var line = Console.ReadLine(); if (string.IsNullOrWhiteSpace(line)) break;
        history.Add(new(MessageRole.Player, line, history.Count));
        try
        {
            var result = brain.Reply(new(conversation, history.Count.ToString(), history, NpcPersona.Default));
            Console.WriteLine(result.Text); history.AddRange(result.MessagesToAppend);
            trace?.WriteLine(JsonDefaults.Write(new { input = line, result, balance = world.Balance, inventory = world.Inventory, memory = memory.Records }));
        }
        catch (ArgumentException ex) { Console.WriteLine(ex.Message); history.RemoveAt(history.Count - 1); }
    }
    }
}
catch (Exception ex) { Console.Error.WriteLine(ex.Message); Environment.ExitCode = 1; }

static GameToolRegistry Registry(DemoWorldState world, SessionMemoryStore memory) => DemoGameTools.CreateMerchant(world).WithTools(ConversationTools.Create()).WithTools(memory.CreateTools());
internal sealed record ReferenceInput(int[] Tokens, int[] Targets, string[] Texts);
internal sealed record TeachingEpisode(string Id, string Family, string Split, string Pool, TeachingStep[] Steps);
internal sealed record TeachingStep(string Player, string? Text = null, TeachingCall? Call = null, bool AllowFailure = false, TeachingCall[]? Calls = null, int[]? RequiredSteps = null);
internal sealed record TeachingCall(string Name, Dictionary<string, JsonElement> Arguments);
