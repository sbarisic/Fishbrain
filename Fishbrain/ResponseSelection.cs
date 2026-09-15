using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Fishbrain;

public sealed partial class LegacyBrain
{
    private static (ResponsePlanDefinition Plan, string Text)? RankResponse(
            StructuredPerception perception, string input, int seed, GameToolRegistry tools)
    {
        var plans = ResponseCatalog.Plans.Where(plan =>
            plan.Policy == perception.Policy &&
            (plan.Domain is null || perception.Domains.Contains(plan.Domain.Value)) &&
            (plan.KnowledgeTarget == KnowledgeTarget.None || plan.KnowledgeTarget == perception.KnowledgeTarget) &&
            (plan.SpeechActs.Count == 0 || plan.SpeechActs.Intersect(perception.SpeechActs).Any()))
            .Select(plan => (Plan: plan, Score: PlanScore(plan, perception, input, seed)))
            .OrderByDescending(item => item.Score).ThenBy(item => item.Plan.Id, StringComparer.Ordinal)
            .Take(5).ToArray();
        if (plans.Length == 0) return null;
        var bestPlan = plans[0].Plan;
        var text = bestPlan.Variations.Select((variation, index) => (Text: variation,
                Score: TokenOverlap(variation, input) * 0.08 + StableTie(bestPlan.Id + ":" + index, seed)))
            .OrderByDescending(item => item.Score).ThenBy(item => item.Text, StringComparer.Ordinal).First().Text;
        return (bestPlan, text);
    }

    private static double PlanScore(ResponsePlanDefinition plan, StructuredPerception perception, string input, int seed) =>
        (plan.Id == perception.ResponseCandidateId ? 5.0 : 0.0) +
        (plan.Domain is not null && perception.Domains.Contains(plan.Domain.Value) ? 1.0 : 0.0) +
        plan.SpeechActs.Intersect(perception.SpeechActs).Count() * 0.8 +
        plan.Keywords.Count(keyword => ContainsPhrase(input, keyword)) * 0.5 +
        StableTie(plan.Id, seed);

    private static string DomainFallback(StructuredPerception perception)
    {
        var domain = perception.Domains.FirstOrDefault();
        return perception.Policy switch
        {
            ResponsePolicy.Refuse => "I WILL NOT DO THAT.",
            ResponsePolicy.Defer => $"I CANNOT HANDLE {SplitWords(domain.ToString())} RIGHT NOW.",
            ResponsePolicy.Acknowledge => $"I UNDERSTAND YOUR {SplitWords(domain.ToString())} MESSAGE.",
            ResponsePolicy.Negotiate => "LET US AGREE ON FAIR TERMS.",
            _ => $"TELL ME WHAT YOU NEED TO KNOW ABOUT {SplitWords(domain.ToString())}."
        };
    }

    private static string ContextualGuidance(IReadOnlyList<DialogueDomain> domains)
    {
        if (domains.Contains(DialogueDomain.Combat))
            return "SECURE THE IMMEDIATE THREAT FIRST, THEN CONFIRM THE NEXT OBJECTIVE.";
        if (domains.Contains(DialogueDomain.Technology))
            return "CHECK THE MOST URGENT SYSTEM FIRST, THEN CONFIRM THE NEXT STEP.";
        if (domains.Contains(DialogueDomain.Survival))
            return "MOVE TO SAFETY FIRST, THEN CHECK YOUR SUPPLIES AND NEXT OBJECTIVE.";
        return "HANDLE THE MOST URGENT RISK FIRST, THEN CONFIRM THE NEXT OBJECTIVE.";
    }

