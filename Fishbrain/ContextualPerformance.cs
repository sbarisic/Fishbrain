using System.Diagnostics;
using System.Text.Json;
using Fishbrain.Neural;

namespace Fishbrain;

internal static class ContextualPerformance
{
    internal static int Run(string path, int iterations)
    {
        if (iterations < 8) throw new ArgumentException("Resource measurement needs at least eight iterations.");
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var managedBefore = GC.GetTotalMemory(true);
        var process = Process.GetCurrentProcess(); process.Refresh();
        var residentBefore = process.WorkingSet64;
        var load = Stopwatch.StartNew();
        var loaded = ContextualCheckpoint.Load(path, DemoDialogueDomains.Merchant);
        var coldLoadMilliseconds = load.Elapsed.TotalMilliseconds;
        var model = loaded.Model;
        var brain = Brain.CreateContextualForEvaluation(model, loaded.Header.CompletedSteps, loaded.Header.ExecutionThresholds);
        var request = new ReplyRequest("PERFORMANCE", "1", [new(0, DialogueRole.Player, "THE ROAD IS QUIET. WHAT DO YOU THINK?")],
            NpcDialogueState.Initial, NpcPersona.Default, PlayerConversationProfile.Empty, 1, 42, ResponseMode.DeterministicOnly);
        var tools = DemoGameTools.CreateMerchant();
        for (var i = 0; i < 32; i++) brain.Reply(request, tools);
        var understanding = new List<double>(); var realization = new List<double>(); var allocations = new List<long>();
        var activeResidentPeak = residentBefore;
        for (var i = 0; i < iterations; i++)
        {
            var allocation = GC.GetAllocatedBytesForCurrentThread();
            var result = brain.Reply(request with { Seed = i }, tools);
            understanding.Add(result.Contextual!.UnderstandingMilliseconds);
            var parameters = model.Parameters();
            var packed = StructuredInput.Pack(request, model.Tokenizer, model.Config.ContextLength, [], model.Domain);
            var output = model.Understand(new TensorGraph(false), parameters, packed);
            var clock = Stopwatch.StartNew();
            var cache = model.CreateDecoderSession(parameters, output.PlanMemory);
            var token = Tokenizer.Bos;
            for (var step = 0; step < 64; step++)
            {
                var logits = cache.Next(token);
                var outputId = model.Tokenizer.GeneratedTextOutputs.Where(x => model.Vocabulary.InputIdFromOutput(x) != Tokenizer.Eos)
                    .MaxBy(x => logits.Data[x]);
                token = model.Vocabulary.InputIdFromOutput(outputId);
                if (step % 8 == 0) { process.Refresh(); activeResidentPeak = Math.Max(activeResidentPeak, process.WorkingSet64); }
            }
            realization.Add(clock.Elapsed.TotalMilliseconds + result.Contextual.UnderstandingMilliseconds);
            allocations.Add(GC.GetAllocatedBytesForCurrentThread() - allocation);
        }
        process.Refresh();
        var steadyResidentDelta = process.WorkingSet64 - residentBefore;
        var shortContextP95 = P95(understanding);
        var boundedFacts = Enumerable.Range(0, 16).Select(i => new DialogueFact(DialogueParticipant.Player,
            DialogueFactKind.Experience, $"I VISITED ROAD {i}", false, i, 1, DialogueFactProvenance.SessionReported)).ToArray();
        var longRequest = request with
        {
            Utterances = Enumerable.Range(0, 32).Select(i => new DialogueUtterance(i,
            i % 2 == 0 ? DialogueRole.Npc : DialogueRole.Player, i == 31 ? "WHAT DID I SAY ABOUT MY TRAVELS?" : string.Join(' ', Enumerable.Repeat("THE ROAD IS QUIET.", 8)))).ToArray(),
            State = NpcDialogueState.Initial with { SessionFacts = boundedFacts },
            ResponseSequence = 32
        };
        var longTimes = new List<double>();
        int longTokens = 0, selectedMemories = 0;
        for (var i = 0; i < iterations; i++)
        {
            var result = brain.Reply(longRequest, tools);
            longTimes.Add(result.Contextual!.UnderstandingMilliseconds);
            longTokens = result.Diagnostics.PackedTokenCount;
            selectedMemories = result.Contextual.Memory.Count;
            process.Refresh(); activeResidentPeak = Math.Max(activeResidentPeak, process.WorkingSet64);
        }
        var concurrentClock = Stopwatch.StartNew();
        var concurrent = Enumerable.Range(0, iterations).AsParallel().WithDegreeOfParallelism(4)
            .Select(i => brain.Reply(request with { TurnId = i.ToString() }, tools).Contextual!.UnderstandingMilliseconds).ToList();
        var concurrentElapsed = concurrentClock.Elapsed.TotalMilliseconds;
        GC.Collect(2, GCCollectionMode.Aggressive, true, true); GC.WaitForPendingFinalizers(); GC.Collect(2, GCCollectionMode.Aggressive, true, true);
        var managedDelta = GC.GetTotalMemory(true) - managedBefore;
        process.Refresh();
        var residentDelta = process.WorkingSet64 - residentBefore;
        var pass = shortContextP95 <= 100 && P95(longTimes) <= 100 && P95(realization) <= 1000 &&
            activeResidentPeak - residentBefore <= 512L * 1024 * 1024;
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            Iterations = iterations,
            model.Config,
            model.ParameterCount,
            ColdLoadMilliseconds = coldLoadMilliseconds,
            UnderstandingP95Milliseconds = P95(understanding),
            Full64TokenReplyP95Milliseconds = P95(realization),
            IncrementalManagedBytes = managedDelta,
            IncrementalResidentBytes = residentDelta,
            AverageAllocatedBytes = allocations.Average(),
            SteadyResidentBytesBeforeCompaction = steadyResidentDelta,
            ActiveResidentPeakBytes = activeResidentPeak - residentBefore,
            LongContext = new { InputTokens = longTokens, CandidateFacts = boundedFacts.Length, SelectedFacts = selectedMemories, UnderstandingP95Milliseconds = P95(longTimes) },
            Concurrent = new { Workers = 4, Replies = iterations, UnderstandingP95Milliseconds = P95(concurrent), ElapsedMilliseconds = concurrentElapsed },
            MeasurementNotes = "Batch-one resource gate includes short and long context. Four-worker timings are separate. Allocation count includes an extra plan preparation for the forced 64-token decode. Compacted resident memory is diagnostic, not the release gate.",
            OptimizerAllocated = false,
            ResourceGate = pass ? "PASS" : "FAIL",
            Machine = Environment.MachineName,
            LogicalProcessors = Environment.ProcessorCount,
            Framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription
        }, new JsonSerializerOptions { WriteIndented = true }));
        GC.KeepAlive(brain); GC.KeepAlive(model);
        return pass ? 0 : 1;
        static double P95(List<double> values) => values.Order().ElementAt((int)Math.Ceiling(values.Count * .95) - 1);
    }
}
