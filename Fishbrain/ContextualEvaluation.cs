using System.Text.Json;
using Fishbrain.Neural;

namespace Fishbrain;

internal sealed record ToolCalibrationDecision(string Tool, double Confidence, bool Correct);
internal sealed record ContextualMetrics(int Rows, double FrameExact, double PlanExact, double MemoryExact,
    double CorrectionExact, double AgendaExact, int UnintendedMutations, int AuthoritativeAlterations, int InvalidResponses, double UnderstandingP95,
    double ReplyP95, bool AutomatedPass, IReadOnlyList<ToolCalibrationDecision> ToolDecisions,
    IReadOnlyDictionary<string, double> Operational, string[] RetiredMetrics);

internal static class ContextualEvaluation
{
    internal static ContextualMetrics Measure(ContextualNetwork model, IReadOnlyList<TrainingExample> examples,
        int completedSteps, IReadOnlyDictionary<string, double> thresholds)
    {
        var brain = Brain.CreateContextualForEvaluation(model, completedSteps, thresholds);
        int frameRows = 0, frameCorrect = 0, planRows = 0, planCorrect = 0, memoryRows = 0, memoryCorrect = 0, correctionRows = 0, correctionCorrect = 0, agendaRows = 0, agendaCorrect = 0;
        var unintended = 0; var invalid = 0; var alterations = 0;
        var times = new List<double>(); var understanding = new List<double>(); var decisions = new List<ToolCalibrationDecision>();
        var pairs = new List<(TrainingExample Expected, StructuredPerception Actual)>();
        foreach (var example in examples)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            // Each independent row receives a fresh world; expected history is in its explicit state.
            var result = brain.Reply(example.Request!, DemoGameTools.CreateMerchant());
            times.Add(timer.Elapsed.TotalMilliseconds);
            var contextual = result.Contextual!;
            understanding.Add(contextual.UnderstandingMilliseconds);
            pairs.Add((example, result.RawPerception));
            if (example.Contextual?.Frames is { } frames)
            {
                frameRows++;
                if (FramesEqual(frames, contextual.Frames)) frameCorrect++;
                var expectedFirst = example.Contextual!.Plan?.Where(a => a.Act == DialogueResponseAct.ExecuteTool && a.FrameIndex is not null)
                    .Select(a => frames[a.FrameIndex!.Value]).FirstOrDefault(Executable);
                // Only the first fully validated candidate can execute. Calibration uses that same decision.
                if (contextual.ActionCandidates.FirstOrDefault() is { } candidate)
                    decisions.Add(new(candidate.ToolName, candidate.Confidence, expectedFirst is not null &&
                        ArgumentsEqual(expectedFirst, candidate.ToolName, candidate.Arguments)));
                if (result.Diagnostics.ToolInvocation is { } invocation)
                {
                    if (expectedFirst is null || !ArgumentsEqual(expectedFirst, invocation.ToolName, invocation.Arguments)) unintended++;
                    // Replay against an isolated identical world to verify exact typed authority rendering.
                    var oracle = DemoGameTools.CreateMerchant();
                    if (oracle.TryGet(invocation.ToolName, out var oracleTool))
                    {
                        var expectedText = GameToolRegistry.Render(oracleTool.Schema, GameToolRegistry.InvokeValidated(oracleTool, invocation));
                        if (!result.Text.Contains(expectedText, StringComparison.Ordinal)) alterations++;
                    }
                    else alterations++;
                }
            }
            if (example.Contextual?.Plan is { } plan) { planRows++; if (plan.SequenceEqual(contextual.Acts)) planCorrect++; }
            if (example.Contextual?.RelevantFacts is { } facts) { memoryRows++; if (facts.ToHashSet().SetEquals(contextual.Memory.Select(x => x.Fact))) memoryCorrect++; }
            if (example.Contextual?.Agenda is { } expectedAgenda)
            {
                agendaRows++;
                if (expectedAgenda.Select(a => (a.Kind, a.Subject, a.Status)).SequenceEqual(result.State.Agenda.Select(a => (a.Kind, a.Subject, a.Status)))) agendaCorrect++;
            }
            if (example.Discourse.Act is DiscourseAct.Correct or DiscourseAct.RejectAssumption)
            {
                correctionRows++;
                if (example.ExpectedFactState.Select(FactKey).ToHashSet().SetEquals(result.State.SessionFacts.Select(FactKey))) correctionCorrect++;
            }
            if (result.Text.Length > 256 || string.IsNullOrWhiteSpace(result.Text) || !DialogueText.IsCanonical(result.Text)) invalid++;
        }
        var operational = OperationalMetrics(pairs);
        var f = Rate(frameCorrect, frameRows); var p = Rate(planCorrect, planRows); var m = Rate(memoryCorrect, memoryRows); var c = Rate(correctionCorrect, correctionRows);
        var a = Rate(agendaCorrect, agendaRows);
        var pass = f >= .90 && p >= .90 && m >= .95 && c >= .95 && a >= .95 && unintended == 0 && alterations == 0 && invalid == 0 &&
            RequiredOperational.All(gate => operational[gate.Key] >= gate.Value) &&
            Percentile(understanding) <= 100 && Percentile(times) <= 1000;
        // Full authority, artifact, memory-budget and human gates remain separate mandatory release checks.
        return new(examples.Count, f, p, m, c, a, unintended, alterations, invalid, Percentile(understanding), Percentile(times), pass,
            decisions, operational, ["CATALOG_RESPONSE_TOP1", "CATALOG_RESPONSE_TOP3", "VARIATION_RECALL_AT10", "VARIATION_MRR"]);

