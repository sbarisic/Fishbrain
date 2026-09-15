using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Fishbrain;

public enum ActionStatus { Affirmative, Negated, Hypothetical, Quoted, Question }
public enum DialogueResponseAct { None, Acknowledge, Correct, Answer, Clarify, ExecuteTool, AskFollowUp, Refuse, Farewell }
public enum AgendaKind { UnansweredQuestion, Goal, Commitment }
public enum AgendaStatus { Active, Completed, Cancelled }
public sealed record DialogueAgendaEntry(AgendaKind Kind, string Subject, long SourceTurn, AgendaStatus Status);
public sealed record SemanticFrame(int Start, int Length, SpeechAct SpeechAct, DialogueParticipant Subject,
    DialogueParticipant Target, string? ToolName, IReadOnlyList<DialogueSlot> Arguments,
    long? Antecedent, ActionStatus Status, double Confidence)
{
    // Null means no clause-level annotation in a training row; Empty is an explicit no-fact target.
    public DiscourseFrame? Fact { get; init; }
}
public sealed record PlannedResponseAct(DialogueResponseAct Act, int? FrameIndex = null, string? Subject = null);
public sealed record MemorySelection(DialogueFact Fact, double Score);
public sealed record ValidatedActionCandidate(int FrameIndex, string ToolName,
    IReadOnlyDictionary<string, string> Arguments, double Confidence);
public sealed record ContextualSupervision(SemanticFrame[]? Frames = null, PlannedResponseAct[]? Plan = null,
    DialogueFact[]? RelevantFacts = null, DialogueAgendaEntry[]? Agenda = null)
{
    public void Validate(string current)
    {
        if (Frames is { Length: > 3 } || Plan is { Length: > 3 } || RelevantFacts is { Length: > 8 } || Agenda is { Length: > 4 })
            throw new InvalidDataException("Contextual supervision exceeds its bounded schema.");
        if (Frames is not null) foreach (var frame in Frames)
        {
            if (frame is null || frame.Start < 0 || frame.Length <= 0 || frame.Start > current.Length - frame.Length ||
                !Enum.IsDefined(frame.Status) || !Enum.IsDefined(frame.SpeechAct) || !Enum.IsDefined(frame.Subject) ||
                !Enum.IsDefined(frame.Target) || !double.IsFinite(frame.Confidence) || frame.Confidence is < 0 or > 1 || frame.Arguments is null)
                throw new InvalidDataException("Invalid semantic frame target.");
            foreach (var slot in frame.Arguments)
                if (!Enum.IsDefined(slot.Type) || slot.Start < frame.Start || slot.Length <= 0 ||
                    slot.Start + slot.Length > frame.Start + frame.Length || slot.Value != current.Substring(slot.Start, slot.Length))
                    throw new InvalidDataException("Semantic frame arguments must copy their clause's source spans.");
            if (frame.Fact is { } fact)
            {
                if (!Enum.IsDefined(fact.Act) || !Enum.IsDefined(fact.Subject) || !Enum.IsDefined(fact.Target) ||
                    fact.FactKind is { } kind && !Enum.IsDefined(kind) || !double.IsFinite(fact.Confidence) || fact.Confidence is < 0 or > 1)
                    throw new InvalidDataException("Invalid clause fact annotation.");
                if (fact.FactValueSpan is { } span)
                {
                    span.Validate(current);
                    if (span.Start < frame.Start || span.Start + span.Length > frame.Start + frame.Length)
                        throw new InvalidDataException("Clause facts must copy values from their own clause.");
                }
            }
        }
        if (Plan is not null && Plan.Any(a => a is null || !Enum.IsDefined(a.Act) || a.FrameIndex is { } i &&
            (i < 0 || Frames is null || i >= Frames.Length))) throw new InvalidDataException("Invalid response plan target.");
        if (RelevantFacts is not null) foreach (var fact in RelevantFacts) PlayerConversationProfile.ValidateFact(fact, fact.Provenance);
        if (Agenda is not null) (NpcDialogueState.Initial with { Agenda = Agenda }).Validate();
    }
}
public sealed record ContextualDiagnostics(IReadOnlyList<MemorySelection> Memory,
    IReadOnlyList<SemanticFrame> Frames, IReadOnlyList<PlannedResponseAct> Acts,
    IReadOnlyList<string> ExecutionVetoes, IReadOnlyList<DialogueAgendaEntry> Agenda,
    double PlanConfidence, double UnsupportedClaimProbability, double UnderstandingMilliseconds)
{
    public IReadOnlyList<ValidatedActionCandidate> ActionCandidates { get; init; } = [];
}
public sealed record DomainToolBinding(ToolSchema Schema, IReadOnlyDictionary<string, SlotType> Parameters,
    string Capability, DialogueDomain Domain);

