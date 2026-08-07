using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Fishbrain;

public sealed partial class Brain
{
    private const double ReadOnlyToolPrecisionThreshold = 0.95;
    private const double MutatingToolPrecisionThreshold = 0.99;

    private static readonly ResponseCandidate[] V10Candidates =
    [
        Candidate("SOCIAL_GREETING", "GREETINGS, TRAVELER.", [ResponsePolicy.Answer, ResponsePolicy.Acknowledge], [DialogueDomain.Social]),
        Candidate("SOCIAL_FAREWELL", "UNTIL NEXT TIME.", [ResponsePolicy.Answer, ResponsePolicy.Acknowledge], [DialogueDomain.Social]),
        Candidate("IDENTITY_TRAVELER", "I AM A TRAVELER FROM THIS VILLAGE.", [ResponsePolicy.Answer], [DialogueDomain.Identity]),
        Candidate("WELLBEING_CALM", "I AM DOING WELL, THANK YOU.", [ResponsePolicy.Answer], [DialogueDomain.Wellbeing]),
        Candidate("ASSISTANCE_ASK", "WHAT DO YOU NEED?", [ResponsePolicy.Answer, ResponsePolicy.Acknowledge], [DialogueDomain.Assistance]),
        Candidate("ACTIVITY_HELP", "I AM HERE TO HELP.", [ResponsePolicy.Answer], [DialogueDomain.Activity]),
        Candidate("ACKNOWLEDGE", "I UNDERSTAND.", [ResponsePolicy.Acknowledge], []),
        Candidate("REFUSAL", "I WILL NOT DO THAT.", [ResponsePolicy.Refuse], []),
        Candidate("HOSTILE_BOUNDARY", "I WILL NOT ARGUE WITH YOU.", [ResponsePolicy.Refuse], [DialogueDomain.Social]),
        Candidate("CLARIFY", "PLEASE EXPLAIN WHAT YOU NEED.", [ResponsePolicy.Clarify], []),
        Candidate("NEGOTIATE", "LET US AGREE ON FAIR TERMS.", [ResponsePolicy.Negotiate], [DialogueDomain.TradeEconomy]),
        Candidate("DEFER", "I CANNOT DO THAT NOW.", [ResponsePolicy.Defer], []),
        Candidate("LOCATION_UNAVAILABLE", "I CANNOT CHECK THAT LOCATION.", [ResponsePolicy.Answer, ResponsePolicy.Defer], [DialogueDomain.LocationNavigation]),
        Candidate("TRADE_UNAVAILABLE", "I CANNOT TRADE WITHOUT ACCESS TO WARES.", [ResponsePolicy.Answer, ResponsePolicy.Defer], [DialogueDomain.TradeEconomy])
    ];

    public ReplyResult Reply(ReplyRequest request, GameToolRegistry tools)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(tools);
        ValidateRequest(request);
        var packed = PackTurns(request.Turns);
        var current = DialogueText.Normalize(request.Turns[^1].Text);
        var legacyState = ToLegacyState(request.State);
        var rawLegacy = DebugPredictRawPerception(current, legacyState);
        var constrainedLegacy = Cognition.Constrain(rawLegacy, current);
        var slots = ExtractSlots(current);
        var raw = ComposePerception(rawLegacy, current, slots, tools, constrained: false);
        var constraints = new List<string>();
        var perception = ComposePerception(constrainedLegacy, current, slots, tools, constrained: true);
        if (raw != perception) constraints.Add("DETERMINISTIC_COGNITIVE_CONSTRAINTS");

        var toolDecision = SelectTool(current, slots, tools);
        if (toolDecision.Name is not null)
        {
            perception = perception with
            {
                ToolSchema = toolDecision.Name,
                Policy = toolDecision.CanExecute ? ResponsePolicy.ExecuteTool : ResponsePolicy.Clarify,
                Confidence = MergeConfidence(perception.Confidence, "TOOL", toolDecision.Confidence)
            };
            if (!toolDecision.CanExecute) constraints.Add("TOOL_CONFIDENCE_OR_SLOT_GATE");
        }

