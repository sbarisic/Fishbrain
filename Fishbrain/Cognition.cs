namespace Fishbrain;

public enum NpcMood { Neutral, Friendly, Cautious, Annoyed }
public enum DialogueIntent
{
    Unknown, Greeting, Farewell, Wellbeing, Identity, Assistance, Clarification,
    Activity, Silence, Gratitude, Apology, Agreement, Refusal, Hostility, GameFact
}
public enum UserAffect { Neutral, Friendly, Distressed, Frustrated, Hostile }
public enum ResponseAction { Respond, Clarify, CallTool, Refuse, NoResponse }
public enum ResponseTone { Neutral, Warm, Calm, Cold }
public enum DialogueTopic { None, Self, Wellbeing, Assistance, Activity, Relationship, GameFact }
public enum NpcGoal { None, BuildRapport, HelpPlayer, ClarifyRequest, ResolveGameFact, EndConversation, Deescalate }

public sealed record NpcState(
    byte Rapport,
    NpcMood Mood,
    DialogueIntent LastIntent,
    UserAffect LastAffect,
    DialogueTopic ActiveTopic,
    NpcGoal ActiveGoal)
{
    public static NpcState Initial { get; } = new(
        1, NpcMood.Neutral, DialogueIntent.Unknown, UserAffect.Neutral,
        DialogueTopic.None, NpcGoal.None);

    public void Validate()
    {
        if (Rapport > 3) throw new ArgumentOutOfRangeException(nameof(Rapport), "Rapport must be between 0 and 3.");
        ValidateEnum(Mood, nameof(Mood));
        ValidateEnum(LastIntent, nameof(LastIntent));
        ValidateEnum(LastAffect, nameof(LastAffect));
        ValidateEnum(ActiveTopic, nameof(ActiveTopic));
        ValidateEnum(ActiveGoal, nameof(ActiveGoal));
    }

    private static void ValidateEnum<T>(T value, string name) where T : struct, Enum
    {
        if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(name, $"Unknown {typeof(T).Name} value.");
    }
}

public sealed record TurnPerception(
    DialogueIntent Intent,
    UserAffect Affect,
    bool ResponseExpected);

public sealed record TurnDecision(ResponseAction Action);

public sealed record ReplyResult(
    string Text,
    NpcState State,
    TurnPerception Perception,
    TurnDecision Decision,
    ResponseTone Tone);

public sealed record CognitiveTransition(NpcState State, ResponseTone Tone);

public static class DialogueText
{
    public static string Normalize(string text) => Tokenizer.Normalize(text);
    public static bool IsCanonical(string text) =>
        text is not null && string.Equals(text, Tokenizer.Normalize(text), StringComparison.Ordinal);
}

public static class Cognition
{
    public static ResponseAction ActionFor(TurnPerception perception)
    {
        ArgumentNullException.ThrowIfNull(perception);
        Validate(perception);
        if (!perception.ResponseExpected) return ResponseAction.NoResponse;
        return perception.Intent switch
        {
            DialogueIntent.GameFact => ResponseAction.CallTool,
            DialogueIntent.Unknown => ResponseAction.Clarify,
            DialogueIntent.Hostility => ResponseAction.Refuse,
            _ => ResponseAction.Respond
        };
    }

    public static CognitiveTransition Apply(
        NpcState state,
        TurnPerception perception,
        TurnDecision decision,
        bool toolSucceeded = false)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(perception);
        ArgumentNullException.ThrowIfNull(decision);
        state.Validate();
        Validate(perception);
        if (!Enum.IsDefined(decision.Action)) throw new ArgumentOutOfRangeException(nameof(decision.Action));
        if (decision.Action != ActionFor(perception))
            throw new ArgumentException("The response action is invalid for the perception.", nameof(decision));

        var hostile = perception.Intent == DialogueIntent.Hostility || perception.Affect == UserAffect.Hostile;
        var rapport = (int)state.Rapport;
        if (hostile) rapport--;
        else if (perception.Intent is DialogueIntent.Gratitude or DialogueIntent.Apology &&
                 perception.Affect == UserAffect.Friendly)
            rapport++;
        rapport = Math.Clamp(rapport, 0, 3);

        var mood = hostile
            ? rapport == 0 ? NpcMood.Annoyed : NpcMood.Cautious
            : perception.Affect is UserAffect.Frustrated or UserAffect.Distressed
                ? NpcMood.Cautious
                : perception.Intent == DialogueIntent.Farewell
                    ? NpcMood.Neutral
                    : perception.Affect == UserAffect.Friendly ||
                      perception.Intent is DialogueIntent.Greeting or DialogueIntent.Gratitude or DialogueIntent.Apology
                        ? rapport >= 2 ? NpcMood.Friendly : NpcMood.Neutral
                        : state.Mood;

        var topic = perception.Intent switch
        {
            DialogueIntent.Identity => DialogueTopic.Self,
            DialogueIntent.Wellbeing => DialogueTopic.Wellbeing,
            DialogueIntent.Assistance or DialogueIntent.Clarification => DialogueTopic.Assistance,
            DialogueIntent.Activity => DialogueTopic.Activity,
            DialogueIntent.GameFact => DialogueTopic.GameFact,
            DialogueIntent.Unknown => state.ActiveTopic,
            _ => DialogueTopic.Relationship
        };

        var goal = perception.Intent switch
        {
            DialogueIntent.Greeting or DialogueIntent.Silence or DialogueIntent.Gratitude or
                DialogueIntent.Apology or DialogueIntent.Agreement => NpcGoal.BuildRapport,
            DialogueIntent.Assistance => NpcGoal.HelpPlayer,
            DialogueIntent.Clarification or DialogueIntent.Unknown => NpcGoal.ClarifyRequest,
            DialogueIntent.GameFact => NpcGoal.ResolveGameFact,
            DialogueIntent.Farewell => NpcGoal.EndConversation,
            DialogueIntent.Hostility => NpcGoal.Deescalate,
            _ => state.ActiveGoal
        };
        if (perception.Affect == UserAffect.Distressed) goal = NpcGoal.HelpPlayer;
        if (hostile) goal = NpcGoal.Deescalate;
        if (toolSucceeded && goal == NpcGoal.ResolveGameFact) goal = NpcGoal.None;

        var updated = new NpcState(
            (byte)rapport, mood, perception.Intent, perception.Affect, topic, goal);
        return new CognitiveTransition(updated, ToneFor(mood));
    }

    public static ResponseTone ToneFor(NpcMood mood) => mood switch
    {
        NpcMood.Neutral => ResponseTone.Neutral,
        NpcMood.Friendly => ResponseTone.Warm,
        NpcMood.Cautious => ResponseTone.Calm,
        NpcMood.Annoyed => ResponseTone.Cold,
        _ => throw new ArgumentOutOfRangeException(nameof(mood))
    };

    private static void Validate(TurnPerception perception)
    {
        if (!Enum.IsDefined(perception.Intent)) throw new ArgumentOutOfRangeException(nameof(perception.Intent));
        if (!Enum.IsDefined(perception.Affect)) throw new ArgumentOutOfRangeException(nameof(perception.Affect));
    }
}