/// <summary>Model-bound semantic capabilities. Registration remains the host's execution authorization.</summary>
public sealed class DialogueDomainDefinition
{
    public string Id { get; }
    public IReadOnlyList<DomainToolBinding> Tools { get; }
    public IReadOnlyDictionary<string, string> EntityAliases { get; }
    public string Fingerprint { get; }

    public DialogueDomainDefinition(string id, IEnumerable<DomainToolBinding> tools,
        IReadOnlyDictionary<string, string>? entityAliases = null)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 64 || id.Any(c => !char.IsAsciiLetterUpper(c) && !char.IsAsciiDigit(c) && c != '_'))
            throw new ArgumentException("Domain ID must be an uppercase identifier.", nameof(id));
        Id = id;
        var bindings = tools.OrderBy(x => x.Schema.Name, StringComparer.Ordinal).ToArray();
        // Registry snapshots and validates all nested schema collections.
        var schemas = new GameToolRegistry(bindings.Select(x => new DefinitionTool(x.Schema))).Schemas.ToDictionary(x => x.Name);
        Tools = Array.AsReadOnly(bindings.Select(binding =>
        {
            if (binding.Parameters.Count != binding.Schema.Parameters.Count ||
                binding.Schema.Parameters.Any(p => !binding.Parameters.ContainsKey(p.Name)) ||
                binding.Parameters.Values.Any(p => !Enum.IsDefined(p)) || !Enum.IsDefined(binding.Domain) ||
                string.IsNullOrWhiteSpace(binding.Capability) || !DialogueText.IsCanonical(binding.Capability))
                throw new ArgumentException("Domain tool bindings must map every parameter and declare a canonical capability.");
            return binding with { Schema = schemas[binding.Schema.Name], Parameters = new ReadOnlyDictionary<string, SlotType>(binding.Parameters.ToDictionary(x => x.Key, x => x.Value)) };
        }).ToArray());
        EntityAliases = new ReadOnlyDictionary<string, string>((entityAliases ?? new Dictionary<string, string>())
            .OrderBy(x => x.Key, StringComparer.Ordinal).ToDictionary(x => DialogueText.Normalize(x.Key), x => DialogueText.Normalize(x.Value), StringComparer.Ordinal));
        Fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { Id, Tools, EntityAliases })))).ToLowerInvariant();
    }

    public string CanonicalEntity(string value) => EntityAliases.GetValueOrDefault(value, value);
    private sealed class DefinitionTool(ToolSchema schema) : IGameTool
    {
        public ToolSchema Schema => schema;
        public GameToolResult Execute(GameToolInvocation invocation) => throw new InvalidOperationException("Definitions are not executable tools.");
    }
}

public sealed record ContextualModelConfig
{
    public int EncoderLayers { get; init; } = 4;
    public int DecoderLayers { get; init; } = 2;
    public int Width { get; init; } = 256;
    public int Heads { get; init; } = 8;
    public int FeedForwardWidth { get; init; } = 1024;
    public int ContextLength { get; init; } = 512;
    public int MaximumOutputTokens { get; init; } = 64;
    public int Seed { get; init; } = 42;
    internal void Validate()
    {
        if (EncoderLayers is < 1 or > 12 || DecoderLayers is < 1 or > 12 || Width is < 4 or > 1024 ||
            Heads < 1 || Width % Heads != 0 || FeedForwardWidth is < 4 or > 8192 || ContextLength is < 16 or > 2048 ||
            MaximumOutputTokens is < 1 or > 64)
            throw new InvalidDataException("Invalid contextual model dimensions.");
    }
}
