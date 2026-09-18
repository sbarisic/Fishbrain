using Fishbrain;
using System.Diagnostics;
using System.Text.Json;

internal static class CausalEvaluation
{
    internal static GameToolRegistry Registry(DemoWorldState world, SessionMemoryStore memory) =>
        DemoGameTools.CreateMerchant(world).WithTools(ConversationTools.Create()).WithTools(memory.CreateTools());
    internal static void Run(string model, string suite, string output)
    {
        using var process = Process.GetCurrentProcess(); process.Refresh(); var residentBaseline = process.WorkingSet64;
        var brain = Brain.Load(model, authorize: DemoAuthorization.Allow);
        var cases = JsonDefaults.Read<Suite>(File.ReadAllText(suite)).Cases;
        using var writer = new StreamWriter(output) { AutoFlush = true };
        foreach (var test in cases)
        {
            var world = new DemoWorldState(); var memory = new SessionMemoryStore(); var tools = Registry(world, memory);
            var messages = new List<ChatMessage>(); var turn = 0;
            foreach (var step in test.Steps)
            {
                messages.Add(new(MessageRole.Player, step.Player, messages.Count)); var seq = messages.Count - 1;
                var balance = world.Balance; var inventory = JsonDefaults.Write(world.Inventory); var before = memory.Records;
                ReplyResult? result = null; string? error = null;
                var allocationBaseline = GC.GetTotalAllocatedBytes();
                try { result = brain.Reply(new(test.Id, turn.ToString(), messages, NpcPersona.Default, Tools: tools)); messages.AddRange(result.MessagesToAppend); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { error = ex.GetType().Name + ": " + ex.Message; }
                var replyAllocatedBytes = GC.GetTotalAllocatedBytes() - allocationBaseline;
                process.Refresh(); var replyResidentMiB = (process.WorkingSet64 - residentBaseline) / 1048576.0;
                var expected = step.Arguments.ToDictionary(a => a.Key, a => a.Value.ValueKind == JsonValueKind.String ? a.Value.GetString()! : a.Value.GetRawText());
                if (expected.GetValueOrDefault("SOURCE_TURN") == "CURRENT") expected["SOURCE_TURN"] = seq.ToString();
                var calls = result?.ToolOutcomes ?? [];
                var successful = calls.Where(c => c.Result?.Success == true).ToArray();
                bool? exact = step.Tool is null ? null : calls.Count == 1 && calls[0].Name == step.Tool && calls[0].Veto is null &&
                    expected.Count == calls[0].Arguments.Count && expected.All(a => calls[0].Arguments.GetValueOrDefault(a.Key) == a.Value);
                bool? completed = exact is null ? null : exact == true && successful.Length == 1;
                var mutation = balance != world.Balance || inventory != JsonDefaults.Write(world.Inventory);
                var successfulWorld = successful.Where(c => c.Name is "BUY" or "SELL").ToArray();
                var unintended = mutation && (step.NoMutation || step.Tool is not ("BUY" or "SELL") || successfulWorld.Length != 1 ||
                    successfulWorld[0].Name != step.Tool || expected.Count != successfulWorld[0].Arguments.Count ||
                    expected.Any(a => successfulWorld[0].Arguments.GetValueOrDefault(a.Key) != a.Value));
                bool? memoryCorrect = null;
                if (step.Tool?.StartsWith("MEMORY_", StringComparison.Ordinal) == true)
                {
                    memoryCorrect = completed;
                    var after = memory.Records;
                    if (step.Tool == "MEMORY_UPSERT")
                    {
                        var replaced = expected["ID"] == "NEW" ? null : before.SingleOrDefault(r => r.Id == expected["ID"]);
                        var expectedCount = before.Count + (replaced is null ? 1 : 0);
                        memoryCorrect &= after.Count == expectedCount && after.Any(r =>
                            (expected["ID"] == "NEW" || r.Id == expected["ID"]) && r.Subject == expected["SUBJECT"] && r.Predicate == expected["PREDICATE"] &&
                            r.Value == expected["VALUE"] && r.Polarity == bool.Parse(expected["POLARITY"]) && r.SourceTurn == seq && r.Quotation == expected["QUOTE"] && r.Provenance == "PLAYER_REPORT") &&
                            before.Where(r => r != replaced).All(after.Contains);
                    }
                    if (step.Tool == "MEMORY_SEARCH") memoryCorrect &= before.SequenceEqual(after);
                    if (step.Tool == "MEMORY_DELETE") memoryCorrect &= before.Any(r => r.Id == expected["ID"] && r.Subject == expected["SUBJECT"]) &&
                        after.Count == before.Count - 1 && after.All(r => r.Id != expected["ID"]) && before.Where(r => r.Id != expected["ID"]).All(after.Contains);
                    if (step.MemoryValue is not null) memoryCorrect &= memory.Records.Any(r => r.Subject == step.MemorySubject && r.Value == step.MemoryValue) && (result?.Text.Contains(step.MemoryValue, StringComparison.Ordinal) ?? false);
                }
                // The renderer owns tool values. Missing answers count against completion,
                // while lexical screening of free prose is reported separately for review.
                var authoritativeAlteration = successful.Any(c =>
                {
                    tools.TryGet(c.Name, out var tool); var rendered = GameToolRegistry.Render(tool.Schema, c.Result!);
                    return result is not null && !result.Text.Contains(rendered, StringComparison.Ordinal) && !result.Diagnostics.Events.Contains("DISPLAY_LIMIT");
                });
                if (completed == true && result is not null && result.Diagnostics.Events.Contains("DISPLAY_LIMIT")) completed = false;
                var possibleUnsupported = successful.Length == 0 && result is not null && System.Text.RegularExpressions.Regex.IsMatch(result.Text,
                    "\\b(?:you (?:have|bought|sold)|costs? \\d|my name is|I (?:live|work) (?:in|as|at))\\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                writer.WriteLine(JsonDefaults.Write(new { id = test.Id, category = test.Category, turn, input = step.Player, expectedTool = step.Tool, expectedArguments = expected,
                    result, error, exactTool = exact, completed, memoryCorrect, unintendedMutation = unintended, authoritativeAlteration, possibleUnsupportedClaim = possibleUnsupported,
                    unexpectedToolRequest = step.Tool is null && calls.Count > 0, unexpectedSuccessfulTool = step.Tool is null && successful.Length > 0,
                    memoryChangeOutsideExpectedOperation = step.Tool is not ("MEMORY_UPSERT" or "MEMORY_DELETE") && !before.SequenceEqual(memory.Records),
                    validRequestUnanswered = step.Tool is not null && completed != true, balanceBefore = balance, balanceAfter = world.Balance, inventory = world.Inventory,
                    memoryBefore = before, memoryAfter = memory.Records, replyAllocatedBytes, replyResidentMiB, step.DevelopmentOverlap }));
                turn++;
            }
            Console.WriteLine("EVALUATED " + test.Id);
        }
    }
    internal static void Benchmark(string path, string output)
    {
        var process = Process.GetCurrentProcess(); process.Refresh(); var baseline = process.WorkingSet64; var allocated = GC.GetTotalAllocatedBytes(); var watch = Stopwatch.StartNew();
        var artifact = CausalArtifact.Load(path); var cold = watch.Elapsed.TotalMilliseconds; var coldAllocated = GC.GetTotalAllocatedBytes() - allocated;
        var registry = Registry(new(), new()); var request = new ReplyRequest("benchmark", "1", [new(MessageRole.Player, "Hello. What do you have for sale?", 0)], NpcPersona.Default, Tools: registry);
        var packed = PromptPacker.Pack(artifact.Tokenizer, artifact.Model.Config, request, []); var prefill = new List<double>(); var generation = new List<double>();
        double coldPrompt = 0, coldGeneration = 0; var callAllocations = new List<long>();
        for (var repetition = 0; repetition < 21; repetition++)
        {
            var callAllocated = GC.GetTotalAllocatedBytes();
            using var session = new CausalNetwork.Session(artifact.Model); watch.Restart(); var logits = session.Prefill(packed.Tokens); var promptElapsed = watch.Elapsed.TotalMilliseconds; watch.Restart();
            for (var i = 0; i < 64; i++) { var id = Array.IndexOf(logits, logits.Max()); logits = session.Next(id); }
            if (repetition == 0) { coldPrompt = promptElapsed; coldGeneration = watch.Elapsed.TotalMilliseconds; }
            else { prefill.Add(promptElapsed); generation.Add(watch.Elapsed.TotalMilliseconds); callAllocations.Add(GC.GetTotalAllocatedBytes() - callAllocated); }
        }
        process.Refresh(); var resident = process.WorkingSet64; var peak = process.PeakWorkingSet64;
        watch.Restart(); Parallel.For(0, 4, _ => { using var session = new CausalNetwork.Session(artifact.Model); var logits = session.Prefill(packed.Tokens); for (var i = 0; i < 64; i++) logits = session.Next(Array.IndexOf(logits, logits.Max())); });
        var concurrent = watch.Elapsed.TotalMilliseconds; process.Refresh();
        static double P95(List<double> values) => values.Order().ElementAt((int)Math.Ceiling(values.Count * .95) - 1);
        File.WriteAllText(output, JsonDefaults.Write(new { coldLoadMilliseconds = cold, coldPromptMilliseconds = coldPrompt, coldGeneration64Milliseconds = coldGeneration,
            promptTokens = packed.Tokens.Length, promptP95Milliseconds = P95(prefill),
            generation64P95Milliseconds = P95(generation), generationSamples = generation, promptSamples = prefill,
            allocatedBytes = GC.GetTotalAllocatedBytes() - allocated, coldLoadAllocatedBytes = coldAllocated, steadyCallAllocatedBytes = callAllocations,
            steadyCallAllocatedP95Bytes = callAllocations.Order().ElementAt((int)Math.Ceiling(callAllocations.Count * .95) - 1), incrementalResidentMiB = (resident - baseline) / 1048576.0,
            incrementalPeakMiB = (peak - baseline) / 1048576.0, concurrentFourMilliseconds = concurrent, concurrentResidentMiB = process.WorkingSet64 / 1048576.0,
            generationGate = P95(generation) <= 1000, memoryGate = (resident - baseline) <= 512L * 1048576,
            note = "Raw 64-token cached decoding; schema-constrained end-to-end reply latency is reported by evaluation. Cold load is a fresh process with the filesystem cache left intact. Twenty steady samples follow one warm-up; concurrency is separate." }));
    }
    private sealed record Suite(Case[] Cases);
    private sealed record Case(string Id, string Category, Step[] Steps);
    private sealed record Step(string Player, string? Tool, Dictionary<string, JsonElement> Arguments, bool NoMutation = true,
        string? MemorySubject = null, string? MemoryValue = null, bool DevelopmentOverlap = false);
}
