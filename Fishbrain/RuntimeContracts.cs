using System.Collections.ObjectModel;

namespace Fishbrain;

public enum DialogueRole { Player, Npc }
public enum ResponseMode { Ranked, GeneratedExperimental }
public enum SpeechAct
{
    Greet, Farewell, Ask, Request, Order, Offer, Inform, Report, Confirm, Correct,
    Accept, Refuse, Warn, Threaten, Apologize, Thank, Challenge, Negotiate
}
public enum DialogueDomain
{
    Social, Identity, Wellbeing, Assistance, Activity, LocationNavigation,
    TradeEconomy, ItemsInventory, QuestTask, Combat, Survival, HealthRepair,
    FactionPolitics, CrimeLaw, Magic, Technology, VehicleTravel, Environment,
    LoreWorld, MetaSystem
}
public enum DialogueGoal
{
    None, Rapport, ConversationClosure, InformationExchange, EntityFinding, Access,
    ItemAcquisition, ItemDisposal, Transaction, TaskStart, TaskAdvance, TaskCompletion,
    Coordination, Travel, Combat, Survival, HealingRepair, Influence, Concealment,
    Negotiation, SystemOperation, EmotionalExpression, Clarification, Other
}
public enum DialogueStance { Friendly, Neutral, Cautious, Hostile, Deceptive }
public enum ResponsePolicy { Answer, Clarify, ExecuteTool, Refuse, NoResponse, Acknowledge, Negotiate, Defer }
public enum ContentFlag
{
    Profanity, FictionalViolence, GraphicViolence, Threat, Crime, IdentityAttack,
    SelfHarm, SexualContent, SexualViolence
}
public enum SlotType
{
    Person, Place, Item, Faction, Quantity, Currency, Time, Direction, Vehicle,
    System, Credential, Action, Proposition, Other
}
public enum BioTag { B, I }
public enum ResponseSource
{
    RankedCandidate, RankedVariation, ToolTemplate, PersonaTemplate, CapabilityTemplate,
    ClarificationTemplate, Fallback, GeneratedExperimental
}

public enum KnowledgeTarget
{
    None, Name, Role, Origin, Home, Family, Occupation, Faction, Traits, Capabilities,
    Balance, Inventory, CurrentLocation, WorldFact
}

public enum PerceptionConstraintOperation { Enforce, Veto, Boost }

public sealed record PerceptionConstraint(
    PerceptionConstraintOperation Operation,
    string Head,
    string Label,
    double ScoreChange,
    double Confidence,
    string Evidence,
    string Reason);

public sealed record DialogueTurn(DialogueRole Role, string Text);

public sealed record DialogueSlot(
    SlotType Type,
    BioTag Tag,
    string Value,
    int Start,
    int Length,
    double Confidence);

public sealed record StructuredPerception(
    IReadOnlyList<SpeechAct> SpeechActs,
    IReadOnlyList<DialogueDomain> Domains,
    IReadOnlyList<DialogueGoal> Goals,
    UserAffect Affect,
    DialogueStance Stance,
    ResponsePolicy Policy,
    IReadOnlyList<DialogueSlot> Slots,
    IReadOnlyList<ContentFlag> ContentFlags,
    string? ToolSchema,
    string? ResponseCandidateId,
    KnowledgeTarget KnowledgeTarget,
    IReadOnlyDictionary<string, double> Confidence)
{
    public static StructuredPerception Empty { get; } = new(
        [], [], [], UserAffect.Neutral, DialogueStance.Neutral, ResponsePolicy.Clarify,
        [], [], null, null, KnowledgeTarget.None,
        new ReadOnlyDictionary<string, double>(new Dictionary<string, double>()));
}

public sealed record NpcPersona(
    string Id,
    string Name,
    string Role,
    string? Origin,
    string? Home,
    string? Family,
    string? Occupation,
    string? Faction,
    IReadOnlyList<string> Traits)
{
    public static NpcPersona Default { get; } = new(
        "DEMO_TRAVELER", "ARIN", "TRAVELER", "THIS VILLAGE", "THE OLD MILL",
        "A SISTER IN THE NORTH", "ROAD WARDEN", null, ["HELPFUL", "CAUTIOUS"]);

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Id) || Id.Length > 64 ||
            Id.Any(character => character is not (>= 'A' and <= 'Z') and not (>= '0' and <= '9') and not '_'))
            throw new ArgumentException("Id must be a 1-64 character uppercase identifier.", nameof(Id));
        ValidateRequired(Name, nameof(Name), 64);
        ValidateRequired(Role, nameof(Role), 64);
        ValidateOptional(Origin, nameof(Origin));
        ValidateOptional(Home, nameof(Home));
        ValidateOptional(Family, nameof(Family));
        ValidateOptional(Occupation, nameof(Occupation));
        ValidateOptional(Faction, nameof(Faction));
        if (Traits is null || Traits.Count > 8)
            throw new ArgumentException("A persona supports at most eight traits.", nameof(Traits));
        foreach (var trait in Traits) ValidateRequired(trait, nameof(Traits), 64);

        static void ValidateRequired(string value, string name, int maximum)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || value != DialogueText.Normalize(value))
                throw new ArgumentException($"{name} must be normalized uppercase text with 1-{maximum} characters.", name);
        }
        static void ValidateOptional(string? value, string name)
        {
            if (value is not null) ValidateRequired(value, name, 128);
        }
    }
}

public sealed record PendingClarification(string Question, string? ToolSchema, IReadOnlyList<string> MissingSlots);
public sealed record DialogueTransaction(string Kind, string Item, int Quantity, string Status);
public sealed record PendingDialogueAction(string Action, string? ToolSchema, IReadOnlyDictionary<string, string> Arguments);

