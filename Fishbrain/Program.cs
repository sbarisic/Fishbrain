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
            if (args.Length == 0)
            {
                args = ["chat"];
            }

            switch (args[0].ToLowerInvariant())
            {
                case "train":
                    Count(args, 3, 4);
                    LegacyBrain.TrainNew(args[1], args[2], args.Length == 4 ? Steps(args[3]) : 260_000);
                    break;
                case "resume":
                    Count(args, 3, 4);
                    LegacyBrain.Resume(args[1], args[2], args.Length == 4 ? Steps(args[3]) : null);
                    break;
                case "teach":
                    var teaching = TeachInvocation.Parse(args[1..]);
                    ContextualTraining.Run(
                        teaching.CorpusDirectory,
                        teaching.CheckpointPath,
                        teaching.PlannedSteps,
                        teaching.UntilStep);
                    break;
                case "evaluate":
                    Count(args, 3, 5);
                    var gate = EvaluationGateParser.Parse(args[3..]);
                    if (Neural.ContextualCheckpoint.Matches(args[2])) return ContextualEvaluation.Run(args[1], args[2]);
                    return Evaluation.Run(args[1], args[2], gate);
                case "diagnose-teaching":
                    Count(args, 3, 4);
                    Console.WriteLine(LegacyBrain.DiagnoseTeaching(args[1], args[2],
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
                case "profile-contextual":
                    Count(args, 2, 3);
                    return ContextualPerformance.Run(args[1], args.Length == 3 ? Steps(args[2]) : 32);
                case "benchmark-training":
                    Count(args, 4, 5);
                    ContextualTrainingBenchmark.Run(args[1], args[2], args[3], args.Length == 5 ? Steps(args[4]) : 3);
                    break;
                case "prepare-torch":
                    Count(args, 3, 3);
                    TorchTrainingBridge.Prepare(args[1], args[2]);
                    break;
                case "torch-reference":
                    Count(args, 4, 4);
                    TorchTrainingBridge.Reference(args[1], args[2], args[3]);
                    break;
                case "validate-generated":
                    Count(args, 2, 2);
                    TorchTrainingBridge.ValidateGenerated(args[1]);
                    break;
                case "assess-torch":
                    Count(args, 3, 3);
                    return TorchTrainingBridge.Assess(args[1], args[2]);
                case "acceptance-contextual":
                    Count(args, 3, 3);
                    return ContextualAcceptance.Run(args[1], args[2]);
                case "compare-contextual":
                    Count(args, 4, 5);
                    ContextualComparison.Run(args[1], args[2], args[3], args.Length == 5 ? Steps(args[4]) : 3);
                    break;
                case "artifact-smoke":
                    Count(args, 1, 2);
                    var shipped = Brain.Load(args.Length == 2 ? args[1] : ResolveDefaultModel());
                    if (shipped.ContextualConfig is null) throw new InvalidDataException("The shipped artifact must use the contextual architecture.");
                    _ = shipped.Reply(new ReplyRequest("ARTIFACT_SMOKE", "1", [new(0, DialogueRole.Player, "HELLO")],
                        NpcDialogueState.Initial, NpcPersona.Default, PlayerConversationProfile.Empty, 1, 42), DemoGameTools.CreateMerchant());
                    Console.WriteLine("PASS SHIPPED CONTEXTUAL ARTIFACT");
                    break;
                case "conversation-sample":
                    Count(args, 4, 4);
                    ConversationEvaluation.Export(args[1], args[2], args[3]);
                    break;
                case "conversation-gate":
                    Count(args, 3, 3);
                    return ConversationEvaluation.Gate(args[1], args[2]);
                case "selftest":
                    Count(args, 1, 1);
                    SelfTests.Run();
                    break;
                default:
                    Usage();
                    return 1;
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
        var history = new List<DialogueUtterance>();
        var tools = DemoGameTools.CreateMerchant();
        var conversationId = "CLI-" + Guid.NewGuid().ToString("N");
        var turn = 0;
        long sequence = 0;
        Console.WriteLine("ENTER DIALOGUE OR AN EMPTY LINE TO QUIT");
        while (true)
        {
            Console.Write("> ");
            var input = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(input)) return;
            history.Add(new DialogueUtterance(++sequence, DialogueRole.Player, input));
            var result = brain.Reply(new ReplyRequest(conversationId,
                (++turn).ToString(CultureInfo.InvariantCulture), history, state, NpcPersona.Default,
                PlayerConversationProfile.Empty, sequence + 1, turn), tools);
            state = result.State;
            Console.WriteLine(result.Text.Length == 0 ? "[NO RESPONSE]" : result.Text);
            Console.WriteLine(
                $"STATE RAPPORT={state.Rapport} TRUST={state.Trust} HOSTILITY={state.Hostility} MOOD={Upper(state.Mood)} " +
                $"ACTS={string.Join(',', result.Perception.SpeechActs.Select(Upper))} " +
                $"DOMAINS={string.Join(',', result.Perception.Domains.Select(Upper))} " +
                $"AFFECT={Upper(result.Perception.Affect)} POLICY={Upper(result.Perception.Policy)} " +
                $"SOURCE={Upper(result.Diagnostics.ResponseSource)} TONE={Upper(result.Tone)}");
            if (result.Text.Length > 0)
                history.Add(new DialogueUtterance(++sequence, DialogueRole.Npc, result.Text));
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
            [new DialogueUtterance(0, DialogueRole.Player, inputs[index % inputs.Length])], NpcDialogueState.Initial,
            NpcPersona.Default, PlayerConversationProfile.Empty, 1, index), tools);
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
    {
        if (args.Length < minimum || args.Length > maximum)
        {
            throw new ArgumentException("Invalid command arguments.");
        }
    }
    private static void Usage()
    {
        Console.WriteLine("FISHBRAIN");
        Console.WriteLine("  train DATA.jsonl CHECKPOINT.json [STEPS]");
        Console.WriteLine("  resume DATA.jsonl CHECKPOINT.json [TOTAL_STEPS]");
        Console.WriteLine("  teach CORPUS_DIRECTORY CHECKPOINT.fbm [--planned 260000] [--until STEP]");
        Console.WriteLine("  evaluate TEST.jsonl CHECKPOINT.json [--gate none|pilot|stage|release]");
        Console.WriteLine("  diagnose-teaching CORPUS_DIRECTORY TRAINING_CHECKPOINT.json [validation|test]");
        Console.WriteLine("  chat [CHECKPOINT]  (default: data/models/model-latest.fbm)");
        Console.WriteLine("  latency [CHECKPOINT] [ITERATIONS]");
        Console.WriteLine("  profile-contextual CHECKPOINT [ITERATIONS]");
        Console.WriteLine("  compare-contextual CORPUS_DIRECTORY CHECKPOINT REPORT.json [LEXICAL_EPOCHS]");
        Console.WriteLine("  artifact-smoke [CHECKPOINT]");
        Console.WriteLine("  export TRAINING_CHECKPOINT.json OUTPUT.fbm [CORPUS_DIRECTORY]");
        Console.WriteLine("  inspect MODEL.fbm");
        Console.WriteLine("  conversation-sample MODEL.fbm SCENARIOS.jsonl REVIEW.jsonl");
        Console.WriteLine("  conversation-gate SAMPLE.jsonl REVIEWED.jsonl");
        Console.WriteLine("  acceptance-contextual MODEL.fbm REPORT.json");
        Console.WriteLine("  selftest");
        Console.WriteLine("  benchmark-training CORPUS_DIRECTORY CHECKPOINT REPORT.json [UPDATES_PER_PHASE]");
        Console.WriteLine("  prepare-torch CORPUS_DIRECTORY EMPTY_OUTPUT_DIRECTORY");
        Console.WriteLine("  torch-reference CORPUS_DIRECTORY MODEL.fbm OUTPUT.jsonl");
        Console.WriteLine("  validate-generated MODEL.fbm  (JSON lines on stdin)");
        Console.WriteLine("  assess-torch CORPUS_DIRECTORY GPU_RUN_DIRECTORY");
    }

    private static string FindProjectPath()
    {
        return ResolveRepositoryFile("Fishbrain", "Fishbrain.csproj");
    }

    private static string ResolveDefaultModel()
    {
        return ResolveRepositoryFile("data", "models", "model-latest.fbm");
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
