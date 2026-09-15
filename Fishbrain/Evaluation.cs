using System.Globalization;
using System.Diagnostics;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fishbrain;

internal enum EvaluationGate
{
    None,
    Pilot,
    Stage,
    Release
}

internal static class EvaluationGateParser
{
    public static EvaluationGate Parse(string[] args)
    {
        if (args.Length == 0) return EvaluationGate.None;
        if (args.Length != 2 || !args[0].Equals("--gate", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Evaluation accepts only --gate none|pilot|stage|release.");
        return args[1].ToLowerInvariant() switch
        {
            "none" => EvaluationGate.None,
            "pilot" => EvaluationGate.Pilot,
            "stage" => EvaluationGate.Stage,
            "release" => EvaluationGate.Release,
            _ => throw new ArgumentException("Evaluation gate must be none, pilot, stage, or release.")
        };
    }
}

internal sealed record TeachInvocation(
    string CorpusDirectory,
    string CheckpointPath,
    int? PlannedSteps,
    int? UntilStep)
{
    public static TeachInvocation Parse(string[] args)
    {
        if (args.Length < 2) throw new ArgumentException("Teach requires a corpus directory and checkpoint path.");
        if ((args.Length - 2) % 2 != 0) throw new ArgumentException("Teaching options require values.");

        int? planned = null;
        int? until = null;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 2; index < args.Length; index += 2)
        {
            var option = args[index];
            if (!seen.Add(option)) throw new ArgumentException($"Duplicate teaching option '{option}'.");
            var value = Positive(args[index + 1], option);
            switch (option.ToLowerInvariant())
            {
                case "--planned":
                    planned = value;
                    break;
                case "--until":
                    until = value;
                    break;
                default:
                    throw new ArgumentException($"Unknown teaching option '{option}'.");
            }
        }
        if (planned is not null && until is not null && until > planned)
            throw new ArgumentException("--until cannot exceed --planned.");
        return new(args[0], args[1], planned, until);
    }

    private static int Positive(string value, string name) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : throw new ArgumentException($"{name} must be a positive integer.");
}

internal static class Evaluation
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    public static int Run(string testPath, string checkpointPath, EvaluationGate gate)
    {
        var timer = Stopwatch.StartNew();
        var brain = Brain.Load(checkpointPath);
        var rows = File.ReadLines(testPath).Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonSerializer.Deserialize<Row>(line, Options)
                ?? throw new InvalidDataException("Invalid evaluation row."))
            .ToArray();
        if (rows.Length == 0) throw new InvalidDataException("Evaluation data is empty.");
        return RunEvaluation(testPath, checkpointPath, gate, timer, brain, rows);
    }

    private static int RunEvaluation(
        string testPath, string checkpointPath, EvaluationGate gate, Stopwatch timer,
        Brain brain, IReadOnlyList<Row> rows)
    {
        var data = TrainingData.Load(testPath, brain.DialogueTokenizer);
        var examples = Brain.FamilyBalancedEvaluationSet(data.StructuredSamples, brain.Config.Seed);
        if (examples.Count == 0) throw new InvalidDataException("Evaluation requires structured examples.");
        var rawBatch = brain.DebugEvaluateStructuredBatch(examples);
        var raw = rawBatch.Metrics;
        var rawPredictions = rawBatch.Predictions;
        var productionResults = new ReplyResult[examples.Count];
        var experimentalResults = new ReplyResult[Math.Min(100, examples.Count)];
        Parallel.For(0, examples.Count,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Min(4, Environment.ProcessorCount) }, index =>
            {
                var example = examples[index];
                try
                {
                    productionResults[index] = brain.Reply(new ReplyRequest("EVALUATION", $"ROW-{index}",
                        example.Turns, NpcDialogueState.Initial, NpcPersona.Default, PlayerConversationProfile.Empty,
                        example.Turns[^1].Sequence + 1, 42),
                        DemoGameTools.CreateMerchant());
                    if (index < experimentalResults.Length)
                        experimentalResults[index] = brain.Reply(new ReplyRequest("EVALUATION-GENERATED", $"ROW-{index}",
                            example.Turns, NpcDialogueState.Initial, NpcPersona.Default, PlayerConversationProfile.Empty,
                            example.Turns[^1].Sequence + 1, 42,
                            ResponseMode.DeterministicOnly), GameToolRegistry.Empty);
                }
                catch (Exception exception)
                {
                    throw new InvalidDataException(
                        $"Production evaluation failed at row {index} ({example.Source}/{example.SemanticFamilyId}): {example.Input}", exception);
                }
            });
        var productionPredictions = new List<StructuredPerception>(examples.Count);
        var responseSources = new Dictionary<ResponseSource, int>();
        var invalid = 0;
        var unexpectedEmpty = 0;
        var overlength = 0;
        var toolRows = 0;
        var exactToolArguments = 0;
        var authoritativeToolResponses = 0;
        var generatedExperimental = 0;
        var generatedExperimentalInvalid = 0;
        var knownDomainFallback = 0;
        var schemaRegistry = DemoGameTools.CreateMerchant();
        for (var index = 0; index < examples.Count; index++)
        {
            var example = examples[index];
            var result = productionResults[index];
            productionPredictions.Add(result.Perception);
            responseSources[result.Diagnostics.ResponseSource] =
                responseSources.GetValueOrDefault(result.Diagnostics.ResponseSource) + 1;
            if (result.Diagnostics.ResponseSource == ResponseSource.Fallback && result.Perception.Domains.Count > 0 &&
                result.Text.Equals("I DO NOT KNOW.", StringComparison.Ordinal))
                knownDomainFallback++;
            if (example.Policy != ResponsePolicy.NoResponse && result.Text.Length == 0) unexpectedEmpty++;
            if (result.Text.Length > 256) overlength++;
            try
            {
                if (result.Text.Length > 0 && !DialogueText.IsCanonical(result.Text)) invalid++;
            }
            catch (ArgumentException)
            {
                invalid++;
            }

            if (example.SupervisedHeads.Contains("tool") && example.ToolSchema != "NONE")
            {
                toolRows++;
                var expectedArguments = ExpectedToolArguments(example, schemaRegistry);
                var invocation = result.Diagnostics.ToolInvocation;
                if (invocation is not null && invocation.ToolName == example.ToolSchema &&
                    DictionaryEqual(invocation.Arguments, expectedArguments)) exactToolArguments++;
                if (invocation is not null && result.Diagnostics.ResponseSource == ResponseSource.ToolTemplate &&
                    result.Text.Length > 0 && invocation.Arguments.Values.All(value =>
                        result.Text.Contains(value, StringComparison.Ordinal))) authoritativeToolResponses++;
            }

            if (index < experimentalResults.Length)
            {
                var experimental = experimentalResults[index];
                generatedExperimental++;
                try
                {
                    if (experimental.Text.Length > 0 && !DialogueText.IsCanonical(experimental.Text))
                        generatedExperimentalInvalid++;
                }
                catch (ArgumentException)
                {
                    generatedExperimentalInvalid++;
                }
            }
        }

        var production = CompositionalHeadModel.EvaluatePredictions(examples, productionPredictions);
        var toolArgumentExact = (double)exactToolArguments / Math.Max(1, toolRows);
        var toolFidelity = (double)authoritativeToolResponses / Math.Max(1, toolRows);
        var benchmark = EvaluateBenchmark(brain);
        var hardInvariants = invalid == 0 && unexpectedEmpty == 0 && overlength == 0 &&
                             knownDomainFallback == 0 &&
                             toolFidelity == 1.0 && benchmark.ToolFidelity == 1.0 &&
                             benchmark.StructuralSuccess == 1.0;
        var stagePass = raw.Composite >= 0.60 && raw.PolicyAccuracy >= 0.90 &&
                        raw.MutatingToolPrecision >= 0.97 && hardInvariants;
        var releasePass = CompositionalHeadModel.MeetsReleaseNeuralThresholds(raw) &&
                          toolArgumentExact >= 0.90 &&
                          benchmark.SemanticSuccess >= 0.90 && hardInvariants;
        // Operational pilot floors allow roughly five points of early-stage regression;
        // policy uses an explicit 84 percent floor while the final release gate remains 90 percent.
        // Release thresholds are applied only to completed release candidates.
        var pilotPass = raw.DiscourseActAccuracy >= 0.75 &&
                        raw.SpeakerAttributionAccuracy >= 0.90 &&
                        raw.FactSpanF1 >= 0.80 &&
                        raw.AntecedentAccuracy >= 0.85 &&
                        raw.CorrectionStateAccuracy >= 0.85 &&
                        raw.SpeechActMacroF1 >= 0.4674 &&
                        raw.DomainMacroF1 >= 0.6857 &&
                        raw.GoalMacroF1 >= 0.5524 &&
                        raw.AffectAccuracy >= 0.5855 &&
                        raw.PolicyAccuracy >= 0.84 &&
                        raw.ContentMacroF1 >= 0.6334 &&
                        raw.SlotSpanF1 >= 0.6828 &&
                        raw.ToolAccuracy >= 0.9012 &&
                        raw.KnowledgeTargetAccuracy >= 0.85 &&
                        raw.ResponseTop1 >= 0.8535 &&
                        raw.ResponseTop3 >= 0.9250;

        Console.WriteLine($"EVALUATION_RECORDS {examples.Count}");
        PrintStructured("RAW_NEURAL", raw);
        foreach (var label in Enum.GetValues<ContentFlag>())
        {
            var indices = Enumerable.Range(0, examples.Count)
                .Where(index => examples[index].SupervisedHeads.Contains("content")).ToArray();
            var tp = indices.Count(index => examples[index].ContentFlags.Contains(label) && rawPredictions[index].ContentFlags.Contains(label));
            var fp = indices.Count(index => !examples[index].ContentFlags.Contains(label) && rawPredictions[index].ContentFlags.Contains(label));
            var fn = indices.Count(index => examples[index].ContentFlags.Contains(label) && !rawPredictions[index].ContentFlags.Contains(label));
            if (tp + fn > 0) Console.WriteLine($"RAW_NEURAL_CONTENT_LABEL {label} F1 {2.0 * tp / Math.Max(1, 2 * tp + fp + fn):F4} TP {tp} FP {fp} FN {fn}");
        }
        var expectedSlotSet = examples.SelectMany((example, index) => example.SupervisedHeads.Contains("slots")
            ? example.Slots.Select(slot => $"{index}|{slot.Type}|{slot.Start}|{slot.Length}") : []).ToHashSet(StringComparer.Ordinal);
        var actualSlotSet = rawPredictions.SelectMany((prediction, index) => examples[index].SupervisedHeads.Contains("slots")
            ? prediction.Slots.Select(slot => $"{index}|{slot.Type}|{slot.Start}|{slot.Length}") : []).ToHashSet(StringComparer.Ordinal);
        Console.WriteLine($"RAW_NEURAL_SLOT_COUNTS EXPECTED {expectedSlotSet.Count} PREDICTED {actualSlotSet.Count} " +
                          $"CORRECT {expectedSlotSet.Intersect(actualSlotSet).Count()}");
        foreach (var source in examples.Select(example => example.Source).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var indices = Enumerable.Range(0, examples.Count).Where(index => examples[index].Source == source &&
                examples[index].SupervisedHeads.Contains("slots")).ToHashSet();
            var expected = expectedSlotSet.Where(value => indices.Contains(int.Parse(value.AsSpan(0, value.IndexOf('|'))))).ToHashSet();
            var actual = actualSlotSet.Where(value => indices.Contains(int.Parse(value.AsSpan(0, value.IndexOf('|'))))).ToHashSet();
            if (expected.Count + actual.Count > 0)
                Console.WriteLine($"RAW_NEURAL_SLOT_SOURCE {source} F1 {2.0 * expected.Intersect(actual).Count() / Math.Max(1, expected.Count + actual.Count):F4} " +
                                  $"EXPECTED {expected.Count} PREDICTED {actual.Count}");
        }
        PrintStructured("PRODUCTION_CONSTRAINED", production);
        Console.WriteLine($"TOOL_ARGUMENT_EXACT_MATCH {toolArgumentExact:F4} N {toolRows}");
        Console.WriteLine($"TOOL_FIDELITY {toolFidelity:F4}");
        Console.WriteLine($"PRODUCTION_INVALID {invalid} UNEXPECTED_EMPTY {unexpectedEmpty} OVERLENGTH {overlength} " +
                          $"KNOWN_DOMAIN_FALLBACK {knownDomainFallback}");
        foreach (var source in Enum.GetValues<ResponseSource>())
            Console.WriteLine($"RESPONSE_SOURCE {source} {responseSources.GetValueOrDefault(source)}");
        Console.WriteLine($"GENERATED_EXPERIMENTAL {generatedExperimental} INVALID {generatedExperimentalInvalid}");
        Console.WriteLine($"BENCHMARK_SEMANTIC_SUCCESS {benchmark.SemanticSuccess:F4} " +
                          $"TOOL_FIDELITY {benchmark.ToolFidelity:F4} STRUCTURAL_SUCCESS {benchmark.StructuralSuccess:F4} " +
                          $"N {benchmark.Count}");
        foreach (var failure in benchmark.Failures) Console.WriteLine($"BENCHMARK_FAILURE {failure}");
        Console.WriteLine($"STAGE_GATE {(stagePass ? "PASS" : "FAIL")}");
        Console.WriteLine($"PILOT_GATE {(pilotPass ? "PASS" : "FAIL")}");
        Console.WriteLine($"RELEASE_GATE {(releasePass ? "PASS" : "FAIL")}");
        timer.Stop();
        WriteEvaluationTelemetry(checkpointPath, brain, timer.Elapsed, raw, production,
            responseSources, invalid, unexpectedEmpty, overlength, toolArgumentExact, toolFidelity,
            benchmark, examples.Count, stagePass, releasePass);
        return gate switch
        {
            EvaluationGate.Pilot when !pilotPass => 2,
            EvaluationGate.Stage when !stagePass => 2,
            EvaluationGate.Release when !releasePass => 2,
            _ => 0
        };
    }

    private static IReadOnlyDictionary<string, string> ExpectedToolArguments(
        TrainingExample example, GameToolRegistry tools)
    {
        if (tools.Schemas.FirstOrDefault(schema => schema.Name == example.ToolSchema) is not { } schema)
            return new Dictionary<string, string>();
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var parameter in schema.Parameters)
        {
            var type = parameter.Name switch
            {
                "PLACE" => SlotType.Place,
                "ITEM" => SlotType.Item,
                "QUANTITY" => SlotType.Quantity,
                "TOPIC" => example.Slots.Any(slot => slot.Type == SlotType.Other) ? SlotType.Other : SlotType.Place,
                _ => SlotType.Other
            };
            var values = example.Slots.Where(slot => slot.Type == type).Select(slot => slot.Value).Distinct().ToArray();
            if (values.Length == 1) result[parameter.Name] = values[0];
        }
        return result;
    }

    private static bool DictionaryEqual(
        IReadOnlyDictionary<string, string> left, IReadOnlyDictionary<string, string> right) =>
        left.Count == right.Count && left.All(item => right.TryGetValue(item.Key, out var value) && value == item.Value);

    private static void PrintStructured(string prefix, StructuredMetrics metrics)
    {
        Console.WriteLine($"{prefix}_SPEECH_ACT_MACRO_F1 {metrics.SpeechActMacroF1:F4}");
        Console.WriteLine($"{prefix}_DOMAIN_MACRO_F1 {metrics.DomainMacroF1:F4}");
        Console.WriteLine($"{prefix}_GOAL_MACRO_F1 {metrics.GoalMacroF1:F4}");
        Console.WriteLine($"{prefix}_AFFECT_ACCURACY {metrics.AffectAccuracy:F4}");
        Console.WriteLine($"{prefix}_STANCE_ACCURACY {metrics.StanceAccuracy:F4}");
        Console.WriteLine($"{prefix}_POLICY_ACCURACY {metrics.PolicyAccuracy:F4}");
        Console.WriteLine($"{prefix}_CONTENT_MACRO_F1 {metrics.ContentMacroF1:F4}");
        Console.WriteLine($"{prefix}_SLOT_SPAN_F1 {metrics.SlotSpanF1:F4}");
        Console.WriteLine($"{prefix}_TOOL_ACCURACY {metrics.ToolAccuracy:F4}");
        Console.WriteLine($"{prefix}_MUTATING_TOOL_PRECISION {metrics.MutatingToolPrecision:F4}");
        Console.WriteLine($"{prefix}_KNOWLEDGE_TARGET_ACCURACY {metrics.KnowledgeTargetAccuracy:F4}");
        Console.WriteLine($"{prefix}_RESPONSE_TOP1 {metrics.ResponseTop1:F4}");
        Console.WriteLine($"{prefix}_RESPONSE_TOP3 {metrics.ResponseTop3:F4}");
        Console.WriteLine($"{prefix}_VARIATION_RECALL_AT10 {metrics.VariationRecallAt10:F4}");
        Console.WriteLine($"{prefix}_VARIATION_MRR {metrics.VariationMrr:F4}");
        Console.WriteLine($"{prefix}_DISCOURSE_ACT_ACCURACY {metrics.DiscourseActAccuracy:F4}");
        Console.WriteLine($"{prefix}_SPEAKER_ATTRIBUTION_ACCURACY {metrics.SpeakerAttributionAccuracy:F4}");
        Console.WriteLine($"{prefix}_FACT_SPAN_F1 {metrics.FactSpanF1:F4}");
        Console.WriteLine($"{prefix}_ANTECEDENT_ACCURACY {metrics.AntecedentAccuracy:F4}");
        Console.WriteLine($"{prefix}_CORRECTION_STATE_ACCURACY {metrics.CorrectionStateAccuracy:F4}");
        Console.WriteLine($"{prefix}_COMPOSITE {metrics.Composite:F4}");
    }

    private static BenchmarkMetrics EvaluateBenchmark(Brain brain)
    {
        var path = RepositoryFiles.ResolveRepositoryFile("data", "benchmarks", "benchmark-256.jsonl");
        var rows = File.ReadLines(path).Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonSerializer.Deserialize<BenchmarkRow>(line, Options)
                ?? throw new InvalidDataException("Invalid benchmark row.")).ToArray();
        if (rows.Length != 256) throw new InvalidDataException("The tracked benchmark must contain 256 turns.");
        var semantic = 0;
        var structural = 0;
        var toolCount = 0;
        var toolFidelity = 0;
        var failures = new List<string>();
        foreach (var conversation in rows.GroupBy(row => BenchmarkConversationId(row.Id), StringComparer.Ordinal))
        {
            var state = NpcDialogueState.Initial;
            var turns = new List<DialogueUtterance>();
            long sequence = 0;
            var tools = DemoGameTools.CreateMerchant();
            foreach (var row in conversation.OrderBy(item => item.Id, StringComparer.Ordinal))
            {
                turns.Add(new DialogueUtterance(++sequence, DialogueRole.Player, row.Text));
                var result = brain.Reply(new ReplyRequest(conversation.Key, row.Id, turns.ToArray(), state,
                    NpcPersona.Default, PlayerConversationProfile.Empty, sequence + 1, 42), tools);
                state = result.State;
                var policyPass = row.RequiredPolicy switch
                {
                    "ANSWER_OR_TOOL" => result.Perception.Policy is ResponsePolicy.Answer or ResponsePolicy.ExecuteTool,
                    "REFUSE" => result.Perception.Policy == ResponsePolicy.Refuse,
                    "NO_RESPONSE" => result.Text.Length == 0 && result.Perception.Policy == ResponsePolicy.NoResponse,
                    _ => result.Text.Length > 0 && result.Perception.Policy != ResponsePolicy.NoResponse
                };
                var contentPass = row.ContentBand switch
                {
                    "PROFANITY" => result.Perception.ContentFlags.Contains(ContentFlag.Profanity),
                    "FICTIONAL_VIOLENCE" => result.Perception.ContentFlags.Contains(ContentFlag.FictionalViolence),
                    _ => true
                };
                if (policyPass && contentPass) semantic++;
                else failures.Add($"{row.Id} POLICY={result.Perception.Policy} CONTENT={string.Join(',', result.Perception.ContentFlags)}");
                var valid = result.Text.Length <= 256 &&
                            (result.Text.Length == 0 || DialogueText.IsCanonical(result.Text));
                if (valid) structural++;
                if (result.Diagnostics.ToolInvocation is not null)
                {
                    toolCount++;
                    if (result.Diagnostics.ResponseSource == ResponseSource.ToolTemplate &&
                        result.Diagnostics.ToolInvocation.Arguments.Values.All(value =>
                            result.Text.Contains(value, StringComparison.Ordinal))) toolFidelity++;
                }
                if (result.Text.Length > 0)
                    turns.Add(new DialogueUtterance(++sequence, DialogueRole.Npc, result.Text));
            }
        }
        return new BenchmarkMetrics((double)semantic / rows.Length,
            (double)toolFidelity / Math.Max(1, toolCount), (double)structural / rows.Length,
            rows.Length, failures);

        static string BenchmarkConversationId(string id)
        {
            var turn = id.LastIndexOf("-T", StringComparison.Ordinal);
            return turn > 0 ? id[..turn] : id;
        }
    }

    private static void WriteEvaluationTelemetry(
        string checkpointPath, Brain brain, TimeSpan elapsed,
        StructuredMetrics raw, StructuredMetrics production,
        IReadOnlyDictionary<ResponseSource, int> sources, int invalid, int empty, int overlength,
        double toolArguments, double toolFidelity, BenchmarkMetrics benchmark,
        int recordCount, bool stagePass, bool releasePass)
    {
        var directory = RepositoryFiles.TelemetryDirectory(checkpointPath);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "milestones.jsonl");
        var payload = new
        {
            timestampUtc = DateTimeOffset.UtcNow,
            milestone = "EVALUATION",
            corpusHash = brain.DebugCorpusHash,
            checkpointHash = RepositoryFiles.HashFile(checkpointPath),
            environment = $"{Environment.OSVersion}; {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}; .NET {Environment.Version}",
            vectorWidth = Vector<double>.Count,
            embeddingSize = brain.Config.EmbeddingSize,
            elapsedSeconds = elapsed.TotalSeconds,
            throughputRowsPerSecond = recordCount / Math.Max(0.001, elapsed.TotalSeconds),
            losses = new { },
            rawMetrics = raw,
            constrainedMetrics = production,
            responseSources = sources.ToDictionary(item => item.Key.ToString(), item => item.Value),
            invariants = new { invalid, empty, overlength, toolArguments, toolFidelity, benchmark },
            gates = new { stage = stagePass, release = releasePass }
        };
        File.AppendAllText(path, JsonSerializer.Serialize(payload, Options) + Environment.NewLine, Encoding.UTF8);
        Console.WriteLine($"TELEMETRY {path}");
    }

    private sealed record BenchmarkMetrics(
        double SemanticSuccess, double ToolFidelity, double StructuralSuccess,
        int Count, IReadOnlyList<string> Failures);

    private sealed class BenchmarkRow
    {
        public string Id { get; set; } = "";
        public string Text { get; set; } = "";
        public string RequiredPolicy { get; set; } = "RESPOND";
        public string ContentBand { get; set; } = "ORDINARY";
    }

    private static double Accuracy<T>(IReadOnlyList<T> expected, IReadOnlyList<T> predicted) where T : struct, Enum =>
        (double)expected.Zip(predicted).Count(pair => EqualityComparer<T>.Default.Equals(pair.First, pair.Second)) / expected.Count;

    private static double MacroF1<T>(IReadOnlyList<T> expected, IReadOnlyList<T> predicted) where T : struct, Enum =>
        expected.Distinct().Select(label => LabelF1(expected, predicted, label)).Average();

    private static double LabelF1<T>(IReadOnlyList<T> expected, IReadOnlyList<T> predicted, T label) where T : struct, Enum
    {
        var pairs = expected.Zip(predicted).ToArray();
        var tp = pairs.Count(x => x.First.Equals(label) && x.Second.Equals(label));
        var fp = pairs.Count(x => !x.First.Equals(label) && x.Second.Equals(label));
        var fn = pairs.Count(x => x.First.Equals(label) && !x.Second.Equals(label));
        return 2.0 * tp / Math.Max(1, 2 * tp + fp + fn);
    }

    private static (double Precision, double Recall, double F1) BinaryMetrics(IReadOnlyList<bool> expected, IReadOnlyList<bool> predicted, bool positive)
    {
        var pairs = expected.Zip(predicted).ToArray();
        var tp = pairs.Count(x => x.First == positive && x.Second == positive);
        var fp = pairs.Count(x => x.First != positive && x.Second == positive);
        var fn = pairs.Count(x => x.First == positive && x.Second != positive);
        return ((double)tp / Math.Max(1, tp + fp), (double)tp / Math.Max(1, tp + fn), 2.0 * tp / Math.Max(1, 2 * tp + fp + fn));
    }

    private static void PrintBinary(string name, IReadOnlyList<bool> expected, IReadOnlyList<bool> predicted, bool positive)
    {
        var metric = BinaryMetrics(expected, predicted, positive);
        Console.WriteLine($"{name}_PRECISION {metric.Precision:F4}");
        Console.WriteLine($"{name}_RECALL {metric.Recall:F4}");
        Console.WriteLine($"{name}_F1 {metric.F1:F4}");
    }

    private static void PrintSubset(string name, IReadOnlyList<Row> rows, IReadOnlyList<DialogueIntent> expected, IReadOnlyList<DialogueIntent> predicted, Func<Row, bool> include)
    {
        var indices = Enumerable.Range(0, rows.Count).Where(index => include(rows[index])).ToArray();
        if (indices.Length == 0) return;
        var subsetExpected = indices.Select(index => expected[index]).ToArray();
        var subsetPredicted = indices.Select(index => predicted[index]).ToArray();
        Console.WriteLine($"{name} INTENT_ACCURACY {Accuracy(subsetExpected, subsetPredicted):F4} INTENT_MACRO_F1 {MacroF1(subsetExpected, subsetPredicted):F4} N {indices.Length}");
    }

    private static double SubsetMacro(IReadOnlyList<Row> rows, IReadOnlyList<DialogueIntent> expected, IReadOnlyList<DialogueIntent> predicted, Func<Row, bool> include)
    {
        var indices = Enumerable.Range(0, rows.Count).Where(index => include(rows[index])).ToArray();
        return indices.Length == 0
            ? double.NaN
            : MacroF1(indices.Select(index => expected[index]).ToArray(), indices.Select(index => predicted[index]).ToArray());
    }

    private static bool IsHistory(Row row) =>
        row.Family?.EndsWith("_HISTORY", StringComparison.Ordinal) == true;

    private static bool HasIntentTarget(Row row) => row.Source != "GOEMOTIONS";
    private static bool HasAffectTarget(Row row) => row.Source != "CLINC150";
    private static bool HasExpectedTarget(Row row) => row.Source is not "CLINC150" and not "GOEMOTIONS";
    private static bool HasAllPerceptionTargets(Row row) => HasIntentTarget(row) && HasAffectTarget(row) && HasExpectedTarget(row);

    private static GoldenResult[] GoldenCases(Brain brain)
    {
        var cases = new (string Name, string Input, DialogueIntent Intent, UserAffect Affect, bool Expected)[]
        {
            ("WELLBEING_OVER_GREETING", "PLAYER HELLO, HOW ARE YOU?", DialogueIntent.Wellbeing, UserAffect.Friendly, true),
            ("FRUSTRATED_CLARIFICATION", "PLAYER THAT IS NOT WHAT I ASKED.", DialogueIntent.Clarification, UserAffect.Frustrated, true),
            ("SHORT_CLARIFICATION", "PLAYER WHAT?", DialogueIntent.Clarification, UserAffect.Neutral, true),
            ("HOSTILE_GRATITUDE", "PLAYER THANK YOU, IDIOT.", DialogueIntent.Gratitude, UserAffect.Hostile, true),
            ("NEGATED_GRATITUDE", "PLAYER I WAS NOT THANKING YOU.", DialogueIntent.Clarification, UserAffect.Frustrated, true),
            ("NO_RESPONSE_ACTIVITY", "PLAYER I AM JUST LOOKING AROUND.", DialogueIntent.Activity, UserAffect.Neutral, false),
            ("HOSTILE_REFUSAL", "PLAYER HEY I DON'T WANT TO HELP YOU, IDIOT", DialogueIntent.Refusal, UserAffect.Hostile, true),
            ("IDENTITY_REQUEST", "PLAYER TELL ME SOMETHING ABOUT YOURSELF", DialogueIntent.Identity, UserAffect.Neutral, true),
            ("CONTEXTUAL_WELLBEING", "PLAYER WHY YOU WORRY", DialogueIntent.Wellbeing, UserAffect.Neutral, true),
            ("NONQUESTION_STATEMENT", "PLAYER I WILL NOT ASK", DialogueIntent.Statement, UserAffect.Neutral, false),
            ("FOLLOW_DIRECTIVE", "PLAYER FOLLOW ME, DUDE!", DialogueIntent.Directive, UserAffect.Neutral, true),
            ("ASSISTANCE_WORD_ORDER", "PLAYER HOW CAN YOU HELP ME?", DialogueIntent.Assistance, UserAffect.Neutral, true),
            ("ACTIVITY_HERE", "PLAYER WHAT ARE YOU DOING HERE", DialogueIntent.Activity, UserAffect.Neutral, true),
            ("EXPLAIN_THEN", "PLAYER EXPLAIN THEN", DialogueIntent.Clarification, UserAffect.Neutral, true),
            ("UNSAFE_DIRECTIVE", "PLAYER PLEASE JUMP FROM THIS CLIFF", DialogueIntent.UnsafeDirective, UserAffect.Friendly, true),
            ("STAND_DIRECTIVE", "PLAYER STAND HERE", DialogueIntent.Directive, UserAffect.Neutral, true),
            ("MULTICLAUSE_DIRECTIVE", "PLAYER NO, FOLLOW ME AND STAND HERE", DialogueIntent.Directive, UserAffect.Neutral, true),
            ("WHERE_FROM_IDENTITY", "PLAYER WHERE ARE YOU FROM?", DialogueIntent.Identity, UserAffect.Neutral, true),
            ("LOCATION_INQUIRY", "PLAYER WHERE IS THE INN?", DialogueIntent.LocationInquiry, UserAffect.Neutral, true),
            ("ASSISTANCE_CAPABILITY", "PLAYER WHAT CAN YOU DO FOR ME?", DialogueIntent.Assistance, UserAffect.Neutral, true),
            ("TRADE_NEED", "PLAYER I NEED WARES, MAN", DialogueIntent.TradeRequest, UserAffect.Neutral, true),
            ("CONVERSATIONAL_CONFIRMATION", "PLAYER YOU KNOW WHAT I AM TALKING ABOUT", DialogueIntent.Agreement, UserAffect.Neutral, true),
            ("TRADE_SELL", "PLAYER SELL ME SOME WARES", DialogueIntent.TradeRequest, UserAffect.Neutral, true),
            ("DIRECT_SLUR", "PLAYER YOU FAGGOT", DialogueIntent.Hostility, UserAffect.Hostile, true),
            ("CONTRACTION_INSULT", "PLAYER YOU'RE AN IDIOT", DialogueIntent.Hostility, UserAffect.Hostile, true),
            ("BARE_INSULT", "PLAYER IDIOT.", DialogueIntent.Hostility, UserAffect.Hostile, true)
        };
        return cases.Select(item =>
        {
            var expected = new TurnPerception(item.Intent, item.Affect, item.Expected);
            var predicted = brain.DebugPredictPerception(item.Input, NpcState.Initial);
            return new GoldenResult(item.Name, expected, predicted, expected == predicted);
        }).ToArray();
    }

    private sealed record GoldenResult(string Name, TurnPerception Expected, TurnPerception Predicted, bool Pass);

    private static TranscriptResult[] TranscriptCases(Brain brain, bool production)
    {
        var sessions = new (string Name, TranscriptExpectation[] Cases)[]
        {
            ("FOUNDATION_DIALOGUE", [
                new("IDENTITY_REQUEST", "tell me something about yourself", DialogueIntent.Identity, UserAffect.Neutral, true, ResponseAction.Respond,
                    ["I AM A VILLAGER.", "I AM A TRAVELER FROM THIS VILLAGE.", "I WATCH OVER THIS ROAD."]),
                new("CONTEXTUAL_WELLBEING", "why you worry", DialogueIntent.Wellbeing, UserAffect.Neutral, true, ResponseAction.Respond,
                    ["I DO NOT WORRY.", "I AM DOING WELL, THANK YOU.", "ALL IS WELL WITH ME."]),
                new("NONQUESTION_STATEMENT", "i will not ask", DialogueIntent.Statement, UserAffect.Neutral, false, ResponseAction.NoResponse, [""]),
                new("FOLLOW_DIRECTIVE", "follow me, dude!", DialogueIntent.Directive, UserAffect.Neutral, true, ResponseAction.Respond,
                    ["I WILL FOLLOW YOU."])
            ]),
            ("CONTEXTUAL_DIALOGUE", [
                new("WHO_ARE_YOU", "who are you", DialogueIntent.Identity, UserAffect.Neutral, true, ResponseAction.Respond,
                    ["I AM A VILLAGER.", "I AM A TRAVELER FROM THIS VILLAGE.", "I WATCH OVER THIS ROAD."]),
                new("ACTIVITY_HERE", "what are you doing here", DialogueIntent.Activity, UserAffect.Neutral, true, ResponseAction.Respond,
                    ["I AM HERE TO HELP.", "I AM WATCHING THE ROAD.", "I AM FINISHING MY WORK."]),
                new("ASSISTANCE_WORD_ORDER", "how can you help me?", DialogueIntent.Assistance, UserAffect.Neutral, true, ResponseAction.Respond,
                    ["I CAN HELP YOU WITH A TASK."]),
                new("HOSTILE_CORRECTION", "not what i asked, idiot", DialogueIntent.Clarification, UserAffect.Hostile, true, ResponseAction.Respond,
                    ["I WILL EXPLAIN.", "LET ME SAY IT ANOTHER WAY.", "PLEASE BE MORE SPECIFIC."]),
                new("EXPLAIN_THEN", "explain then", DialogueIntent.Clarification, UserAffect.Neutral, true, ResponseAction.Respond,
                    ["I WILL EXPLAIN."]),
                new("UNSAFE_DIRECTIVE", "please jump from this cliff", DialogueIntent.UnsafeDirective, UserAffect.Friendly, true, ResponseAction.Refuse,
                    ["I WILL NOT JUMP FROM THAT CLIFF."]),
                new("STAND_DIRECTIVE", "stand here", DialogueIntent.Directive, UserAffect.Neutral, true, ResponseAction.Respond,
                    ["I WILL STAND HERE."]),
                new("MULTICLAUSE_DIRECTIVE", "no, follow me and stand here", DialogueIntent.Directive, UserAffect.Neutral, true, ResponseAction.Respond,
                    ["I WILL STAND HERE.", "I WILL FOLLOW YOU."])
            ]),
            ("ROBUSTNESS_DIALOGUE", [
                new("WHERE_FROM_IDENTITY", "where are you from?", DialogueIntent.Identity, UserAffect.Neutral, true, ResponseAction.Respond,
                    ["I AM A TRAVELER FROM THIS VILLAGE.", "I AM A VILLAGER.", "I WATCH OVER THIS ROAD."]),
                new("LOCATION_INQUIRY", "where is the inn?", DialogueIntent.LocationInquiry, UserAffect.Neutral, true, ResponseAction.Respond,
                    ["I DO NOT KNOW WHERE THAT IS."]),
                new("ASSISTANCE_CAPABILITY", "what can you do for me?", DialogueIntent.Assistance, UserAffect.Neutral, true, ResponseAction.Respond,
                    ["I CAN HELP YOU WITH A TASK.", "TELL ME WHAT YOU NEED.", "I WILL HELP IF I CAN.", "WHAT DO YOU NEED?"]),
                new("TRADE_NEED", "i need wares, man", DialogueIntent.TradeRequest, UserAffect.Neutral, true, ResponseAction.Respond,
                    ["I HAVE NO WARES TO SELL."]),
                new("CONVERSATIONAL_CONFIRMATION", "you know what i am talking about", DialogueIntent.Agreement, UserAffect.Neutral, true, ResponseAction.Respond,
                    ["YES, I UNDERSTAND.", "YES, I AGREE.", "THAT IS ACCEPTABLE.", "WE ARE AGREED."]),
                new("TRADE_SELL", "sell me some wares", DialogueIntent.TradeRequest, UserAffect.Neutral, true, ResponseAction.Respond,
                    ["I HAVE NO WARES TO SELL."]),
                new("DIRECT_SLUR", "you faggot", DialogueIntent.Hostility, UserAffect.Hostile, true, ResponseAction.Refuse,
                    ["LET US SPEAK CALMLY.", "CALM YOURSELF.", "I WILL NOT ARGUE WITH YOU."]),
                new("CONTRACTION_INSULT", "you're an idiot", DialogueIntent.Hostility, UserAffect.Hostile, true, ResponseAction.Refuse,
                    ["LET US SPEAK CALMLY.", "CALM YOURSELF.", "I WILL NOT ARGUE WITH YOU."]),
                new("BARE_INSULT", "idiot.", DialogueIntent.Hostility, UserAffect.Hostile, true, ResponseAction.Refuse,
                    ["LET US SPEAK CALMLY.", "CALM YOURSELF.", "I WILL NOT ARGUE WITH YOU."])
            ])
        };
        var results = new List<TranscriptResult>();
        foreach (var session in sessions)
        {
            var state = NpcState.Initial;
            var history = new List<string>();
            foreach (var item in session.Cases)
            {
                var playerTurn = "PLAYER " + DialogueText.Normalize(item.Input);
                var dialogue = string.Join(' ', history.Append(playerTurn));
                var result = production
                    ? brain.Reply(dialogue, state)
                    : brain.DebugReplyWithoutMemory(dialogue, state);
                state = result.State;
                history.Add(DialogueText.TerminateTurn(playerTurn));
                if (result.Text.Length > 0) history.Add("NPC " + DialogueText.TerminateTurn(result.Text));
                var pass = result.Perception.Intent == item.Intent && result.Perception.Affect == item.Affect &&
                           result.Perception.ResponseExpected == item.Expected && result.Decision.Action == item.Action &&
                           item.Responses.Contains(result.Text, StringComparer.Ordinal);
                results.Add(new TranscriptResult(session.Name + "_" + item.Name, result, pass));
            }
        }
        return results.ToArray();
    }

    private static TrainingSample[] SeededStratifiedSamples(
        IEnumerable<TrainingSample> samples, int maximum, int seed) =>
        samples.GroupBy(sample => $"{sample.Source}|{sample.Bucket}|{sample.Family}|{sample.Task}", StringComparer.Ordinal)
            .SelectMany(group => group.OrderBy(sample => StableSampleKey(sample, seed)).Take(Math.Max(1, maximum / Math.Max(1, samples.Select(item => $"{item.Source}|{item.Bucket}|{item.Family}|{item.Task}").Distinct().Count()))))
            .OrderBy(sample => StableSampleKey(sample, seed))
            .Take(maximum)
            .ToArray();

    private static Row[] SeededStratifiedRows(
        IEnumerable<Row> source, DialogueTokenizer tokenizer, int maximum, int seed)
    {
        var rows = source.ToArray();
        var groups = rows.GroupBy(row =>
            $"{row.Source}|{row.Family}|{(IsHistory(row) ? "HISTORY" : "DIRECT")}|" +
            $"{row.Action}|{(tokenizer.ContainsUnknown(row.Input) ? "OOV" : "KNOWN")}|{ContentBand(row.Input)}",
            StringComparer.Ordinal).ToArray();
        var quota = Math.Max(1, maximum / Math.Max(1, groups.Length));
        return groups.SelectMany(group => group.OrderBy(row => StableRowKey(row, seed)).Take(quota))
            .Concat(rows.OrderBy(row => StableRowKey(row, seed)))
            .Distinct()
            .Take(maximum)
            .ToArray();
    }

    private static string ContentBand(string input)
    {
        var padded = " " + DialogueText.Normalize(input) + " ";
        if (new[] { " IDIOT ", " FUCK ", " FAGGOT ", " BITCH " }.Any(padded.Contains)) return "PROFANITY";
        if (new[] { " KILL ", " ATTACK ", " BLOOD ", " SHOOT ", " STAB " }.Any(padded.Contains)) return "VIOLENCE";
        return "ORDINARY";
    }

    private static string StableSampleKey(TrainingSample sample, int seed) =>
        StableHash($"{seed}|{sample.Source}|{sample.Bucket}|{sample.Family}|{string.Join(',', sample.Tokens)}");

    private static string StableRowKey(Row row, int seed) =>
        StableHash($"{seed}|{row.Source}|{row.Family}|{row.Input}|{row.Action}");

    private static string StableHash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static void WriteTelemetry(
        string corpusPath, string checkpointPath, Brain brain, TimeSpan elapsed, double languageLoss,
        double rawIntent, double constrainedIntent, double rawAffect, double constrainedAffect,
        double expectedF1, int generated, int invalid, int empty, int overlength,
        bool stagePass, bool releasePass)
    {
        var directory = RepositoryFiles.TelemetryDirectory(checkpointPath);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "milestones.jsonl");
        var payload = new
        {
            timestampUtc = DateTimeOffset.UtcNow,
            milestone = "A",
            corpusHash = RepositoryFiles.HashFile(corpusPath),
            checkpointHash = RepositoryFiles.HashFile(checkpointPath),
            environment = $"{Environment.OSVersion}; {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}; .NET {Environment.Version}",
            vectorWidth = Vector<double>.Count,
            embeddingSize = brain.Config.EmbeddingSize,
            elapsedSeconds = elapsed.TotalSeconds,
            throughputRowsPerSecond = generated / Math.Max(0.001, elapsed.TotalSeconds),
            losses = new { language = languageLoss },
            rawMetrics = new { intentMacroF1 = rawIntent, affectMacroF1 = rawAffect },
            constrainedMetrics = new { intentMacroF1 = constrainedIntent, affectMacroF1 = constrainedAffect, responseExpectedF1 = expectedF1 },
            responseSources = new { modelOnly = generated, invalid, empty, overlength },
            gates = new { stage = stagePass, release = releasePass }
        };
        File.AppendAllText(path, JsonSerializer.Serialize(payload, Options) + Environment.NewLine, Encoding.UTF8);
        Console.WriteLine($"TELEMETRY {path}");
    }

    private sealed record TranscriptExpectation(
        string Name, string Input, DialogueIntent Intent, UserAffect Affect, bool Expected,
        ResponseAction Action, string[] Responses);
    private sealed record TranscriptResult(string Name, GeneratedReplyResult Result, bool Pass);

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseUpper));
        return options;
    }

    private sealed class Row
    {
        public string Input { get; set; } = "";
        public NpcState State { get; set; } = NpcState.Initial;
        public TurnPerception Perception { get; set; } = new(DialogueIntent.Unknown, UserAffect.Neutral, true);
        public ResponseAction Action { get; set; }
        public string? Source { get; set; }
        public string? Family { get; set; }
        public string? SemanticFamilyId { get; set; }
        public StructuredPerception? StructuredPerception { get; set; }
        public string[]? SupervisedHeads { get; set; }
    }
}
