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
        var integrity = SHA256.HashData(headerBytes.Concat(weightBytes).ToArray());

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
        if (header.Version != 11 || header.WeightEncoding != "IEEE754_FLOAT32_LITTLE_ENDIAN")
            throw new InvalidDataException("Unsupported Fishbrain inference checkpoint.");
        header.Config.Validate();
        V11Schemas.Validate(header.LabelSchemas, header.ConfidenceCalibration);
        if (!header.CandidateCatalog.Select(candidate => candidate.Id)
                .SequenceEqual(V11Candidates.OrderBy(candidate => candidate.Id).Select(candidate => candidate.Id)))
            throw new InvalidDataException("Checkpoint response candidate schema does not match this runtime.");
        var weightByteCount = checked((header.TransformerWeightCount + header.StructuredWeightCount) * sizeof(float));
        var weightBytes = reader.ReadBytes(weightByteCount);
        if (weightBytes.Length != weightByteCount) throw new EndOfStreamException("Checkpoint weights are truncated.");
        var integrity = reader.ReadBytes(32);
        if (integrity.Length != 32 || stream.Position != stream.Length)
            throw new InvalidDataException("Checkpoint has a missing checksum or trailing data.");
        if (!SHA256.HashData(headerBytes.Concat(weightBytes).ToArray()).SequenceEqual(integrity))
            throw new InvalidDataException("Checkpoint integrity checksum failed.");
        if (!Convert.ToHexString(SHA256.HashData(weightBytes)).Equals(header.WeightsSha256, StringComparison.OrdinalIgnoreCase))
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
            for (var index = 0; index < brain._weights.Length; index++) brain._weights[index] = weightReader.ReadSingle();
            for (var index = 0; index < structured.Length; index++) structured[index] = weightReader.ReadSingle();
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
            CheckpointSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant(),
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
        ["content"] = Enum.GetNames<ContentFlag>()
        , ["knowledgeTarget"] = Enum.GetNames<KnowledgeTarget>()
    };

    public static Dictionary<string, ConfidenceThreshold> DefaultCalibration => new(StringComparer.Ordinal)
    {
        ["speechActs"] = new(0.50, 0.10), ["domains"] = new(0.50, 0.10),
        ["goals"] = new(0.50, 0.10), ["affect"] = new(0.65, 0.12),
        ["stance"] = new(0.65, 0.12), ["policy"] = new(0.75, 0.15),
        ["slots"] = new(0.80, 0.10), ["content"] = new(0.50, 0.10),
        ["toolReadOnly"] = new(0.95, 0.05), ["toolMutating"] = new(0.99, 0.01),
        ["responseCandidate"] = new(0.70, 0.10), ["knowledgeTarget"] = new(0.85, 0.10)
    };

    public static void Validate(
        IReadOnlyDictionary<string, string[]> labels,
        IReadOnlyDictionary<string, ConfidenceThreshold> calibration)
    {
        foreach (var expected in Labels)
            if (!labels.TryGetValue(expected.Key, out var actual) || !actual.SequenceEqual(expected.Value))
                throw new InvalidDataException($"Checkpoint label schema '{expected.Key}' does not match this runtime.");
        foreach (var expected in DefaultCalibration.Keys)
            if (!calibration.ContainsKey(expected))
                throw new InvalidDataException($"Checkpoint confidence calibration '{expected}' is missing.");
        foreach (var item in calibration)
            if (item.Value.Threshold is < 0 or > 1 || item.Value.Margin is < 0 or > 1)
                throw new InvalidDataException($"Invalid confidence calibration for '{item.Key}'.");
    }
}