    private static string CandidateIdFor(
        IReadOnlyList<SpeechAct> acts,
        IReadOnlyList<DialogueDomain> domains,
        ResponsePolicy policy,
        KnowledgeTarget target)
    {
        if (acts.Contains(SpeechAct.Greet)) return "SOCIAL_GREETING";
        if (acts.Contains(SpeechAct.Farewell)) return "SOCIAL_FAREWELL";
        if (acts.Contains(SpeechAct.Apologize)) return "APOLOGY_ACCEPT";
        if (acts.Contains(SpeechAct.Thank)) return "THANKS_REPLY";
        if (acts.Contains(SpeechAct.Threaten)) return "THREAT_RESPONSE";
        if (policy == ResponsePolicy.Refuse) return "HOSTILE_BOUNDARY";
        if (policy == ResponsePolicy.Clarify) return "CLARIFY";
        if (target == KnowledgeTarget.Capabilities && domains.Contains(DialogueDomain.TradeEconomy)) return "TRADE_OPEN";
        if (domains.Contains(DialogueDomain.TradeEconomy) && policy is ResponsePolicy.Answer or ResponsePolicy.Negotiate) return "TRADE_OPEN";
        if (domains.Contains(DialogueDomain.ItemsInventory)) return "ITEM_REQUEST";
        if (domains.Contains(DialogueDomain.Assistance)) return "ASSISTANCE_OFFER";
        var domain = domains.FirstOrDefault();
        return $"{domain.ToString().ToUpperInvariant()}_{policy.ToString().ToUpperInvariant()}";
    }

    private static NpcState ToModelState(NpcDialogueState state) => new(
        state.Rapport, state.Mood, DialogueIntent.Unknown, state.LastAffect, DialogueTopic.None, NpcGoal.None);

    private static IReadOnlyDictionary<string, double> MergeConfidence(
        IReadOnlyDictionary<string, double> current, string key, double value)
    {
        var result = current.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        result[key] = value;
        return new ReadOnlyDictionary<string, double>(result);
    }

    private static PerceptionConstraint Enforce(string head, string label, string evidence, string reason) =>
        new(PerceptionConstraintOperation.Enforce, head, label, 1.0, 0.99, evidence, reason);
    private static PerceptionConstraint Boost(string head, string label, string evidence, string reason, double amount) =>
        new(PerceptionConstraintOperation.Boost, head, label, amount, 0.95, evidence, reason);

    private static IReadOnlyList<T> AddLimited<T>(IEnumerable<T> current, T value, int maximum) where T : struct, Enum =>
        current.Append(value).Distinct().Take(maximum).ToArray();
    private static IReadOnlyList<T> AddLimited<T>(
        IEnumerable<T> preferred, IEnumerable<T> current, int maximum) where T : struct, Enum =>
        preferred.Concat(current).Distinct().Take(maximum).ToArray();
    private static bool ContainsAny(string text, params string[] values) =>
        values.Any(value => ContainsPhrase(text, value));
    private static bool StartsWithAny(string text, params string[] values) =>
        values.Any(value =>
        {
            var phrase = value.Trim();
            return text.StartsWith(phrase, StringComparison.Ordinal) &&
                   (text.Length == phrase.Length || !Tokenizer.IsIdentifierCharacter(text[phrase.Length]));
        });
    private static bool ContainsPhrase(string text, string value)
    {
        return FindPhrase(text, value) >= 0;
    }

