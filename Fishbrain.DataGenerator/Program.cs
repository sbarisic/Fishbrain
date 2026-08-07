using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fishbrain.DataGenerator;

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
                case "generate":
                {
                    var options = Cli.ParseGenerateOptions(args[1..]);
                    var summary = TrainingDataGenerator.Generate(options);
                    Console.WriteLine($"REQUESTED {summary.RequestedCount}");
                    Console.WriteLine($"WROTE {summary.WrittenCount} RECORDS");
                    Console.WriteLine($"DIRECT {summary.DirectCount}");
                    Console.WriteLine($"HISTORY {summary.HistoryCount}");
                    Console.WriteLine($"ATTEMPTS {summary.Attempts}");
                    Console.WriteLine($"SEED {summary.Seed}");
                    Console.WriteLine($"OUTPUT {summary.OutputPath}");
                    if (summary.WrittenCount < summary.RequestedCount)
                        Console.WriteLine($"WARNING SHORTFALL {summary.RequestedCount - summary.WrittenCount}");
                    return 0;
                }
                case "selftest" when args.Length == 1:
                    SelfTests.Run();
                    return 0;
                default:
                    PrintUsage();
                    return 1;
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"ERROR {exception.Message}");
            return 1;
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("FISHBRAIN DATA GENERATOR");
        Console.WriteLine("  generate [--output data.jsonl] [--count 2000] [--seed 42]");
        Console.WriteLine("  selftest");
    }
}

internal sealed record TrainingRecord(
    [property: JsonPropertyName("input")] string Input,
    [property: JsonPropertyName("response")] string Response);

internal sealed record GenerateOptions(string OutputPath = "data.jsonl", int Count = 2_000, int Seed = 42);

internal sealed record GenerationSummary(
    string OutputPath,
    int RequestedCount,
    int WrittenCount,
    int DirectCount,
    int HistoryCount,
    int Attempts,
    int Seed);

internal sealed record GeneratedExchange(
    string Intent,
    string Question,
    string Answer,
    string RawQuestion,
    string RawAnswer,
    IReadOnlyDictionary<string, string> Bindings,
    bool IsNovel);

internal sealed record GeneratedSample(
    TrainingRecord Record,
    GeneratedExchange Current,
    GeneratedExchange? Prior);

internal sealed record GenerationBatch(
    IReadOnlyList<GeneratedSample> Samples,
    int RequestedCount,
    int DirectCount,
    int HistoryCount,
    int Attempts)
{
    public int WrittenCount => Samples.Count;
}

internal static class Cli
{
    public static GenerateOptions ParseGenerateOptions(string[] args)
    {
        var output = "data.jsonl";
        var count = 2_000;
        var seed = 42;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < args.Length; i++)
        {
            var option = args[i];
            if (!seen.Add(option)) throw new ArgumentException($"Duplicate option '{option}'.");
            var value = ReadValue(args, ref i, option);

            switch (option)
            {
                case "--output":
                    if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Output path cannot be empty.");
                    output = value;
                    break;
                case "--count":
                    count = ParsePositive(value, "count");
                    break;
                case "--seed":
                    if (!int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out seed))
                        throw new ArgumentException("Seed must be a 32-bit integer.");
                    break;
                default:
                    throw new ArgumentException($"Unknown option '{option}'.");
            }
        }

        return new GenerateOptions(output, count, seed);
    }

    private static string ReadValue(string[] args, ref int index, string option)
    {
        if (!option.StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException($"Unexpected argument '{option}'.");
        if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException($"Option '{option}' requires a value.");
        return args[++index];
    }

    private static int ParsePositive(string value, string name) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : throw new ArgumentException($"{name} must be a positive integer.");
}