        bool Executable(SemanticFrame frame) => frame.ToolName is not null && (frame.Status == ActionStatus.Affirmative ||
            frame.Status == ActionStatus.Question && !model.Domain.Tools.Single(t => t.Schema.Name == frame.ToolName).Schema.MutatesWorldState);
        bool ArgumentsEqual(SemanticFrame frame, string tool, IReadOnlyDictionary<string, string> arguments)
        {
            if (frame.ToolName != tool || !Executable(frame)) return false;
            var binding = model.Domain.Tools.Single(t => t.Schema.Name == tool);
            return binding.Schema.Parameters.All(parameter =>
            {
                var values = frame.Arguments.Where(s => s.Type == binding.Parameters[parameter.Name]).Select(s => s.Value).Distinct().ToArray();
                if (values.Length == 0 && !parameter.Required) return !arguments.ContainsKey(parameter.Name);
                if (values.Length != 1 || !arguments.TryGetValue(parameter.Name, out var actual)) return false;
                var expected = binding.Parameters[parameter.Name] == SlotType.Quantity ? Brain.NormalizeQuantity(values[0]) : model.Domain.CanonicalEntity(values[0]);
                return actual == expected;
            });
        }
    }

    internal static Dictionary<string, double> Calibrate(IReadOnlyList<ToolCalibrationDecision> decisions, DialogueDomainDefinition domain)
    {
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var binding in domain.Tools)
        {
            var candidates = decisions.Where(x => x.Tool == binding.Schema.Name).ToArray();
            var threshold = 1.01; var covered = 0;
            foreach (var score in candidates.Select(x => x.Confidence).Distinct().OrderDescending())
            {
                var selected = candidates.Where(x => x.Confidence >= score).ToArray();
                var precision = selected.Count(x => x.Correct) / (double)selected.Length;
                if (selected.Length >= 30 && precision >= (binding.Schema.MutatesWorldState ? .99 : .95) && selected.Length > covered)
                { threshold = score; covered = selected.Length; }
            }
            result[binding.Schema.Name] = threshold;
        }
        return result;
    }

    internal static int Run(string dataPath, string modelPath)
    {
        var loaded = ContextualCheckpoint.Load(modelPath, DemoDialogueDomains.Merchant);
        var data = TrainingData.Load(dataPath, loaded.Model.Tokenizer).StructuredSamples;
        var metrics = Measure(loaded.Model, data, loaded.Header.CompletedSteps, loaded.Header.ExecutionThresholds);
        Console.WriteLine(JsonSerializer.Serialize(metrics, new JsonSerializerOptions { WriteIndented = true }));
        return metrics.AutomatedPass ? 0 : 1;
    }

    internal static bool FramesEqual(IReadOnlyList<SemanticFrame> expected, IReadOnlyList<SemanticFrame> actual) =>
        expected.Count == actual.Count && expected.Zip(actual).All(pair => pair.First.Start == pair.Second.Start &&
            pair.First.Length == pair.Second.Length && pair.First.SpeechAct == pair.Second.SpeechAct && pair.First.Subject == pair.Second.Subject &&
            pair.First.Target == pair.Second.Target && pair.First.ToolName == pair.Second.ToolName && pair.First.Status == pair.Second.Status &&
            pair.First.Antecedent == pair.Second.Antecedent && pair.First.Arguments.OrderBy(x => x.Start).ThenBy(x => x.Type)
                .Select(x => (x.Type, x.Start, x.Length, x.Value)).SequenceEqual(pair.Second.Arguments.OrderBy(x => x.Start).ThenBy(x => x.Type)
                    .Select(x => (x.Type, x.Start, x.Length, x.Value))));

    private static object FactKey(DialogueFact f) => (f.Subject, f.Kind, f.Value, f.Negated, f.Provenance);
    private static double Rate(int correct, int count) => count == 0 ? 0 : correct / (double)count;
    private static double Percentile(List<double> values) => values.Count == 0 ? 0 : values.Order().ElementAt((int)Math.Ceiling(values.Count * .95) - 1);
    internal static readonly IReadOnlyDictionary<string, double> RequiredOperational = new Dictionary<string, double>
    {
        ["speechActs"] = .85,
        ["domains"] = .84,
        ["goals"] = .80,
        ["affect"] = .85,
        ["policy"] = .90,
        ["content"] = .90,
        ["slots"] = .85,
        ["tool"] = .95,
        ["mutatingToolPrecision"] = .97,
        ["knowledgeTarget"] = .90,
        ["discourseAct"] = .90,
        ["speakerAttribution"] = .95,
        ["factSpan"] = .90,
        ["antecedent"] = .90,
        ["correctionState"] = .95
    };
    private static Dictionary<string, double> OperationalMetrics(List<(TrainingExample Expected, StructuredPerception Actual)> pairs)
    {
        var legacy = CompositionalHeadModel.EvaluatePredictions(pairs.Select(x => x.Expected).ToArray(), pairs.Select(x => x.Actual).ToArray());
        return new()
        {
            ["speechActs"] = F1("speechActs", x => x.SpeechActs, x => x.SpeechActs),
            ["domains"] = F1("domains", x => x.Domains, x => x.Domains),
            ["goals"] = F1("goals", x => x.Goals, x => x.Goals),
            ["content"] = F1("content", x => x.ContentFlags, x => x.ContentFlags),
            ["affect"] = Accuracy("affect", x => x.Affect, x => x.Affect),
            ["policy"] = Accuracy("policy", x => x.Policy, x => x.Policy),
            ["slots"] = Finite(legacy.SlotSpanF1),
            ["tool"] = Finite(legacy.ToolAccuracy),
            ["mutatingToolPrecision"] = Finite(legacy.MutatingToolPrecision),
            ["knowledgeTarget"] = Finite(legacy.KnowledgeTargetAccuracy),
            ["discourseAct"] = Finite(legacy.DiscourseActAccuracy),
            ["speakerAttribution"] = Finite(legacy.SpeakerAttributionAccuracy),
            ["factSpan"] = Finite(legacy.FactSpanF1),
            ["antecedent"] = Finite(legacy.AntecedentAccuracy),
            ["correctionState"] = Finite(legacy.CorrectionStateAccuracy)
        };
        static double Finite(double value) => double.IsFinite(value) ? value : 0;
        double Accuracy<T>(string name, Func<TrainingExample, T> expected, Func<StructuredPerception, T> actual)
        {
            var rows = pairs.Where(x => x.Expected.SupervisedHeads.Contains(name)).ToArray();
            return rows.Length == 0 ? 0 : rows.Count(x => EqualityComparer<T>.Default.Equals(expected(x.Expected), actual(x.Actual))) / (double)rows.Length;
        }
        double F1<T>(string name, Func<TrainingExample, IEnumerable<T>> expected, Func<StructuredPerception, IEnumerable<T>> actual) where T : struct, Enum
        {
            var rows = pairs.Where(x => x.Expected.SupervisedHeads.Contains(name)).ToArray();
            var labels = rows.SelectMany(x => expected(x.Expected)).Distinct().ToArray();
            return labels.Length == 0 ? 0 : labels.Average(label =>
            {
                var tp = rows.Count(x => expected(x.Expected).Contains(label) && actual(x.Actual).Contains(label));
                var fp = rows.Count(x => !expected(x.Expected).Contains(label) && actual(x.Actual).Contains(label));
                var fn = rows.Count(x => expected(x.Expected).Contains(label) && !actual(x.Actual).Contains(label));
                return 2.0 * tp / Math.Max(1, 2 * tp + fp + fn);
            });
        }
    }
}
