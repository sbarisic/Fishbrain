using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Fishbrain;

internal sealed record CausalHeader(string Architecture, CausalConfig Config, BpeDefinition Tokenizer, ToolSchema[] Tools,
    WeightShape[] Parameters, string TrainingFingerprint, int Updates, string Phase, JsonElement Training, string? PromptFormat = null);
internal static class CausalArtifact
{
    internal static readonly byte[] Magic = "FISHBRAIN CAUSAL V1\n"u8.ToArray();
    internal const string Architecture = "CAUSAL_RMS_GELU_TIED_BPE_V1";
    internal static (CausalHeader Header, CausalNetwork Model, ByteBpe Tokenizer) Load(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length is < 64 or > 512L * 1024 * 1024) throw new InvalidDataException("Invalid causal artifact size.");
        using var reader = new BinaryReader(stream, Encoding.UTF8, true);
        if (!reader.ReadBytes(Magic.Length).SequenceEqual(Magic))
            throw new InvalidDataException("Expected a causal V1 model. Older Fishbrain weights need their matching legacy runtime; no migration is supported.");
        stream.Position = 0;
        using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            var bytes = new byte[65536]; var remaining = stream.Length - 32;
            while (remaining > 0) { var n = stream.Read(bytes, 0, (int)Math.Min(bytes.Length, remaining)); if (n == 0) throw new EndOfStreamException(); hash.AppendData(bytes, 0, n); remaining -= n; }
            var expected = reader.ReadBytes(32);
            if (!CryptographicOperations.FixedTimeEquals(expected, hash.GetHashAndReset())) throw new InvalidDataException("Causal artifact integrity failure.");
        }
        stream.Position = Magic.Length;
        var length = reader.ReadInt32();
        if (length is < 1 or > 8 * 1024 * 1024 || stream.Position + length > stream.Length - 32) throw new InvalidDataException("Invalid causal header.");
        var header = JsonDefaults.Read<CausalHeader>(Encoding.UTF8.GetString(reader.ReadBytes(length)));
        if (header.Architecture != Architecture || header.TrainingFingerprint is null || header.TrainingFingerprint.Length != 64 ||
            header.TrainingFingerprint.Any(c => !Uri.IsHexDigit(c)) || header.Config is null || header.Tokenizer is null || header.Parameters is null ||
            header.Tools is null || header.Training.ValueKind != JsonValueKind.Object || header.Updates < 0 || string.IsNullOrWhiteSpace(header.Phase))
            throw new InvalidDataException("Invalid causal architecture or training binding.");
        if (header.PromptFormat != PromptPacker.Format)
            throw new InvalidDataException("Model prompt format differs from this runtime. Use the model's preserved matching package; repacking requires a separately trained candidate.");
        header.Config.Validate(); var tokenizer = new ByteBpe(header.Tokenizer);
        if (tokenizer.Count != header.Config.Vocabulary) throw new InvalidDataException("Tokenizer and model vocabulary differ.");
        var shapes = CausalNetwork.Layout(header.Config).ToArray();
        if (!shapes.SequenceEqual(header.Parameters) || stream.Position + shapes.Sum(s => (long)s.Rows * s.Columns) * 4 + 32 != stream.Length)
            throw new InvalidDataException("Causal parameter layout mismatch.");
        var registry = new GameToolRegistry(header.Tools.Select(s => new DefinitionOnly(s)));
        var weights = new Dictionary<string, float[]>();
        foreach (var s in shapes)
        {
            var values = new float[checked(s.Rows * s.Columns)];
            stream.ReadExactly(System.Runtime.InteropServices.MemoryMarshal.AsBytes(values.AsSpan()));
            if (!BitConverter.IsLittleEndian) throw new PlatformNotSupportedException("Little-endian float32 weights required.");
            weights.Add(s.Name, values);
        }
        return (header with { Tools = registry.Schemas.ToArray() }, new(header.Config, weights), tokenizer);
    }
    private sealed class DefinitionOnly(ToolSchema schema) : IGameTool
    {
        public ToolSchema Schema => schema;
        public GameToolResult Execute(GameToolInvocation invocation) => throw new InvalidOperationException("Artifact schemas do not authorize execution.");
    }
}
