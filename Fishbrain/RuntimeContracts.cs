using System.Collections.ObjectModel;

namespace Fishbrain;

public enum DialogueRole { Player, Npc }
public enum ResponseMode { Production, DeterministicOnly }
public enum DialogueParticipant { Player, Npc, None }
public enum DiscourseAct { None, Inform, Correct, RejectAssumption, AskExplanation, ReferBack }
public enum DialogueFactKind
{
    Name, Role, Occupation, Origin, Home, Family, Activity, Preference, Dislike, Opinion, Experience
}
public enum DialogueFactProvenance { SessionReported, CallerApproved }
public enum DiscourseResponseAction
{
    None, AcknowledgeFact, AcknowledgeCorrection, ExplainPreviousResponse,
    RepairMisunderstanding, ClarifyReference
}
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
    System, Credential, Action, Other
}
public enum BioTag { B, I }
public enum ResponseSource
{
    RankedCandidate, RankedVariation, ToolTemplate, PersonaTemplate, CapabilityTemplate,
    ClarificationTemplate, Fallback, ConversationalRepair, ConversationalGenerated
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

public sealed record DialogueUtterance(long Sequence, DialogueRole Speaker, string Text);

public sealed record DialogueFact(
    DialogueParticipant Subject,
    DialogueFactKind Kind,
    string Value,
    bool Negated,
    long SourceUtterance,
    double Confidence,
    DialogueFactProvenance Provenance);

public sealed record DialogueTextSpan(
    string NormalizedValue,
    int Start,
    int Length)
{
    public void Validate(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (string.IsNullOrWhiteSpace(NormalizedValue) ||
            NormalizedValue != DialogueText.Normalize(NormalizedValue) ||
            Start < 0 || Length != NormalizedValue.Length ||
            Start + Length > source.Length ||
            source.Substring(Start, Length) != NormalizedValue)
        {
            throw new ArgumentException("Dialogue text span does not match its normalized source text.", nameof(source));
        }
    }
}

public sealed record PlayerConversationProfile
{
    public static PlayerConversationProfile Empty { get; } = new([]);

    public PlayerConversationProfile(IReadOnlyList<DialogueFact> facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        Facts = Array.AsReadOnly(facts.ToArray());
        Validate();
    }

    public IReadOnlyList<DialogueFact> Facts { get; }

    public void Validate()
    {
        if (Facts is null || Facts.Count > 32)
            throw new ArgumentException("A player profile supports at most 32 facts.", nameof(Facts));
        foreach (var fact in Facts)
        {
            ValidateFact(fact, DialogueFactProvenance.CallerApproved);
            if (fact.Subject != DialogueParticipant.Player)
                throw new ArgumentException("Player-profile facts must describe the player.", nameof(Facts));
        }
    }

    internal static void ValidateFact(DialogueFact fact, DialogueFactProvenance provenance)
    {
        ArgumentNullException.ThrowIfNull(fact);
        if (!Enum.IsDefined(fact.Subject) || fact.Subject == DialogueParticipant.None ||
            !Enum.IsDefined(fact.Kind) || !Enum.IsDefined(fact.Provenance) || fact.Provenance != provenance ||
            fact.SourceUtterance < 0 || !double.IsFinite(fact.Confidence) || fact.Confidence is < 0 or > 1 ||
            string.IsNullOrWhiteSpace(fact.Value) || fact.Value.Length > 128 ||
            fact.Value != DialogueText.Normalize(fact.Value))
            throw new ArgumentException("Dialogue fact is invalid.", nameof(fact));
    }
}

public sealed record DialogueTopicSummary(long SourceUtterance, string Topic);

public sealed record ResponseSemanticTrace(
    long UtteranceSequence,
    DiscourseResponseAction Action,
    string Topic,
    IReadOnlyList<DialogueFactKind> ReferencedFacts,
    string? CandidateId,
    string? FallbackReason);

public sealed record DiscourseFrame(
    DiscourseAct Act,
    DialogueParticipant Subject,
    DialogueParticipant Target,
    DialogueFactKind? FactKind,
    DialogueTextSpan? FactValueSpan,
    bool Negated,
    long? AntecedentUtterance,
    double Confidence,
    string Evidence)
{
    public static DiscourseFrame Empty { get; } = new(
        DiscourseAct.None, DialogueParticipant.None, DialogueParticipant.None,
        null, null, false, null, 1.0, "NONE");
}

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
    IReadOnlyDictionary<string, double> Confidence,
    DiscourseFrame? Discourse = null)
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
public sealed record PendingDialogueAction(string Action, string? ToolSchema, IReadOnlyDictionary<string, string> Arguments)
{
    public long? SourceUtterance { get; init; }
}

