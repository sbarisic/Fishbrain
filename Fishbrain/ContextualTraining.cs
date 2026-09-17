using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fishbrain.Neural;

namespace Fishbrain;

internal static class ContextualTraining
{
    public static void Run(string corpusDirectory, string checkpointPath, int? planned, int? until)
    {
        if (File.Exists(Path.Combine(corpusDirectory, "conversation-v4.json")))
            throw new InvalidDataException("Conversation v4 requires the GPU trainer's balanced sampler and frozen public-response batches. Use the bounded conversation pilot command.");
        if (planned is { } steps && steps != 260_000) throw new ArgumentException("The contextual curriculum has exactly 260000 updates.");
        var final = until ?? 260_000;
        if (final is < 1 or > 260_000) throw new ArgumentException("Training endpoint must be within 1-260000.");
        checkpointPath = Path.GetFullPath(checkpointPath);
        Directory.CreateDirectory(Path.GetDirectoryName(checkpointPath)!);
        // A second trainer must never race checkpoint writes or consume the same sampler position.
        using var lease = new FileStream(checkpointPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var corpusHash = CorpusHash(corpusDirectory);
        var sampleWorkers = ConfiguredSampleWorkers();
        ContextualTrainer trainer;
        Dictionary<string, double> thresholds = [];
        if (File.Exists(checkpointPath))
        {
            var loaded = ContextualCheckpoint.Load(checkpointPath, DemoDialogueDomains.Merchant, training: true);
            if (loaded.Header.CorpusHash != corpusHash) throw new InvalidDataException("Training corpus differs from the checkpoint.");
            trainer = ContextualTrainer.Restore(loaded, sampleWorkers);
            thresholds = loaded.Header.ExecutionThresholds;
        }
        else
        {
            var vocabulary = BuildVocabulary(Path.Combine(corpusDirectory, "train.jsonl"));
            trainer = new(new ContextualNetwork(new(), vocabulary, DemoDialogueDomains.Merchant), sampleWorkers: sampleWorkers);
        }
        if (final <= trainer.Step) throw new ArgumentException("Training endpoint must exceed completed updates.");
        var data = TrainingData.Load(Path.Combine(corpusDirectory, "train.jsonl"), trainer.Architecture.Tokenizer).StructuredSamples;
        var validation = TrainingData.Load(Path.Combine(corpusDirectory, "validation.jsonl"), trainer.Architecture.Tokenizer).StructuredSamples;
        // Family-disjoint calibration and scoring prevent threshold selection from grading itself.
        var calibrationRows = validation.Where(x => CalibrationFamily(x.SemanticFamilyId)).ToArray();
        var validationRows = validation.Where(x => !CalibrationFamily(x.SemanticFamilyId)).ToArray();
        var bestPath = Path.ChangeExtension(checkpointPath, ".automated-candidate.fbm");
        var bestReportPath = bestPath + ".validation.json";
        var bestScore = File.Exists(bestReportPath)
            ? JsonSerializer.Deserialize<ContextualMetrics>(File.ReadAllText(bestReportPath))?.Operational.Values.Average() ?? -1
            : -1;
        if (!data.Any(x => x.Contextual?.Frames is not null) || !data.Any(x => x.Contextual?.RelevantFacts is not null))
            throw new InvalidDataException("Compile the 100000-row contextual corpus before training.");
        // Validate packability up front; never silently discard difficult training examples.
        foreach (var example in data.Concat(validation))
            _ = StructuredInput.Pack(example.Request!, trainer.Architecture.Tokenizer, trainer.Architecture.Config.ContextLength,
                example.Contextual?.RelevantFacts ?? [], trainer.Architecture.Domain);
        var language = data.Where(ContextualTrainer.ProjectResponse).ToArray();
        foreach (var example in language)
            if (trainer.Architecture.Tokenizer.Encode(example.Response!).Length + 1 > trainer.Architecture.Config.MaximumOutputTokens)
                throw new InvalidDataException($"Project response exceeds the decoder budget: {example.SemanticFamilyId}");
        var families = Families(data);
        var languageFamilies = Families(language);
        var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancel;
        var timer = Stopwatch.StartNew();
        var startStep = trainer.Step;
        try
        {
            while (trainer.Step < final && !cancellation.IsCancellationRequested)
            {
                if (File.Exists(checkpointPath + ".stop")) { cancellation.Cancel(); break; }
                var phase = ContextualTrainer.Phase(trainer.Step);
                var pool = phase is ContextualPhase.JointRealization or ContextualPhase.DecoderPolish ? languageFamilies : families;
                var batch = Enumerable.Range(0, 32).Select(i => Select(pool, checked((long)trainer.Step * 32 + i), trainer.Architecture.Config.Seed)).ToArray();
                var loss = trainer.TrainBatch(batch);
                if (trainer.Step == startStep + 1 || trainer.Step % 10 == 0)
                {
                    Console.WriteLine($"CONTEXTUAL STEP {trainer.Step} PHASE {phase} LOSS {loss:F5} SECONDS_PER_UPDATE {timer.Elapsed.TotalSeconds / (trainer.Step - startStep):F3}");
                    WriteProgress("RUNNING", loss);
                }
                // Durable progress between the more expensive 5000-update validation gates.
                if (trainer.Step % 100 == 0)
                    ContextualCheckpoint.Save(checkpointPath, trainer.Snapshot(), corpusHash, trainer.Step, thresholds, trainer);
                if (trainer.Step % 5000 == 0)
                {
                    var model = trainer.Snapshot();
                    var calibration = ContextualEvaluation.Measure(model, calibrationRows, trainer.Step, thresholds);
                    thresholds = ContextualEvaluation.Calibrate(calibration.ToolDecisions, model.Domain);
                    var result = ContextualEvaluation.Measure(model, validationRows, trainer.Step, thresholds);
                    ContextualCheckpoint.Save(checkpointPath, model, corpusHash, trainer.Step, thresholds, trainer);
                    var reportPath = checkpointPath + ".validation.json";
                    File.WriteAllText(reportPath, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
                    var score = result.Operational.Values.Average();
                    if (result.AutomatedPass && score > bestScore)
                    {
                        bestScore = score;
                        ContextualCheckpoint.Save(bestPath, model, corpusHash, trainer.Step, thresholds);
                        File.WriteAllText(bestReportPath, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
                    }
                    Console.WriteLine($"CHECKPOINT {Path.GetFullPath(checkpointPath)} AUTOMATED_PASS {result.AutomatedPass}");
                }
            }
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
            ContextualCheckpoint.Save(checkpointPath, trainer.Snapshot(), corpusHash, trainer.Step, thresholds, trainer);
            WriteProgress(trainer.Step >= final ? "ENDPOINT_REACHED" : cancellation.IsCancellationRequested ? "STOPPED" : "FAILED", null);
        }
        if (cancellation.IsCancellationRequested) Console.WriteLine("TRAINING INTERRUPTED AFTER A COMPLETE UPDATE; CHECKPOINT SAVED.");

        void WriteProgress(string status, float? loss)
        {
            var progress = new
            {
                Status = status,
                ProcessId = Environment.ProcessId,
                CompletedSteps = trainer.Step,
                RequestedEndpoint = final,
                NextPhase = trainer.Step == 260000 ? "COMPLETE" : ContextualTrainer.Phase(trainer.Step).ToString(),
                SampleWorkers = trainer.SampleWorkers,
                CorpusHash = corpusHash,
                UpdatedUtc = DateTimeOffset.UtcNow,
                Loss = loss,
                SecondsPerUpdate = trainer.Step == startStep ? (double?)null : timer.Elapsed.TotalSeconds / (trainer.Step - startStep),
                Checkpoint = checkpointPath,
                LastDurableCheckpointUtc = File.Exists(checkpointPath) ? (DateTime?)File.GetLastWriteTimeUtc(checkpointPath) : null
            };
            var path = checkpointPath + ".progress.json";
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(progress, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(path + ".tmp", path, true);
        }
    }

    internal static WordVocabulary BuildVocabulary(string path)
    {
        var existing = WordVocabulary.Build(path);
        var structure = "NAME ROLE ORIGIN HOME FAMILY OCCUPATION FACTION TRAITS UNKNOWN MOOD RAPPORT TRUST GOALS PENDING SUBJECT PREDICATE VALUE SOURCE PROVENANCE PLAYER NPC NONE APPROVED SESSION REPORTED CALLER ACTIVE COMPLETED CANCELLED";
        var additional = Tokenizer.Lex(structure).Where(x => x.Kind == LexicalTokenKind.Word).Select(x => x.Text)
            .Concat(Enum.GetNames<DialogueFactProvenance>().Select(x => x.ToUpperInvariant()));
        return new(existing.Words.Concat(additional), existing.OutputWords);
    }

    internal static TrainingExample[][] Families(IEnumerable<TrainingExample> examples) => examples.GroupBy(x => x.SemanticFamilyId, StringComparer.Ordinal)
        .OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => x.OrderBy(y => y.Input, StringComparer.Ordinal).ToArray()).ToArray();

    internal static TrainingExample Select(TrainingExample[][] families, long ordinal, int seed)
    {
        if (families.Length == 0) throw new InvalidDataException("Training phase has no eligible families.");
        var epoch = ordinal / families.Length;
        // A coprime affine permutation visits every family once per epoch without storing mutable sampler state.
        var stride = Math.Max(1, (seed * 2 + 1) % families.Length);
        while (Gcd(stride, families.Length) != 1) stride++;
        var index = (int)((ordinal % families.Length * stride + epoch % families.Length) % families.Length);
        var family = families[index];
        return family[(int)(epoch % family.Length)];
        static int Gcd(int a, int b) { while (b != 0) (a, b) = (b, a % b); return a; }
    }

    internal static string CorpusHash(string directory)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536];
        foreach (var split in new[] { "train", "validation", "test" })
        {
            using var stream = File.OpenRead(Path.Combine(directory, split + ".jsonl"));
            int count;
            while ((count = stream.Read(buffer)) > 0) hash.AppendData(buffer, 0, count);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    internal static int ConfiguredSampleWorkers()
    {
        var value = Environment.GetEnvironmentVariable("FISHBRAIN_TRAINING_WORKERS");
        if (value is null) return 1;
        return int.TryParse(value, out var workers) && workers is >= 1 and <= 6 ? workers
            : throw new ArgumentException("FISHBRAIN_TRAINING_WORKERS must be an integer within 1-6.");
    }

    internal static bool CalibrationFamily(string family) => SHA256.HashData(Encoding.UTF8.GetBytes(family))[0] < 128;
}
