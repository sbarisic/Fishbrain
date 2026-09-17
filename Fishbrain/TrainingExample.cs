namespace Fishbrain;

internal sealed record TrainingExample(
    string Context,
    string Input,
    DialogueUtterance[] Turns,
    SpeechAct[] SpeechActs,
    DialogueDomain[] Domains,
    DialogueGoal[] Goals,
    UserAffect Affect,
    DialogueStance Stance,
    ResponsePolicy Policy,
    DialogueSlot[] Slots,
    ContentFlag[] ContentFlags,
    string ToolSchema,
    string ResponseCandidateId,
    KnowledgeTarget KnowledgeTarget,
    string Source,
    string SemanticFamilyId,
    IReadOnlySet<string> SupervisedHeads,
    DiscourseFrame Discourse,
    DialogueFact[] InitialFacts,
    DialogueFact[] ExpectedFactState,
    DiscourseResponseAction ResponseAction,
    string[] AcceptableResponseConstraints,
    string? RejectedResponse)
{
    public ReplyRequest? Request { get; init; }
    public ContextualSupervision? Contextual { get; init; }
    public string? Response { get; init; }
    public TrainingEligibility? Training { get; init; }
}
