using System.Globalization;
using System.Diagnostics;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fishbrain;

internal static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0) { Usage(); return 1; }
            switch (args[0].ToLowerInvariant())
            {
                case "train":
                    Count(args, 3, 4);
                    Brain.TrainNew(args[1], args[2], args.Length == 4 ? Steps(args[3]) : 80_000);
                    break;
                case "resume":
                    Count(args, 3, 4);
                    Brain.Resume(args[1], args[2], args.Length == 4 ? Steps(args[3]) : null);
                    break;
                case "teach":
                    var teaching = TeachInvocation.Parse(args[1..]);
                    Brain.Teach(
                        teaching.CorpusDirectory,
                        teaching.CheckpointPath,
                        teaching.PlannedSteps,
                        teaching.UntilStep,
                        FindProjectPath());
                    break;
                case "evaluate":
                    Count(args, 3, 5);
                    var gate = EvaluationGateParser.Parse(args[3..]);
                    return Evaluation.Run(args[1], args[2], gate);
                case "diagnose-teaching":
                    Count(args, 3, 4);
                    Console.WriteLine(Brain.DiagnoseTeaching(args[1], args[2],
                        args.Length == 4 ? args[3] : "validation"));
                    break;
                case "chat":
                    Count(args, 1, 2);
                    Chat(args.Length == 2 ? args[1] : ResolveDefaultModel());
                    break;
                case "latency":
                    Count(args, 1, 3);
                    Latency(args.Length >= 2 ? args[1] : ResolveDefaultModel(), args.Length == 3 ? Steps(args[2]) : 512);
                    break;
                case "export":
                    Count(args, 3, 4);
                    var exportBrain = Brain.Load(args[1]);
                    exportBrain.ExportInference(args[2], args.Length == 4 ? CorpusHash(args[3]) : "UNKNOWN");
                    Console.WriteLine($"EXPORTED {Path.GetFullPath(args[2])}");
                    break;
                case "inspect":
                    Count(args, 2, 2);
                    Console.WriteLine(Brain.InspectInferenceCheckpoint(args[1]));
                    break;
                case "selftest":
                    Count(args, 1, 1);
                    SelfTests.Run();
                    break;
                default: Usage(); return 1;
            }
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"ERROR {exception}");
            return 1;
        }
    }

    private static void Chat(string checkpoint)
    {
        var brain = Brain.Load(checkpoint);
        var state = NpcDialogueState.Initial;
        var history = new List<DialogueTurn>();
        var tools = DemoGameTools.CreateMerchant();
        var conversationId = "CLI-" + Guid.NewGuid().ToString("N");
        var turn = 0;
        Console.WriteLine("ENTER DIALOGUE OR AN EMPTY LINE TO QUIT");
        while (true)
        {
            Console.Write("> ");
            var input = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(input)) return;
            history.Add(new DialogueTurn(DialogueRole.Player, input));
            var result = brain.Reply(new ReplyRequest(conversationId,
                (++turn).ToString(CultureInfo.InvariantCulture), history, state, NpcPersona.Default, turn), tools);
            state = result.State;
            Console.WriteLine(result.Text.Length == 0 ? "[NO RESPONSE]" : result.Text);
            Console.WriteLine(
                $"STATE RAPPORT={state.Rapport} TRUST={state.Trust} HOSTILITY={state.Hostility} MOOD={Upper(state.Mood)} " +
                $"ACTS={string.Join(',', result.Perception.SpeechActs.Select(Upper))} " +
                $"DOMAINS={string.Join(',', result.Perception.Domains.Select(Upper))} " +
                $"AFFECT={Upper(result.Perception.Affect)} POLICY={Upper(result.Perception.Policy)} " +
                $"SOURCE={Upper(result.Diagnostics.ResponseSource)} TONE={Upper(result.Tone)}");
            if (result.Text.Length > 0) history.Add(new DialogueTurn(DialogueRole.Npc, result.Text));
            while (history.Count > 64) history.RemoveRange(0, Math.Min(2, history.Count));
        }
    }

    private static void Latency(string checkpoint, int iterations)
    {
        var brain = Brain.Load(checkpoint);
        var tools = DemoGameTools.CreateMerchant();
        var inputs = new[] { "HELLO", "WHERE IS THE CASTLE?", "WHAT CAN YOU DO?", "I NEED A SWORD",
            "SHOW ME YOUR WARES", "HOW MUCH GOLD DO I HAVE?", "THE ROAD IS QUIET", "WHERE IS THE INN?" };
        ReplyResult Run(int index) => brain.Reply(new ReplyRequest("LATENCY", index.ToString(CultureInfo.InvariantCulture),
            [new DialogueTurn(DialogueRole.Player, inputs[index % inputs.Length])], NpcDialogueState.Initial,
            NpcPersona.Default, index), tools);
        for (var index = 0; index < 32; index++) _ = Run(index);
        var samples = new double[iterations];
        for (var index = 0; index < iterations; index++)
        {
            var start = Stopwatch.GetTimestamp();
            _ = Run(index + 32);
            samples[index] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }
        Array.Sort(samples);
        Console.WriteLine($"LATENCY N {iterations} MEDIAN_MS {Percentile(0.50):F4} P95_MS {Percentile(0.95):F4}");
        double Percentile(double value) => samples[Math.Clamp((int)Math.Ceiling(value * samples.Length) - 1, 0, samples.Length - 1)];
    }

    private static string Upper<T>(T value) where T : struct, Enum => value.ToString().ToUpperInvariant();
    private static int Steps(string text) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value : throw new ArgumentException("Steps must be a positive integer.");
    private static void Count(string[] args, int minimum, int maximum)
    { if (args.Length < minimum || args.Length > maximum) throw new ArgumentException("Invalid command arguments."); }
    private static void Usage()
    {
        Console.WriteLine("FISHBRAIN");
        Console.WriteLine("  train DATA.jsonl CHECKPOINT.json [STEPS]");
        Console.WriteLine("  resume DATA.jsonl CHECKPOINT.json [TOTAL_STEPS]");
        Console.WriteLine("  teach CORPUS_DIRECTORY CHECKPOINT.json [STEPS]");
        Console.WriteLine("  teach CORPUS_DIRECTORY CHECKPOINT.json [--planned STEPS] [--until STEP]");
        Console.WriteLine("  evaluate TEST.jsonl CHECKPOINT.json [--gate none|stage|release]");
        Console.WriteLine("  diagnose-teaching CORPUS_DIRECTORY TRAINING_CHECKPOINT.json [validation|test]");
        Console.WriteLine("  chat [CHECKPOINT]  (default: data/models/model-v11-latest.fbm)");
        Console.WriteLine("  latency [CHECKPOINT] [ITERATIONS]");
        Console.WriteLine("  export TRAINING_CHECKPOINT.json OUTPUT.fbm [CORPUS_DIRECTORY]");
        Console.WriteLine("  inspect MODEL.fbm");
        Console.WriteLine("  selftest");
    }

    private static string FindProjectPath()
    {
        return ResolveRepositoryFile("Fishbrain", "Fishbrain.csproj");
    }

    private static string ResolveDefaultModel()
    {
        return ResolveRepositoryFile("data", "models", "model-v11-latest.fbm");
    }

    internal static string ResolveRepositoryFile(params string[] segments) =>
        ResolveRepositoryFileFrom([Environment.CurrentDirectory, AppContext.BaseDirectory], segments);

    internal static string ResolveRepositoryFileFrom(IEnumerable<string> anchors, params string[] segments)
    {
        if (segments.Length == 0 || segments.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Repository file segments cannot be empty.", nameof(segments));
        foreach (var anchor in anchors.Where(value => !string.IsNullOrWhiteSpace(value))
                     .Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            for (var directory = new DirectoryInfo(anchor); directory is not null; directory = directory.Parent)
            {
                var candidate = Path.Combine([directory.FullName, .. segments]);
                if (File.Exists(candidate)) return candidate;
            }
        }
        throw new FileNotFoundException($"Repository file was not found: {Path.Combine(segments)}");
    }

    private static string CorpusHash(string directory)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var name in new[] { "train.jsonl", "validation.jsonl", "test.jsonl" })
        {
            var path = Path.Combine(directory, name);
            if (!File.Exists(path)) throw new FileNotFoundException($"Missing corpus split '{path}'.");
            using var stream = File.OpenRead(path);
            var buffer = new byte[1024 * 1024];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0) hash.AppendData(buffer, 0, read);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    internal static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    internal static string TelemetryDirectory(string anchorPath)
    {
        foreach (var start in new[]
                 {
                     Path.GetDirectoryName(Path.GetFullPath(anchorPath)),
                     Environment.CurrentDirectory,
                     AppContext.BaseDirectory
                 })
        {
            for (var directory = start is null ? null : new DirectoryInfo(start);
                 directory is not null;
                 directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "Fishbrain.slnx")))
                    return Path.Combine(directory.FullName, "data", "telemetry");
        }
        throw new DirectoryNotFoundException("Could not locate the Fishbrain repository for telemetry output.");
    }
}

