using System.Globalization;

namespace Fishbrain;

internal static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0)
            {
                PrintUsage();
                return 1;
            }

            switch (args[0].ToLowerInvariant())
            {
                case "train":
                    RequireCount(args, 3, 4);
                    Brain.TrainNew(args[1], args[2], args.Length == 4 ? ParseSteps(args[3]) : 10_000);
                    break;

                case "resume":
                    RequireCount(args, 3, 4);
                    Brain.Resume(args[1], args[2], args.Length == 4 ? ParseSteps(args[3]) : null);
                    break;

                case "chat":
                    RequireCount(args, 2, 2);
                    Chat(args[1]);
                    break;

                case "selftest":
                    RequireCount(args, 1, 1);
                    SelfTests.Run();
                    break;

                default:
                    PrintUsage();
                    return 1;
            }

            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"ERROR {exception.Message}");
            return 1;
        }
    }

    private static void Chat(string checkpointPath)
    {
        var brain = Brain.Load(checkpointPath);
        Console.WriteLine("ENTER UPPERCASE DIALOGUE OR AN EMPTY LINE TO QUIT");
        while (true)
        {
            Console.Write("> ");
            var input = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(input)) return;
            Console.WriteLine(brain.Reply(input));
        }
    }

    private static int ParseSteps(string text) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var steps) && steps > 0
            ? steps
            : throw new ArgumentException("Steps must be a positive integer.");

    private static void RequireCount(string[] args, int minimum, int maximum)
    {
        if (args.Length < minimum || args.Length > maximum) throw new ArgumentException("Invalid command arguments.");
    }

    private static void PrintUsage()
    {
        Console.WriteLine("FISHBRAIN");
        Console.WriteLine("  train  DATA.jsonl CHECKPOINT.json [TOTAL_STEPS]");
        Console.WriteLine("  resume DATA.jsonl CHECKPOINT.json [NEW_TOTAL_STEPS]");
        Console.WriteLine("  chat CHECKPOINT.json");
        Console.WriteLine("  selftest");
    }
}