public sealed record DialogueReferenceState(
    string? Person,
    string? Place,
    string? Item,
    string? Vehicle,
    string? System)
{
    public static DialogueReferenceState Empty { get; } = new(null, null, null, null, null);
}

public sealed record NpcDialogueState(
    byte Rapport,
    byte Trust,
    byte Familiarity,
    byte Hostility,
    NpcMood Mood,
    IReadOnlyList<DialogueDomain> ActiveDomains,
    string? LastBehaviorId,
    UserAffect LastAffect,
    PendingClarification? PendingClarification,
    DialogueTransaction? CurrentTransaction,
    IReadOnlyList<DialogueGoal> ActiveGoals,
    IReadOnlyList<PendingDialogueAction> PendingActions,
    DialogueReferenceState References,
    byte ThreatLevel,
    byte CalmTurns,
    DialogueDomain? PendingTopic,
    KnowledgeTarget PendingKnowledgeTarget,
    string? LastTool,
    string? LastToolOutcome)
{
    public static NpcDialogueState Initial { get; } = new(
        1, 1, 0, 0, NpcMood.Neutral, [], null, UserAffect.Neutral,
        null, null, [], [], DialogueReferenceState.Empty, 0, 0, null,
        KnowledgeTarget.None, null, null);

    public void Validate()
    {
        if (Rapport > 3 || Trust > 3 || Familiarity > 3 || Hostility > 3)
            throw new ArgumentOutOfRangeException(nameof(NpcDialogueState), "Social values must be between 0 and 3.");
        if (ThreatLevel > 3 || CalmTurns > 3)
            throw new ArgumentOutOfRangeException(nameof(NpcDialogueState), "Threat and calm values must be between 0 and 3.");
        if (!Enum.IsDefined(Mood) || !Enum.IsDefined(LastAffect))
            throw new ArgumentOutOfRangeException(nameof(NpcDialogueState), "State contains an unknown enum value.");
        if (ActiveDomains is null || ActiveDomains.Count > 4 || ActiveDomains.Any(value => !Enum.IsDefined(value)))
            throw new ArgumentException("State supports at most four valid active domains.", nameof(ActiveDomains));
        if (ActiveGoals is null || ActiveGoals.Count > 4 || ActiveGoals.Any(value => !Enum.IsDefined(value)))
            throw new ArgumentException("State supports at most four valid active goals.", nameof(ActiveGoals));
        if (PendingActions is null || PendingActions.Count > 3)
            throw new ArgumentException("State supports at most three pending actions.", nameof(PendingActions));
        ArgumentNullException.ThrowIfNull(References);
        foreach (var value in new[] { References.Person, References.Place, References.Item, References.Vehicle, References.System })
            if (value is not null && (value.Length is < 1 or > 32 || value != DialogueText.Normalize(value)))
                throw new ArgumentException("Reference identifiers must be 1-32 normalized characters.", nameof(References));
        if (LastBehaviorId?.Length > 64) throw new ArgumentException("Last behavior ID is too long.", nameof(LastBehaviorId));
        if (PendingTopic is not null && !Enum.IsDefined(PendingTopic.Value))
            throw new ArgumentOutOfRangeException(nameof(PendingTopic));
        if (!Enum.IsDefined(PendingKnowledgeTarget))
            throw new ArgumentOutOfRangeException(nameof(PendingKnowledgeTarget));
        if (LastTool is not null && (LastTool.Length > 48 || !IsIdentifier(LastTool)))
            throw new ArgumentException("LastTool must be a normalized tool identifier.", nameof(LastTool));
        if (LastToolOutcome is not null && (LastToolOutcome.Length > 64 || !IsIdentifier(LastToolOutcome)))
            throw new ArgumentException("LastToolOutcome must be normalized uppercase text.", nameof(LastToolOutcome));

        static bool IsIdentifier(string value) => value.Length > 0 && value.All(character =>
            character is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_');
    }
}

public sealed record ReplyRequest(
    string ConversationId,
    string TurnId,
    IReadOnlyList<DialogueTurn> Turns,
    NpcDialogueState State,
    NpcPersona Persona,
    int Seed,
    ResponseMode ResponseMode = ResponseMode.Ranked);

public sealed record TurnPlan(
    ResponsePolicy Policy,
    string? ToolSchema,
    string? ResponseCandidateId,
    KnowledgeTarget KnowledgeTarget,
    IReadOnlyList<PendingDialogueAction> PendingActions,
    string? Clarification);

public sealed record ReplyDiagnostics(
    IReadOnlyDictionary<string, double> Confidence,
    IReadOnlyList<PerceptionConstraint> AppliedConstraints,
    ResponseSource ResponseSource,
    string? SelectedCandidate,
    GameToolInvocation? ToolInvocation,
    IReadOnlyList<DialogueSlot> Slots,
    IReadOnlyList<string> OovWords,
    string? FallbackReason,
    int PackedTurnCount,
    int PackedTokenCount);

public sealed record ReplyResult(
    string Text,
    NpcDialogueState State,
    StructuredPerception RawPerception,
    StructuredPerception Perception,
    TurnPlan Plan,
    ResponseTone Tone,
    ReplyDiagnostics Diagnostics);

public sealed record ResponseCandidate(
    string Id,
    string Text,
    IReadOnlyList<string> BehaviorIds,
    IReadOnlyList<ResponsePolicy> AllowedPolicies,
    IReadOnlyList<DialogueDomain> AllowedDomains,
    IReadOnlyList<ResponseTone> AllowedTones,
    bool RequiresToolResult,
    IReadOnlyList<string> TemplateFields,
    IReadOnlyList<string> EligibilityConditions);
