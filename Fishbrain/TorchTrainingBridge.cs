using System.Security.Cryptography;
using System.Text.Json;
using Fishbrain.Neural;

namespace Fishbrain;

/// <summary>Exports canonical packed inputs and masked targets; Python never reparses dialogue roles or spans.</summary>
internal static class TorchTrainingBridge
{
    internal static void NormalizeConversation()
    {
        string? line;
        while ((line = Console.ReadLine()) is not null)
        {
            var values = JsonSerializer.Deserialize<string[]>(line) ?? [];
            Console.WriteLine(JsonSerializer.Serialize(values.Select(value =>
            {
                try { return new { Text = (string?)DialogueText.Normalize(value), Error = (string?)null }; }
                catch (ArgumentException error) { return new { Text = (string?)null, Error = (string?)error.Message }; }
            })));
        }
    }

    internal static void AuditConversation(string corpus, string report)
    {
        var model = new ContextualNetwork(new(), ContextualTraining.BuildVocabulary(Path.Combine(corpus, "train.jsonl")), DemoDialogueDomains.Merchant);
        var rejected = new List<object>();
        var histories = new Dictionary<string, object>();
        var counts = new Dictionary<string, int>();
        foreach (var split in new[] { "train", "validation", "test" })
        {
            var rows = TrainingData.Load(Path.Combine(corpus, split + ".jsonl"), model.Tokenizer).StructuredSamples;
            counts[split] = rows.Count;
            foreach (var row in rows)
            {
                if (row.Training is null) throw new InvalidDataException("Conversation v4 requires explicit eligibility metadata.");
                try
                {
                    var packed = StructuredInput.Pack(row.Request!, model.Tokenizer, 512, row.Contextual?.RelevantFacts ?? [], model.Domain);
                    if (row.Training.ResponseEligible) histories[row.Training.ExampleId] = new { Turns = packed.Utterances.Length, Tokens = packed.Tokens.Length };
                    if (row.Training.ResponseEligible && model.Tokenizer.Encode(row.Response!).Length + 1 > 64)
                        throw new ArgumentException("RESPONSE_TOKEN_BUDGET");
                }
                catch (ArgumentException error) { rejected.Add(new { Id = row.Request!.TurnId, Reason = error.Message }); }
            }
        }
        File.WriteAllText(report, JsonSerializer.Serialize(new { Counts = counts, Rejected = rejected, Histories = histories }));
    }

    internal static void CalibratePilot(string corpus, string input, string output)
    {
        var loaded = ContextualCheckpoint.Load(input);
        if (loaded.Header.CorpusHash != ContextualTraining.CorpusHash(corpus))
            throw new InvalidDataException("Pilot calibration corpus mismatch.");
        var rows = TrainingData.Load(Path.Combine(corpus, "validation.jsonl"), loaded.Model.Tokenizer).StructuredSamples
            .Where(x => x.Contextual?.Frames is not null && ContextualTraining.CalibrationFamily(x.SemanticFamilyId)).ToArray();
        var metrics = ContextualEvaluation.Measure(loaded.Model, rows, loaded.Header.CompletedSteps, loaded.Header.ExecutionThresholds);
        var thresholds = ContextualEvaluation.Calibrate(metrics.ToolDecisions, loaded.Model.Domain);
        ContextualCheckpoint.Save(output, loaded.Model, loaded.Header.CorpusHash, loaded.Header.CompletedSteps, thresholds);
        File.WriteAllText(output + ".calibration.json", JsonSerializer.Serialize(new { Rows = rows.Length, Thresholds = thresholds,
            Coverage = metrics.ToolDecisions.GroupBy(x => x.Tool).ToDictionary(g => g.Key, g => new { Candidates = g.Count(), Correct = g.Count(x => x.Correct) }),
            Method = "VALIDATION_FAMILIES_ONLY; existing minimum 30 decisions and 99% mutating / 95% read-only precision", Promoted = false }, Json));
        Console.WriteLine($"PILOT CALIBRATED {rows.Length} HELD-OUT ROWS; NO MODEL PROMOTED");
    }
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    internal sealed record Target(string Name, int Row, int Label, float Weight);
    internal sealed record MultiTarget(string Name, int[] Labels);
    internal sealed record Input(int[] Tokens, int[] Segments, int[] Current, int[][] Utterances);
    internal sealed record ClaimContext(NpcPersona Persona, DialogueFact[] Facts);
    internal sealed record GeneratedBatch(int[][] Outputs, ClaimContext[] Contexts);