    private static int FindPhrase(string text, string value)
    {
        var phrase = value.Trim();
        if (phrase.Length == 0)
        {
            return -1;
        }

        for (var index = text.IndexOf(phrase, StringComparison.Ordinal);
             index >= 0;
             index = text.IndexOf(phrase, index + 1, StringComparison.Ordinal))
        {
            var before = index == 0 || !Tokenizer.IsIdentifierCharacter(text[index - 1]);
            var end = index + phrase.Length;
            var after = end == text.Length || !Tokenizer.IsIdentifierCharacter(text[end]);
            if (before && after)
            {
                return index;
            }
        }

        return -1;
    }
    private static bool IsAnaphoric(string text)
    {
        var words = Tokenizer.Lex(text).Where(token => token.Kind == LexicalTokenKind.Word)
            .Select(token => token.Text).ToHashSet(StringComparer.Ordinal);
        return words.Overlaps(["IT", "THAT", "THERE", "NOW"]);
    }
    private static bool IsContinuation(string text)
    {
        var bare = text.Trim().TrimEnd('.', '?', '!');
        return bare is "YES" or "CONTINUE" or "NEXT" or "THEN" or "DO IT" or "GO AHEAD" or
            "DO THE NEXT ONE" or "CONTINUE WITH THE NEXT ACTION";
    }
    private static bool IsPlanningFollowUp(string text)
    {
        var bare = text.Trim().TrimEnd('.', '?', '!');
        return bare is "WHAT SHOULD WE DO" or "WHAT DO WE DO" or "WHAT NEXT" or "WHAT SHOULD WE DO NEXT";
    }
    private static bool IsDirectInsult(string text)
    {
        if (ContainsAny(text, "SORRY I CALLED YOU", "APOLOGIZE FOR CALLING YOU", "I SHOULD NOT HAVE CALLED YOU"))
            return false;
        return ContainsAny(text, "IDIOT", "FAGGOT", "NIGGER", "KIKE", "FUCK YOU", "SHUT UP", "I HATE YOU",
            "YOU ARE USELESS", "YOU'RE USELESS");
    }
    private static bool IsIdentityExclusion(string text) =>
        ContainsAny(text, "GERMAN PEOPLE", "JEWISH PEOPLE", "MUSLIM PEOPLE", "BLACK PEOPLE", "WHITE PEOPLE",
            "GAY PEOPLE") &&
        ContainsAny(text, "ONLY NEED TO HAVE", "MUST HAVE", "SHOULD HAVE", "ARE SUPERIOR", "ARE INFERIOR",
            "DO NOT BELONG");
    private static bool IsPoisonReport(string text) => ContainsAny(text,
        "DRANK POISON", "DRINK POISON", "IS POISONED", "WAS POISONED", "ATE POISON");
    private static bool IsClassificationQuestion(string text)
    {
        var bare = text.Trim().TrimEnd('.', '?', '!');
        return StartsWithAny(bare, "WHAT", "WHY", "HOW") && ContainsAny(bare,
            "ITEMS INVENTORY MESSAGE", "SOCIAL MESSAGE", "MESSAGE CLASSIFICATION", "WHY DID YOU CALL THAT");
    }
    private static bool IsIncompleteQuestion(string text) =>
        text.Trim().TrimEnd('.', '?', '!') is "WHAT" or "HUH";
    private static string? UnsupportedActivityCommand(string text, GameToolRegistry tools)
    {
        var bare = text.Trim().TrimEnd('.', '?', '!');
        var command = bare is "FOLLOW" or "FOLLOW ME" || bare.StartsWith("FOLLOW ", StringComparison.Ordinal)
            ? "FOLLOW YOU"
            : bare is "STOP" or "STAY" or "WAIT" || StartsWithAny(bare, "STOP ", "STAY ", "WAIT ")
                ? bare.Split(' ', 2)[0]
                : null;
        if (command is null) return null;
        var schemaName = command == "FOLLOW YOU" ? "FOLLOW" : command;
        return tools.Schemas.Any(schema => schema.Name == schemaName) ? null : command;
    }
    private static bool IsUnsafeDirective(string text) => ContainsAny(text,
        "JUMP OFF", "HELP ME KILL", "ATTACK THE", "KILL YOURSELF");
    private static string CanonicalItem(string item) => item switch { "SWORD" => "IRON SWORD", "POTION" => "HEALTH POTION", _ => item };
    private static int TokenOverlap(string left, string right)
    {
        var words = Tokenizer.Lex(left).Where(token => token.Kind == LexicalTokenKind.Word)
            .Select(token => token.Text).ToHashSet(StringComparer.Ordinal);
        return Tokenizer.Lex(right).Count(token => token.Kind == LexicalTokenKind.Word && words.Contains(token.Text));
    }
    private static double StableTie(string id, int seed)
    {
        uint hash = unchecked((uint)seed);
        foreach (var character in id) hash = (hash ^ character) * 16777619;
        return (hash & 0xffff) / 65535.0 * 0.001;
    }
    private static string SplitWords(string value) =>
        string.Concat(value.Select((character, index) => index > 0 && char.IsUpper(character)
            ? " " + character : character.ToString())).ToUpperInvariant();

    private static readonly IReadOnlyDictionary<string, string> EmptyArguments =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>());

    private sealed record ToolDecision(
        string? Name,
        IReadOnlyDictionary<string, string> Arguments,
        double Confidence,
        bool CanExecute,
        IReadOnlyList<string> Reasons,
        IReadOnlyList<PendingDialogueAction> AdditionalActions)
    {
        public static ToolDecision None { get; } = new(null, EmptyArguments, 1.0, false, [], []);
    }
}