internal static class TrainingDataGenerator
{
    private const int MaximumQuestionWords = 18;
    private const int MaximumAnswerWords = 20;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    public static GenerationSummary Generate(GenerateOptions options)
    {
        var batch = BuildRecords(options.Seed, options.Count, Templates.Intents, Templates.Anchors);
        if (batch.WrittenCount == 0) throw new InvalidOperationException("No valid records could be generated.");

        var outputPath = Path.GetFullPath(options.OutputPath);
        var outputDirectory = Path.GetDirectoryName(outputPath)
            ?? throw new ArgumentException("Output path has no parent directory.");
        Directory.CreateDirectory(outputDirectory);

        var temporaryPath = Path.Combine(
            outputDirectory,
            $".{Path.GetFileName(outputPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                foreach (var sample in batch.Samples)
                    writer.WriteLine(JsonSerializer.Serialize(sample.Record, JsonOptions));
            }
            File.Move(temporaryPath, outputPath, true);
        }
        catch
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            throw;
        }

        return new GenerationSummary(
            outputPath,
            options.Count,
            batch.WrittenCount,
            batch.DirectCount,
            batch.HistoryCount,
            batch.Attempts,
            options.Seed);
    }

    internal static GenerationBatch BuildRecords(
        int seed,
        int requestedCount,
        IReadOnlyList<IntentCorpus> corpora,
        IReadOnlyList<TrainingRecord>? anchors = null)
    {
        if (requestedCount <= 0) throw new ArgumentOutOfRangeException(nameof(requestedCount));
        if (corpora.Count == 0) throw new ArgumentException("At least one intent corpus is required.", nameof(corpora));

        var models = corpora.Select(x => new IntentModel(x)).ToArray();
        var random = new Random(seed);
        var rotation = new IntentRotation(models, random);
        var samples = new List<GeneratedSample>(requestedCount);
        var answersByInput = new Dictionary<string, string>(StringComparer.Ordinal);
        var answersByQuestion = new Dictionary<string, string>(StringComparer.Ordinal);
        var directTarget = (requestedCount + 1) / 2;
        var historyTarget = requestedCount / 2;
        var maximumAttempts = Math.Max(10_000, requestedCount * 200);
        var attempts = 0;
        var directAttempts = 0;

        foreach (var anchor in anchors ?? [])
        {
            if (samples.Count >= directTarget) break;
            if (!anchor.Input.StartsWith("PLAYER ", StringComparison.Ordinal) ||
                !TryAdd(anchor, answersByInput))
            {
                continue;
            }

            var question = anchor.Input["PLAYER ".Length..];
            if (answersByQuestion.TryGetValue(question, out var existing) && existing != anchor.Response)
                continue;
            answersByQuestion[question] = anchor.Response;
            var exchange = AnchorExchange(anchor);
            samples.Add(new GeneratedSample(anchor, exchange, null));
        }

        while (samples.Count < directTarget && directAttempts < maximumAttempts)
        {
            directAttempts++;
            attempts++;
            if (!TryGenerateExchange(rotation.Next(), random, out var exchange)) continue;
            if (!IsCompatible(exchange, answersByQuestion)) continue;
            var record = new TrainingRecord($"PLAYER {exchange.Question}", exchange.Answer);
            if (!TryAdd(record, answersByInput)) continue;
            Remember(exchange, answersByQuestion);
            samples.Add(new GeneratedSample(record, exchange, null));
        }

        var directCount = samples.Count;
        var historyAttempts = 0;
        var anchorHistoryTarget = Math.Min(historyTarget, (anchors?.Count ?? 0) * 5);
        var anchorIndex = 0;
        while (samples.Count - directCount < anchorHistoryTarget && historyAttempts < maximumAttempts)
        {
            historyAttempts++;
            attempts++;
            if (!TryGenerateExchange(rotation.Next(), random, out var prior)) continue;

            var anchor = anchors![anchorIndex % anchors.Count];
            var current = AnchorExchange(anchor);
            if (!IsCompatible(prior, answersByQuestion)) continue;

            var record = new TrainingRecord(
                $"PLAYER {prior.Question} NPC {prior.Answer} PLAYER {current.Question}",
                current.Answer);
            try
            {
                Validate(record);
            }
            catch (InvalidDataException)
            {
                continue;
            }
            if (!TryAdd(record, answersByInput)) continue;
            Remember(prior, answersByQuestion);
            samples.Add(new GeneratedSample(record, current, prior));
            anchorIndex++;
        }

        while (samples.Count - directCount < historyTarget && historyAttempts < maximumAttempts)
        {
            historyAttempts++;
            attempts++;
            if (!TryGenerateExchange(rotation.Next(), random, out var prior) ||
                !TryGenerateExchange(rotation.Next(), random, out var current) ||
                prior.Intent == current.Intent)
            {
                continue;
            }

            if (!IsCompatible(prior, answersByQuestion) ||
                !IsCompatible(current, answersByQuestion))
            {
                continue;
            }

            var record = new TrainingRecord(
                $"PLAYER {prior.Question} NPC {prior.Answer} PLAYER {current.Question}",
                current.Answer);
            try
            {
                Validate(record);
            }
            catch (InvalidDataException)
            {
                continue;
            }
            if (!TryAdd(record, answersByInput)) continue;
            Remember(prior, answersByQuestion);
            Remember(current, answersByQuestion);
            samples.Add(new GeneratedSample(record, current, prior));
        }

        var historyCount = samples.Count - directCount;
        Shuffle(samples, random);
        return new GenerationBatch(samples, requestedCount, directCount, historyCount, attempts);
    }

    internal static bool TryGenerateExchange(IntentModel model, Random random, out GeneratedExchange exchange)
    {
        exchange = null!;
        var bindings = model.Corpus.Slots.ToDictionary(
            x => x.Key,
            x => x.Value[random.Next(x.Value.Length)],
            StringComparer.Ordinal);

        var rawQuestion = model.Questions.Generate(random, MaximumQuestionWords, out var questionTerminated);
        var rawAnswer = model.Answers.Generate(random, MaximumAnswerWords, out var answerTerminated);
        if (!questionTerminated || !answerTerminated || rawQuestion is null || rawAnswer is null) return false;

        var question = Resolve(rawQuestion, bindings);
        var answer = Resolve(rawAnswer, bindings);
        if (!IsQualityText(question, 1, MaximumQuestionWords) ||
            !IsQualityText(answer, 1, MaximumAnswerWords) ||
            question == answer)
        {
            return false;
        }

        try
        {
            Validate(new TrainingRecord($"PLAYER {question}", answer));
        }
        catch (InvalidDataException)
        {
            return false;
        }

        exchange = new GeneratedExchange(
            model.Corpus.Name,
            question,
            answer,
            rawQuestion,
            rawAnswer,
            bindings,
            !model.QuestionSeeds.Contains(rawQuestion) || !model.AnswerSeeds.Contains(rawAnswer));
        return true;
    }

    internal static TrainingRecord Validate(TrainingRecord record)
    {
        ValidateText(record.Input, nameof(record.Input));
        ValidateText(record.Response, nameof(record.Response));
        return record;
    }

    private static bool TryAdd(TrainingRecord record, Dictionary<string, string> answersByInput)
    {
        try
        {
            Validate(record);
        }
        catch (InvalidDataException)
        {
            return false;
        }

        if (answersByInput.ContainsKey(record.Input)) return false;
        answersByInput.Add(record.Input, record.Response);
        return true;
    }

    private static bool IsCompatible(
        GeneratedExchange exchange,
        IReadOnlyDictionary<string, string> answersByQuestion) =>
        !answersByQuestion.TryGetValue(exchange.Question, out var answer) || answer == exchange.Answer;

    private static void Remember(GeneratedExchange exchange, IDictionary<string, string> answersByQuestion) =>
        answersByQuestion[exchange.Question] = exchange.Answer;

    private static GeneratedExchange AnchorExchange(TrainingRecord anchor)
    {
        var question = anchor.Input["PLAYER ".Length..];
        return new GeneratedExchange(
            "ANCHOR", question, anchor.Response, question, anchor.Response,
            new Dictionary<string, string>(), false);
    }

    private static string Resolve(string text, IReadOnlyDictionary<string, string> bindings)
    {
        foreach (var binding in bindings)
            text = text.Replace($"{{{binding.Key}}}", binding.Value, StringComparison.Ordinal);
        return text;
    }

    private static bool IsQualityText(string text, int minimumWords, int maximumWords)
    {
        if (text.Contains('{') || text.Contains('}')) return false;
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < minimumWords || words.Length > maximumWords) return false;
        if (words.Any(x => x is "PLAYER" or "NPC")) return false;
        for (var i = 1; i < words.Length; i++)
            if (words[i] == words[i - 1]) return false;
        return true;
    }

    private static void ValidateText(string text, string field)
    {
        if (string.IsNullOrEmpty(text)) throw new InvalidDataException($"{field} cannot be empty.");
        if (text.Length > 256) throw new InvalidDataException($"{field} exceeds 256 characters.");
        if (text[0] == ' ' || text[^1] == ' ' || text.Contains("  ", StringComparison.Ordinal))
            throw new InvalidDataException($"{field} contains invalid whitespace.");
        if (text.Any(character => character != ' ' && character is not (>= 'A' and <= 'Z') and not (>= '0' and <= '9')))
            throw new InvalidDataException($"{field} contains unsupported characters.");
    }

    private static void Shuffle<T>(IList<T> values, Random random)
    {
        for (var i = values.Count - 1; i > 0; i--)
        {
            var other = random.Next(i + 1);
            (values[i], values[other]) = (values[other], values[i]);
        }
    }

    internal sealed class IntentModel
    {
        public IntentModel(IntentCorpus corpus)
        {
            Corpus = corpus;
            Questions = new MarkovChain(corpus.Questions);
            var answerSeeds = Templates.CanonicalAnswers.TryGetValue(corpus.Name, out var canonical)
                ? new[] { canonical }
                : corpus.Answers;
            Answers = new MarkovChain(answerSeeds);
            QuestionSeeds = corpus.Questions.ToHashSet(StringComparer.Ordinal);
            AnswerSeeds = answerSeeds.ToHashSet(StringComparer.Ordinal);
        }

        public IntentCorpus Corpus { get; }
        public MarkovChain Questions { get; }
        public MarkovChain Answers { get; }
        public HashSet<string> QuestionSeeds { get; }
        public HashSet<string> AnswerSeeds { get; }
    }

    private sealed class IntentRotation
    {
        private readonly IntentModel[] _models;
        private readonly Random _random;
        private int _index;

        public IntentRotation(IntentModel[] models, Random random)
        {
            _models = [.. models];
            _random = random;
            Shuffle(_models, _random);
        }

        public IntentModel Next()
        {
            if (_index == _models.Length)
            {
                Shuffle(_models, _random);
                _index = 0;
            }
            return _models[_index++];
        }
    }
}

