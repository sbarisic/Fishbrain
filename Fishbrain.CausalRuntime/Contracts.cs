using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

[assembly: InternalsVisibleTo("Fishbrain")]
[assembly: InternalsVisibleTo("Fishbrain.CausalDemo")]
[assembly: InternalsVisibleTo("Fishbrain.CausalTests")]

namespace Fishbrain;

public enum MessageRole { Player, Assistant, AssistantToolCall, ToolResult }
public enum ResponseMode { Production, DeterministicOnly }
public sealed record ChatMessage(MessageRole Role, string Text, long Sequence, string? ToolName = null);
public sealed record NpcPersona(string Name, string Role, string? Occupation = null, string? Home = null, string? Origin = null)
{
    public static NpcPersona Default { get; } = new("Arin", "traveler", "road warden", "the old mill", "this village");
}
public sealed record GenerationSettings(int Seed = 42, float Temperature = 0, int MaximumToolCalls = 4, ResponseMode Mode = ResponseMode.Production);
public sealed record ReplyRequest(string ConversationId, string TurnId, IReadOnlyList<ChatMessage> Messages, NpcPersona Persona, GenerationSettings? Generation = null,
    GameToolRegistry? Tools = null);
public sealed record ToolOutcome(string Name, IReadOnlyDictionary<string, string> Arguments, GameToolResult? Result, string? Veto, string IdempotencyKey);
public sealed record ReplyDiagnostics(int PromptTokens, int GeneratedTokens, double Milliseconds, IReadOnlyList<string> Events);
public sealed record ReplyResult(string Text, IReadOnlyList<ChatMessage> MessagesToAppend, IReadOnlyList<ToolOutcome> ToolOutcomes, ReplyDiagnostics Diagnostics);

internal static class JsonDefaults
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
    internal static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);
    internal static T Read<T>(string text) => JsonSerializer.Deserialize<T>(text, Options) ?? throw new InvalidDataException("Missing JSON object.");
}

// The demo domain uses uppercase entity keys; conversation text and tool result values
// bypass this helper and keep their original casing and Unicode.
internal static class DialogueText
{
    internal static string Normalize(string value) => value.Normalize(NormalizationForm.FormC).Trim().ToUpperInvariant();
}

public sealed record CausalConfig(int Layers = 8, int Width = 384, int Heads = 6, int FeedForward = 1536,
    int Context = 1024, int Vocabulary = 8192, int Seed = 42)
{
    internal void Validate()
    {
        if (Layers is < 1 or > 16 || Width is < 8 or > 1024 || Heads < 1 || Width % Heads != 0 ||
            FeedForward < Width || FeedForward > 4096 || Context is < 32 or > 2048 || Vocabulary is < 272 or > 32768)
            throw new InvalidDataException("Unsupported causal model dimensions.");
    }
}
