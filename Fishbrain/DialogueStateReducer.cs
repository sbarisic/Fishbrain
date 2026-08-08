using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Fishbrain;

internal static class DialogueStateReducer
{
    public static NpcDialogueState Apply(
        NpcDialogueState state, StructuredPerception perception, TurnPlan plan, GameToolResult? toolResult)
    {
        state.Validate();
        var hostileEvent = perception.Stance == DialogueStance.Hostile ||
            perception.SpeechActs.Contains(SpeechAct.Threaten) || perception.ContentFlags.Contains(ContentFlag.Threat);
        var repairEvent = perception.SpeechActs.Contains(SpeechAct.Apologize);
        var gratitudeEvent = perception.SpeechActs.Contains(SpeechAct.Thank);
        var successfulHelp = toolResult?.Success == true;

        var calmTurns = hostileEvent ? 0 : Math.Min(3, state.CalmTurns + 1);
        var hostility = state.Hostility;
        if (hostileEvent) hostility = (byte)Math.Min(3, hostility + 1);
        else if (repairEvent || calmTurns >= 3) hostility = (byte)Math.Max(0, hostility - 1);
        if (calmTurns >= 3) calmTurns = 0;

        var threat = hostileEvent && (perception.SpeechActs.Contains(SpeechAct.Threaten) || perception.ContentFlags.Contains(ContentFlag.Threat))
            ? Math.Min(3, state.ThreatLevel + 1)
            : repairEvent || calmTurns == 0 ? Math.Max(0, state.ThreatLevel - 1) : state.ThreatLevel;
        var rapport = Math.Clamp(state.Rapport + (hostileEvent ? -1 : repairEvent || gratitudeEvent || successfulHelp ? 1 : 0), 0, 3);
        var trust = Math.Clamp(state.Trust + (hostileEvent ? -1 : repairEvent || successfulHelp ? 1 : 0), 0, 3);
        var familiarity = Math.Clamp(state.Familiarity + (perception.SpeechActs.Contains(SpeechAct.Greet) ? 1 : 0), 0, 3);
        var mood = hostileEvent ? NpcMood.Annoyed
            : hostility > 0 || threat > 0 ? NpcMood.Cautious
            : perception.Affect == UserAffect.Friendly ? NpcMood.Friendly
            : NpcMood.Neutral;
        var domains = perception.Domains.Concat(state.ActiveDomains).Distinct().Take(4).ToArray();
        var goals = perception.Goals.Where(goal => goal != DialogueGoal.None)
            .Concat(state.ActiveGoals).Distinct().Take(4).ToArray();
        var clarification = plan.Policy == ResponsePolicy.Clarify
            ? new PendingClarification(plan.Clarification ?? "PLEASE EXPLAIN.", plan.ToolSchema,
                plan.ToolSchema is null ? [] : plan.MissingSlots?.ToArray() ?? [])
            : null;
        var transaction = plan.ToolSchema is "BUY" or "SELL"
            ? new DialogueTransaction(plan.ToolSchema,
                perception.Slots.FirstOrDefault(slot => slot.Type == SlotType.Item)?.Value ?? "UNKNOWN",
                int.TryParse(perception.Slots.FirstOrDefault(slot => slot.Type == SlotType.Quantity)?.Value,
                    NumberStyles.None, CultureInfo.InvariantCulture, out var quantity) ? quantity : 0,
                toolResult?.Success == true ? "COMPLETE" : toolResult is null ? "PENDING" : "FAILED")
            : state.CurrentTransaction;
        var authoritativePlace = ToolField("PLACE") ?? ToolField("LOCATION");
        var contextualPlace = plan.KnowledgeTarget == KnowledgeTarget.WorldFact
            ? Latest(SlotType.Other)
            : null;
        var references = new DialogueReferenceState(
            Latest(SlotType.Person) ?? state.References.Person,
            authoritativePlace ?? Latest(SlotType.Place) ?? contextualPlace ?? state.References.Place,
            Latest(SlotType.Item) ?? state.References.Item,
            Latest(SlotType.Vehicle) ?? state.References.Vehicle,
            Latest(SlotType.System) ?? state.References.System);
        var result = new NpcDialogueState((byte)rapport, (byte)trust, (byte)familiarity, hostility,
            mood, domains, perception.ResponseCandidateId, perception.Affect, clarification, transaction,
            goals, plan.PendingActions.Take(3).ToArray(), references, (byte)threat, (byte)calmTurns,
            perception.Domains.Count == 0 ? null : perception.Domains[0], perception.KnowledgeTarget,
            toolResult is null ? state.LastTool : plan.ToolSchema,
            toolResult is null ? state.LastToolOutcome : toolResult.Success ? "SUCCESS" : toolResult.ErrorCode ?? "FAILED");
        result.Validate();
        return result;

        string? Latest(SlotType type) => perception.Slots.LastOrDefault(slot => slot.Type == type)?.Value is { } value
            ? value.Length <= 32 ? value : value[..32] : null;
        string? ToolField(string name) => toolResult?.Fields.TryGetValue(name, out var value) == true
            ? value.Length <= 32 ? value : value[..32]
            : null;
    }
}

