using System.Globalization;
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
                    Brain.TrainNew(args[1], args[2], args.Length == 4 ? Steps(args[3]) : 40_000);
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
                    Count(args, 3, 3);
                    Evaluation.Run(args[1], args[2]);
                    break;
                case "chat":
                    Count(args, 2, 2);
                    Chat(args[1]);
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
            Console.Error.WriteLine($"ERROR {exception.Message}");
            return 1;
        }
    }

    private static void Chat(string checkpoint)
    {
        var brain = Brain.Load(checkpoint);
        var state = NpcState.Initial;
        Console.WriteLine("ENTER DIALOGUE OR AN EMPTY LINE TO QUIT");
        while (true)
        {
            Console.Write("> ");
            var input = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(input)) return;
            var result = brain.Reply(input, state);
            state = result.State;
            Console.WriteLine(result.Text.Length == 0 ? "[NO RESPONSE]" : result.Text);
            Console.WriteLine(
                $"STATE RAPPORT={state.Rapport} MOOD={Upper(state.Mood)} " +
                $"INTENT={Upper(result.Perception.Intent)} AFFECT={Upper(result.Perception.Affect)} " +
                $"EXPECTED={result.Perception.ResponseExpected.ToString().ToUpperInvariant()} " +
                $"ACTION={Upper(result.Decision.Action)} TOPIC={Upper(state.ActiveTopic)} " +
                $"GOAL={Upper(state.ActiveGoal)} TONE={Upper(result.Tone)}");
        }
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
        Console.WriteLine("  evaluate TEST.jsonl CHECKPOINT.json");
        Console.WriteLine("  chat CHECKPOINT.json");
        Console.WriteLine("  selftest");
    }

    private static string FindProjectPath()
    {
        var besideBuild = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "Fishbrain.csproj"));
        if (File.Exists(besideBuild)) return besideBuild;
        var beneathWorkingDirectory = Path.GetFullPath(Path.Combine("Fishbrain", "Fishbrain.csproj"));
        return beneathWorkingDirectory;
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

    public static void Run(string testPath, string checkpointPath)
    {
        var brain = Brain.Load(checkpointPath);
        var rows = File.ReadLines(testPath).Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonSerializer.Deserialize<Row>(line, Options)
                ?? throw new InvalidDataException("Invalid evaluation row."))
            .ToArray();
        if (rows.Length == 0) throw new InvalidDataException("Evaluation data is empty.");

        var expectedIntent = new List<DialogueIntent>();
        var predictedIntent = new List<DialogueIntent>();
        var expectedAffect = new List<UserAffect>();
        var predictedAffect = new List<UserAffect>();
        var expectedResponse = new List<bool>();
        var predictedResponse = new List<bool>();
        var actionCorrect = 0;
        var sourceStats = new Dictionary<string, (int Correct, int Total)>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            row.State.Validate();
            var predicted = brain.DebugPredictPerception(row.Input, row.State);
            expectedIntent.Add(row.Perception.Intent);
            predictedIntent.Add(predicted.Intent);
            expectedAffect.Add(row.Perception.Affect);
            predictedAffect.Add(predicted.Affect);
            expectedResponse.Add(row.Perception.ResponseExpected);
            predictedResponse.Add(predicted.ResponseExpected);
            if (Cognition.ActionFor(predicted) == row.Action) actionCorrect++;
            var source = row.Source ?? "unknown";
            var current = sourceStats.GetValueOrDefault(source);
            sourceStats[source] = (current.Correct + (predicted.Intent == row.Perception.Intent ? 1 : 0), current.Total + 1);
        }

        var trainingData = TrainingData.Load(testPath);
        var lossSamples = trainingData.LanguageSamples.Take(100).ToArray();
        var languageLoss = brain.DebugAverageLoss(lossSamples);
        var generated = 0; var invalid = 0; var unexpectedEmpty = 0; var overlength = 0;
        foreach (var row in rows.Where(row => row.Perception.ResponseExpected && row.Action != ResponseAction.CallTool).Take(100))
        {
            var result = brain.DebugReplyWithoutMemory(row.Input, row.State);
            generated++;
            if (result.Text.Length == 0) unexpectedEmpty++;
            if (result.Text.Length > 256) overlength++;
            try { if (result.Text.Length > 0 && !DialogueText.IsCanonical(result.Text)) invalid++; }
            catch (ArgumentException) { invalid++; }
        }

        var syntheticMacro = SubsetMacro(rows, expectedIntent, predictedIntent, row => row.Source == "SYNTHETIC");
        var externalMacro = SubsetMacro(rows, expectedIntent, predictedIntent, row => row.Source != "SYNTHETIC");
        var affectMacro = MacroF1(expectedAffect, predictedAffect);
        var expectedF1 = BinaryMetrics(expectedResponse, predictedResponse, true).F1;
        var goldenPass = GoldenCases(brain);
        var releasePass = syntheticMacro >= 0.85 && externalMacro >= 0.70 && affectMacro >= 0.75 &&
                          expectedF1 >= 0.90 && invalid == 0 && overlength == 0 && goldenPass;

        Console.WriteLine($"RECORDS {rows.Length}");
        Console.WriteLine($"INTENT_ACCURACY {Accuracy(expectedIntent, predictedIntent):F4}");
        Console.WriteLine($"INTENT_MACRO_F1 {MacroF1(expectedIntent, predictedIntent):F4}");
        Console.WriteLine($"AFFECT_ACCURACY {Accuracy(expectedAffect, predictedAffect):F4}");
        Console.WriteLine($"AFFECT_MACRO_F1 {MacroF1(expectedAffect, predictedAffect):F4}");
        PrintBinary("RESPONSE_EXPECTED", expectedResponse, predictedResponse, true);
        PrintBinary("NO_RESPONSE", expectedResponse, predictedResponse, false);
        Console.WriteLine($"ACTION_ACCURACY {(double)actionCorrect / rows.Length:F4}");
        Console.WriteLine($"REALIZATION_LOSS {languageLoss:F4}");
        Console.WriteLine($"REALIZATION_LOSS_SAMPLES {lossSamples.Length}");
        Console.WriteLine($"GENERATED {generated} INVALID_RATE {(double)invalid / Math.Max(1, generated):F4} EMPTY_RATE {(double)unexpectedEmpty / Math.Max(1, generated):F4} OVERLENGTH_RATE {(double)overlength / Math.Max(1, generated):F4}");
        foreach (var pair in sourceStats.OrderBy(x => x.Key, StringComparer.Ordinal))
            Console.WriteLine($"SOURCE {pair.Key} INTENT_ACCURACY {(double)pair.Value.Correct / pair.Value.Total:F4} N {pair.Value.Total}");
        PrintSubset("SYNTHETIC_HELD_OUT", rows, expectedIntent, predictedIntent, row => row.Source == "SYNTHETIC");
        PrintSubset("EXTERNAL_HELD_OUT", rows, expectedIntent, predictedIntent, row => row.Source != "SYNTHETIC");
        foreach (var family in rows.Where(x => x.Family is not null).Select(x => x.Family!).Distinct().Order(StringComparer.Ordinal))
            PrintSubset("FAMILY_" + family, rows, expectedIntent, predictedIntent, row => row.Family == family);
        foreach (var expected in Enum.GetValues<DialogueIntent>())
        foreach (var predicted in Enum.GetValues<DialogueIntent>())
        {
            var count = expectedIntent.Zip(predictedIntent).Count(pair => pair.First == expected && pair.Second == predicted);
            if (count > 0) Console.WriteLine($"CONFUSION {expected} {predicted} {count}");
        }
        Console.WriteLine($"GOLDEN_CASES {(goldenPass ? "PASS" : "FAIL")}");
        Console.WriteLine($"RELEASE_GATE {(releasePass ? "PASS" : "FAIL")}");
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
        return MacroF1(indices.Select(index => expected[index]).ToArray(), indices.Select(index => predicted[index]).ToArray());
    }

    private static bool GoldenCases(Brain brain)
    {
        var cases = new (string Input, DialogueIntent Intent, UserAffect Affect, bool Expected)[]
        {
            ("PLAYER HELLO, HOW ARE YOU?", DialogueIntent.Wellbeing, UserAffect.Friendly, true),
            ("PLAYER THAT IS NOT WHAT I ASKED.", DialogueIntent.Clarification, UserAffect.Frustrated, true),
            ("PLAYER WHAT?", DialogueIntent.Clarification, UserAffect.Neutral, true),
            ("PLAYER THANK YOU, IDIOT.", DialogueIntent.Gratitude, UserAffect.Hostile, true),
            ("PLAYER I WAS NOT THANKING YOU.", DialogueIntent.Clarification, UserAffect.Frustrated, true),
            ("PLAYER I AM JUST LOOKING AROUND.", DialogueIntent.Activity, UserAffect.Neutral, false)
        };
        return cases.All(item => brain.DebugPredictPerception(item.Input, NpcState.Initial) ==
                                 new TurnPerception(item.Intent, item.Affect, item.Expected));
    }

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
        const string visible = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 .,?!'-:";
        Assert(new string(Tokenizer.Encode(visible).Select(Tokenizer.DecodeVisible).ToArray()) == visible, "roundtrip");
        Assert(Tokenizer.VocabularySize == 105 && Tokenizer.AffectStart == 97, "v4 token layout");
        Assert(Tokenizer.Action(ResponseAction.NoResponse) == 104, "no-response token");
        Assert(Tokenizer.Normalize("hello , friend!!!") == "HELLO, FRIEND!", "punctuation repair");
        Assert(Tokenizer.Normalize("it’s ready — now??") == "IT'S READY-NOW?", "unicode punctuation normalization");
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
    }

    private static void ModelChecks()
    {
        var first = Brain.CreateForTesting(TinyConfig()); var second = Brain.CreateForTesting(TinyConfig());
        Assert(first.DebugWeights().SequenceEqual(second.DebugWeights()), "deterministic initialization");
        var fullFirst = Brain.CreateForTesting(new BrainConfig()); var fullSecond = Brain.CreateForTesting(new BrainConfig());
        Assert(fullFirst.Config.EmbeddingSize == 64 && fullFirst.DebugWeights().SequenceEqual(fullSecond.DebugWeights()), "64D deterministic initialization");
        Assert(first.DebugNextLogits([Tokenizer.Bos]).Length == 105, "logit count");
        var causalA = first.DebugSequenceLogits([Tokenizer.Bos, 0, 1]);
        var causalB = first.DebugSequenceLogits([Tokenizer.Bos, 0, 2]);
        Assert(causalA[1].SequenceEqual(causalB[1]), "causal masking");
        var optimized = Brain.CreateForTesting(TinyConfig());
        var reference = Brain.CreateForTesting(TinyConfig());
        int[] equivalenceWindow = [Tokenizer.Bos, 0, 1, Tokenizer.Decide, Tokenizer.Intent(DialogueIntent.Greeting), Tokenizer.Eos];
        var optimizedLogits = optimized.DebugTargetLogits(equivalenceWindow[..^1], 2, optimizedForward: true);
        var referenceLogits = reference.DebugTargetLogits(equivalenceWindow[..^1], 2, optimizedForward: false);
        Assert(optimizedLogits.SelectMany(row => row).SequenceEqual(referenceLogits.SelectMany(row => row)),
            "optimized forward logit equivalence");
        var optimizedGradient = optimized.DebugLossAndGradients(equivalenceWindow, 3, optimizedForward: true);
        var referenceGradient = reference.DebugLossAndGradients(equivalenceWindow, 3, optimizedForward: false);
        Assert(Math.Abs(optimizedGradient.Loss - referenceGradient.Loss) < 1e-10 &&
               optimizedGradient.Gradients.Zip(referenceGradient.Gradients)
                   .All(pair => Math.Abs(pair.First - pair.Second) < 1e-10),
            "optimized forward gradient equivalence");
        optimized = Brain.CreateForTesting(TinyConfig());
        reference = Brain.CreateForTesting(TinyConfig());
        var optimizedLoss = optimized.DebugTrainSample(equivalenceWindow, 3, 20);
        var referenceLoss = reference.DebugTrainSampleReference(equivalenceWindow, 3, 20);
        Assert(Math.Abs(optimizedLoss - referenceLoss) < 1e-10, "optimized forward loss equivalence");
        Assert(optimized.DebugWeights().Zip(reference.DebugWeights()).All(pair => Math.Abs(pair.First - pair.Second) < 1e-10),
            "optimized forward update equivalence");
        var window = new[] { Tokenizer.Bos, Tokenizer.Decide, Tokenizer.Intent(DialogueIntent.Greeting), Tokenizer.Eos };
        var before = first.DebugTrainWindow(window, 20); var after = before;
        for (var index = 0; index < 19; index++) after = first.DebugTrainWindow(window, 20);
        Assert(after < before, "overfit loss");
    }

    private static void TrainingDataChecks()
    {
        var path = Path.Combine(Path.GetTempPath(), $"fishbrain-v4-{Guid.NewGuid():N}.jsonl");
        try
        {
            File.WriteAllLines(path,
            [
                Row("PLAYER HELLO!", "GREETING", "FRIENDLY", true, "RESPOND", "HELLO, TRAVELER!"),
                Row("PLAYER I AM LOOKING AROUND.", "ACTIVITY", "NEUTRAL", false, "NORESPONSE", ""),
                Row("PLAYER WHAT?", "CLARIFICATION", "FRUSTRATED", true, "RESPOND", null)
            ]);
            var data = TrainingData.Load(path);
            Assert(data.PerceptionSamples.Count >= 3 && data.LanguageSamples.Count >= 1, "task streams");
            Assert(data.Examples.Count == 1, "exact memory forms");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static string Row(string input, string intent, string affect, bool expected, string action, string? response)
    {
        var responseJson = response is null ? "null" : JsonSerializer.Serialize(response);
        return $"{{\"input\":{JsonSerializer.Serialize(input)},\"state\":{{\"rapport\":1,\"mood\":\"NEUTRAL\",\"lastIntent\":\"UNKNOWN\",\"lastAffect\":\"NEUTRAL\",\"activeTopic\":\"NONE\",\"activeGoal\":\"NONE\"}},\"perception\":{{\"intent\":\"{intent}\",\"affect\":\"{affect}\",\"responseExpected\":{expected.ToString().ToLowerInvariant()}}},\"action\":\"{action}\",\"response\":{responseJson},\"source\":\"synthetic\"}}";
    }

    private static void CheckpointChecks()
    {
        var path = Path.Combine(Path.GetTempPath(), $"fishbrain-v4-{Guid.NewGuid():N}.json");
        try
        {
            var brain = Brain.CreateForTesting(TinyConfig()); brain.Save(path);
            var loaded = Brain.Load(path);
            Assert(loaded.Config.EmbeddingSize == 8 && loaded.DebugWeights().SequenceEqual(brain.DebugWeights()), "roundtrip");
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
                Row("C", "ACTIVITY", "NEUTRAL", false, "NO_RESPONSE", "")
            ]);
            var data = TrainingData.Load(dataPath);
            var uninterrupted = Brain.CreateForTesting(TinyConfig());
            uninterrupted.DebugTrainCurriculum(data, uninterruptedPath, plannedSteps: 12, untilStep: 12);

            var interrupted = Brain.CreateForTesting(TinyConfig());
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

    private static BrainConfig TinyConfig() => new()
    {
        EmbeddingSize = 8, HeadCount = 2, MlpSize = 12, ContextLength = 24,
        AttentionWindow = 8, PositionPeriod = 8, MaximumOutputLength = 16,
        LearningRate = 0.01, PlannedSteps = 20, Seed = 42
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
