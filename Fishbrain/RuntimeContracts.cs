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
    RankedCandidate, ToolTemplate, ClarificationTemplate, Fallback, GeneratedExperimental
}

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
    IReadOnlyDictionary<string, double> Confidence)
{
    public static StructuredPerception Empty { get; } = new(
        [], [], [], UserAffect.Neutral, DialogueStance.Neutral, ResponsePolicy.Clarify,
        [], [], null, null,
        new ReadOnlyDictionary<string, double>(new Dictionary<string, double>()));
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
    DialogueReferenceState References)
{
    public static NpcDialogueState Initial { get; } = new(
        1, 1, 0, 0, NpcMood.Neutral, [], null, UserAffect.Neutral,
        null, null, [], [], DialogueReferenceState.Empty);

    public void Validate()
    {
        if (Rapport > 3 || Trust > 3 || Familiarity > 3 || Hostility > 3)
            throw new ArgumentOutOfRangeException(nameof(NpcDialogueState), "Social values must be between 0 and 3.");
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
    }
}

public sealed record ReplyRequest(
    string ConversationId,
    string TurnId,
    IReadOnlyList<DialogueTurn> Turns,
    NpcDialogueState State,
    int Seed,
    ResponseMode ResponseMode = ResponseMode.Ranked);

public sealed record TurnPlan(
    ResponsePolicy Policy,
    string? ToolSchema,
    string? ResponseCandidateId,
    IReadOnlyList<PendingDialogueAction> PendingActions,
    string? Clarification);

public sealed record ReplyDiagnostics(
    IReadOnlyDictionary<string, double> Confidence,
    IReadOnlyList<string> AppliedConstraints,
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