        GameToolInvocation? invocation = null;
        GameToolResult? toolResult = null;
        string text;
        string? selectedCandidate = null;
        string? fallbackReason = null;
        ResponseSource source;
        var pendingActions = toolDecision.AdditionalActions.ToList();

        if (perception.Policy == ResponsePolicy.ExecuteTool && toolDecision.Name is not null)
        {
            if (!tools.TryGet(toolDecision.Name, out var tool))
                throw new InvalidOperationException($"Selected unregistered tool '{toolDecision.Name}'.");
            invocation = new GameToolInvocation(toolDecision.Name, toolDecision.Arguments,
                GameToolRegistry.IdempotencyKey(request.ConversationId, request.TurnId));
            toolResult = GameToolRegistry.InvokeValidated(tool, invocation);
            text = GameToolRegistry.Render(tool.Schema, toolResult);
            source = ResponseSource.ToolTemplate;
        }
        else if (perception.Policy == ResponsePolicy.NoResponse)
        {
            text = string.Empty;
            source = ResponseSource.RankedCandidate;
            selectedCandidate = "NO_RESPONSE";
        }
        else if (request.ResponseMode == ResponseMode.GeneratedExperimental)
        {
            text = DebugReplyWithoutMemory(current, legacyState).Text;
            source = ResponseSource.GeneratedExperimental;
        }
        else if (perception.Policy == ResponsePolicy.Clarify)
        {
            text = ClarificationFor(toolDecision);
            source = ResponseSource.ClarificationTemplate;
        }
        else
        {
            var ranked = RankCandidates(perception, current, request.Seed, tools);
            if (ranked is null)
            {
                text = "I DO NOT KNOW.";
                source = ResponseSource.Fallback;
                fallbackReason = "NO_ELIGIBLE_CANDIDATE_ABOVE_CONFIDENCE";
            }
            else
            {
                text = ranked.Text;
                selectedCandidate = ranked.Id;
                source = ResponseSource.RankedCandidate;
                perception = perception with { ResponseCandidateId = ranked.Id };
            }
        }

        if (text.Length > 256) throw new InvalidDataException("Runtime produced an overlength response.");
        if (text.Length > 0 && !DialogueText.IsCanonical(text))
            throw new InvalidDataException("Runtime produced noncanonical response text.");

