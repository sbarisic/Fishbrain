using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Fishbrain.Neural;

internal sealed record ContextualCheckpointHeader(string Architecture, ContextualModelConfig Config,
    string[] Words, string[] OutputWords, string DomainFingerprint, string SchemaFingerprint, string CorpusHash,
    int CompletedSteps, bool Training, ulong RandomState, ParameterShape[] Parameters,
    Dictionary<string, int> ParameterUpdates, Dictionary<string, double> ExecutionThresholds)
{
    public DomainSnapshot? Domain { get; init; }
    public string? NextPhase { get; init; }
    public string? Sampler { get; init; }
    public int EffectiveBatchSize { get; init; }
}

internal sealed record DomainSnapshot(string Id, DomainToolBinding[] Tools, Dictionary<string, string> EntityAliases);

internal static class ContextualCheckpoint
{
    private static readonly byte[] Magic = "FISHBRAIN CONTEXT\n"u8.ToArray();
    internal const string ArchitectureName = "BIDIRECTIONAL_ENCODER_CAUSAL_CROSS_ATTENTION_DECODER";
    public static bool Matches(string path)
    {
        using var stream = File.OpenRead(path);
        var prefix = new byte[Magic.Length];
        return stream.Read(prefix) == prefix.Length && prefix.SequenceEqual(Magic);
    }