internal static class SelfTests
{
    public static void Run()
    {
        var tests = new (string Name, Action Test)[]
        {
            ("MARKOV", Markov),
            ("GENERATION", Generation),
            ("SHORTFALL", Shortfall),
            ("OPTIONS", Options),
            ("OUTPUT", Output)
        };

        foreach (var (name, test) in tests)
        {
            test();
            Console.WriteLine($"PASS {name}");
        }
        Console.WriteLine($"PASS ALL {tests.Length} TESTS");
    }

    private static void Markov()
    {
        var chain = new MarkovChain(["A B C", "A B C", "A B D"]);
        var starts = chain.GetTransitions(MarkovChain.StartMarker, MarkovChain.StartMarker);
        var branch = chain.GetTransitions("A", "B");
        Assert(starts.Count == 1 && starts["A"] == 3, "start transition weights are wrong");
        Assert(branch["C"] == 2 && branch["D"] == 1, "trigram transition weights are wrong");

        var firstRandom = new Random(42);
        var secondRandom = new Random(42);
        var first = Enumerable.Range(0, 20).Select(index => chain.Generate(firstRandom, 5, out _)).ToArray();
        var second = Enumerable.Range(0, 20).Select(index => chain.Generate(secondRandom, 5, out _)).ToArray();
        Assert(first.SequenceEqual(second), "Markov generation is not deterministic");
        Assert(first.All(x => x is "A B C" or "A B D"), "Markov chain emitted an invalid sentence");
        Assert(chain.Generate(new Random(1), 2, out var terminated) is null && !terminated,
            "unterminated generation was accepted");
    }