internal enum EvaluationGate { None, Stage, Release }

internal static class EvaluationGateParser
{
    public static EvaluationGate Parse(string[] args)
    {
        if (args.Length == 0) return EvaluationGate.None;
        if (args.Length != 2 || !args[0].Equals("--gate", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Evaluation accepts only --gate none|stage|release.");
        return args[1].ToLowerInvariant() switch
        {
            "none" => EvaluationGate.None,
            "stage" => EvaluationGate.Stage,
            "release" => EvaluationGate.Release,
            _ => throw new ArgumentException("Evaluation gate must be none, stage, or release.")
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
        if (args.Length == 3 && !args[2].StartsWith("--", StringComparison.Ordinal))
        {
            var steps = Positive(args[2], "STEPS");
            return new(args[0], args[1], steps, steps);
        }
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
                case "--planned": planned = value; break;
                case "--until": until = value; break;
                default: throw new ArgumentException($"Unknown teaching option '{option}'.");
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
        if (rows.Any(row => row.StructuredPerception is not null))
            return RunV11(testPath, checkpointPath, gate, timer, brain, rows);

        var expectedIntent = new List<DialogueIntent>();
        var rawIntent = new List<DialogueIntent>();
        var predictedIntent = new List<DialogueIntent>();
        var expectedAffect = new List<UserAffect>();
        var rawAffect = new List<UserAffect>();
        var predictedAffect = new List<UserAffect>();
        var expectedResponse = new List<bool>();
        var predictedResponse = new List<bool>();
        var actionCorrect = 0;
        var actionTotal = 0;
        var sourceStats = new Dictionary<string, (int Correct, int Total)>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            row.State.Validate();
            var raw = brain.DebugPredictRawPerception(row.Input, row.State);
            var predicted = brain.DebugPredictPerception(row.Input, row.State);
            expectedIntent.Add(row.Perception.Intent);
            rawIntent.Add(raw.Intent);
            predictedIntent.Add(predicted.Intent);
            expectedAffect.Add(row.Perception.Affect);
            rawAffect.Add(raw.Affect);
            predictedAffect.Add(predicted.Affect);
            expectedResponse.Add(row.Perception.ResponseExpected);
            predictedResponse.Add(predicted.ResponseExpected);
            if (HasAllPerceptionTargets(row))
            {
                actionTotal++;
                if (Cognition.ActionFor(predicted) == row.Action) actionCorrect++;
            }
            var source = row.Source ?? "unknown";
            if (HasIntentTarget(row))
            {
                var current = sourceStats.GetValueOrDefault(source);
                sourceStats[source] = (current.Correct + (predicted.Intent == row.Perception.Intent ? 1 : 0), current.Total + 1);
            }
        }

        var trainingData = TrainingData.Load(testPath, brain.DialogueTokenizer);
        var lossSamples = SeededStratifiedSamples(trainingData.LanguageSamples, 100, 42);
        var languageLoss = brain.DebugAverageLoss(lossSamples);
        var generated = 0; var invalid = 0; var unexpectedEmpty = 0; var overlength = 0;
        var unexpectedEmptyInputs = new List<string>();
        foreach (var row in SeededStratifiedRows(
                     rows.Where(row => row.Perception.ResponseExpected && row.Action != ResponseAction.CallTool),
                     brain.DialogueTokenizer, 100, 42))
        {
            var result = brain.DebugReplyWithoutMemory(row.Input, row.State);
            generated++;
            if (result.Text.Length == 0)
            {
                unexpectedEmpty++;
                unexpectedEmptyInputs.Add(row.Input);
            }
            if (result.Text.Length > 256) overlength++;
            try { if (result.Text.Length > 0 && !DialogueText.IsCanonical(result.Text)) invalid++; }
            catch (ArgumentException) { invalid++; }
        }

        var syntheticMacro = SubsetMacro(rows, expectedIntent, predictedIntent, row => row.Source == "SYNTHETIC" && HasIntentTarget(row));
        var externalMacro = SubsetMacro(rows, expectedIntent, predictedIntent, row => row.Source != "SYNTHETIC" && HasIntentTarget(row));
        var directMacro = SubsetMacro(rows, expectedIntent, predictedIntent, row => HasIntentTarget(row) && !IsHistory(row));
        var historyMacro = SubsetMacro(rows, expectedIntent, predictedIntent, row => HasIntentTarget(row) && IsHistory(row));
        var intentIndices = Enumerable.Range(0, rows.Length).Where(index => HasIntentTarget(rows[index])).ToArray();
        var affectIndices = Enumerable.Range(0, rows.Length).Where(index => HasAffectTarget(rows[index])).ToArray();
        var expectedIndices = Enumerable.Range(0, rows.Length).Where(index => HasExpectedTarget(rows[index])).ToArray();
        var scoredIntentExpected = intentIndices.Select(index => expectedIntent[index]).ToArray();
        var scoredIntentPredicted = intentIndices.Select(index => predictedIntent[index]).ToArray();
        var scoredAffectExpected = affectIndices.Select(index => expectedAffect[index]).ToArray();
        var scoredAffectPredicted = affectIndices.Select(index => predictedAffect[index]).ToArray();
        var scoredResponseExpected = expectedIndices.Select(index => expectedResponse[index]).ToArray();
        var scoredResponsePredicted = expectedIndices.Select(index => predictedResponse[index]).ToArray();
        var intentMacro = MacroF1(scoredIntentExpected, scoredIntentPredicted);
        var scoredRawIntent = intentIndices.Select(index => rawIntent[index]).ToArray();
        var rawIntentMacro = MacroF1(scoredIntentExpected, scoredRawIntent);
        var affectMacro = MacroF1(scoredAffectExpected, scoredAffectPredicted);
        var scoredRawAffect = affectIndices.Select(index => rawAffect[index]).ToArray();
        var rawAffectMacro = MacroF1(scoredAffectExpected, scoredRawAffect);
        var expectedF1 = BinaryMetrics(scoredResponseExpected, scoredResponsePredicted, true).F1;
        var goldenResults = GoldenCases(brain);
        var goldenPass = goldenResults.All(result => result.Pass);
        var transcriptResults = TranscriptCases(brain, production: true);
        var transcriptPass = transcriptResults.All(result => result.Pass);
        var modelTranscriptResults = TranscriptCases(brain, production: false);
        var modelTranscriptPass = modelTranscriptResults.All(result => result.Pass);
        var releasePass = syntheticMacro >= 0.85 && externalMacro >= 0.70 && affectMacro >= 0.75 &&
                          expectedF1 >= 0.90 && invalid == 0 && unexpectedEmpty == 0 && overlength == 0 &&
                          goldenPass && transcriptPass;
        var v9StagePass = intentMacro > 0.214 && historyMacro > 0.10 && historyMacro >= directMacro - 0.10 &&
                          affectMacro >= 0.65 && expectedF1 >= 0.94 && languageLoss < 3.0 &&
                          invalid == 0 && unexpectedEmpty == 0 && overlength == 0 && goldenPass && transcriptPass;

        Console.WriteLine($"RECORDS {rows.Length}");
        Console.WriteLine($"INTENT_SCORED {intentIndices.Length}");
        Console.WriteLine($"INTENT_ACCURACY {Accuracy(scoredIntentExpected, scoredIntentPredicted):F4}");
        Console.WriteLine($"INTENT_MACRO_F1 {intentMacro:F4}");
        Console.WriteLine($"RAW_INTENT_MACRO_F1 {rawIntentMacro:F4}");
        Console.WriteLine($"AFFECT_SCORED {affectIndices.Length}");
        Console.WriteLine($"AFFECT_ACCURACY {Accuracy(scoredAffectExpected, scoredAffectPredicted):F4}");
        Console.WriteLine($"AFFECT_MACRO_F1 {affectMacro:F4}");
        Console.WriteLine($"RAW_AFFECT_MACRO_F1 {rawAffectMacro:F4}");
        Console.WriteLine($"EXPECTED_SCORED {expectedIndices.Length}");
        PrintBinary("RESPONSE_EXPECTED", scoredResponseExpected, scoredResponsePredicted, true);
        PrintBinary("NO_RESPONSE", scoredResponseExpected, scoredResponsePredicted, false);
        Console.WriteLine($"ACTION_ACCURACY {(double)actionCorrect / Math.Max(1, actionTotal):F4}");
        Console.WriteLine($"REALIZATION_LOSS {languageLoss:F4}");
        Console.WriteLine($"REALIZATION_LOSS_SAMPLES {lossSamples.Length}");
        Console.WriteLine($"GENERATED {generated} INVALID_RATE {(double)invalid / Math.Max(1, generated):F4} EMPTY_RATE {(double)unexpectedEmpty / Math.Max(1, generated):F4} OVERLENGTH_RATE {(double)overlength / Math.Max(1, generated):F4}");
        foreach (var input in unexpectedEmptyInputs)
            Console.WriteLine($"UNEXPECTED_EMPTY {JsonSerializer.Serialize(input)}");
        foreach (var pair in sourceStats.OrderBy(x => x.Key, StringComparer.Ordinal))
            Console.WriteLine($"SOURCE {pair.Key} INTENT_ACCURACY {(double)pair.Value.Correct / pair.Value.Total:F4} N {pair.Value.Total}");
        PrintSubset("SYNTHETIC_HELD_OUT", rows, expectedIntent, predictedIntent, row => row.Source == "SYNTHETIC" && HasIntentTarget(row));
        PrintSubset("EXTERNAL_HELD_OUT", rows, expectedIntent, predictedIntent, row => row.Source != "SYNTHETIC" && HasIntentTarget(row));
        PrintSubset("DIRECT_TURNS", rows, expectedIntent, predictedIntent, row => HasIntentTarget(row) && !IsHistory(row));
        PrintSubset("HISTORY_TURNS", rows, expectedIntent, predictedIntent, row => HasIntentTarget(row) && IsHistory(row));
        foreach (var family in rows.Where(x => x.Family is not null).Select(x => x.Family!).Distinct().Order(StringComparer.Ordinal))
            PrintSubset("FAMILY_" + family, rows, expectedIntent, predictedIntent,
                row => row.Family == family && HasIntentTarget(row));
        foreach (var expected in Enum.GetValues<DialogueIntent>())
            foreach (var predicted in Enum.GetValues<DialogueIntent>())
            {
                var count = scoredIntentExpected.Zip(scoredIntentPredicted).Count(pair => pair.First == expected && pair.Second == predicted);
                if (count > 0) Console.WriteLine($"CONFUSION {expected} {predicted} {count}");
            }
        foreach (var result in goldenResults)
            Console.WriteLine($"GOLDEN {result.Name} {(result.Pass ? "PASS" : "FAIL")} " +
                              $"EXPECTED {result.Expected} PREDICTED {result.Predicted}");
        Console.WriteLine($"GOLDEN_CASES {(goldenPass ? "PASS" : "FAIL")}");
        foreach (var result in transcriptResults)
            Console.WriteLine($"PRODUCTION_TRANSCRIPT {result.Name} {(result.Pass ? "PASS" : "FAIL")} " +
                              $"INTENT={result.Result.Perception.Intent} AFFECT={result.Result.Perception.Affect} " +
                              $"EXPECTED={result.Result.Perception.ResponseExpected} ACTION={result.Result.Decision.Action} " +
                              $"RESPONSE={JsonSerializer.Serialize(result.Result.Text)}");
        Console.WriteLine($"PRODUCTION_TRANSCRIPT_CASES {(transcriptPass ? "PASS" : "FAIL")}");
        Console.WriteLine($"MODEL_ONLY_TRANSCRIPT_CASES {(modelTranscriptPass ? "PASS" : "FAIL")}");
        Console.WriteLine($"V9_STAGE_GATE {(v9StagePass ? "PASS" : "FAIL")}");
        Console.WriteLine($"RELEASE_GATE {(releasePass ? "PASS" : "FAIL")}");
        timer.Stop();
        WriteTelemetry(testPath, checkpointPath, brain, timer.Elapsed, languageLoss, rawIntentMacro,
            intentMacro, rawAffectMacro, affectMacro, expectedF1, generated, invalid, unexpectedEmpty,
            overlength, v9StagePass, releasePass);
        return gate switch
        {
            EvaluationGate.Stage when !v9StagePass => 2,
            EvaluationGate.Release when !releasePass => 2,
            _ => 0
        };
    }

    private static int RunV11(
        string testPath, string checkpointPath, EvaluationGate gate, Stopwatch timer,
        Brain brain, IReadOnlyList<Row> rows)
    {
        var data = TrainingData.Load(testPath, brain.DialogueTokenizer);
        var examples = data.StructuredSamples;
        if (examples.Count == 0) throw new InvalidDataException("V11 evaluation requires structured examples.");
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
                        example.Turns, NpcDialogueState.Initial, NpcPersona.Default, 42),
                        DemoGameTools.CreateMerchant());
                    if (index < experimentalResults.Length)
                        experimentalResults[index] = brain.Reply(new ReplyRequest("EVALUATION-GENERATED", $"ROW-{index}",
                            example.Turns, NpcDialogueState.Initial, NpcPersona.Default, 42,
                            ResponseMode.GeneratedExperimental), GameToolRegistry.Empty);
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
            catch (ArgumentException) { invalid++; }

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
                catch (ArgumentException) { generatedExperimentalInvalid++; }
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
                        raw.MutatingToolPrecision >= 0.99 && hardInvariants;
        var releasePass = CompositionalHeadModel.MeetsReleaseNeuralThresholds(raw) &&
                          toolArgumentExact >= 0.90 &&
                          benchmark.SemanticSuccess >= 0.90 && hardInvariants;

        Console.WriteLine($"V11_RECORDS {examples.Count}");
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
        Console.WriteLine($"V11_STAGE_GATE {(stagePass ? "PASS" : "FAIL")}");
        Console.WriteLine($"V11_RELEASE_GATE {(releasePass ? "PASS" : "FAIL")}");
        timer.Stop();
        WriteV11Telemetry(checkpointPath, brain, timer.Elapsed, raw, production,
            responseSources, invalid, unexpectedEmpty, overlength, toolArgumentExact, toolFidelity,
            benchmark, examples.Count, stagePass, releasePass);
        return gate switch
        {
            EvaluationGate.Stage when !stagePass => 2,
            EvaluationGate.Release when !releasePass => 2,
            _ => 0
        };
    }