    internal static string SchemaFingerprint(ContextualNetwork model) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
    {
        model.Shapes,
        InputContract = model.Config.CurrentUtteranceFirst || model.Config.IndependentMemorySelection
            ? $"TYPED_ROLES_OWNED_FACTS_AGENDA_SOURCE_TOPICS_CURRENT_FIRST_{model.Config.CurrentUtteranceFirst}_MEMORY_SET_{model.Config.IndependentMemorySelection}"
            : "TYPED_ROLES_OWNED_FACTS_AGENDA_SOURCE_TOPICS",
        Segments = Enum.GetNames<InputSegment>(),
        Acts = Enum.GetNames<DialogueResponseAct>(),
        Status = Enum.GetNames<ActionStatus>(),
        AgendaKinds = Enum.GetNames<AgendaKind>(),
        AgendaStatuses = Enum.GetNames<AgendaStatus>(),
        Facts = Enum.GetNames<DialogueFactKind>(),
        Slots = Enum.GetNames<SlotType>(),
        Heads = ContextualNetwork.HeadSizes,
        Labels = ModelSchemas.Labels,
        Domain = model.Domain.Fingerprint
    }))).ToLowerInvariant();

    public static void Save(string path, ContextualNetwork model, string corpusHash, int step,
        IReadOnlyDictionary<string, double> thresholds, IContextualTrainingState? trainer = null)
    {
        var parameters = trainer?.Parameters ?? model.Parameters();
        var header = new ContextualCheckpointHeader(ArchitectureName, model.Config, model.Vocabulary.Words, model.Vocabulary.OutputWords,
            model.Domain.Fingerprint, SchemaFingerprint(model), corpusHash, step, trainer is not null, trainer?.Random.State ?? 0,
            model.Shapes.ToArray(), trainer?.ParameterUpdates ?? [], thresholds.ToDictionary(x => x.Key, x => x.Value))
        {
            Domain = new(model.Domain.Id, model.Domain.Tools.ToArray(), model.Domain.EntityAliases.ToDictionary(x => x.Key, x => x.Value)),
            NextPhase = step == 260_000 ? "COMPLETE" : ContextualSchedule.Phase(step).ToString(),
            Sampler = "COPRIME_FAMILY_PERMUTATION_MEMBER_ROTATION",
            EffectiveBatchSize = 32
        };
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temporary = full + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(Magic);
            var json = JsonSerializer.SerializeToUtf8Bytes(header);
            writer.Write(json.Length);
            writer.Write(json);
            foreach (var shape in model.Shapes) Write(parameters[shape.Name].Data);
            if (trainer is not null)
            {
                foreach (var shape in model.Shapes) Write(trainer.FirstMoments[shape.Name]);
                foreach (var shape in model.Shapes) Write(trainer.SecondMoments[shape.Name]);
            }
            void Write(float[] values) { foreach (var value in values) writer.Write(value); }
        }
        byte[] digest;
        using (var stream = File.OpenRead(temporary)) digest = SHA256.HashData(stream);
        using (var stream = new FileStream(temporary, FileMode.Append, FileAccess.Write, FileShare.None)) stream.Write(digest);
        File.Move(temporary, full, true);
    }

    public static (ContextualNetwork Model, ContextualCheckpointHeader Header, ContextualTrainingState? TrainingState) Load(
        string path, DialogueDomainDefinition? domain = null, bool training = false)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length < Magic.Length + 4 + 32) throw new InvalidDataException("Truncated contextual checkpoint.");
        using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            var remaining = stream.Length - 32;
            var buffer = new byte[64 * 1024];
            while (remaining > 0)
            {
                var readCount = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                if (readCount == 0) throw new EndOfStreamException();
                hash.AppendData(buffer, 0, readCount);
                remaining -= readCount;
            }
            var expected = new byte[32];
            stream.ReadExactly(expected);
            if (!CryptographicOperations.FixedTimeEquals(expected, hash.GetHashAndReset())) throw new InvalidDataException("Contextual checkpoint integrity failure.");
        }
        stream.Position = 0;
        using var reader = new BinaryReader(stream, Encoding.UTF8, true);
        if (!reader.ReadBytes(Magic.Length).SequenceEqual(Magic)) throw new InvalidDataException("Invalid contextual checkpoint magic.");
        var headerLength = reader.ReadInt32();
        if (headerLength is < 1 or > 16_777_216) throw new InvalidDataException("Invalid contextual header length.");
        var header = JsonSerializer.Deserialize<ContextualCheckpointHeader>(reader.ReadBytes(headerLength)) ?? throw new InvalidDataException("Missing contextual header.");
        var definition = header.Domain ?? throw new InvalidDataException("Checkpoint has no embedded domain definition.");
        var embeddedDomain = new DialogueDomainDefinition(definition.Id, definition.Tools, definition.EntityAliases);
        if (embeddedDomain.Fingerprint != header.DomainFingerprint) throw new InvalidDataException("Embedded domain fingerprint mismatch.");
        domain ??= embeddedDomain;
        if (header.Architecture != ArchitectureName || header.DomainFingerprint != domain.Fingerprint ||
            header.CompletedSteps is < 0 or > 260_000 || header.Config is null || header.Words is null || header.OutputWords is null ||
            header.Parameters is null || header.ExecutionThresholds is null || header.ParameterUpdates is null ||
            string.IsNullOrWhiteSpace(header.CorpusHash)) throw new InvalidDataException("Contextual checkpoint metadata mismatch.");
        header.Config.Validate();
        if (header.NextPhase != (header.CompletedSteps == 260_000 ? "COMPLETE" : ContextualSchedule.Phase(header.CompletedSteps).ToString()) ||
            header.Sampler != "COPRIME_FAMILY_PERMUTATION_MEMBER_ROTATION" || header.EffectiveBatchSize != 32)
            throw new InvalidDataException("Contextual phase or sampler contract mismatch.");
        var wordSet = header.Words.ToHashSet(StringComparer.Ordinal);
        if (header.Words.Length > 100_000 || header.OutputWords.Length > 100_000 ||
            header.Words.Any(string.IsNullOrWhiteSpace) || header.Words.Distinct().Count() != header.Words.Length ||
            header.OutputWords.Any(w => !wordSet.Contains(w))) throw new InvalidDataException("Invalid contextual vocabulary.");
        var vocabulary = new WordVocabulary(header.Words, header.OutputWords);
        if (!vocabulary.Words.SequenceEqual(header.Words) || !vocabulary.OutputWords.SequenceEqual(header.OutputWords))
            throw new InvalidDataException("Contextual vocabulary is not in canonical token order.");
        var shapes = ContextualNetwork.Layout(header.Config, vocabulary.InputSize, vocabulary.OutputSize, domain.Tools.Count + 1).ToArray();
        var count = shapes.Sum(s => (long)s.Rows * s.Columns);
        if (!shapes.SequenceEqual(header.Parameters) || count > 100_000_000 || stream.Position + count * 4 * (header.Training ? 3 : 1) + 32 != stream.Length)
            throw new InvalidDataException("Contextual parameter layout or file length mismatch.");
        if (header.ExecutionThresholds.Any(x => !domain.Tools.Any(t => t.Schema.Name == x.Key) || !double.IsFinite(x.Value) || x.Value is < 0 or > 1.01))
            throw new InvalidDataException("Invalid execution calibration.");
        var weights = ReadParameters(false);
        var model = new ContextualNetwork(header.Config, vocabulary, domain, weights, takeOwnership: true);
        if (SchemaFingerprint(model) != header.SchemaFingerprint) throw new InvalidDataException("Contextual label schema mismatch.");
        ContextualTrainingState? trainer = null;
        if (training)
        {
            if (!header.Training || header.RandomState == 0 || header.ParameterUpdates.Count != shapes.Length ||
                shapes.Any(s => !header.ParameterUpdates.TryGetValue(s.Name, out var updates) || updates < 0 || updates > header.CompletedSteps))
                throw new InvalidDataException("Inference checkpoints cannot resume training or are missing optimizer state.");
            var first = ReadParameters(false);
            var second = ReadParameters(true);
            trainer = new(header.RandomState, first, second, header.ParameterUpdates);
        }
        return (model, header, trainer);

        Dictionary<string, float[]> ReadParameters(bool nonnegative)
        {
            var result = new Dictionary<string, float[]>(StringComparer.Ordinal);
            foreach (var shape in shapes)
            {
                var values = new float[checked(shape.Rows * shape.Columns)];
                for (var i = 0; i < values.Length; i++)
                {
                    values[i] = reader.ReadSingle();
                    if (!float.IsFinite(values[i]) || nonnegative && values[i] < 0) throw new InvalidDataException("Invalid contextual weight or moment.");
                }
                result.Add(shape.Name, values);
            }
            return result;
        }
    }
}