    private static void Generation()
    {
        var first = TrainingDataGenerator.BuildRecords(42, 2_000, Templates.Intents, Templates.Anchors);
        var second = TrainingDataGenerator.BuildRecords(42, 2_000, Templates.Intents, Templates.Anchors);
        var different = TrainingDataGenerator.BuildRecords(43, 2_000, Templates.Intents, Templates.Anchors);

        Assert(first.WrittenCount == 2_000, $"default generation wrote {first.WrittenCount} records");
        Assert(first.DirectCount == 1_000 && first.HistoryCount == 1_000, "direct/history split is not even");
        Assert(first.Samples.Select(x => x.Record).SequenceEqual(second.Samples.Select(x => x.Record)),
            "same seed did not produce identical records");
        Assert(!first.Samples.Select(x => x.Record).SequenceEqual(different.Samples.Select(x => x.Record)),
            "different seed did not change generated records");

        var records = first.Samples.Select(x => x.Record).ToArray();
        Assert(records.Select(x => x.Input).Distinct(StringComparer.Ordinal).Count() == records.Length,
            "generated inputs are not unique");
        Assert(records.GroupBy(x => x.Input).All(x => x.Select(y => y.Response).Distinct().Count() == 1),
            "an input maps to competing answers");
        var exchanges = first.Samples.SelectMany(sample =>
            sample.Prior is null ? [sample.Current] : new[] { sample.Prior, sample.Current });
        Assert(exchanges.GroupBy(x => x.Question).All(x => x.Select(y => y.Answer).Distinct().Count() == 1),
            "a current question maps to competing answers across histories");
        Assert(Templates.Anchors.All(anchor => records.Contains(anchor)),
            "default generation omitted a canonical anchor");
        Assert(first.Samples.Any(x => x.Current.IsNovel || x.Prior?.IsNovel == true),
            "Markov chains generated no novel sentences");

        foreach (var sample in first.Samples)
        {
            TrainingDataGenerator.Validate(sample.Record);
            Assert(sample.Prior is null || sample.Prior.Intent != sample.Current.Intent,
                "history reused the same intent twice");
            VerifyBindings(sample.Current);
            if (sample.Prior is not null) VerifyBindings(sample.Prior);
        }
    }