    internal static void ValidateGenerated(string checkpoint)
    {
        var model = ContextualCheckpoint.Load(checkpoint).Model;
        string? line;
        while ((line = Console.ReadLine()) is not null)
        {
            var batch = JsonSerializer.Deserialize<GeneratedBatch>(line, Json) ?? throw new InvalidDataException("Missing generated batch.");
            if (batch.Outputs.Length != batch.Contexts.Length || batch.Outputs.Length > 32) throw new InvalidDataException("Invalid generated batch length.");
            var rejected = batch.Outputs.Select((outputs, i) =>
            {
                if (outputs.Length > model.Config.MaximumOutputTokens) throw new InvalidDataException("Generated token budget exceeded.");
                var text = model.Tokenizer.DetokenizeOutput(outputs);
                return !string.IsNullOrWhiteSpace(text) && !ConversationalOutputValidator.IsSafe(text, batch.Contexts[i].Persona, batch.Contexts[i].Facts, out _)
                    ? model.Tokenizer.Encode(text) : [];
            }).ToArray();
            Console.WriteLine(JsonSerializer.Serialize(rejected));
        }
    }

    internal static void Prepare(string corpus, string directory)
    {
        var conversation = File.Exists(Path.Combine(corpus, "conversation-v4.json"));
        if (conversation) ValidateConversationBinding(corpus);
        directory = Path.GetFullPath(directory);
        if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any())
            throw new ArgumentException("Use an empty output directory for a fresh GPU training dataset.");
        Directory.CreateDirectory(directory);
        var corpusHash = ContextualTraining.CorpusHash(corpus);
        var model = new ContextualNetwork(new(), ContextualTraining.BuildVocabulary(Path.Combine(corpus, "train.jsonl")), DemoDialogueDomains.Merchant);
        ContextualCheckpoint.Save(Path.Combine(directory, "initial.fbm"), model, corpusHash, 0, new Dictionary<string, double>());
        var counts = new Dictionary<string, int>();
        var hashes = new Dictionary<string, string>();
        if (conversation)
        {
            File.Copy(Path.Combine(corpus, "sampling.json"), Path.Combine(directory, "sampling.json"), false);
        }
        foreach (var split in new[] { "train", "validation", "test" })
        {
            var examples = TrainingData.Load(Path.Combine(corpus, split + ".jsonl"), model.Tokenizer).StructuredSamples;
            var path = Path.Combine(directory, split + ".jsonl");
            using (var writer = new StreamWriter(path))
                foreach (var example in examples) writer.WriteLine(JsonSerializer.Serialize(Pack(model, example), Json));
            counts[split] = examples.Count;
            using var stream = File.OpenRead(path);
            hashes[split] = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            Console.WriteLine($"TORCH PACKED {split} {examples.Count}");
        }
        File.WriteAllText(Path.Combine(directory, "manifest.json"), JsonSerializer.Serialize(new
        {
            Format = conversation ? 4 : 1,
            CorpusHash = corpusHash,
            Config = model.Config,
            Counts = counts,
            SplitHashes = hashes,
            HeadSizes = ContextualNetwork.HeadSizes,
            MaskSegment = (int)InputSegment.Mask,
            UnknownToken = Tokenizer.Unknown,
            BosToken = Tokenizer.Bos,
            EosToken = Tokenizer.Eos,
            OutputToInput = Enumerable.Range(0, model.Vocabulary.OutputSize).Select(model.Vocabulary.InputIdFromOutput).ToArray(),
            GeneratedOutputs = model.Vocabulary.GeneratedTextOutputs.Order().ToArray(),
            InitialRandomState = new DeterministicRandom(42).State,
            Sampler = conversation ? "CONVERSATION_V4_BALANCED" : "COPRIME_FAMILY_PERMUTATION_MEMBER_ROTATION",
            CorpusMetadataHash = conversation ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(corpus, "conversation-v4.json")))).ToLowerInvariant() : null
            , SamplingHash = conversation ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(corpus, "sampling.json")))).ToLowerInvariant() : null
        }, Json));
    }

    internal static void ValidateConversationBinding(string corpus)
    {
        using var metadata = JsonDocument.Parse(File.ReadAllText(Path.Combine(corpus, "conversation-v4.json")));
        var root = metadata.RootElement;
        if (root.GetProperty("schema").GetString() != "conversation-v4" ||
            root.GetProperty("preparationStatus").GetString() != "READY_FOR_DATA_REVIEW")
            throw new InvalidDataException("Conversation corpus did not pass preparation gates.");
        foreach (var split in new[] { "train", "validation", "test" })
            Verify(split + ".jsonl", root.GetProperty("splitHashes").GetProperty(split).GetString());
        Verify("sampling.json", root.GetProperty("samplingHash").GetString());
        void Verify(string file, string? expected)
        {
            using var stream = File.OpenRead(Path.Combine(corpus, file));
            var actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            if (expected != actual) throw new InvalidDataException($"Reviewed conversation corpus changed: {file}.");
        }
    }

    internal static object Pack(ContextualNetwork model, TrainingExample example)
    {
        var request = example.Request ?? throw new InvalidDataException("GPU training requires structured requests.");
        var facts = request.PlayerProfile.Facts.Concat(request.State.SessionFacts).Distinct().ToArray();
        var selected = example.Contextual?.RelevantFacts ?? [];
        var input = StructuredInput.Pack(request, model.Tokenizer, model.Config.ContextLength, selected, model.Domain);
        var targets = new List<Target>();
        var multi = new List<MultiTarget>();
        var supervised = example.SupervisedHeads;
        void Cat(string name, int label, int row = 0, float weight = 1) => targets.Add(new(name, row, label, weight));
        void Head(string name, int label) { if (supervised.Contains(name)) Cat("head." + name, label); }
        void Multi(string name, IEnumerable<int> labels) { if (supervised.Contains(name)) multi.Add(new("head." + name, labels.ToArray())); }
        Multi("speechActs", example.SpeechActs.Select(x => (int)x)); Multi("domains", example.Domains.Select(x => (int)x));
        Multi("goals", example.Goals.Select(x => (int)x)); Multi("content", example.ContentFlags.Select(x => (int)x));
        Head("affect", (int)example.Affect); Head("stance", (int)example.Stance); Head("policy", (int)example.Policy);
        Head("knowledgeTarget", (int)example.KnowledgeTarget); Head("discourseAct", (int)example.Discourse.Act);
        Head("discourseSubject", (int)example.Discourse.Subject); Head("discourseTarget", (int)example.Discourse.Target);
        Head("factKind", example.Discourse.FactKind is { } kind ? (int)kind + 1 : 0); Head("factPolarity", example.Discourse.Negated ? 1 : 0);
        if (supervised.Contains("antecedent")) Cat("antecedents", Antecedent(example.Discourse.AntecedentUtterance));
        var sources = input.CurrentPositions.Select((position, row) => (Source: input.Sources[position], Row: row))
            .DistinctBy(x => (x.Source.Start, x.Source.Length)).ToArray();
        foreach (var (source, row) in sources)
        {
            if (supervised.Contains("slots"))
            {
                var slot = example.Slots.FirstOrDefault(x => source.Start >= x.Start && source.Start < x.Start + x.Length);
                Cat("slots", slot is null ? 0 : 1 + (int)slot.Type * 2 + (source.Start == slot.Start ? 0 : 1), row, 1f / sources.Length);
            }
            if (supervised.Contains("factSpan")) Cat("factSpans", SpanLabel(example.Discourse.FactValueSpan, source), row, 1f / sources.Length);
        }
        if (example.Contextual?.Frames is { } frames)
            for (var i = 0; i < 3; i++)
            {
                var prefix = $"frame.{i}.";
                Cat(prefix + "active", i < frames.Length ? 1 : 0, weight: 1f / 3);
                if (i >= frames.Length) continue;
                var frame = frames[i];
                var w = 1f / frames.Length;
                if (frame.Fact is { } fact)
                {
                    foreach (var (name, label) in new[] { ("factAct", (int)fact.Act), ("factKind", fact.FactKind is { } k ? (int)k + 1 : 0),
                        ("factPolarity", fact.Negated ? 1 : 0), ("factSubject", (int)fact.Subject), ("factTarget", (int)fact.Target) }) Cat(prefix + name, label, weight: w);
                    foreach (var (source, row) in sources) Cat(prefix + "factSpans", SpanLabel(fact.FactValueSpan, source), row, w / sources.Length);
                }
                foreach (var (name, label) in new[] { ("act", (int)frame.SpeechAct), ("subject", (int)frame.Subject), ("target", (int)frame.Target),
                    ("status", (int)frame.Status), ("tool", model.ToolIndex(frame.ToolName)) }) Cat(prefix + name, label, weight: w);
                var first = Array.FindIndex(input.CurrentPositions, p => input.Sources[p].Start >= frame.Start);
                var last = Array.FindLastIndex(input.CurrentPositions, p => input.Sources[p].Start + input.Sources[p].Length <= frame.Start + frame.Length);
                if (first < 0 || last < first) throw new InvalidDataException("Frame does not fit the current utterance.");
                Cat(prefix + "start", first, weight: w); Cat(prefix + "end", last, weight: w); Cat(prefix + "antecedent", Antecedent(frame.Antecedent), weight: w);
            }
        if (example.Contextual?.Plan is { } plan)
            for (var i = 0; i < 3; i++)
            {
                Cat($"plan.{i}", i < plan.Length ? (int)plan[i].Act : 0, weight: 1f / 3);
                Cat($"planFrame.{i}", i < plan.Length && plan[i].FrameIndex is { } frame ? frame + 1 : 0, weight: 1f / 3);
            }
        if (example.Contextual?.Agenda is { } agenda)
            for (var i = 0; i < 4; i++)
            {
                Cat($"agenda.{i}.kind", i < agenda.Length ? (int)agenda[i].Kind + 1 : 0, weight: .25f);
                if (i >= agenda.Length) continue;
                Cat($"agenda.{i}.status", (int)agenda[i].Status, weight: 1f / agenda.Length);
                Cat($"agenda.{i}.subject", model.AgendaSubjectIndex(agenda[i].Subject, input.Agenda), weight: 1f / agenda.Length);
            }
        int[]? memoryTargets = null;
        if (example.Contextual?.RelevantFacts is { } relevant)
        {
            memoryTargets = relevant.Select(f => Array.IndexOf(facts, f) + 1).Distinct().ToArray();
            if (memoryTargets.Length == 0) memoryTargets = [0];
        }
        var project = ContextualTrainer.ProjectResponse(example);
        var response = project ? model.Tokenizer.Encode(example.Response!).Append(Tokenizer.Eos).ToArray() : [];
        if (response.Length > model.Config.MaximumOutputTokens) throw new InvalidDataException("GPU response exceeds decoder budget.");
        return new
        {
            example.SemanticFamilyId,
            example.Input,
            example.Source,
            Calibration = ContextualTraining.CalibrationFamily(example.SemanticFamilyId),
            InputData = Packed(input),
            Retrieval = Packed(StructuredInput.Pack(request, model.Tokenizer, model.Config.ContextLength, [], model.Domain)),
            Facts = facts.Select(f => model.Tokenizer.Encode(StructuredInput.FactText(f))).ToArray(),
            MemoryTargets = memoryTargets,
            Targets = targets,
            Multi = multi,
            TeacherFrames = example.Contextual?.Frames?.Select(f => new[] { (int)f.Status, model.ToolIndex(f.ToolName) }).ToArray(),
            TeacherPlan = example.Contextual?.Plan?.Select(p => (int)p.Act).ToArray(),
            Response = response,
            ResponseTargets = response.Select(model.Vocabulary.OutputId).ToArray(),
            ClaimPositive = ContextualTrainer.ClaimPositive(example) ? model.Tokenizer.Encode(example.Response!) : [],
            ClaimNegative = ContextualTrainer.ClaimNegative(example) ? model.Tokenizer.Encode(example.RejectedResponse!) : [],
            Training = example.Training,
            ClaimContext = new { request.Persona, Facts = selected },
            ProjectResponse = project
        };

        int Antecedent(long? sequence) => Array.FindIndex(input.Utterances, u => u.Sequence == sequence) + 1;
        static int SpanLabel(DialogueTextSpan? span, TokenSource source) => span is not null && source.Start >= span.Start && source.Start < span.Start + span.Length
            ? source.Start == span.Start ? 1 : 2 : 0;
    }

    private static Input Packed(PackedInput input) => new(input.Tokens, input.Segments, input.CurrentPositions,
        input.Utterances.Select(u => Enumerable.Range(0, input.Sources.Length).Where(i => input.Sources[i].Utterance == u.Sequence && input.Sources[i].Length > 0).ToArray()).ToArray());

    internal static void Reference(string corpus, string checkpoint, string outputPath)
    {
        var loaded = ContextualCheckpoint.Load(checkpoint);
        var model = loaded.Model;
        if (loaded.Header.CorpusHash != ContextualTraining.CorpusHash(corpus)) throw new InvalidDataException("Reference corpus mismatch.");
        var all = TrainingData.Load(Path.Combine(corpus, "validation.jsonl"), model.Tokenizer).StructuredSamples;
        var selected = all.Where(x => ContextualTrainer.ProjectResponse(x)).Take(2)
            .Concat(all.Where(x => x.Contextual?.RelevantFacts?.Length > 0).Take(2))
            .Concat(all.Where(x => x.Contextual?.Frames?.Length > 1).Take(2))
            .Concat(all.Where(x => x.Contextual?.Agenda?.Length > 0).Take(2))
            .Concat(all.Where(x => x.Request!.Utterances.Count > 3).Take(2)).Distinct().ToArray();
        using var writer = new StreamWriter(outputPath);
        foreach (var example in selected)
        {
            var trainer = new ContextualTrainer(model, 100001);
            var input = StructuredInput.Pack(example.Request!, model.Tokenizer, model.Config.ContextLength, example.Contextual?.RelevantFacts ?? [], model.Domain);
            var prediction = model.Understand(new TensorGraph(false), trainer.Parameters, input, example.Contextual?.Frames, example.Contextual?.Plan);
            var logits = new Dictionary<string, Tensor>(prediction.Heads.ToDictionary(x => "head." + x.Key, x => x.Value))
            { ["encoded"] = prediction.Encoded, ["slots"] = prediction.Slots, ["factSpans"] = prediction.FactSpans, ["antecedents"] = prediction.Antecedents };
            for (var i = 0; i < 3; i++)
            {
                foreach (var (key, tensor) in prediction.Frames[i].Fields) logits[$"frame.{i}.{key}"] = tensor;
                logits[$"frame.{i}.start"] = prediction.Frames[i].Start; logits[$"frame.{i}.end"] = prediction.Frames[i].End;
                logits[$"frame.{i}.antecedent"] = prediction.Frames[i].Antecedent; logits[$"frame.{i}.factSpans"] = prediction.Frames[i].FactSpans;
                logits[$"plan.{i}"] = prediction.Plans[i]; logits[$"planFrame.{i}"] = prediction.PlanFrames[i];
            }
            for (var i = 0; i < 4; i++)
            {
                logits[$"agenda.{i}.kind"] = prediction.Agenda[i].Kind; logits[$"agenda.{i}.status"] = prediction.Agenda[i].Status;
                logits[$"agenda.{i}.subject"] = prediction.Agenda[i].Subject;
            }
            var losses = new Dictionary<string, object>();
            foreach (var phase in Enum.GetValues<ContextualPhase>())
            {
                if (phase is ContextualPhase.JointRealization or ContextualPhase.DecoderPolish && !ContextualTrainer.ProjectResponse(example)) continue;
                foreach (var p in trainer.Parameters.Values) Array.Clear(p.Gradient!);
                trainer.Random.State = new DeterministicRandom(42).State;
                var loss = trainer.Loss(example, phase);
                var gradients = trainer.Parameters.ToDictionary(x => x.Key, x =>
                {
                    var index = Enumerable.Range(0, x.Value.Data.Length).MaxBy(i => Math.Abs(x.Value.Gradient![i]));
                    return new { Index = index, Value = x.Value.Gradient![index] };
                });
                losses[phase.ToString()] = new { Loss = loss, Gradients = gradients };
            }
            writer.WriteLine(JsonSerializer.Serialize(new
            {
                Row = Pack(model, example),
                Logits = logits.ToDictionary(x => x.Key, x => new { x.Value.Rows, x.Value.Columns, x.Value.Data }),
                Losses = losses
            }, Json));
        }
        Console.WriteLine($"TORCH REFERENCE {selected.Length}");
    }

    internal static int Assess(string corpus, string runDirectory)
    {
        var paths = Directory.EnumerateFiles(runDirectory, "best-*.fbm")
            .Where(path => Path.GetFileName(path) != "best-MaskedLanguage.fbm")
            .Order(StringComparer.Ordinal).Append(Path.Combine(runDirectory, "latest.fbm")).ToArray();
        var hashes = new HashSet<string>();
        var corpusHash = ContextualTraining.CorpusHash(corpus);
        ContextualNetwork? selected = null;
        ContextualCheckpointHeader? selectedHeader = null;
        Dictionary<string, double>? selectedThresholds = null;
        ContextualMetrics? selectedMetrics = null;
        var best = double.NegativeInfinity;
        var assessments = new List<object>();
        foreach (var path in paths)
        {
            using (var stream = File.OpenRead(path)) if (!hashes.Add(Convert.ToHexString(SHA256.HashData(stream)))) continue;
            var loaded = ContextualCheckpoint.Load(path);
            if (loaded.Header.CorpusHash != corpusHash) throw new InvalidDataException("Candidate corpus mismatch.");
            var validation = TrainingData.Load(Path.Combine(corpus, "validation.jsonl"), loaded.Model.Tokenizer).StructuredSamples;
            Console.WriteLine($"CALIBRATING {Path.GetFileName(path)}");
            var calibration = ContextualEvaluation.Measure(loaded.Model, validation.Where(x => ContextualTraining.CalibrationFamily(x.SemanticFamilyId)).ToArray(),
                loaded.Header.CompletedSteps, loaded.Header.ExecutionThresholds);
            var thresholds = ContextualEvaluation.Calibrate(calibration.ToolDecisions, loaded.Model.Domain);
            Console.WriteLine($"VALIDATING {Path.GetFileName(path)}");
            var metrics = ContextualEvaluation.Measure(loaded.Model, validation.Where(x => !ContextualTraining.CalibrationFamily(x.SemanticFamilyId)).ToArray(),
                loaded.Header.CompletedSteps, thresholds);
            var score = (metrics.AutomatedPass ? 10 : 0) + metrics.FrameExact + metrics.PlanExact + metrics.MemoryExact + metrics.CorrectionExact + metrics.AgendaExact;
            assessments.Add(new { Path = Path.GetFullPath(path), Metrics = metrics, Thresholds = thresholds });
            if (score > best)
            {
                best = score; selected = loaded.Model; selectedHeader = loaded.Header; selectedThresholds = thresholds; selectedMetrics = metrics;
            }
        }
        if (selected is null) throw new InvalidDataException("No GPU candidates were available.");
        var candidate = Path.Combine(runDirectory, "calibrated.fbm");
        ContextualCheckpoint.Save(candidate, selected, corpusHash, selectedHeader!.CompletedSteps, selectedThresholds!);
        Console.WriteLine("EVALUATING HELD-OUT TEST SPLIT");
        var test = TrainingData.Load(Path.Combine(corpus, "test.jsonl"), selected.Tokenizer).StructuredSamples;
        var testMetrics = ContextualEvaluation.Measure(selected, test, selectedHeader.CompletedSteps, selectedThresholds!);
        File.WriteAllText(Path.Combine(runDirectory, "native-assessment.json"), JsonSerializer.Serialize(new
        {
            Candidate = Path.GetFullPath(candidate),
            Validation = assessments,
            Test = testMetrics,
            AutomatedPass = selectedMetrics!.AutomatedPass && testMetrics.AutomatedPass,
            Promoted = false,
            Selection = "BEST_PHASE_VALIDATION_LOSS_AND_FINAL_SHORTLIST; full validation snapshots retained in candidates",
            RemainingGates = new[] { "AUTHORED_ACCEPTANCE", "RESOURCES", "ABLATIONS", "TWO_HUMAN_REVIEWS", "PACKAGING" }
        }, new JsonSerializerOptions { WriteIndented = true }));
        return selectedMetrics.AutomatedPass && testMetrics.AutomatedPass ? 0 : 1;
    }
}
