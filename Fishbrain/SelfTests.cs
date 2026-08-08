using System.Globalization;
using System.Diagnostics;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fishbrain;

internal static class SelfTests
{
    public static void Run()
    {
        var tests = new (string, Action)[]
        {
            ("AUTOGRAD", Autograd),
            ("TOKENIZER", TokenizerChecks),
            ("COGNITION", CognitionChecks),
            ("MODEL", ModelChecks),
            ("TRAINING DATA", TrainingDataChecks),
            ("CHECKPOINT", CheckpointChecks),
            ("TEACHING", TeachingChecks)
        };
        foreach (var (name, test) in tests)
        {
            test();
            Console.WriteLine($"PASS {name}");
        }

        Console.WriteLine($"PASS ALL {tests.Length} TESTS");
    }

    private static void Autograd()
    {
        var x = new Value(1.4);
        var y = new Value(0.7);
        var expression = (x * y + x.Pow(2) + y.Exp()).Log();
        expression.Backward();
        Assert(double.IsFinite(x.Grad) && double.IsFinite(y.Grad), "finite gradients");
        var left = new[] { new Value(1.0), new Value(2.0) };
        var right = new[] { new Value(3.0), new Value(4.0) };
        var dot = Value.Dot(left, right);
        dot.Backward();
        Assert(Math.Abs(dot.Data - 11) < 1e-12 && left[0].Grad == 3, "dot gradients");

        var logits = new[] { new Value(1.2), new Value(-0.7), new Value(3.4) };
        var crossEntropy = Value.CrossEntropy(logits, 2);
        crossEntropy.Backward();
        var numeric = NumericCrossEntropy([1.2, -0.7, 3.4], 2);
        Assert(Math.Abs(crossEntropy.Data - numeric) < 1e-12, "fused cross-entropy value");
        for (var index = 0; index < logits.Length; index++)
        {
            const double epsilon = 1e-6;
            var plus = new[] { 1.2, -0.7, 3.4 };
            plus[index] += epsilon;
            var minus = new[] { 1.2, -0.7, 3.4 };
            minus[index] -= epsilon;
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
        Assert(Tokenizer.WordStart == 113 && Tokenizer.AffectStart == 60, "stable control and character layout");
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
        var substring = Cognition.Constrain(
            new TurnPerception(DialogueIntent.Statement, UserAffect.Neutral, false),
            "SHUT UPPER DOOR");
        Assert(substring.Intent == DialogueIntent.Statement && substring.Affect == UserAffect.Neutral,
            "insult phrases require lexical boundaries");
    }

    private static void ModelChecks()
    {
        var first = Brain.CreateForTesting(TinyConfig());
        var second = Brain.CreateForTesting(TinyConfig());
        Assert(first.DebugWeights().SequenceEqual(second.DebugWeights()), "deterministic initialization");
        var fullFirst = Brain.CreateForTesting(new BrainConfig());
        var fullSecond = Brain.CreateForTesting(new BrainConfig());
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
        var before = first.DebugTrainWindow(window, 20);
        var after = before;
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
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
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
        var path = Path.Combine(Path.GetTempPath(), $"fishbrain-checkpoint-{Guid.NewGuid():N}.json");
        try
        {
            var brain = Brain.CreateForTesting(TinyConfig());
            brain.Save(path);
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
            File.WriteAllText(path, "{}");
            AssertThrows<InvalidDataException>(() => Brain.Load(path));
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static void TeachingChecks()
    {
        var defaults = TeachInvocation.Parse(["corpus", "model.json"]);
        Assert(defaults.PlannedSteps is null && defaults.UntilStep is null, "teaching defaults");
        AssertThrows<ArgumentException>(() => TeachInvocation.Parse(["corpus", "model.json", "123"]));
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
    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
    private static void AssertThrows<T>(Action action) where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }

        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
}