        var plan = new TurnPlan(perception.Policy, perception.ToolSchema, selectedCandidate,
            pendingActions, perception.Policy == ResponsePolicy.Clarify ? text : null);
        var state = DialogueStateReducer.Apply(request.State, perception, plan, toolResult);
        var tone = Cognition.ToneFor(state.Mood);
        var diagnostics = new ReplyDiagnostics(
            perception.Confidence, constraints.ToArray(), source, selectedCandidate, invocation,
            slots, _tokenizer.UnknownWords(current), fallbackReason, packed.TurnCount, packed.TokenCount);
        return new ReplyResult(text, state, raw, perception, plan, tone, diagnostics);
    }

    private static void ValidateRequest(ReplyRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ConversationId) || request.ConversationId.Length > 128)
            throw new ArgumentException("ConversationId must contain 1-128 characters.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.TurnId) || request.TurnId.Length > 128)
            throw new ArgumentException("TurnId must contain 1-128 characters.", nameof(request));
        if (request.Turns is null || request.Turns.Count == 0)
            throw new ArgumentException("At least one structured turn is required.", nameof(request));
        if (request.Turns[^1].Role != DialogueRole.Player)
            throw new ArgumentException("The final structured turn must be a player turn.", nameof(request));
        if (request.Turns.Any(turn => string.IsNullOrWhiteSpace(turn.Text)))
            throw new ArgumentException("Structured turns cannot be empty.", nameof(request));
        request.State.Validate();
    }

    private (string Text, int TurnCount, int TokenCount) PackTurns(IReadOnlyList<DialogueTurn> turns)
    {
        var retained = new List<(string Text, int Tokens)>();
        var count = 0;
        for (var index = turns.Count - 1; index >= 0; index--)
        {
            var normalized = DialogueText.Normalize(turns[index].Text);
            var role = turns[index].Role == DialogueRole.Player ? "PLAYER" : "NPC";
            var complete = role + " " + DialogueText.TerminateTurn(normalized);
            var tokens = _tokenizer.Encode(complete).Length;
            if (index != turns.Count - 1 && count + tokens > Config.ContextLength) break;
            retained.Add((complete, tokens));
            count += tokens;
        }
        retained.Reverse();
        return (string.Join(' ', retained.Select(item => item.Text)), retained.Count, count);
    }

    private StructuredPerception ComposePerception(
        TurnPerception legacy, string current, IReadOnlyList<DialogueSlot> slots,
        GameToolRegistry tools, bool constrained)
    {
        var speechActs = SpeechActsFor(legacy.Intent, current);
        var domains = DomainsFor(legacy.Intent, current);
        var goals = GoalsFor(legacy.Intent, current);
        var content = ContentFor(current);
        var stance = legacy.Affect switch
        {
            UserAffect.Friendly => DialogueStance.Friendly,
            UserAffect.Hostile => DialogueStance.Hostile,
            UserAffect.Frustrated or UserAffect.Distressed => DialogueStance.Cautious,
            _ => DialogueStance.Neutral
        };
        var policy = PolicyFor(legacy, speechActs, current);
        var tool = SelectTool(current, slots, tools);
        if (tool.Name is not null) policy = tool.CanExecute ? ResponsePolicy.ExecuteTool : ResponsePolicy.Clarify;
        var confidence = new ReadOnlyDictionary<string, double>(new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["SPEECH_ACT"] = constrained ? 0.98 : 0.90,
            ["DOMAIN"] = constrained ? 0.98 : 0.90,
            ["GOAL"] = constrained ? 0.96 : 0.88,
            ["AFFECT"] = constrained ? 0.97 : 0.90,
            ["STANCE"] = constrained ? 0.97 : 0.90,
            ["POLICY"] = constrained ? 0.99 : 0.90,
            ["SLOTS"] = slots.Count == 0 ? 1.0 : slots.Min(slot => slot.Confidence),
            ["CONTENT"] = 0.99,
            ["TOOL"] = tool.Confidence,
            ["RESPONSE_CANDIDATE"] = 0.90
        });
        return new StructuredPerception(speechActs, domains, goals, legacy.Affect, stance, policy,
            slots, content, tool.Name, CandidateIdFor(legacy.Intent, policy, domains), confidence);
    }

    private static IReadOnlyList<SpeechAct> SpeechActsFor(DialogueIntent intent, string text)
    {
        var values = new HashSet<SpeechAct>();
        values.Add(intent switch
        {
            DialogueIntent.Greeting => SpeechAct.Greet,
            DialogueIntent.Farewell => SpeechAct.Farewell,
            DialogueIntent.Directive or DialogueIntent.UnsafeDirective => SpeechAct.Order,
            DialogueIntent.Refusal => SpeechAct.Refuse,
            DialogueIntent.Gratitude => SpeechAct.Thank,
            DialogueIntent.Apology => SpeechAct.Apologize,
            DialogueIntent.Agreement => SpeechAct.Accept,
            DialogueIntent.Hostility => SpeechAct.Challenge,
            DialogueIntent.TradeRequest => SpeechAct.Negotiate,
            DialogueIntent.Statement or DialogueIntent.Activity => SpeechAct.Inform,
            _ when text.EndsWith('?') => SpeechAct.Ask,
            _ => SpeechAct.Request
        });
        var padded = " " + text + " ";
        if (text.EndsWith('?')) values.Add(SpeechAct.Ask);
        if (padded.Contains(" AND ", StringComparison.Ordinal) &&
            new[] { " FOLLOW ", " STAND ", " BUY ", " SELL ", " ATTACK " }.Any(padded.Contains))
            values.Add(SpeechAct.Order);
        if (padded.Contains(" I WARN ", StringComparison.Ordinal)) values.Add(SpeechAct.Warn);
        if (padded.Contains(" OR ELSE ", StringComparison.Ordinal)) values.Add(SpeechAct.Threaten);
        return values.Order().ToArray();
    }

    private static IReadOnlyList<DialogueDomain> DomainsFor(DialogueIntent intent, string text)
    {
        var values = new HashSet<DialogueDomain>
        {
            intent switch
            {
                DialogueIntent.Identity => DialogueDomain.Identity,
                DialogueIntent.Wellbeing => DialogueDomain.Wellbeing,
                DialogueIntent.Assistance or DialogueIntent.Clarification => DialogueDomain.Assistance,
                DialogueIntent.Activity or DialogueIntent.Directive or DialogueIntent.UnsafeDirective => DialogueDomain.Activity,
                DialogueIntent.LocationInquiry => DialogueDomain.LocationNavigation,
                DialogueIntent.TradeRequest => DialogueDomain.TradeEconomy,
                DialogueIntent.GameFact => DialogueDomain.LoreWorld,
                _ => DialogueDomain.Social
            }
        };
        var padded = " " + text + " ";
        AddDomain([" BUY ", " SELL ", " PRICE ", " WARES ", " TRADE "], DialogueDomain.TradeEconomy);
        AddDomain([" WHERE ", " ROAD ", " INN ", " MARKET ", " FOLLOW "], DialogueDomain.LocationNavigation);
        AddDomain([" SWORD ", " POTION ", " INVENTORY ", " ITEM "], DialogueDomain.ItemsInventory);
        AddDomain([" QUEST ", " MISSION ", " TASK "], DialogueDomain.QuestTask);
        AddDomain([" ATTACK ", " KILL ", " FIGHT ", " ENEMY "], DialogueDomain.Combat);
        AddDomain([" SHIP ", " VEHICLE ", " STARSHIP ", " HORSE "], DialogueDomain.VehicleTravel);
        AddDomain([" SPELL ", " MAGIC ", " CURSE "], DialogueDomain.Magic);
        AddDomain([" COMPUTER ", " TERMINAL ", " REACTOR ", " SYSTEM "], DialogueDomain.Technology);
        return values.Take(4).Order().ToArray();

        void AddDomain(string[] words, DialogueDomain domain)
        {
            if (words.Any(padded.Contains)) values.Add(domain);
        }
    }

    private static IReadOnlyList<DialogueGoal> GoalsFor(DialogueIntent intent, string text)
    {
        var values = new HashSet<DialogueGoal>
        {
            intent switch
            {
                DialogueIntent.Greeting => DialogueGoal.Rapport,
                DialogueIntent.Farewell => DialogueGoal.ConversationClosure,
                DialogueIntent.LocationInquiry => DialogueGoal.EntityFinding,
                DialogueIntent.TradeRequest => DialogueGoal.Transaction,
                DialogueIntent.Clarification or DialogueIntent.Unknown => DialogueGoal.Clarification,
                DialogueIntent.Directive => DialogueGoal.Coordination,
                _ => DialogueGoal.InformationExchange
            }
        };
        var padded = " " + text + " ";
        if (padded.Contains(" BUY ", StringComparison.Ordinal)) values.Add(DialogueGoal.ItemAcquisition);
        if (padded.Contains(" SELL ", StringComparison.Ordinal)) values.Add(DialogueGoal.ItemDisposal);
        if (new[] { " GO ", " FOLLOW ", " TRAVEL " }.Any(padded.Contains)) values.Add(DialogueGoal.Travel);
        if (new[] { " ATTACK ", " FIGHT ", " KILL " }.Any(padded.Contains)) values.Add(DialogueGoal.Combat);
        return values.Take(4).Order().ToArray();
    }

    private static IReadOnlyList<ContentFlag> ContentFor(string text)
    {
        var padded = " " + text + " ";
        var flags = new HashSet<ContentFlag>();
        if (new[] { " FUCK ", " SHIT ", " BITCH ", " IDIOT ", " ASSHOLE " }.Any(padded.Contains)) flags.Add(ContentFlag.Profanity);
        if (new[] { " KILL ", " ATTACK ", " SHOOT ", " STAB ", " FIGHT " }.Any(padded.Contains)) flags.Add(ContentFlag.FictionalViolence);
        if (new[] { " GUTS ", " DISEMBOWEL ", " DECAPITATE ", " GORE " }.Any(padded.Contains)) flags.Add(ContentFlag.GraphicViolence);
        if (new[] { " OR ELSE ", " I WILL KILL ", " YOU WILL DIE " }.Any(padded.Contains)) flags.Add(ContentFlag.Threat);
        if (new[] { " STEAL ", " ROB ", " SMUGGLE ", " MURDER " }.Any(padded.Contains)) flags.Add(ContentFlag.Crime);
        if (new[] { " FAGGOT ", " NIGGER ", " KIKE " }.Any(padded.Contains)) flags.Add(ContentFlag.IdentityAttack);
        if (new[] { " KILL MYSELF ", " SUICIDE ", " HURT MYSELF " }.Any(padded.Contains)) flags.Add(ContentFlag.SelfHarm);
        if (new[] { " SEX ", " NAKED ", " FUCK ME " }.Any(padded.Contains)) flags.Add(ContentFlag.SexualContent);
        if (new[] { " RAPE ", " SEXUAL ASSAULT " }.Any(padded.Contains)) flags.Add(ContentFlag.SexualViolence);
        return flags.Order().ToArray();
    }

    private static ResponsePolicy PolicyFor(TurnPerception legacy, IReadOnlyList<SpeechAct> speechActs, string text)
    {
        if (!legacy.ResponseExpected) return ResponsePolicy.NoResponse;
        if (legacy.Intent is DialogueIntent.Hostility or DialogueIntent.UnsafeDirective) return ResponsePolicy.Refuse;
        if (legacy.Intent is DialogueIntent.Unknown or DialogueIntent.Clarification) return ResponsePolicy.Clarify;
        if (speechActs.Contains(SpeechAct.Negotiate)) return ResponsePolicy.Negotiate;
        if (speechActs.Contains(SpeechAct.Inform) && !text.EndsWith('?')) return ResponsePolicy.Acknowledge;
        return ResponsePolicy.Answer;
    }

    private static IReadOnlyList<DialogueSlot> ExtractSlots(string text)
    {
        var slots = new List<DialogueSlot>();
        AddMatches(SlotType.Quantity, "\\b[0-9]+\\b", 1.0);
        AddCapture(SlotType.Place, "\\bWHERE (?:IS|ARE) (?:THE )?(?<VALUE>[A-Z0-9][A-Z0-9 '\\-]{0,31}?)(?:[?.!]|$)", 0.99);
        AddCapture(SlotType.Item, "\\b(?:PRICE|COST) OF (?:THE )?(?<VALUE>[A-Z0-9][A-Z0-9 '\\-]{0,31}?)(?:[?.!]|$)", 0.99);
        AddCapture(SlotType.Item, "\\b(?:BUY|SELL|PURCHASE) (?:ME )?(?:[0-9]+ )?(?:SOME )?(?<VALUE>[A-Z][A-Z '\\-]{0,31}?)(?:[?.!]|$)", 0.98);
        foreach (var item in new[] { "IRON SWORD", "HEALTH POTION", "ROPE", "WARES" })
        {
            var index = text.IndexOf(item, StringComparison.Ordinal);
            if (index >= 0 && slots.All(slot => slot.Type != SlotType.Item || slot.Start != index))
                slots.Add(new DialogueSlot(SlotType.Item, BioTag.B, item, index, item.Length, 1.0));
        }
        return slots.OrderBy(slot => slot.Start).ThenBy(slot => slot.Type).ToArray();

        void AddMatches(SlotType type, string pattern, double confidence)
        {
            foreach (Match match in Regex.Matches(text, pattern, RegexOptions.CultureInvariant))
                slots.Add(new DialogueSlot(type, BioTag.B, match.Value, match.Index, match.Length, confidence));
        }
        void AddCapture(SlotType type, string pattern, double confidence)
        {
            var match = Regex.Match(text, pattern, RegexOptions.CultureInvariant);
            if (!match.Success) return;
            var value = match.Groups["VALUE"];
            slots.Add(new DialogueSlot(type, BioTag.B, value.Value.Trim(), value.Index, value.Length, confidence));
        }
    }

    private static ToolDecision SelectTool(
        string text, IReadOnlyList<DialogueSlot> slots, GameToolRegistry tools)
    {
        var padded = " " + text + " ";
        var recognized = new List<string>();
        if (padded.Contains(" WHERE IS ", StringComparison.Ordinal) || padded.Contains(" WHERE ARE ", StringComparison.Ordinal)) recognized.Add("LOOKUP_LOCATION");
        if (new[] { " LIST WARES ", " SHOW ME YOUR WARES ", " WHAT DO YOU SELL ", " NEED WARES ", " SOME WARES " }.Any(padded.Contains)) recognized.Add("LIST_WARES");
        if (padded.Contains(" PRICE ", StringComparison.Ordinal) || padded.Contains(" COST ", StringComparison.Ordinal)) recognized.Add("LOOKUP_PRICE");
        if (padded.Contains(" BUY ", StringComparison.Ordinal) || padded.Contains(" PURCHASE ", StringComparison.Ordinal)) recognized.Add("BUY");
        if (padded.Contains(" SELL ", StringComparison.Ordinal) && !padded.Contains(" WHAT DO YOU SELL ", StringComparison.Ordinal)) recognized.Add("SELL");
        recognized = recognized.Distinct(StringComparer.Ordinal).ToList();
        if (recognized.Count == 0) return ToolDecision.None;
        var name = recognized[0];
        if (!tools.TryGet(name, out var tool))
            return new(name, EmptyArguments, 1.0, false, ["CAPABILITY_UNAVAILABLE"], Additional(recognized));

        var arguments = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var parameter in tool.Schema.Parameters)
        {
            var slotType = parameter.Name switch
            {
                "PLACE" => SlotType.Place,
                "ITEM" => SlotType.Item,
                "QUANTITY" => SlotType.Quantity,
                _ => SlotType.Other
            };
            var matching = slots.Where(slot => slot.Type == slotType).ToArray();
            if (matching.Length == 1) arguments[parameter.Name] = matching[0].Value;
        }
        var missing = tool.Schema.Parameters.Where(parameter => parameter.Required && !arguments.ContainsKey(parameter.Name))
            .Select(parameter => parameter.Name).ToArray();
        var ambiguous = tool.Schema.Parameters.Any(parameter =>
        {
            var slotType = parameter.Name == "PLACE" ? SlotType.Place : parameter.Name == "ITEM" ? SlotType.Item : parameter.Name == "QUANTITY" ? SlotType.Quantity : SlotType.Other;
            return slots.Count(slot => slot.Type == slotType) > 1;
        });
        var confidence = missing.Length > 0 || ambiguous ? 0.50 : tool.Schema.MutatesWorldState ? 0.995 : 0.98;
        var threshold = tool.Schema.MutatesWorldState ? MutatingToolPrecisionThreshold : ReadOnlyToolPrecisionThreshold;
        var canExecute = missing.Length == 0 && !ambiguous && confidence >= threshold;
        return new(name, new ReadOnlyDictionary<string, string>(arguments), confidence, canExecute,
            missing.Length > 0 ? missing : ambiguous ? ["AMBIGUOUS_SLOT"] : [], Additional(recognized));

        static IReadOnlyList<PendingDialogueAction> Additional(IReadOnlyList<string> names) => names.Skip(1)
            .Take(3).Select(name => new PendingDialogueAction("EXECUTE_TOOL", name, EmptyArguments)).ToArray();
    }

    private static string ClarificationFor(ToolDecision decision)
    {
        if (decision.Reasons.Contains("CAPABILITY_UNAVAILABLE"))
            return "I CANNOT DO THAT WITHOUT THE REQUIRED GAME TOOL.";
        if (decision.Reasons.Contains("PLACE")) return "WHICH PLACE DO YOU MEAN?";
        if (decision.Reasons.Contains("ITEM")) return "WHICH ITEM DO YOU MEAN?";
        if (decision.Reasons.Contains("QUANTITY")) return "HOW MANY DO YOU MEAN?";
        if (decision.Reasons.Contains("AMBIGUOUS_SLOT")) return "PLEASE NAME ONE TARGET.";
        return "PLEASE EXPLAIN WHAT YOU NEED.";
    }

    private static ResponseCandidate? RankCandidates(
        StructuredPerception perception, string input, int seed, GameToolRegistry tools)
    {
        var eligible = V10Candidates.Where(candidate =>
            candidate.AllowedPolicies.Contains(perception.Policy) &&
            (candidate.AllowedDomains.Count == 0 || candidate.AllowedDomains.Intersect(perception.Domains).Any()) &&
            !candidate.RequiresToolResult).ToArray();
        if (eligible.Length == 0) return null;
        var expected = perception.ResponseCandidateId;
        var ranked = eligible.Select(candidate => (Candidate: candidate, Score:
                (candidate.Id == expected ? 4.0 : 0.0) +
                candidate.AllowedDomains.Intersect(perception.Domains).Count() * 0.7 +
                TokenOverlap(candidate.Text, input) * 0.05 + StableTie(candidate.Id, seed)))
            .OrderByDescending(item => item.Score).ThenBy(item => item.Candidate.Id, StringComparer.Ordinal).First();
        return ranked.Score >= 0.5 ? ranked.Candidate : null;
    }

    private static string? CandidateIdFor(
        DialogueIntent intent, ResponsePolicy policy, IReadOnlyList<DialogueDomain> domains) => policy switch
    {
        ResponsePolicy.Refuse when intent == DialogueIntent.Hostility => "HOSTILE_BOUNDARY",
        ResponsePolicy.Refuse => "REFUSAL",
        ResponsePolicy.Clarify => "CLARIFY",
        ResponsePolicy.Negotiate => "NEGOTIATE",
        ResponsePolicy.Acknowledge => "ACKNOWLEDGE",
        ResponsePolicy.Defer => domains.Contains(DialogueDomain.TradeEconomy) ? "TRADE_UNAVAILABLE" : "DEFER",
        _ => intent switch
        {
            DialogueIntent.Greeting => "SOCIAL_GREETING",
            DialogueIntent.Farewell => "SOCIAL_FAREWELL",
            DialogueIntent.Identity => "IDENTITY_TRAVELER",
            DialogueIntent.Wellbeing => "WELLBEING_CALM",
            DialogueIntent.Assistance => "ASSISTANCE_ASK",
            DialogueIntent.Activity => "ACTIVITY_HELP",
            DialogueIntent.LocationInquiry => "LOCATION_UNAVAILABLE",
            DialogueIntent.TradeRequest => "TRADE_UNAVAILABLE",
            _ => "ACKNOWLEDGE"
        }
    };

    private static NpcState ToLegacyState(NpcDialogueState state) => new(
        state.Rapport, state.Mood, DialogueIntent.Unknown, state.LastAffect,
        DialogueTopic.None, NpcGoal.None);

    private static ResponseCandidate Candidate(
        string id, string text, IReadOnlyList<ResponsePolicy> policies, IReadOnlyList<DialogueDomain> domains) =>
        new(id, text, [id], policies, domains, Enum.GetValues<ResponseTone>(), false, [], []);

    private static IReadOnlyDictionary<string, double> MergeConfidence(
        IReadOnlyDictionary<string, double> current, string key, double value)
    {
        var result = current.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        result[key] = value;
        return new ReadOnlyDictionary<string, double>(result);
    }

    private static int TokenOverlap(string left, string right)
    {
        var words = left.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        return right.Split(' ', StringSplitOptions.RemoveEmptyEntries).Count(words.Contains);
    }

    private static double StableTie(string id, int seed)
    {
        uint hash = unchecked((uint)seed);
        foreach (var character in id) hash = (hash ^ character) * 16777619;
        return (hash & 0xffff) / 65535.0 * 0.001;
    }

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