internal static class SelfTests
{
    public static void Run()
    {
        var tests = new (string Name, Action Test)[]
        {
            ("AUTOGRAD", Autograd),
            ("TOKENIZER", TokenizerChecks),
            ("MODEL", ModelChecks),
            ("TRAINING DATA", TrainingDataChecks),
            ("CHECKPOINT", CheckpointChecks),
            ("TOOLS", ToolChecks)
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
        const double xData = 1.4;
        const double yData = 0.7;
        var x = new Value(xData);
        var y = new Value(yData);
        var expression = (x * y + x.Pow(2.0) + y.Exp()).Log();
        expression.Backward();

        AssertClose(NumericGradientX(xData, yData), x.Grad, 1e-5, "x gradient");
        AssertClose(NumericGradientY(xData, yData), y.Grad, 1e-5, "y gradient");

        var first = new[] { new Value(1.0), new Value(2.0), new Value(3.0) };
        var second = new[] { new Value(4.0), new Value(5.0), new Value(6.0) };
        var dot = Value.Dot(first, second);
        dot.Backward();
        AssertClose(32.0, dot.Data, 1e-12, "dot data");
        for (var i = 0; i < first.Length; i++)
        {
            AssertClose(second[i].Data, first[i].Grad, 1e-12, "dot left gradient");
            AssertClose(first[i].Data, second[i].Grad, 1e-12, "dot right gradient");
        }

        var negative = new Value(-2.0);
        negative.Relu().Backward();
        AssertClose(0.0, negative.Grad, 0.0, "relu gradient");
    }

    private static void TokenizerChecks()
    {
        var normalized = Tokenizer.Normalize("  hello\t42\r\nworld  ");
        Assert(normalized == "HELLO 42 WORLD", "normalization failed");
        var roundTrip = new string(Tokenizer.Encode(normalized).Select(Tokenizer.DecodeVisible).ToArray());
        Assert(roundTrip == normalized, "tokenizer round-trip failed");
        AssertThrows<ArgumentException>(() => Tokenizer.Normalize("HELLO!"));
        AssertThrows<ArgumentOutOfRangeException>(() => Tokenizer.DecodeVisible(Tokenizer.Bos));
    }

    private static void ModelChecks()
    {
        var config = TinyConfig();
        var first = Brain.CreateForTesting(config);
        var second = Brain.CreateForTesting(TinyConfig());
        Assert(first.DebugWeights().SequenceEqual(second.DebugWeights()), "seeded initialization is not deterministic");

        var prefixA = new[] { Tokenizer.Bos, Tokenizer.EncodeCharacter('A') };
        var prefixB = new[] { Tokenizer.Bos, Tokenizer.EncodeCharacter('B') };
        var firstPositionA = first.DebugLogitsAt(prefixA, 0);
        var firstPositionB = first.DebugLogitsAt(prefixB, 0);
        Assert(firstPositionA.SequenceEqual(firstPositionB), "future token changed a causal position");
        Assert(firstPositionA.All(double.IsFinite), "model produced non-finite logits");

        var longContext = Enumerable.Repeat(Tokenizer.EncodeCharacter('A'), config.ContextLength + 5).ToArray();
        var tail = longContext[^config.ContextLength..];
        Assert(first.DebugNextLogits(longContext).SequenceEqual(first.DebugNextLogits(tail)), "rolling context differs from its tail");

        var window = new[]
        {
            Tokenizer.Bos, Tokenizer.EncodeCharacter('A'), Tokenizer.Sep,
            Tokenizer.Text, Tokenizer.EncodeCharacter('B'), Tokenizer.Eos
        };
        var initial = first.DebugTrainWindow(window, 40);
        var final = initial;
        for (var i = 0; i < 39; i++) final = first.DebugTrainWindow(window, 40);
        Assert(final < initial, $"tiny overfit loss did not decrease ({initial} -> {final})");

        var memorized = Brain.CreateForTestingWithExamples(
            TinyConfig(),
            new Dictionary<string, string> { ["PLAYER HELLO"] = "HELLO TRAVELER" });
        Assert(memorized.Reply("player hello") == "HELLO TRAVELER",
            "exact trained example lookup failed");
    }

    private static void TrainingDataChecks()
    {
        var path = Path.Combine(Path.GetTempPath(), $"fishbrain-data-{Guid.NewGuid():N}.jsonl");
        try
        {
            File.WriteAllLines(path,
            [
                "{\"input\":\"PLAYER HELLO\",\"response\":\"WELCOME\"}",
                "{\"input\":\"HOW MUCH GOLD\",\"tool\":\"GETGOLD\",\"arguments\":[\"PLAYER1\"],\"result\":\"42\",\"response\":\"YOU HAVE 42 GOLD\"}"
            ]);
            var data = TrainingData.Load(path);
            Assert(data.Samples.Count == 3, "normal and tool rows should produce three focused samples");
            Assert(data.Samples.All(sample =>
                    sample.Tokens.Length is >= 2 and <= TrainingData.MaximumSampleLength &&
                    sample.FirstTargetIndex is >= 1 && sample.FirstTargetIndex < sample.Tokens.Length),
                "training samples must contain a conditioning prefix and output targets");
            Assert(data.Samples.All(sample =>
                    sample.Tokens[sample.FirstTargetIndex] is Tokenizer.Text or Tokenizer.Call),
                "training samples must begin their loss at TEXT or CALL");
            Assert(data.ToolNames.SetEquals(["GETGOLD"]), "tool name was not collected");
            Assert(data.Responses.SetEquals(["WELCOME", "YOU HAVE 42 GOLD"]),
                "trained response vocabulary was not collected");
            Assert(data.Examples.Count == 1 && data.Examples["PLAYER HELLO"] == "WELCOME" &&
                   !data.Examples.ContainsKey("HOW MUCH GOLD"),
                "dialogue memory included a tool-backed training row");

            File.WriteAllText(path, "{\"input\":\"BAD!\",\"response\":\"NO\"}");
            AssertThrows<InvalidDataException>(() => TrainingData.Load(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static void CheckpointChecks()
    {
        var firstPath = Path.Combine(Path.GetTempPath(), $"fishbrain-{Guid.NewGuid():N}.json");
        var secondPath = Path.Combine(Path.GetTempPath(), $"fishbrain-{Guid.NewGuid():N}.json");
        try
        {
            var brain = Brain.CreateForTesting(TinyConfig(), "GETGOLD");
            var window = new[] { Tokenizer.Bos, Tokenizer.EncodeCharacter('A'), Tokenizer.Eos };
            brain.DebugTrainWindow(window, 10);
            var expectedLogits = brain.DebugNextLogits([Tokenizer.Bos, Tokenizer.EncodeCharacter('A')]);
            brain.Save(firstPath);

            var loaded = Brain.Load(firstPath);
            Assert(loaded.CompletedSteps == brain.CompletedSteps, "completed step was not restored");
            Assert(loaded.DebugWeights().SequenceEqual(brain.DebugWeights()), "weights were not restored exactly");
            Assert(loaded.DebugNextLogits([Tokenizer.Bos, Tokenizer.EncodeCharacter('A')]).SequenceEqual(expectedLogits),
                "checkpoint logits changed");
            loaded.Save(secondPath);
            Assert(File.ReadAllText(firstPath) == File.ReadAllText(secondPath), "optimizer or RNG checkpoint state changed on load");
        }
        finally
        {
            if (File.Exists(firstPath)) File.Delete(firstPath);
            if (File.Exists(secondPath)) File.Delete(secondPath);
        }
    }

    private static void ToolChecks()
    {
        var brain = Brain.CreateForTesting(TinyConfig(), "GETGOLD", "HASITEM", "FAIL", "BADRESULT");
        brain.Tools.Register(new TestGameTools());

        Assert(brain.Tools.TryInvoke("GETGOLD", ["PLAYER1"], out var gold) && gold == "42", "integer tool failed");
        Assert(brain.Tools.TryInvoke("HASITEM", ["7"], out var hasItem) && hasItem == "TRUE", "boolean tool failed");
        Assert(!brain.Tools.TryInvoke("HASITEM", ["NOPE"], out _), "invalid integer argument was accepted");
        Assert(!brain.Tools.TryInvoke("FAIL", [], out _), "throwing tool was accepted");
        Assert(!brain.Tools.TryInvoke("BADRESULT", [], out _), "invalid result characters were accepted");
        AssertThrows<InvalidOperationException>(() => brain.Tools.Register(new TestGameTools()));

        var untrained = Brain.CreateForTesting(TinyConfig());
        AssertThrows<InvalidOperationException>(() => untrained.Tools.Register(new UntrainedTools()));
    }

    private static BrainConfig TinyConfig() => new()
    {
        EmbeddingSize = 8,
        HeadCount = 2,
        MlpSize = 12,
        ContextLength = 16,
        AttentionWindow = 8,
        PositionPeriod = 8,
        MaximumOutputLength = 16,
        LearningRate = 0.01,
        PlannedSteps = 40,
        Seed = 42
    };

    private static double NumericGradientX(double x, double y)
    {
        const double epsilon = 1e-6;
        return (Function(x + epsilon, y) - Function(x - epsilon, y)) / (2.0 * epsilon);
    }

    private static double NumericGradientY(double x, double y)
    {
        const double epsilon = 1e-6;
        return (Function(x, y + epsilon) - Function(x, y - epsilon)) / (2.0 * epsilon);
    }

    private static double Function(double x, double y) => Math.Log(x * y + x * x + Math.Exp(y));

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void AssertClose(double expected, double actual, double tolerance, string message)
    {
        if (Math.Abs(expected - actual) > tolerance)
            throw new InvalidOperationException($"{message}: expected {expected}, got {actual}");
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

    private sealed class TestGameTools
    {
        [GameTool("GETGOLD")]
        public int GetGold(string playerId) => playerId == "PLAYER1" ? 42 : 0;

        [GameTool("HASITEM")]
        public bool HasItem(int itemId) => itemId == 7;

        [GameTool("FAIL")]
        public string Fail() => throw new InvalidOperationException("Expected test failure.");

        [GameTool("BADRESULT")]
        public string BadResult() => "NOT VALID!";
    }

    private sealed class UntrainedTools
    {
        [GameTool("UNKNOWN")]
        public int Unknown() => 0;
    }
}
