using System.Text.Json;
using Fishbrain.Neural;

namespace Fishbrain;

/// <summary>Immutable contextual inference model. Scratch tensors belong to each reply.</summary>
public sealed partial class Brain
{
    public static Brain Load(string path)
    {
        var loaded = ContextualCheckpoint.Load(path);
        return new Brain(loaded.Model, loaded.Header.CompletedSteps, loaded.Header.ExecutionThresholds, loaded.Header.CorpusHash);
    }

    public ReplyResult Reply(ReplyRequest request, GameToolRegistry tools)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(tools);
        return ContextualReply(request, tools);
    }

    internal void ExportInference(string path, string corpusHash = "UNKNOWN") =>
        ContextualCheckpoint.Save(path, _contextual, corpusHash == "UNKNOWN" ? _contextualCorpusHash : corpusHash, _step, _executionThresholds);

    internal static string InspectInferenceCheckpoint(string path)
    {
        var loaded = ContextualCheckpoint.Load(path);
        return JsonSerializer.Serialize(new
        {
            loaded.Header,
            ParameterCount = loaded.Model.Shapes.Sum(s => (long)s.Rows * s.Columns),
            FileBytes = new FileInfo(path).Length,
            OptimizerAllocated = false
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    private static bool SchemaEquals<T>(T left, T right) => JsonSerializer.Serialize(left) == JsonSerializer.Serialize(right);
    private static void ValidateRequest(ReplyRequest request)
    {
        if (!ValidRequestId(request.ConversationId))
            throw new ArgumentException("ConversationId must contain 1-128 characters.", nameof(request));
        if (!ValidRequestId(request.TurnId))
            throw new ArgumentException("TurnId must contain 1-128 characters.", nameof(request));
        if (request.Utterances is null || request.Utterances.Count == 0)
            throw new ArgumentException("At least one structured utterance is required.", nameof(request));
        if (!Enum.IsDefined(request.ResponseMode))
            throw new ArgumentOutOfRangeException(nameof(request), "ResponseMode is invalid.");
        if (request.Utterances.Any(turn => turn is null || !Enum.IsDefined(turn.Speaker) || turn.Sequence < 0 ||
            string.IsNullOrWhiteSpace(turn.Text) || turn.Text.Length > 4_096))
            throw new ArgumentException("Structured utterances must have a valid sequence, speaker, and 1-4096 text characters.", nameof(request));
        if (request.Utterances.Zip(request.Utterances.Skip(1)).Any(pair => pair.First.Sequence >= pair.Second.Sequence))
            throw new ArgumentException("Utterance sequences must be unique and strictly increasing.", nameof(request));
        if (request.Utterances[^1].Speaker != DialogueRole.Player)
            throw new ArgumentException("The final structured utterance must be a player utterance.", nameof(request));
        if (request.ResponseSequence <= request.Utterances[^1].Sequence ||
            request.Utterances.Any(utterance => utterance.Sequence == request.ResponseSequence))
            throw new ArgumentException(
                "ResponseSequence must be unused and greater than the current player sequence.",
                nameof(request));
        ArgumentNullException.ThrowIfNull(request.State);
        request.State.Validate();
        ArgumentNullException.ThrowIfNull(request.Persona);
        request.Persona.Validate();
        ArgumentNullException.ThrowIfNull(request.PlayerProfile);
        request.PlayerProfile.Validate();

        static bool ValidRequestId(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 128 &&
            value == value.Trim() && value.All(character => !char.IsControl(character));
    }


    internal static bool TryRenderPersona(
        KnowledgeTarget target, NpcPersona persona, GameToolRegistry tools,
        out string text, out ResponseSource source)
    {
        source = target == KnowledgeTarget.Capabilities ? ResponseSource.CapabilityTemplate : ResponseSource.PersonaTemplate;
        text = target switch
        {
            KnowledgeTarget.Name => $"MY NAME IS {persona.Name}.",
            KnowledgeTarget.Role => $"I AM {WithArticle(persona.Role)}.",
            KnowledgeTarget.Origin => Fact("I AM FROM", persona.Origin, "MY ORIGIN HAS NOT BEEN AUTHORED"),
            KnowledgeTarget.Home => Fact("MY HOME IS", persona.Home, "MY HOME HAS NOT BEEN AUTHORED"),
            KnowledgeTarget.Family => Fact("MY FAMILY IS", persona.Family, "MY FAMILY HAS NOT BEEN AUTHORED"),
            KnowledgeTarget.Occupation => Fact("I WORK AS", persona.Occupation, "MY OCCUPATION HAS NOT BEEN AUTHORED"),
            KnowledgeTarget.Faction => Fact("MY FACTION IS", persona.Faction, "MY FACTION HAS NOT BEEN AUTHORED"),
            KnowledgeTarget.Traits => persona.Traits.Count > 0
                ? $"I AM {string.Join(", ", persona.Traits)}."
                : "MY TRAITS HAVE NOT BEEN AUTHORED.",
            _ => string.Empty
        };
        return text.Length > 0;

        static string Fact(string prefix, string? value, string unknown) => value is null ? unknown + "." : $"{prefix} {value}.";
        static string WithArticle(string role) => "AEIOU".Contains(role[0]) ? "AN " + role : "A " + role;
    }

}
