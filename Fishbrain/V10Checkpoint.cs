using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Fishbrain;

public sealed partial class Brain
{
    private static readonly byte[] V11Magic = "FISHBRN11\n"u8.ToArray();

    internal void ExportInference(string path, string corpusHash = "UNKNOWN")
    {
        SyncScalarWeights();
        var header = new V11InferenceHeader
        {
            Format = "FISHBRAIN INFERENCE MODEL",
            Version = 11,
            Config = Config,
            CompletedSteps = _step,
            CorpusHash = corpusHash,
            Words = _vocabulary.Words,
            OutputWords = _vocabulary.OutputWords,
            TrainedTools = _trainedTools.Order(StringComparer.Ordinal).ToArray(),
            ResponseCatalog = V11ResponseCatalog.SurfaceCatalog.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal),
            LabelSchemas = V11Schemas.Labels,
            ConfidenceCalibration = _confidenceCalibration,
            ToolSchemas = DemoGameTools.CreateMerchant().Schemas.OrderBy(schema => schema.Name).ToArray(),
            CandidateCatalog = V11Candidates.OrderBy(candidate => candidate.Id).ToArray(),
            TransformerWeightCount = _weights.Length,
            StructuredWeightCount = _structuredHeads.WeightCount,
            StructuredUpdates = _structuredHeads.Updates,
            StructuredLabelThresholds = _structuredHeads.SnapshotLabelThresholds(),
            WeightEncoding = "IEEE754_FLOAT32_LITTLE_ENDIAN"
        };
        var structuredWeights = _structuredHeads.Snapshot();
        var weightBytes = new byte[(_weights.Length + structuredWeights.Length) * sizeof(float)];
        using (var stream = new MemoryStream(weightBytes, writable: true))
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            foreach (var weight in _weights) writer.Write((float)weight);
            foreach (var weight in structuredWeights) writer.Write((float)weight);
        }
        header.WeightsSha256 = Convert.ToHexString(SHA256.HashData(weightBytes)).ToLowerInvariant();
        var headerBytes = JsonSerializer.SerializeToUtf8Bytes(header, new JsonSerializerOptions { WriteIndented = true });
        var integrity = CombinedSha256(headerBytes, weightBytes);

        var temporary = path + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false))
        {
            writer.Write(V11Magic);
            writer.Write(headerBytes.Length);
            writer.Write(headerBytes);
            writer.Write(weightBytes);
            writer.Write(integrity);
        }
        File.Move(temporary, path, true);
    }

    internal static bool IsInferenceCheckpoint(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length < V11Magic.Length) return false;
        var magic = new byte[V11Magic.Length];
        stream.ReadExactly(magic);
        return magic.SequenceEqual(V11Magic);
    }

    internal static bool HasFishbrainBinaryPrefix(string path)
    {
        ReadOnlySpan<byte> prefix = "FISHBRN"u8;
        using var stream = File.OpenRead(path);
        if (stream.Length < prefix.Length) return false;
        Span<byte> actual = stackalloc byte[prefix.Length];
        stream.ReadExactly(actual);
        return actual.SequenceEqual(prefix);
    }

    private static Brain LoadInferenceCheckpoint(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
        if (!reader.ReadBytes(V11Magic.Length).SequenceEqual(V11Magic))
            throw new InvalidDataException("Invalid Fishbrain inference checkpoint magic.");
        var headerLength = reader.ReadInt32();
        if (headerLength is <= 0 or > 16_777_216) throw new InvalidDataException("Invalid checkpoint header length.");
        var headerBytes = reader.ReadBytes(headerLength);
        if (headerBytes.Length != headerLength) throw new EndOfStreamException("Checkpoint header is truncated.");
        var header = JsonSerializer.Deserialize<V11InferenceHeader>(headerBytes)
            ?? throw new InvalidDataException("Checkpoint header is empty.");
        if (header.Format != "FISHBRAIN INFERENCE MODEL" || header.Version != 11 ||
            header.WeightEncoding != "IEEE754_FLOAT32_LITTLE_ENDIAN")
            throw new InvalidDataException("Unsupported Fishbrain inference checkpoint.");
        if (header.Config is null || header.Words is null || header.OutputWords is null ||
            header.TrainedTools is null || header.ResponseCatalog is null || header.LabelSchemas is null ||
            header.ConfidenceCalibration is null || header.ToolSchemas is null || header.CandidateCatalog is null ||
            header.StructuredLabelThresholds is null)
            throw new InvalidDataException("Checkpoint header contains null schema data.");
        header.Config.Validate();
        if (header.CompletedSteps < 0 || header.CompletedSteps > header.Config.PlannedSteps)
            throw new InvalidDataException("Checkpoint completed-step count is invalid.");
        ValidateCorpusHash(header.CorpusHash);
        ValidateVocabularyArrays(header.Words, header.OutputWords);
        ValidateTrainedTools(header.TrainedTools);
        ValidateResponseCatalog(header.ResponseCatalog, V11ResponseCatalog.SurfaceCatalog);
        V11Schemas.Validate(header.LabelSchemas, header.ConfidenceCalibration);
        if (header.CandidateCatalog.Any(candidate => candidate is null) ||
            !SchemaEquals(header.CandidateCatalog.OrderBy(candidate => candidate.Id).ToArray(),
                V11Candidates.OrderBy(candidate => candidate.Id).ToArray()))
            throw new InvalidDataException("Checkpoint response candidate schema does not match this runtime.");
        var expectedTools = DemoGameTools.CreateMerchant().Schemas.OrderBy(schema => schema.Name).ToArray();
        if (header.ToolSchemas.Any(schema => schema is null) ||
            !SchemaEquals(header.ToolSchemas.OrderBy(schema => schema.Name).ToArray(), expectedTools))
            throw new InvalidDataException("Checkpoint tool schemas do not match this runtime.");
        var expectedTransformerWeights = ExpectedTransformerWeightCount(header.Config,
            header.Words.Length, header.OutputWords.Length);
        if (header.TransformerWeightCount != expectedTransformerWeights || header.StructuredWeightCount <= 0 ||
            header.StructuredWeightCount > 10_000_000)
            throw new InvalidDataException("Checkpoint parameter count is invalid.");
        var weightByteCount = checked(checked(header.TransformerWeightCount + header.StructuredWeightCount) * sizeof(float));
        var weightBytes = reader.ReadBytes(weightByteCount);
        if (weightBytes.Length != weightByteCount) throw new EndOfStreamException("Checkpoint weights are truncated.");
        var integrity = reader.ReadBytes(32);
        if (integrity.Length != 32 || stream.Position != stream.Length)
            throw new InvalidDataException("Checkpoint has a missing checksum or trailing data.");
        if (!CombinedSha256(headerBytes, weightBytes).SequenceEqual(integrity))
            throw new InvalidDataException("Checkpoint integrity checksum failed.");
        if (header.WeightsSha256 is null || header.WeightsSha256.Length != 64 ||
            !Convert.ToHexString(SHA256.HashData(weightBytes)).Equals(header.WeightsSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Checkpoint weight checksum failed.");

        var vocabulary = new WordVocabulary(header.Words, header.OutputWords);
        var brain = new Brain(header.Config, vocabulary, new DeterministicRandom(header.Config.Seed),
            header.TrainedTools, [], header.ResponseCatalog);
        if (brain._weights.Length != header.TransformerWeightCount ||
            brain._structuredHeads.WeightCount != header.StructuredWeightCount)
            throw new InvalidDataException("Checkpoint parameter count does not match its architecture.");
        var structured = new double[header.StructuredWeightCount];
        using (var weights = new MemoryStream(weightBytes, writable: false))
        using (var weightReader = new BinaryReader(weights, Encoding.UTF8, leaveOpen: false))
        {
            for (var index = 0; index < brain._weights.Length; index++)
            {
                var value = weightReader.ReadSingle();
                if (!float.IsFinite(value)) throw new InvalidDataException("Checkpoint contains a non-finite transformer weight.");
                brain._weights[index] = value;
            }
            for (var index = 0; index < structured.Length; index++)
            {
                var value = weightReader.ReadSingle();
                if (!float.IsFinite(value)) throw new InvalidDataException("Checkpoint contains a non-finite structured weight.");
                structured[index] = value;
            }
        }
        brain._structuredHeads.Restore(structured, header.StructuredUpdates, header.StructuredLabelThresholds);
        brain._confidenceCalibration = header.ConfidenceCalibration;
        brain._corpusHash = header.CorpusHash;
        brain._scalarWeightsCurrent = false;
        brain.SyncScalarWeights();
        brain._adamM = new double[brain._weights.Length];
        brain._adamV = new double[brain._weights.Length];
        brain._step = header.CompletedSteps;
        brain._curriculumPhase = "INFERENCE_ONLY";
        return brain;
    }

    internal static string InspectInferenceCheckpoint(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
        if (!reader.ReadBytes(V11Magic.Length).SequenceEqual(V11Magic))
            throw new InvalidDataException("Invalid Fishbrain inference checkpoint magic.");
        var length = reader.ReadInt32();
        if (length is <= 0 or > 16_777_216) throw new InvalidDataException("Invalid checkpoint header length.");
        var headerBytes = reader.ReadBytes(length);
        var header = JsonSerializer.Deserialize<V11InferenceHeader>(headerBytes)
            ?? throw new InvalidDataException("Checkpoint header is empty.");
        _ = LoadInferenceCheckpoint(path);
        return JsonSerializer.Serialize(new
        {
            header.Format,
            header.Version,
            header.CompletedSteps,
            header.CorpusHash,
            CheckpointSha256 = FileSha256(path),
            FileBytes = stream.Length,
            header.Config,
            VocabularyWords = header.Words.Length,
            OutputWords = header.OutputWords.Length,
            LabelSchemas = header.LabelSchemas.ToDictionary(item => item.Key, item => item.Value.Length),
            ConfidenceCalibration = header.ConfidenceCalibration,
            ToolSchemas = header.ToolSchemas.Select(schema => schema.Name).ToArray(),
            ResponseCandidates = header.CandidateCatalog.Select(candidate => candidate.Id).ToArray(),
            header.TransformerWeightCount,
            header.StructuredWeightCount,
            header.StructuredUpdates,
            header.StructuredLabelThresholds,
            header.WeightEncoding,
            header.WeightsSha256,
            Integrity = "VALID"
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    private sealed class V11InferenceHeader
    {
        public string Format { get; set; } = "";
        public int Version { get; set; }
        public BrainConfig Config { get; set; } = new();
        public int CompletedSteps { get; set; }
        public string CorpusHash { get; set; } = "";
        public string[] Words { get; set; } = [];
        public string[] OutputWords { get; set; } = [];
        public string[] TrainedTools { get; set; } = [];
        public Dictionary<string, string[]> ResponseCatalog { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, string[]> LabelSchemas { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, V11Schemas.ConfidenceThreshold> ConfidenceCalibration { get; set; } = new(StringComparer.Ordinal);
        public ToolSchema[] ToolSchemas { get; set; } = [];
        public ResponseCandidate[] CandidateCatalog { get; set; } = [];
        public int TransformerWeightCount { get; set; }
        public int StructuredWeightCount { get; set; }
        public int StructuredUpdates { get; set; }
        public Dictionary<string, double> StructuredLabelThresholds { get; set; } = new(StringComparer.Ordinal);
        public string WeightEncoding { get; set; } = "";
        public string WeightsSha256 { get; set; } = "";
    }

    private static byte[] CombinedSha256(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(first);
        hash.AppendData(second);
        return hash.GetHashAndReset();
    }

    private static string FileSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static bool SchemaEquals<T>(T left, T right) =>
        JsonSerializer.Serialize(left) == JsonSerializer.Serialize(right);

    private static void ValidateVocabularyArrays(string[] words, string[] outputWords)
    {
        if (words.Any(string.IsNullOrWhiteSpace) || outputWords.Any(string.IsNullOrWhiteSpace))
            throw new InvalidDataException("Checkpoint vocabulary contains an empty word.");
        var vocabulary = new WordVocabulary(words, outputWords);
        if (!words.SequenceEqual(vocabulary.Words) || !outputWords.SequenceEqual(vocabulary.OutputWords))
            throw new InvalidDataException("Checkpoint vocabulary must be canonical, distinct, and ordinally sorted.");
    }

    private static void ValidateTrainedTools(string[] tools)
    {
        var expected = DemoGameTools.CreateMerchant().Schemas.Select(schema => schema.Name).ToHashSet(StringComparer.Ordinal);
        if (tools.Distinct(StringComparer.Ordinal).Count() != tools.Length ||
            tools.Any(tool => string.IsNullOrWhiteSpace(tool) || !expected.Contains(tool)))
            throw new InvalidDataException("Checkpoint trained-tool list is invalid.");
    }

    private static void ValidateResponseCatalog(
        IReadOnlyDictionary<string, string[]> actual,
        IReadOnlyDictionary<string, string[]> expected)
    {
        if (actual.Count != expected.Count || actual.Any(item => item.Key is null || item.Value is null) ||
            !expected.All(item => actual.TryGetValue(item.Key, out var values) && values.SequenceEqual(item.Value)))
            throw new InvalidDataException("Checkpoint response catalog does not match this runtime.");
    }

    private static void ValidateCorpusHash(string hash)
    {
        if (hash == "UNKNOWN") return;
        if (hash is null || hash.Length != 64 || hash.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidDataException("Checkpoint corpus hash is invalid.");
    }

    private static int ExpectedTransformerWeightCount(BrainConfig config, int wordCount, int outputWordCount)
    {
        try
        {
            var embedding = (long)config.EmbeddingSize;
            var inputSize = checked((long)Tokenizer.WordStart + wordCount);
            var outputSize = checked((long)Tokenizer.WordStart + outputWordCount);
            var perLayer = checked(4L * embedding * embedding + 2L * config.MlpSize * embedding);
            var total = checked(inputSize * embedding + outputSize * embedding +
                (long)config.PositionPeriod * embedding + config.LayerCount * perLayer +
                (long)Enum.GetValues<DialogueIntent>().Length * embedding +
                (long)Enum.GetValues<UserAffect>().Length * embedding + 2L * embedding);
            if (total is <= 0 or > 50_000_000)
                throw new InvalidDataException("Checkpoint architecture exceeds the supported 50-million-parameter limit.");
            return (int)total;
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException("Checkpoint architecture parameter count overflowed.", exception);
        }
    }
}

internal static class V11Schemas
{
    internal sealed record ConfidenceThreshold(double Threshold, double Margin);

    public static Dictionary<string, string[]> Labels => new(StringComparer.Ordinal)
    {
        ["speechActs"] = Enum.GetNames<SpeechAct>(),
        ["domains"] = Enum.GetNames<DialogueDomain>(),
        ["goals"] = Enum.GetNames<DialogueGoal>(),
        ["affect"] = Enum.GetNames<UserAffect>(),
        ["stance"] = Enum.GetNames<DialogueStance>(),
        ["policy"] = Enum.GetNames<ResponsePolicy>(),
        ["slots"] = Enum.GetNames<SlotType>(),
        ["content"] = Enum.GetNames<ContentFlag>(),
        ["knowledgeTarget"] = Enum.GetNames<KnowledgeTarget>()
    };

    public static Dictionary<string, ConfidenceThreshold> DefaultCalibration => new(StringComparer.Ordinal)
    {
        ["speechActs"] = new(0.50, 0.10),
        ["domains"] = new(0.50, 0.10),
        ["goals"] = new(0.50, 0.10),
        ["affect"] = new(0.65, 0.12),
        ["stance"] = new(0.65, 0.12),
        ["policy"] = new(0.75, 0.15),
        ["slots"] = new(0.80, 0.10),
        ["content"] = new(0.50, 0.10),
        ["toolReadOnly"] = new(0.95, 0.05),
        ["toolMutating"] = new(0.99, 0.01),
        ["responseCandidate"] = new(0.70, 0.10),
        ["knowledgeTarget"] = new(0.85, 0.10)
    };

    public static void Validate(
        IReadOnlyDictionary<string, string[]> labels,
        IReadOnlyDictionary<string, ConfidenceThreshold> calibration)
    {
        if (labels.Count != Labels.Count)
            throw new InvalidDataException("Checkpoint label schemas contain unexpected entries.");
        foreach (var expected in Labels)
            if (!labels.TryGetValue(expected.Key, out var actual) || actual is null ||
                !actual.SequenceEqual(expected.Value))
                throw new InvalidDataException($"Checkpoint label schema '{expected.Key}' does not match this runtime.");
        if (calibration.Count != DefaultCalibration.Count)
            throw new InvalidDataException("Checkpoint confidence calibration contains unexpected entries.");
        foreach (var expected in DefaultCalibration.Keys)
            if (!calibration.ContainsKey(expected))
                throw new InvalidDataException($"Checkpoint confidence calibration '{expected}' is missing.");
        foreach (var item in calibration)
            if (item.Value is null || !double.IsFinite(item.Value.Threshold) || !double.IsFinite(item.Value.Margin) ||
                item.Value.Threshold is < 0 or > 1 || item.Value.Margin is < 0 or > 1)
                throw new InvalidDataException($"Invalid confidence calibration for '{item.Key}'.");
    }
}