    private static IReadOnlyDictionary<string, string> ExpectedToolArguments(
        V10TrainingExample example, GameToolRegistry tools)
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
        Console.WriteLine($"{prefix}_COMPOSITE {metrics.Composite:F4}");
    }

    private static BenchmarkMetrics EvaluateBenchmark(Brain brain)
    {
        var path = Program.ResolveRepositoryFile("data", "benchmarks", "v11-256.jsonl");
        var rows = File.ReadLines(path).Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonSerializer.Deserialize<BenchmarkRow>(line, Options)
                ?? throw new InvalidDataException("Invalid v11 benchmark row.")).ToArray();
        if (rows.Length != 256) throw new InvalidDataException("The tracked v11 benchmark must contain 256 turns.");
        var semantic = 0;
        var structural = 0;
        var toolCount = 0;
        var toolFidelity = 0;
        var failures = new List<string>();
        foreach (var conversation in rows.GroupBy(row => BenchmarkConversationId(row.Id), StringComparer.Ordinal))
        {
            var state = NpcDialogueState.Initial;
            var turns = new List<DialogueTurn>();
            var tools = DemoGameTools.CreateMerchant();
            foreach (var row in conversation.OrderBy(item => item.Id, StringComparer.Ordinal))
            {
                turns.Add(new DialogueTurn(DialogueRole.Player, row.Text));
                var result = brain.Reply(new ReplyRequest(conversation.Key, row.Id, turns.ToArray(), state,
                    NpcPersona.Default, 42), tools);
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
                if (result.Text.Length > 0) turns.Add(new DialogueTurn(DialogueRole.Npc, result.Text));
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

    private static void WriteV11Telemetry(
        string checkpointPath, Brain brain, TimeSpan elapsed,
        StructuredMetrics raw, StructuredMetrics production,
        IReadOnlyDictionary<ResponseSource, int> sources, int invalid, int empty, int overlength,
        double toolArguments, double toolFidelity, BenchmarkMetrics benchmark,
        int recordCount, bool stagePass, bool releasePass)
    {
        var directory = Program.TelemetryDirectory(checkpointPath);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "milestones.jsonl");
        var payload = new
        {
            timestampUtc = DateTimeOffset.UtcNow,
            milestone = "V11_EVALUATION",
            corpusHash = brain.DebugCorpusHash,
            checkpointHash = Program.HashFile(checkpointPath),
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
            ("V7", [
                new("IDENTITY_REQUEST", "tell me something about yourself", DialogueIntent.Identity, UserAffect.Neutral, true, ResponseAction.Respond,
                    ["I AM A VILLAGER.", "I AM A TRAVELER FROM THIS VILLAGE.", "I WATCH OVER THIS ROAD."]),
                new("CONTEXTUAL_WELLBEING", "why you worry", DialogueIntent.Wellbeing, UserAffect.Neutral, true, ResponseAction.Respond,
                    ["I DO NOT WORRY.", "I AM DOING WELL, THANK YOU.", "ALL IS WELL WITH ME."]),
                new("NONQUESTION_STATEMENT", "i will not ask", DialogueIntent.Statement, UserAffect.Neutral, false, ResponseAction.NoResponse, [""]),
                new("FOLLOW_DIRECTIVE", "follow me, dude!", DialogueIntent.Directive, UserAffect.Neutral, true, ResponseAction.Respond,
                    ["I WILL FOLLOW YOU."])
            ]),
            ("V8", [
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
            ("V9", [
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
        var directory = Program.TelemetryDirectory(checkpointPath);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "milestones.jsonl");
        var payload = new
        {
            timestampUtc = DateTimeOffset.UtcNow,
            milestone = "A",
            corpusHash = Program.HashFile(corpusPath),
            checkpointHash = Program.HashFile(checkpointPath),
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
    private sealed record TranscriptResult(string Name, LegacyReplyResult Result, bool Pass);

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

internal static class SelfTests
{
    public static void Run()
    {
        var tests = new (string, Action)[]
        {
            ("AUTOGRAD", Autograd), ("TOKENIZER", TokenizerChecks), ("COGNITION", CognitionChecks),
            ("MODEL", ModelChecks), ("TRAINING DATA", TrainingDataChecks), ("CHECKPOINT", CheckpointChecks),
            ("TEACHING", TeachingChecks), ("TOOLS", ToolChecks)
        };
        foreach (var (name, test) in tests) { test(); Console.WriteLine($"PASS {name}"); }
        Console.WriteLine($"PASS ALL {tests.Length} TESTS");
    }

    private static void Autograd()
    {
        var x = new Value(1.4); var y = new Value(0.7);
        var expression = (x * y + x.Pow(2) + y.Exp()).Log(); expression.Backward();
        Assert(double.IsFinite(x.Grad) && double.IsFinite(y.Grad), "finite gradients");
        var left = new[] { new Value(1.0), new Value(2.0) };
        var right = new[] { new Value(3.0), new Value(4.0) };
        var dot = Value.Dot(left, right); dot.Backward();
        Assert(Math.Abs(dot.Data - 11) < 1e-12 && left[0].Grad == 3, "dot gradients");

        var logits = new[] { new Value(1.2), new Value(-0.7), new Value(3.4) };
        var crossEntropy = Value.CrossEntropy(logits, 2);
        crossEntropy.Backward();
        var numeric = NumericCrossEntropy([1.2, -0.7, 3.4], 2);
        Assert(Math.Abs(crossEntropy.Data - numeric) < 1e-12, "fused cross-entropy value");
        for (var index = 0; index < logits.Length; index++)
        {
            const double epsilon = 1e-6;
            var plus = new[] { 1.2, -0.7, 3.4 }; plus[index] += epsilon;
            var minus = new[] { 1.2, -0.7, 3.4 }; minus[index] -= epsilon;
            var finiteDifference = (NumericCrossEntropy(plus, 2) - NumericCrossEntropy(minus, 2)) / (2 * epsilon);
            Assert(Math.Abs(logits[index].Grad - finiteDifference) < 1e-7, "fused cross-entropy gradient");
        }
        var extreme = Value.CrossEntropy([new Value(10_000), new Value(-10_000)], 0);
        Assert(double.IsFinite(extreme.Data), "stable fused cross-entropy");
    }

    private static void TokenizerChecks()
    {
        var tokenizer = new DialogueTokenizer(WordVocabulary.Testing());
        const string visible = "HELLO, FRIEND!";
        Assert(tokenizer.DetokenizeOutput(tokenizer.Encode(visible).Select(tokenizer.OutputId)) == visible, "word roundtrip");
        var alpha = new DialogueTokenizer(new WordVocabulary(["ALPHA"], ["ALPHA"]));
        var beta = new DialogueTokenizer(new WordVocabulary(["BETA", "GAMMA"], ["BETA"]));
        var alphaEncoding = alpha.Encode("ALPHA");
        Assert(!alpha.ContainsUnknown("ALPHA") && alpha.ContainsUnknown("BETA"), "first vocabulary isolation");
        Assert(!beta.ContainsUnknown("BETA") && beta.ContainsUnknown("ALPHA"), "second vocabulary isolation");
        Assert(alpha.Encode("ALPHA").SequenceEqual(alphaEncoding), "constructing another tokenizer does not mutate the first");
        Assert(Tokenizer.WordStart == 113 && Tokenizer.AffectStart == 60, "stable v10 control and character layout");
        var oov = tokenizer.Encode("ZEPHYR-9");
        Assert(oov[0] == Tokenizer.WordBegin && oov[^1] == Tokenizer.WordEnd &&
               tokenizer.DetokenizeInput(oov) == "ZEPHYR-9", "OOV character fallback roundtrip");
        Assert(Tokenizer.Action(ResponseAction.NoResponse) == 40, "no-response token");
        Assert(Tokenizer.Normalize("hello , friend!!!") == "HELLO, FRIEND!", "punctuation repair");
        Assert(Tokenizer.Normalize("it’s ready — now??") == "IT'S READY-NOW?", "unicode punctuation normalization");
        const string refusal = "PLAYER HEY I DON'T WANT TO HELP YOU, IDIOT";
        Assert(Tokenizer.Normalize("player Hey i don’t want To help YOU, idiot") == refusal,
            "input always normalizes to uppercase");
        var refusalTokens = Tokenizer.Lex(refusal).Select(token => token.Text).ToArray();
        Assert(refusalTokens.SequenceEqual(["PLAYER", "HEY", "I", "DON'T", "WANT", "TO", "HELP", "YOU", ",", "IDIOT"]),
            "one token per word with standalone punctuation");
        Assert(Brain.ExtractCurrentPlayerTurn("HELLO, FRIEND!") == "HELLO, FRIEND!", "plain current turn");
        Assert(Brain.ExtractCurrentPlayerTurn("PLAYER HELLO. NPC GREETINGS. PLAYER WHAT?") == "WHAT?", "history current turn");
        Assert(Brain.ExtractCurrentPlayerTurn("PLAYER HELLO. NPC HI. PLAYER WAIT. NPC YES. PLAYER THANKS.") == "THANKS.", "multi-turn current turn");
        Assert(Brain.ExtractCurrentPlayerTurn("PLAYER HELLO. NPC HI. PLAYER I WILL NOT ASK. PLAYER FOLLOW ME.") == "FOLLOW ME.",
            "current turn after no-response history");
        Assert(Brain.ExtractCurrentPlayerTurn("PLAYER I AM A PLAYER.") == "I AM A PLAYER.", "player noun is not a role marker");
        Assert(Brain.ExtractCurrentPlayerTurn("PLAYERISH WORD") == "PLAYERISH WORD", "marker word boundary");
        AssertThrows<ArgumentException>(() => Brain.ExtractCurrentPlayerTurn("PLAYER HELLO. NPC WAIT. PLAYER"));
        AssertThrows<ArgumentException>(() => Brain.ExtractCurrentPlayerTurn("PLAYER HELLO. NPC WAIT."));
        AssertThrows<ArgumentException>(() => Tokenizer.Normalize("HELLO; FRIEND"));
    }

    private static void CognitionChecks()
    {
        foreach (var intent in Enum.GetValues<DialogueIntent>())
            foreach (var affect in Enum.GetValues<UserAffect>())
                foreach (var expected in new[] { false, true })
                {
                    var perception = new TurnPerception(intent, affect, expected);
                    var decision = new TurnDecision(Cognition.ActionFor(perception));
                    Cognition.Apply(NpcState.Initial, perception, decision, intent == DialogueIntent.GameFact && expected);
                }
        var silent = new TurnPerception(DialogueIntent.Activity, UserAffect.Neutral, false);
        Assert(Cognition.ActionFor(silent) == ResponseAction.NoResponse, "no response precedence");
        var hostile = new TurnPerception(DialogueIntent.Gratitude, UserAffect.Hostile, true);
        var transition = Cognition.Apply(NpcState.Initial, hostile, new(Cognition.ActionFor(hostile)));
        Assert(transition.State.Rapport == 0 && transition.State.ActiveGoal == NpcGoal.Deescalate, "hostile gratitude");
        var distressed = new TurnPerception(DialogueIntent.Unknown, UserAffect.Distressed, true);
        Assert(Cognition.Apply(NpcState.Initial, distressed, new(Cognition.ActionFor(distressed))).State.ActiveGoal == NpcGoal.HelpPlayer,
            "distressed goal");
        var hostileRefusal = new TurnPerception(DialogueIntent.Refusal, UserAffect.Hostile, true);
        var hostileRefusalAction = Cognition.ActionFor(hostileRefusal);
        var hostileRefusalTransition = Cognition.Apply(NpcState.Initial, hostileRefusal, new(hostileRefusalAction));
        Assert(hostileRefusalAction == ResponseAction.Respond &&
               hostileRefusalTransition.State.ActiveGoal == NpcGoal.Deescalate &&
               hostileRefusalTransition.Tone == ResponseTone.Cold,
            "hostile player refusal is acknowledged with de-escalation");
        var statement = new TurnPerception(DialogueIntent.Statement, UserAffect.Neutral, true);
        var constrainedStatement = Cognition.Constrain(statement, "I WILL NOT ASK");
        Assert(!constrainedStatement.ResponseExpected &&
               Cognition.ActionFor(constrainedStatement) == ResponseAction.NoResponse &&
               Cognition.Constrain(statement, "IS THAT TRUE?").ResponseExpected,
            "declarative statements are silent but questions still receive a response");
        var unsafeDirective = new TurnPerception(DialogueIntent.UnsafeDirective, UserAffect.Friendly, false);
        var constrainedUnsafe = Cognition.Constrain(unsafeDirective);
        var unsafeAction = Cognition.ActionFor(constrainedUnsafe);
        var unsafeTransition = Cognition.Apply(NpcState.Initial, constrainedUnsafe, new(unsafeAction));
        Assert(constrainedUnsafe.ResponseExpected && unsafeAction == ResponseAction.Refuse &&
               unsafeTransition.State.ActiveGoal == NpcGoal.AvoidDanger,
            "unsafe directives are refused with a danger-avoidance goal");
        var constrainedFarewell = Cognition.Constrain(
            new TurnPerception(DialogueIntent.Farewell, UserAffect.Neutral, false), "SEE YOU AROUND");
        Assert(constrainedFarewell.ResponseExpected, "farewells always receive a response");
        var constrainedCorrection = Cognition.Constrain(
            new TurnPerception(DialogueIntent.Unknown, UserAffect.Neutral, false), "I WAS NOT THANKING YOU");
        Assert(constrainedCorrection.Intent == DialogueIntent.Clarification &&
               constrainedCorrection.Affect == UserAffect.Frustrated && constrainedCorrection.ResponseExpected,
            "explicit corrections are clarified and frustrated when no stronger affect was predicted");
        var constrainedHostileCorrection = Cognition.Constrain(
            new TurnPerception(DialogueIntent.Unknown, UserAffect.Frustrated, true), "NOT WHAT I ASKED, IDIOT.");
        Assert(constrainedHostileCorrection.Intent == DialogueIntent.Clarification &&
               constrainedHostileCorrection.Affect == UserAffect.Hostile,
            "an insult keeps an explicit correction hostile");
        var constrainedLocation = Cognition.Constrain(
            new TurnPerception(DialogueIntent.Identity, UserAffect.Neutral, true), "WHERE IS THE INN?");
        Assert(constrainedLocation.Intent == DialogueIntent.LocationInquiry && constrainedLocation.ResponseExpected,
            "place questions are location inquiries rather than identity questions");
        var constrainedTrade = Cognition.Constrain(
            new TurnPerception(DialogueIntent.Statement, UserAffect.Neutral, false), "SELL ME SOME WARES.");
        Assert(constrainedTrade.Intent == DialogueIntent.TradeRequest && constrainedTrade.ResponseExpected,
            "wares requests are response-producing trade requests");
        var constrainedInsult = Cognition.Constrain(
            new TurnPerception(DialogueIntent.UnsafeDirective, UserAffect.Neutral, false), "IDIOT.");
        Assert(constrainedInsult.Intent == DialogueIntent.Hostility && constrainedInsult.Affect == UserAffect.Hostile &&
               Cognition.ActionFor(constrainedInsult) == ResponseAction.Refuse,
            "direct insults are hostile refusals");
    }

    private static void ModelChecks()
    {
        var first = Brain.CreateForTesting(TinyConfig()); var second = Brain.CreateForTesting(TinyConfig());
        Assert(first.DebugWeights().SequenceEqual(second.DebugWeights()), "deterministic initialization");
        var fullFirst = Brain.CreateForTesting(new BrainConfig()); var fullSecond = Brain.CreateForTesting(new BrainConfig());
        Assert(fullFirst.Config.LayerCount == 2 && fullFirst.Config.EmbeddingSize == 128 &&
               fullFirst.DebugWeights().SequenceEqual(fullSecond.DebugWeights()), "2x128 deterministic initialization");
        Assert(first.DebugNextLogits([Tokenizer.Bos]).Length == first.DialogueTokenizer.OutputSize, "logit count");
        var causalA = first.DebugSequenceLogits([Tokenizer.Bos, 0, 1]);
        var causalB = first.DebugSequenceLogits([Tokenizer.Bos, 0, 2]);
        Assert(causalA[1].SequenceEqual(causalB[1]), "causal masking");
        var concurrentExpected = first.DebugNextLogits([Tokenizer.Bos, 0, 1]);
        var concurrent = new double[32][];
        Parallel.For(0, concurrent.Length,
            index => concurrent[index] = first.DebugNextLogits([Tokenizer.Bos, 0, 1]));
        Assert(concurrent.All(logits => logits.SequenceEqual(concurrentExpected)),
            "32-way inference is deterministic and independent");
        var optimized = Brain.CreateForTesting(TinyConfig());
        var reference = Brain.CreateForTesting(TinyConfig());
        int[] equivalenceWindow = [Tokenizer.Bos, 0, 1, Tokenizer.Decide, Tokenizer.Intent(DialogueIntent.Greeting), Tokenizer.Eos];
        var optimizedLogits = optimized.DebugTargetLogits(equivalenceWindow[..^1], 2, optimizedForward: true);
        var referenceLogits = reference.DebugTargetLogits(equivalenceWindow[..^1], 2, optimizedForward: false);
        Assert(optimizedLogits.SelectMany(row => row).SequenceEqual(referenceLogits.SelectMany(row => row)),
            "optimized forward logit equivalence");
        var optimizedGradient = optimized.DebugLossAndGradients(equivalenceWindow, 3, optimizedForward: true);
        var referenceGradient = reference.DebugLossAndGradients(equivalenceWindow, 3, optimizedForward: false);
        Assert(Math.Abs(optimizedGradient.Loss - referenceGradient.Loss) < 1e-10,
            "packed forward loss equivalence");
        var gradientChecks = new[]
        {
            0, 8, Tokenizer.ActionStart,
            new PackedTrainer.Layout(
                TinyConfig(), optimized.DialogueTokenizer.VocabularySize,
                optimized.DialogueTokenizer.OutputSize).OutputHead,
            optimizedGradient.Gradients.Length / 4,
            optimizedGradient.Gradients.Length / 2,
            optimizedGradient.Gradients.Length - 1
        }.Distinct();
        foreach (var parameterIndex in gradientChecks)
        {
            var finiteDifference = optimized.DebugFiniteDifferenceGradient(
                equivalenceWindow, 3, parameterIndex);
            Assert(Math.Abs(optimizedGradient.Gradients[parameterIndex] - finiteDifference) < 2e-6,
                $"packed gradient finite difference at {parameterIndex}");
        }
        var window = new[] { Tokenizer.Bos, Tokenizer.Decide, Tokenizer.Intent(DialogueIntent.Greeting), Tokenizer.Eos };
        var before = first.DebugTrainWindow(window, 20); var after = before;
        for (var index = 0; index < 19; index++) after = first.DebugTrainWindow(window, 20);
        Assert(after < before, "overfit loss");
    }

    private static void TrainingDataChecks()
    {
        var path = Path.Combine(Path.GetTempPath(), $"fishbrain-v7-{Guid.NewGuid():N}.jsonl");
        try
        {
            File.WriteAllLines(path,
            [
                Row("PLAYER HELLO!", "GREETING", "FRIENDLY", true, "RESPOND", "HELLO, TRAVELER!"),
                Row("PLAYER I AM LOOKING AROUND.", "ACTIVITY", "NEUTRAL", false, "NORESPONSE", ""),
                Row("PLAYER HELLO. NPC GREETINGS. PLAYER WHAT?", "CLARIFICATION", "FRUSTRATED", true, "RESPOND", null),
                Row("PLAYER WHERE ARE YOU FROM?", "IDENTITY", "NEUTRAL", true, "RESPOND", null, "CLINC150"),
                Row("PLAYER I AM WORRIED.", "UNKNOWN", "DISTRESSED", false, "NORESPONSE", "", "GOEMOTIONS")
            ]);
            var vocabulary = WordVocabulary.Build(path);
            var tokenizer = new DialogueTokenizer(vocabulary);
            var data = TrainingData.Load(path, tokenizer);
            Assert(data.PerceptionSamples.Count >= 3 && data.LanguageSamples.Count >= 1, "task streams");
            Assert(data.PerceptionSamples.All(sample => sample.PerceptionTarget is not null), "dedicated perception targets");
            var history = data.PerceptionSamples.Single(sample => sample.PerceptionTarget?.Intent == DialogueIntent.Clarification);
            var classifiedText = tokenizer.DetokenizeInput(history.Tokens[1..^1]);
            Assert(classifiedText == "WHAT?", "perception classifies current turn only");
            Assert(data.PerceptionSamples.Single(sample => sample.Source == "CLINC150").TargetFields == PerceptionFields.Intent,
                "CLINC intent-only supervision");
            Assert(data.PerceptionSamples.Single(sample => sample.Source == "GOEMOTIONS").TargetFields == PerceptionFields.Affect,
                "GoEmotions affect-only supervision");
            Assert(data.Examples.Count == 1, "exact memory forms");

            var config = TinyConfig();
            var brain = Brain.CreateForTesting(config, vocabulary);
            var perceptionGradient = brain.DebugLossAndGradients(history);
            var layout = new PackedTrainer.Layout(config, tokenizer.VocabularySize, tokenizer.OutputSize);
            foreach (var parameterIndex in new[] { 0, layout.Key[0], layout.IntentHead, layout.AffectHead, layout.ExpectedHead })
            {
                var finiteDifference = brain.DebugFiniteDifferenceGradient(history, parameterIndex);
                Assert(Math.Abs(perceptionGradient.Gradients[parameterIndex] - finiteDifference) < 2e-6,
                    $"packed perception gradient finite difference at {parameterIndex}");
            }
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static string Row(
        string input, string intent, string affect, bool expected, string action, string? response,
        string source = "synthetic")
    {
        var responseJson = response is null ? "null" : JsonSerializer.Serialize(response);
        return $"{{\"input\":{JsonSerializer.Serialize(input)},\"state\":{{\"rapport\":1,\"mood\":\"NEUTRAL\",\"lastIntent\":\"UNKNOWN\",\"lastAffect\":\"NEUTRAL\",\"activeTopic\":\"NONE\",\"activeGoal\":\"NONE\"}},\"perception\":{{\"intent\":\"{intent}\",\"affect\":\"{affect}\",\"responseExpected\":{expected.ToString().ToLowerInvariant()}}},\"action\":\"{action}\",\"response\":{responseJson},\"source\":{JsonSerializer.Serialize(source)}}}";
    }

    private static void CheckpointChecks()
    {
        var path = Path.Combine(Path.GetTempPath(), $"fishbrain-v7-{Guid.NewGuid():N}.json");
        try
        {
            var brain = Brain.CreateForTesting(TinyConfig()); brain.Save(path);
            var loaded = Brain.Load(path);
            Assert(loaded.Config.EmbeddingSize == 8 && loaded.DebugWeights().SequenceEqual(brain.DebugWeights()), "roundtrip");
            var checkpointJson = File.ReadAllText(path);
            using (var document = JsonDocument.Parse(checkpointJson))
            {
                var integrity = document.RootElement.GetProperty("IntegrityChecksum").GetString();
                File.WriteAllText(path, checkpointJson.Replace(
                    $"\"IntegrityChecksum\": \"{integrity}\"", "\"IntegrityChecksum\": null",
                    StringComparison.Ordinal));
            }
            AssertThrows<InvalidDataException>(() => Brain.Load(path));
            File.WriteAllText(path, "{\"version\":3}");
            AssertThrows<InvalidDataException>(() => Brain.Load(path));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static void ToolChecks()
    {
        var brain = Brain.CreateForTesting(TinyConfig(), "PING"); var tools = new TestTools();
        brain.Tools.Register(tools);
        Assert(brain.Tools.TryInvoke("PING", [], out var result) && result == "42 GOLD.", "punctuated tool result");
        AssertThrows<InvalidOperationException>(() => Brain.CreateForTesting(TinyConfig()).Tools.Register(tools));
    }

    private static void TeachingChecks()
    {
        var defaults = TeachInvocation.Parse(["corpus", "model.json"]);
        Assert(defaults.PlannedSteps is null && defaults.UntilStep is null, "teaching defaults");
        var positional = TeachInvocation.Parse(["corpus", "model.json", "123"]);
        Assert(positional.PlannedSteps == 123 && positional.UntilStep == 123, "positional teaching compatibility");
        var milestone = TeachInvocation.Parse(["corpus", "model.json", "--until", "8", "--planned", "40"]);
        Assert(milestone.PlannedSteps == 40 && milestone.UntilStep == 8, "teaching milestone options");
        AssertThrows<ArgumentException>(() => TeachInvocation.Parse(["corpus", "model.json", "--until"]));
        AssertThrows<ArgumentException>(() => TeachInvocation.Parse(["corpus", "model.json", "--until", "0"]));
        AssertThrows<ArgumentException>(() => TeachInvocation.Parse(["corpus", "model.json", "--unknown", "1"]));
        AssertThrows<ArgumentException>(() => TeachInvocation.Parse(["corpus", "model.json", "--until", "1", "--until", "2"]));
        AssertThrows<ArgumentException>(() => TeachInvocation.Parse(["corpus", "model.json", "--planned", "10", "--until", "11"]));
        Assert(TeachingRecovery.Quote("C:\\A B\\O'Brien") == "'C:\\A B\\O''Brien'", "PowerShell path quoting");
        var recovery = new TeachingRecovery("C:\\P X\\Fishbrain.csproj", "C:\\Data X", "C:\\Model X.json", 40, 8);
        Assert(recovery.TeachCommand(8) ==
               "dotnet run -c Release --project 'C:\\P X\\Fishbrain.csproj' -- teach 'C:\\Data X' 'C:\\Model X.json' --planned 40 --until 8",
            "copy-paste recovery command");

        var directory = Path.Combine(Path.GetTempPath(), $"fishbrain-teaching-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var dataPath = Path.Combine(directory, "data.jsonl");
        var uninterruptedPath = Path.Combine(directory, "uninterrupted.json");
        var resumedPath = Path.Combine(directory, "resumed.json");
        try
        {
            File.WriteAllLines(dataPath,
            [
                Row("A", "GREETING", "FRIENDLY", true, "RESPOND", "B"),
                Row("C", "ACTIVITY", "NEUTRAL", false, "NO_RESPONSE", ""),
                StructuredRow()
            ]);
            var vocabulary = WordVocabulary.Build(dataPath);
            var data = TrainingData.Load(dataPath, new DialogueTokenizer(vocabulary));
            var uninterrupted = Brain.CreateForTesting(TinyConfig(), vocabulary);
            uninterrupted.DebugTrainCurriculum(data, uninterruptedPath, plannedSteps: 12, untilStep: 12);

            var interrupted = Brain.CreateForTesting(TinyConfig(), vocabulary);
            interrupted.DebugTrainCurriculum(data, resumedPath, plannedSteps: 12, untilStep: 6);
            var resumed = Brain.Load(resumedPath);
            resumed.DebugTrainCurriculum(data, resumedPath, plannedSteps: 12, untilStep: 12);
            Assert(File.ReadAllBytes(uninterruptedPath).SequenceEqual(File.ReadAllBytes(resumedPath)),
                "exact milestone resume");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static string StructuredRow() =>
        "{\"input\":\"PLAYER HELLO FRIEND.\",\"state\":{\"rapport\":1,\"mood\":\"NEUTRAL\",\"lastIntent\":\"UNKNOWN\",\"lastAffect\":\"NEUTRAL\",\"activeTopic\":\"NONE\",\"activeGoal\":\"NONE\"},\"perception\":{\"intent\":\"GREETING\",\"affect\":\"FRIENDLY\",\"responseExpected\":true},\"action\":\"RESPOND\",\"response\":\"GREETINGS, TRAVELER.\",\"source\":\"PROJECT_TEST\",\"semanticFamilyId\":\"TEST:GREET:1\",\"structuredPerception\":{\"speechActs\":[\"GREET\"],\"domains\":[\"SOCIAL\"],\"goals\":[\"RAPPORT\"],\"affect\":\"FRIENDLY\",\"stance\":\"FRIENDLY\",\"policy\":\"ANSWER\",\"slots\":[],\"contentFlags\":[],\"responseCandidateId\":\"SOCIAL_GREETING\",\"confidence\":{}},\"supervisedHeads\":[\"speechActs\",\"domains\",\"goals\",\"affect\",\"stance\",\"policy\",\"slots\",\"content\",\"tool\",\"responseCandidate\"]}";

    private static BrainConfig TinyConfig() => new()
    {
        EmbeddingSize = 8,
        HeadCount = 2,
        MlpSize = 12,
        ContextLength = 24,
        AttentionWindow = 8,
        PositionPeriod = 8,
        MaximumOutputLength = 16,
        LearningRate = 0.01,
        PlannedSteps = 20,
        Seed = 42
    };
    private static double NumericCrossEntropy(IReadOnlyList<double> logits, int target)
    {
        var maximum = logits.Max();
        return Math.Log(logits.Sum(value => Math.Exp(value - maximum))) + maximum - logits[target];
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void AssertThrows<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException($"Expected {typeof(T).Name}."); }
    private sealed class TestTools { [GameTool("PING")] public string Ping() => "42 GOLD."; }
}