internal static class DialogueStateReducer
{
    public static NpcDialogueState Apply(
        NpcDialogueState state, StructuredPerception perception, TurnPlan plan, GameToolResult? toolResult)
    {
        state.Validate();
        var hostile = perception.Stance == DialogueStance.Hostile;
        var rapport = Math.Clamp(state.Rapport + (hostile ? -1 : perception.Affect == UserAffect.Friendly ? 1 : 0), 0, 3);
        var trust = Math.Clamp(state.Trust + (hostile ? -1 : perception.SpeechActs.Contains(SpeechAct.Thank) ? 1 : 0), 0, 3);
        var familiarity = Math.Clamp(state.Familiarity + (perception.SpeechActs.Contains(SpeechAct.Greet) ? 1 : 0), 0, 3);
        var hostility = Math.Clamp(state.Hostility + (hostile ? 1 : -1), 0, 3);
        var mood = hostile ? NpcMood.Annoyed : perception.Affect is UserAffect.Distressed or UserAffect.Frustrated
            ? NpcMood.Cautious : perception.Affect == UserAffect.Friendly ? NpcMood.Friendly : state.Mood;
        var domains = perception.Domains.Concat(state.ActiveDomains).Distinct().Take(4).ToArray();
        var goals = perception.Goals.Where(goal => goal != DialogueGoal.None)
            .Concat(state.ActiveGoals).Distinct().Take(4).ToArray();
        var clarification = plan.Policy == ResponsePolicy.Clarify
            ? new PendingClarification(plan.Clarification ?? "PLEASE EXPLAIN.", plan.ToolSchema,
                plan.ToolSchema is null ? [] : ["REQUIRED_ARGUMENT"])
            : null;
        var transaction = plan.ToolSchema is "BUY" or "SELL"
            ? new DialogueTransaction(plan.ToolSchema,
                perception.Slots.FirstOrDefault(slot => slot.Type == SlotType.Item)?.Value ?? "UNKNOWN",
                int.TryParse(perception.Slots.FirstOrDefault(slot => slot.Type == SlotType.Quantity)?.Value,
                    NumberStyles.None, CultureInfo.InvariantCulture, out var quantity) ? quantity : 0,
                toolResult?.Success == true ? "COMPLETE" : toolResult is null ? "PENDING" : "FAILED")
            : state.CurrentTransaction;
        var references = new DialogueReferenceState(
            Latest(SlotType.Person) ?? state.References.Person,
            Latest(SlotType.Place) ?? state.References.Place,
            Latest(SlotType.Item) ?? state.References.Item,
            Latest(SlotType.Vehicle) ?? state.References.Vehicle,
            Latest(SlotType.System) ?? state.References.System);
        var result = new NpcDialogueState((byte)rapport, (byte)trust, (byte)familiarity, (byte)hostility,
            mood, domains, perception.ResponseCandidateId, perception.Affect, clarification, transaction,
            goals, plan.PendingActions.Take(3).ToArray(), references);
        result.Validate();
        return result;

        string? Latest(SlotType type) => perception.Slots.LastOrDefault(slot => slot.Type == type)?.Value is { } value
            ? value.Length <= 32 ? value : value[..32]
            : null;
    }
}