public sealed record DialogueReferenceState(
    string? Person,
    string? Place,
    string? Item,
    string? Vehicle,
    string? System,
    long? UtteranceSequence)
{
    public static DialogueReferenceState Empty { get; } = new(null, null, null, null, null, null);
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
    string? LastToolOutcome,
    IReadOnlyList<DialogueFact> SessionFacts,
    IReadOnlyList<DialogueTopicSummary> TopicSummaries,
    ResponseSemanticTrace? LastResponseTrace)
{
    public IReadOnlyList<DialogueAgendaEntry> Agenda { get; init; } = Array.Empty<DialogueAgendaEntry>();
    public static NpcDialogueState Initial { get; } = new(
        1, 1, 0, 0, NpcMood.Neutral, [], null, UserAffect.Neutral,
        null, null, [], [], DialogueReferenceState.Empty, 0, 0, null,
        KnowledgeTarget.None, null, null, [], [], null);

    public void Validate()
    {
        if (Agenda is null || Agenda.Count > 4 || Agenda.Any(a => a is null || !Enum.IsDefined(a.Kind) ||
            !Enum.IsDefined(a.Status) || a.SourceTurn < 0 || string.IsNullOrWhiteSpace(a.Subject) || a.Subject.Length > 128 ||
            a.Subject != DialogueText.Normalize(a.Subject)))
            throw new ArgumentException("Dialogue agenda is invalid.");
        if (Rapport > 3 || Trust > 3 || Familiarity > 3 || Hostility > 3)
            throw new ArgumentOutOfRangeException(nameof(NpcDialogueState), "Social values must be between 0 and 3.");
        if (ThreatLevel > 3 || CalmTurns > 3)
            throw new ArgumentOutOfRangeException(nameof(NpcDialogueState), "Threat and calm values must be between 0 and 3.");
        if (!Enum.IsDefined(Mood) || !Enum.IsDefined(LastAffect))
            throw new ArgumentOutOfRangeException(nameof(NpcDialogueState), "State contains an unknown enum value.");
        if (ActiveDomains is null || ActiveDomains.Count > 4 || ActiveDomains.Any(value => !Enum.IsDefined(value)) ||
            ActiveDomains.Distinct().Count() != ActiveDomains.Count)
            throw new ArgumentException("State supports at most four valid active domains.", nameof(ActiveDomains));
        if (ActiveGoals is null || ActiveGoals.Count > 4 || ActiveGoals.Any(value => !Enum.IsDefined(value)) ||
            ActiveGoals.Distinct().Count() != ActiveGoals.Count)
            throw new ArgumentException("State supports at most four valid active goals.", nameof(ActiveGoals));
        if (PendingActions is null || PendingActions.Count > 3 || PendingActions.Any(action => action is null))
            throw new ArgumentException("State supports at most three pending actions.", nameof(PendingActions));
        ArgumentNullException.ThrowIfNull(References);
        foreach (var value in new[] { References.Person, References.Place, References.Item, References.Vehicle, References.System })
            if (value is not null && (value.Length is < 1 or > 32 || value != DialogueText.Normalize(value)))
                throw new ArgumentException("Reference identifiers must be 1-32 normalized characters.", nameof(References));
        if (References.UtteranceSequence is < 0)
            throw new ArgumentException("An utterance reference must be non-negative.", nameof(References));
        if (SessionFacts is null || SessionFacts.Count > 16)
            throw new ArgumentException("State supports at most 16 session facts.", nameof(SessionFacts));
        foreach (var fact in SessionFacts)
            PlayerConversationProfile.ValidateFact(fact, DialogueFactProvenance.SessionReported);
        if (TopicSummaries is null || TopicSummaries.Count > 8 || TopicSummaries.Any(summary =>
                summary is null || summary.SourceUtterance < 0 || string.IsNullOrWhiteSpace(summary.Topic) ||
                summary.Topic.Length > 128 || summary.Topic != DialogueText.Normalize(summary.Topic)))
            throw new ArgumentException("State supports at most eight valid topic summaries.", nameof(TopicSummaries));
        if (LastResponseTrace is not null)
        {
            if (LastResponseTrace.UtteranceSequence < 0 || !Enum.IsDefined(LastResponseTrace.Action) ||
                string.IsNullOrWhiteSpace(LastResponseTrace.Topic) || LastResponseTrace.Topic.Length > 128 ||
                LastResponseTrace.Topic != DialogueText.Normalize(LastResponseTrace.Topic) ||
                LastResponseTrace.ReferencedFacts is null ||
                LastResponseTrace.ReferencedFacts.Any(value => !Enum.IsDefined(value)) ||
                LastResponseTrace.CandidateId is { } candidateId &&
                (candidateId.Length > 64 || !IsIdentifier(candidateId)) ||
                LastResponseTrace.FallbackReason is { } fallbackReason &&
                (fallbackReason.Length > 64 || !IsIdentifier(fallbackReason)))
                throw new ArgumentException("Last response trace is invalid.", nameof(LastResponseTrace));
        }
        if (LastBehaviorId is not null && (LastBehaviorId.Length > 64 || !IsIdentifier(LastBehaviorId)))
            throw new ArgumentException("Last behavior ID must be a normalized identifier.", nameof(LastBehaviorId));
        if (PendingTopic is not null && !Enum.IsDefined(PendingTopic.Value))
            throw new ArgumentOutOfRangeException(nameof(PendingTopic));
        if (!Enum.IsDefined(PendingKnowledgeTarget))
            throw new ArgumentOutOfRangeException(nameof(PendingKnowledgeTarget));
        if (LastTool is not null && (LastTool.Length > 48 || !IsIdentifier(LastTool)))
            throw new ArgumentException("LastTool must be a normalized tool identifier.", nameof(LastTool));
        if (LastToolOutcome is not null && (LastToolOutcome.Length > 64 || !IsIdentifier(LastToolOutcome)))
            throw new ArgumentException("LastToolOutcome must be normalized uppercase text.", nameof(LastToolOutcome));

        if (PendingClarification is not null)
        {
            ValidateText(PendingClarification.Question, 256, nameof(PendingClarification));
            if (PendingClarification.ToolSchema is not null &&
                (PendingClarification.ToolSchema.Length > 48 || !IsIdentifier(PendingClarification.ToolSchema)))
                throw new ArgumentException("Pending clarification tool must be a normalized identifier.", nameof(PendingClarification));
            if (PendingClarification.MissingSlots is null || PendingClarification.MissingSlots.Count > 8 ||
                PendingClarification.ToolSchema is not null && PendingClarification.MissingSlots.Count == 0 ||
                PendingClarification.MissingSlots.Distinct(StringComparer.Ordinal).Count() != PendingClarification.MissingSlots.Count ||
                PendingClarification.MissingSlots.Any(slot => slot.Length > 48 || !IsIdentifier(slot)))
                throw new ArgumentException("Pending clarification slots are invalid.", nameof(PendingClarification));
        }
        if (CurrentTransaction is not null)
        {
            if (!IsIdentifier(CurrentTransaction.Kind) || !IsIdentifier(CurrentTransaction.Status) ||
                CurrentTransaction.Quantity < 0)
                throw new ArgumentException("Current transaction metadata is invalid.", nameof(CurrentTransaction));
            ValidateText(CurrentTransaction.Item, 128, nameof(CurrentTransaction));
        }
        foreach (var action in PendingActions)
        {
            if (!IsIdentifier(action.Action) || action.ToolSchema is not null && !IsIdentifier(action.ToolSchema) ||
                action.Arguments is null || action.Arguments.Count > 8 || action.SourceUtterance is < 0)
                throw new ArgumentException("Pending action metadata is invalid.", nameof(PendingActions));
            foreach (var argument in action.Arguments)
            {
                if (!IsIdentifier(argument.Key))
                    throw new ArgumentException("Pending action argument names must be identifiers.", nameof(PendingActions));
                ValidateText(argument.Value, 128, nameof(PendingActions));
            }
        }

        static bool IsIdentifier(string value) => value.Length > 0 && value.All(character =>
            character is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_');
        static void ValidateText(string value, int maximum, string name)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || value != DialogueText.Normalize(value))
                throw new ArgumentException($"{name} must contain normalized uppercase text.", name);
        }
    }
}

public sealed record ReplyRequest(
    string ConversationId,
    string TurnId,
    IReadOnlyList<DialogueUtterance> Utterances,
    NpcDialogueState State,
    NpcPersona Persona,
    PlayerConversationProfile PlayerProfile,
    long ResponseSequence,
    int Seed,
    ResponseMode ResponseMode = ResponseMode.Production);

public sealed record TurnPlan(
    ResponsePolicy Policy,
    string? ToolSchema,
    string? ResponseCandidateId,
    KnowledgeTarget KnowledgeTarget,
    IReadOnlyList<PendingDialogueAction> PendingActions,
    string? Clarification,
    IReadOnlyList<string>? MissingSlots = null,
    DiscourseResponseAction DiscourseAction = DiscourseResponseAction.None,
    long? AntecedentUtterance = null);

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
    ReplyDiagnostics Diagnostics)
{
    public ContextualDiagnostics? Contextual { get; init; }
}

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
