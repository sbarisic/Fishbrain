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
                    Count(args, 3, 5);
                    var gate = EvaluationGateParser.Parse(args[3..]);
                    return Evaluation.Run(args[1], args[2], gate);
                case "chat":
                    Count(args, 1, 2);
                    Chat(args.Length == 2 ? args[1] : Path.Combine("data", "models", "model-v9-latest.json"));
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
        var history = new List<string>();
        Console.WriteLine("ENTER DIALOGUE OR AN EMPTY LINE TO QUIT");
        while (true)
        {
            Console.Write("> ");
            var input = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(input)) return;
            var playerTurn = "PLAYER " + DialogueText.Normalize(input);
            var recentDialogue = string.Join(' ', history.Append(playerTurn));
            var result = brain.Reply(recentDialogue, state);
            state = result.State;
            Console.WriteLine(result.Text.Length == 0 ? "[NO RESPONSE]" : result.Text);
            Console.WriteLine(
                $"STATE RAPPORT={state.Rapport} MOOD={Upper(state.Mood)} " +
                $"INTENT={Upper(result.Perception.Intent)} AFFECT={Upper(result.Perception.Affect)} " +
                $"EXPECTED={result.Perception.ResponseExpected.ToString().ToUpperInvariant()} " +
                $"ACTION={Upper(result.Decision.Action)} TOPIC={Upper(state.ActiveTopic)} " +
                $"GOAL={Upper(state.ActiveGoal)} TONE={Upper(result.Tone)}");
            history.Add(DialogueText.TerminateTurn(playerTurn));
            if (result.Text.Length > 0) history.Add("NPC " + DialogueText.TerminateTurn(result.Text));
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
        Console.WriteLine("  evaluate TEST.jsonl CHECKPOINT.json [--gate none|stage|release]");
        Console.WriteLine("  chat [CHECKPOINT.json]  (default: data/models/model-v9-latest.json)");
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
        var directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "data", "telemetry"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "milestones.jsonl");
        var payload = new
        {
            timestampUtc = DateTimeOffset.UtcNow,
            milestone = "A",
            corpusHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(corpusPath))).ToLowerInvariant(),
            checkpointHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(checkpointPath))).ToLowerInvariant(),
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
    private sealed record TranscriptResult(string Name, ReplyResult Result, bool Pass);

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
        var tokenizer = new DialogueTokenizer(WordVocabulary.Testing());
        const string visible = "HELLO, FRIEND!";
        Assert(tokenizer.DetokenizeOutput(tokenizer.Encode(visible).Select(tokenizer.OutputId)) == visible, "word roundtrip");
        var alpha = new DialogueTokenizer(new WordVocabulary(["ALPHA"], ["ALPHA"]));
        var beta = new DialogueTokenizer(new WordVocabulary(["BETA", "GAMMA"], ["BETA"]));
        var alphaEncoding = alpha.Encode("ALPHA");
        Assert(!alpha.ContainsUnknown("ALPHA") && alpha.ContainsUnknown("BETA"), "first vocabulary isolation");
        Assert(!beta.ContainsUnknown("BETA") && beta.ContainsUnknown("ALPHA"), "second vocabulary isolation");
        Assert(alpha.Encode("ALPHA").SequenceEqual(alphaEncoding), "constructing another tokenizer does not mutate the first");
        Assert(Tokenizer.WordStart == 74 && Tokenizer.AffectStart == 60, "stable v9 control layout");
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
        Assert(fullFirst.Config.EmbeddingSize == 64 && fullFirst.DebugWeights().SequenceEqual(fullSecond.DebugWeights()), "64D deterministic initialization");
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
            foreach (var parameterIndex in new[] { 0, layout.Key, layout.IntentHead, layout.AffectHead, layout.ExpectedHead })
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