    private static void Shortfall()
    {
        var tiny = new IntentCorpus(
            "TINY",
            ["HELLO {ADDRESS}"],
            ["HELLO {ADDRESS}"],
            new Dictionary<string, string[]> { ["ADDRESS"] = ["FRIEND"] });
        var batch = TrainingDataGenerator.BuildRecords(42, 10, [tiny]);
        Assert(batch.WrittenCount < 10, "tiny corpus did not exercise shortfall behavior");
        Assert(batch.Attempts <= 20_000, "shortfall exceeded the phase attempt limits");
    }

    private static void Options()
    {
        Assert(Cli.ParseGenerateOptions([]) == new GenerateOptions(), "default options changed");
        Assert(Cli.ParseGenerateOptions(["--output", "custom.jsonl", "--count", "5", "--seed", "-7"])
               == new GenerateOptions("custom.jsonl", 5, -7), "explicit options parsed incorrectly");
        AssertThrows<ArgumentException>(() => Cli.ParseGenerateOptions(["--unknown", "1"]));
        AssertThrows<ArgumentException>(() => Cli.ParseGenerateOptions(["--count"]));
        AssertThrows<ArgumentException>(() => Cli.ParseGenerateOptions(["--count", "0"]));
        AssertThrows<ArgumentException>(() => Cli.ParseGenerateOptions(["--seed", "1", "--seed", "2"]));
    }

    private static void Output()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"fishbrain-generator-{Guid.NewGuid():N}");
        var firstPath = Path.Combine(directory, "first.jsonl");
        var secondPath = Path.Combine(directory, "second.jsonl");

        try
        {
            TrainingDataGenerator.Generate(new GenerateOptions(firstPath, 50, 42));
            TrainingDataGenerator.Generate(new GenerateOptions(secondPath, 50, 42));
            var firstBytes = File.ReadAllBytes(firstPath);
            Assert(firstBytes.SequenceEqual(File.ReadAllBytes(secondPath)), "same seed files differ");
            Assert(firstBytes.Length > 3 && !(firstBytes[0] == 0xEF && firstBytes[1] == 0xBB && firstBytes[2] == 0xBF),
                "output contains a UTF-8 BOM");
            Assert(firstBytes[^1] == (byte)'\n', "output does not end with a complete line");

            var lines = File.ReadAllLines(firstPath);
            Assert(lines.Length == 50, "output count is wrong");
            foreach (var line in lines)
            {
                using var document = JsonDocument.Parse(line);
                var properties = document.RootElement.EnumerateObject().ToArray();
                Assert(properties.Length == 2, "JSON object contains unexpected fields");
                Assert(properties.Select(x => x.Name).ToHashSet(StringComparer.Ordinal).SetEquals(["input", "response"]),
                    "JSON property names are incorrect");
            }

            TrainingDataGenerator.Generate(new GenerateOptions(firstPath, 3, 42));
            Assert(File.ReadAllLines(firstPath).Length == 3, "existing output was not replaced");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private static void VerifyBindings(GeneratedExchange exchange)
    {
        foreach (var binding in exchange.Bindings)
        {
            var placeholder = $"{{{binding.Key}}}";
            if (exchange.RawQuestion.Contains(placeholder, StringComparison.Ordinal))
                Assert(exchange.Question.Contains(binding.Value, StringComparison.Ordinal), "question slot binding was lost");
            if (exchange.RawAnswer.Contains(placeholder, StringComparison.Ordinal))
                Assert(exchange.Answer.Contains(binding.Value, StringComparison.Ordinal), "answer slot binding was lost");
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
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
