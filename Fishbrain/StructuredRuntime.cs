using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Fishbrain;

public sealed partial class Brain
{
    private const double ReadOnlyToolPrecisionThreshold = 0.95;
    private const double MutatingToolPrecisionThreshold = 0.99;

    internal static IReadOnlyList<ResponseCandidate> V11Candidates { get; } = V11ResponseCatalog.Plans
        .Select(plan => new ResponseCandidate(plan.Id, plan.Variations[0], [plan.Id], [plan.Policy],
            plan.Domain is null ? [] : [plan.Domain.Value], Enum.GetValues<ResponseTone>(), false, [], []))
        .ToArray();

    public ReplyResult Reply(ReplyRequest request, GameToolRegistry tools)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(tools);
        ValidateRequest(request);
        var packed = PackTurns(request.Turns);
        var current = DialogueText.Normalize(request.Turns[^1].Text);
        var classificationQuestion = IsClassificationQuestion(current);
        var unsupportedActivity = UnsupportedActivityCommand(current, tools);
        var slots = ExtractSlots(current).ToList();
        CompleteClarificationSlots(current, request.State, slots);
        ResolveReferences(current, request.State, slots);

        var learned = _structuredHeads.Updates > 0
            ? _structuredHeads.Predict(packed.Text, slots, ContextVector(packed.Text), current)
            : RulePerception(current, slots, tools);
        var raw = learned;
        var constraints = new List<PerceptionConstraint>();
        var perception = ApplyConstraints(learned, current, slots, request.State, tools, constraints);
        var explicitTarget = KnowledgeTargetFor(current);
        var actionableTarget = explicitTarget != KnowledgeTarget.None
            ? explicitTarget
            : IsAnaphoric(current) ? request.State.PendingKnowledgeTarget : KnowledgeTarget.None;
        var toolDecision = perception.Policy is ResponsePolicy.Refuse or ResponsePolicy.NoResponse or ResponsePolicy.Defer
            ? ToolDecision.None
            : SelectTool(current, slots, request.State, actionableTarget, tools);

        if (toolDecision.Name is not null)
        {
            var policy = toolDecision.CanExecute
                ? ResponsePolicy.ExecuteTool
                : toolDecision.Reasons.Contains("CAPABILITY_UNAVAILABLE") ? ResponsePolicy.Defer : ResponsePolicy.Clarify;
            var toolDomain = DomainForTool(toolDecision.Name);
            perception = perception with
            {
                ToolSchema = toolDecision.Name,
                Policy = policy,
                Domains = perception.Domains.Prepend(toolDomain).Distinct().Take(3).ToArray(),
                Confidence = MergeConfidence(perception.Confidence, "TOOL", toolDecision.Confidence)
            };
            constraints.Add(new PerceptionConstraint(PerceptionConstraintOperation.Enforce, "POLICY", policy.ToString().ToUpperInvariant(),
                1.0, toolDecision.Confidence, toolDecision.Name, "TOOL_SCHEMA_AND_SLOT_GATE"));
            constraints.Add(Enforce("DOMAIN", toolDomain.ToString().ToUpperInvariant(), toolDecision.Name,
                "AUTHORITATIVE_TOOL_DOMAIN"));
        }
        else if (perception.Policy == ResponsePolicy.ExecuteTool || perception.ToolSchema is not null)
        {
            var fallbackPolicy = RulePolicy(current, perception.SpeechActs, perception.Stance);
            perception = perception with
            {
                ToolSchema = null,
                Policy = fallbackPolicy == ResponsePolicy.ExecuteTool ? ResponsePolicy.Clarify : fallbackPolicy
            };
            constraints.Add(new PerceptionConstraint(PerceptionConstraintOperation.Veto, "POLICY", "EXECUTE_TOOL",
                -1.0, 0.99, current, "NO_VALIDATED_TOOL_DECISION"));
        }
        else if (_structuredHeads.Updates > 0 &&
                 perception.Policy is not (ResponsePolicy.Clarify or ResponsePolicy.Refuse or ResponsePolicy.NoResponse) &&
                 BelowCalibration(perception, "POLICY", "policy") &&
                 !HasValidatedResponseShape(current, perception))
        {
            perception = perception with { Policy = ResponsePolicy.Clarify, ResponseCandidateId = "CLARIFY" };
            constraints.Add(new PerceptionConstraint(PerceptionConstraintOperation.Enforce, "POLICY", "CLARIFY", 1.0,
                perception.Confidence.GetValueOrDefault("POLICY"), "VALIDATION", "LOW_CONFIDENCE_PRODUCTION_DECISION"));
        }

        GameToolInvocation? invocation = null;
        GameToolResult? toolResult = null;
        string text;
        string? selectedCandidate = null;
        string? fallbackReason = null;
        ResponseSource source;
        var pendingActions = toolDecision.Name is null
            ? request.State.PendingActions.ToList()
            : toolDecision.AdditionalActions.ToList();

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
            source = ResponseSource.RankedVariation;
            selectedCandidate = "NO_RESPONSE";
        }
        else if (perception.Policy == ResponsePolicy.Clarify)
        {
            text = ClarificationFor(toolDecision, perception.KnowledgeTarget);
            source = ResponseSource.ClarificationTemplate;
        }
        else if (perception.Policy == ResponsePolicy.Defer && toolDecision.Reasons.Contains("CAPABILITY_UNAVAILABLE"))
        {
            text = "I CANNOT DO THAT WITHOUT THE REQUIRED GAME TOOL.";
            source = ResponseSource.CapabilityTemplate;
            fallbackReason = "CAPABILITY_UNAVAILABLE";
        }
        else if (perception.Policy == ResponsePolicy.Defer && unsupportedActivity is not null)
        {
            text = $"I CANNOT {unsupportedActivity} WITHOUT THE REQUIRED GAME TOOL.";
            source = ResponseSource.CapabilityTemplate;
            fallbackReason = "CAPABILITY_UNAVAILABLE";
        }
        else if (classificationQuestion && perception.Policy is (ResponsePolicy.Answer or ResponsePolicy.Acknowledge))
        {
            text = "I WAS DESCRIBING THE TOPIC OF YOUR LAST MESSAGE.";
            source = ResponseSource.Fallback;
            fallbackReason = "CLASSIFICATION_EXPLANATION";
        }
        else if (IsPlanningFollowUp(current))
        {
            text = ContextualGuidance(perception.Domains);
            source = ResponseSource.Fallback;
            fallbackReason = "CONTEXTUAL_GUIDANCE";
        }
        else if (perception.Policy is ResponsePolicy.Answer or ResponsePolicy.Acknowledge &&
                 TryRenderPersona(actionableTarget, request.Persona, tools, out text, out source))
        {
            selectedCandidate = "PERSONA_" + actionableTarget.ToString().ToUpperInvariant();
        }
        else if (request.ResponseMode == ResponseMode.GeneratedExperimental)
        {
            text = GeneratedReply(packed.Text, current, ToLegacyState(request.State), request.Seed).Text;
            source = ResponseSource.GeneratedExperimental;
        }
        else
        {
            var ranked = RankResponse(perception, current, request.Seed, tools);
            if (ranked is null)
            {
                text = DomainFallback(perception);
                source = ResponseSource.Fallback;
                fallbackReason = "NO_ELIGIBLE_RESPONSE_PLAN";
            }
            else
            {
                text = ranked.Value.Text;
                selectedCandidate = ranked.Value.Plan.Id;
                source = ResponseSource.RankedVariation;
                perception = perception with { ResponseCandidateId = selectedCandidate };
            }
        }

        if (text.Length > 256) throw new InvalidDataException("Runtime produced an overlength response.");
        if (text.Length > 0 && !DialogueText.IsCanonical(text))
            throw new InvalidDataException("Runtime produced noncanonical response text.");

        var plan = new TurnPlan(perception.Policy, perception.ToolSchema, selectedCandidate,
            perception.KnowledgeTarget, pendingActions,
            perception.Policy == ResponsePolicy.Clarify ? text : null,
            perception.Policy == ResponsePolicy.Clarify ? toolDecision.Reasons : []);
        var state = DialogueStateReducer.Apply(request.State, perception, plan, toolResult);
        var tone = Cognition.ToneFor(state.Mood);
        var diagnostics = new ReplyDiagnostics(
            perception.Confidence, constraints, source, selectedCandidate, invocation,
            slots, _tokenizer.UnknownWords(current), fallbackReason, packed.TurnCount, packed.TokenCount);
        return new ReplyResult(text, state, raw, perception, plan, tone, diagnostics);
    }

    private bool BelowCalibration(StructuredPerception perception, string confidenceName, string schemaName) =>
        perception.Confidence.TryGetValue(confidenceName, out var confidence) &&
        _confidenceCalibration.TryGetValue(schemaName, out var calibration) &&
        confidence < calibration.Threshold;

    private static bool HasValidatedResponseShape(string text, StructuredPerception perception) =>
        text.EndsWith("?", StringComparison.Ordinal) ||
        perception.KnowledgeTarget != KnowledgeTarget.None ||
        RuleSpeechActs(text).Any(act => act is SpeechAct.Ask or SpeechAct.Request or SpeechAct.Order or
            SpeechAct.Greet or SpeechAct.Farewell or SpeechAct.Apologize or SpeechAct.Thank or SpeechAct.Refuse or
            SpeechAct.Threaten or SpeechAct.Report or SpeechAct.Inform);

    private static DialogueDomain DomainForTool(string name) => name switch
    {
        "LOOKUP_LOCATION" or "GET_CURRENT_LOCATION" => DialogueDomain.LocationNavigation,
        "LIST_INVENTORY" => DialogueDomain.ItemsInventory,
        "LOOKUP_WORLD_FACT" => DialogueDomain.LoreWorld,
        _ => DialogueDomain.TradeEconomy
    };

    private static void ValidateRequest(ReplyRequest request)
    {
        if (!ValidRequestId(request.ConversationId))
            throw new ArgumentException("ConversationId must contain 1-128 characters.", nameof(request));
        if (!ValidRequestId(request.TurnId))
            throw new ArgumentException("TurnId must contain 1-128 characters.", nameof(request));
        if (request.Turns is null || request.Turns.Count == 0)
            throw new ArgumentException("At least one structured turn is required.", nameof(request));
        if (!Enum.IsDefined(request.ResponseMode))
            throw new ArgumentOutOfRangeException(nameof(request), "ResponseMode is invalid.");
        if (request.Turns.Any(turn => turn is null || !Enum.IsDefined(turn.Role) ||
            string.IsNullOrWhiteSpace(turn.Text) || turn.Text.Length > 4_096))
            throw new ArgumentException("Structured turns must have a valid role and 1-4096 text characters.", nameof(request));
        if (request.Turns[^1].Role != DialogueRole.Player)
            throw new ArgumentException("The final structured turn must be a player turn.", nameof(request));
        ArgumentNullException.ThrowIfNull(request.State);
        request.State.Validate();
        ArgumentNullException.ThrowIfNull(request.Persona);
        request.Persona.Validate();

        static bool ValidRequestId(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 128 &&
            value == value.Trim() && value.All(character => !char.IsControl(character));
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
            if (index == turns.Count - 1 && tokens > Config.ContextLength)
                throw new ArgumentException(
                    $"The current turn requires {tokens} tokens, but the model context allows {Config.ContextLength}.", nameof(turns));
            if (index != turns.Count - 1 && count + tokens > Config.ContextLength) break;
            retained.Add((complete, tokens));
            count += tokens;
        }
        retained.Reverse();
        return (string.Join(' ', retained.Select(item => item.Text)), retained.Count, count);
    }

    private static StructuredPerception RulePerception(
        string current, IReadOnlyList<DialogueSlot> slots, GameToolRegistry tools)
    {
        var acts = RuleSpeechActs(current);
        var domains = RuleDomains(current);
        var goals = RuleGoals(current, domains);
        var content = ContentFor(current);
        var affect = RuleAffect(current, content);
        var stance = affect switch
        {
            UserAffect.Friendly => DialogueStance.Friendly,
            UserAffect.Hostile => DialogueStance.Hostile,
            UserAffect.Distressed or UserAffect.Frustrated => DialogueStance.Cautious,
            _ => DialogueStance.Neutral
        };
        var policy = RulePolicy(current, acts, stance);
        var target = KnowledgeTargetFor(current);
        var confidence = new ReadOnlyDictionary<string, double>(new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["SPEECH_ACT"] = 0.97,
            ["DOMAIN"] = 0.95,
            ["GOAL"] = 0.93,
            ["AFFECT"] = 0.97,
            ["STANCE"] = 0.97,
            ["POLICY"] = 0.98,
            ["SLOTS"] = slots.Count == 0 ? 1.0 : slots.Min(slot => slot.Confidence),
            ["CONTENT"] = 0.99,
            ["TOOL"] = 0.0,
            ["RESPONSE_CANDIDATE"] = 0.90,
            ["KNOWLEDGE_TARGET"] = target == KnowledgeTarget.None ? 0.80 : 0.99
        });
        return new StructuredPerception(acts, domains, goals, affect, stance, policy, slots, content,
            null, CandidateIdFor(acts, domains, policy, target), target, confidence);
    }

    private static StructuredPerception ApplyConstraints(
        StructuredPerception learned,
        string current,
        IReadOnlyList<DialogueSlot> slots,
        NpcDialogueState state,
        GameToolRegistry tools,
        List<PerceptionConstraint> constraints)
    {
        // A learned tool label is diagnostic only. Deterministic schema and slot checks below
        // are the sole authority allowed to produce an executable tool decision.
        var result = learned with { Slots = slots, ToolSchema = null };
        var rules = RulePerception(current, slots, tools);
        var classificationQuestion = IsClassificationQuestion(current);
        var tradeEvidence = ContainsAny(current, "TRADE", "BUY", "SELL", "SALE", "WARES", "PRICE", "COST", "GOLD",
            "MONEY", "BALANCE");
        if (!tradeEvidence && result.Domains.Contains(DialogueDomain.TradeEconomy))
        {
            result = result with { Domains = result.Domains.Where(domain => domain != DialogueDomain.TradeEconomy).ToArray() };
            constraints.Add(new PerceptionConstraint(PerceptionConstraintOperation.Veto, "DOMAIN", "TRADE_ECONOMY",
                -1.0, 0.99, current, "NO_VALIDATED_TRADE_EVIDENCE"));
        }
        var itemEvidence = ContainsAny(current, "SWORD", "POTION", "ROPE", "ITEM", "INVENTORY", "FIREWOOD", "WARES",
            "PACK", "CARRY", "GEAR", "POSSESSION");
        var staleItemDomainRemoved = !itemEvidence && result.Domains.Contains(DialogueDomain.ItemsInventory);
        if (staleItemDomainRemoved)
        {
            result = result with { Domains = result.Domains.Where(domain => domain != DialogueDomain.ItemsInventory).ToArray() };
            constraints.Add(new PerceptionConstraint(PerceptionConstraintOperation.Veto, "DOMAIN", "ITEMS_INVENTORY",
                -1.0, 0.99, current, "NO_VALIDATED_ITEM_EVIDENCE"));
        }
        var metaEvidence = classificationQuestion || ContainsAny(current, "COMMAND", "SETTING", "SAVE GAME", "CONTROL");
        if (!metaEvidence && result.Domains.Contains(DialogueDomain.MetaSystem))
        {
            result = result with { Domains = result.Domains.Where(domain => domain != DialogueDomain.MetaSystem).ToArray() };
            constraints.Add(new PerceptionConstraint(PerceptionConstraintOperation.Veto, "DOMAIN", "META_SYSTEM",
                -1.0, 0.99, current, "NO_VALIDATED_META_EVIDENCE"));
        }
        var locationEvidence = ContainsAny(current, "WHERE", "LOCATION", "CASTLE", "INN", "MARKET", "DIRECTION",
            "HOW FAR", "IS IT FAR", "FAR FROM HERE", "ROAD", "DOCK", "LOCATE", "FIND", "GET THERE",
            "REACH IT", "GUIDE ME THERE");
        if (!locationEvidence && result.Domains.Contains(DialogueDomain.LocationNavigation))
        {
            result = result with { Domains = result.Domains.Where(domain => domain != DialogueDomain.LocationNavigation).ToArray() };
            constraints.Add(new PerceptionConstraint(PerceptionConstraintOperation.Veto, "DOMAIN", "LOCATION_NAVIGATION",
                -1.0, 0.99, current, "NO_VALIDATED_LOCATION_EVIDENCE"));
        }
        var vehicleEvidence = ContainsAny(current, "SHIP", "STARSHIP", "HORSE", "VEHICLE", "TRAVEL", "DRIVE", "FLY",
            "SAIL");
        if (!vehicleEvidence && result.Domains.Contains(DialogueDomain.VehicleTravel))
        {
            result = result with { Domains = result.Domains.Where(domain => domain != DialogueDomain.VehicleTravel).ToArray() };
            constraints.Add(new PerceptionConstraint(PerceptionConstraintOperation.Veto, "DOMAIN", "VEHICLE_TRAVEL",
                -1.0, 0.99, current, "NO_VALIDATED_VEHICLE_EVIDENCE"));
        }
        foreach (var domain in rules.Domains.Where(domain => domain != DialogueDomain.Social && !result.Domains.Contains(domain)))
        {
            result = result with { Domains = AddLimited([domain], result.Domains, 3) };
            constraints.Add(Boost("DOMAIN", domain.ToString().ToUpperInvariant(), current, "VALIDATED_DOMAIN_EVIDENCE", 0.35));
        }
        if (staleItemDomainRemoved)
        {
            foreach (var staleAct in new[] { SpeechAct.Ask, SpeechAct.Request, SpeechAct.Order })
            {
                if (rules.SpeechActs.Contains(staleAct) || !result.SpeechActs.Contains(staleAct)) continue;
                result = result with { SpeechActs = result.SpeechActs.Where(act => act != staleAct).ToArray() };
                constraints.Add(new PerceptionConstraint(PerceptionConstraintOperation.Veto, "SPEECH_ACT",
                    staleAct.ToString().ToUpperInvariant(), -1.0, 0.99, current, "NO_CURRENT_TURN_ACT_EVIDENCE"));
            }
            foreach (var ruleAct in rules.SpeechActs.Where(act => !result.SpeechActs.Contains(act)))
                result = result with { SpeechActs = AddLimited([ruleAct], result.SpeechActs, 3) };
        }
        if (!result.ContentFlags.ToHashSet().SetEquals(rules.ContentFlags))
        {
            foreach (var removed in result.ContentFlags.Except(rules.ContentFlags))
                constraints.Add(new PerceptionConstraint(PerceptionConstraintOperation.Veto, "CONTENT",
                    removed.ToString().ToUpperInvariant(), -1.0, 0.99, current, "NO_VALIDATED_CONTENT_EVIDENCE"));
            foreach (var added in rules.ContentFlags.Except(result.ContentFlags))
                constraints.Add(Enforce("CONTENT", added.ToString().ToUpperInvariant(), current, "VALIDATED_CONTENT_EVIDENCE"));
            result = result with { ContentFlags = rules.ContentFlags };
        }
        var structuralQuestion = current.EndsWith("?", StringComparison.Ordinal);
        if (structuralQuestion && !result.SpeechActs.Contains(SpeechAct.Ask))
            result = result with { SpeechActs = AddLimited(result.SpeechActs, SpeechAct.Ask, 3) };
        if (structuralQuestion)
            constraints.Add(Boost("SPEECH_ACT", "ASK", "QUESTION_MARK", "STRUCTURAL_QUESTION", 0.20));

        if (IsPlanningFollowUp(current) && state.ActiveDomains.Count > 0)
        {
            result = result with
            {
                Domains = state.ActiveDomains.Take(3).ToArray(),
                Goals = state.ActiveGoals.Take(3).DefaultIfEmpty(DialogueGoal.Coordination).ToArray(),
                Policy = ResponsePolicy.Answer
            };
            constraints.Add(Enforce("POLICY", "ANSWER", current, "CONTEXTUAL_NEXT_STEP"));
            constraints.Add(Enforce("DOMAIN", result.Domains[0].ToString().ToUpperInvariant(), current,
                "CONTEXTUAL_NEXT_STEP"));
        }
        if (classificationQuestion)
        {
            result = result with
            {
                SpeechActs = [SpeechAct.Ask],
                Domains = [DialogueDomain.MetaSystem],
                Goals = [DialogueGoal.InformationExchange],
                Policy = ResponsePolicy.Answer
            };
            constraints.Add(Enforce("DOMAIN", "META_SYSTEM", current, "CLASSIFICATION_EXPLANATION"));
        }

        EnforceExact("HELLO", SpeechAct.Greet, DialogueDomain.Social, ResponsePolicy.Answer);
        EnforceExact("HI", SpeechAct.Greet, DialogueDomain.Social, ResponsePolicy.Answer);
        EnforceExact("GOODBYE", SpeechAct.Farewell, DialogueDomain.Social, ResponsePolicy.Answer);
        if (ContainsAny(current, "TRADE", "BUY", "SELL", "SALE", "WARES", "PRICE", "COST"))
        {
            result = result with
            {
                Domains = AddLimited(result.Domains.Where(domain => domain != DialogueDomain.MetaSystem), DialogueDomain.TradeEconomy, 3),
                Goals = AddLimited(result.Goals, DialogueGoal.Transaction, 3)
            };
            constraints.Add(Boost("DOMAIN", "TRADE_ECONOMY", "TRADE LEXEME", "HIGH_PRECISION_DOMAIN", 0.35));
        }
        if (!classificationQuestion && ContainsAny(current, "SWORD", "POTION", "ROPE", "ITEM", "INVENTORY"))
            result = result with { Domains = AddLimited(result.Domains, DialogueDomain.ItemsInventory, 3) };
        if (current.Contains("YOU DON'T KNOW WHAT", StringComparison.Ordinal) ||
            current.Contains("YOU DO NOT KNOW WHAT", StringComparison.Ordinal) ||
            current.Contains("NOT WHAT I ASKED", StringComparison.Ordinal))
        {
            result = result with
            {
                SpeechActs = [SpeechAct.Ask, SpeechAct.Correct],
                Affect = current.Contains("IDIOT", StringComparison.Ordinal) ? UserAffect.Hostile : UserAffect.Frustrated,
                Stance = current.Contains("IDIOT", StringComparison.Ordinal) ? DialogueStance.Hostile : DialogueStance.Cautious,
                Policy = ResponsePolicy.Clarify
            };
            constraints.Add(Enforce("SPEECH_ACT", "CORRECT", current, "EXPLICIT_CORRECTION"));
            constraints.Add(new PerceptionConstraint(PerceptionConstraintOperation.Veto, "SPEECH_ACT", "APOLOGIZE", -1.0,
                0.99, current, "CORRECTION_IS_NOT_AN_APOLOGY"));
        }
        var selfHarmEvidence = rules.ContentFlags.Contains(ContentFlag.SelfHarm);
        var poisonEvidence = IsPoisonReport(current);
        var refusalEvidence = IsDirectInsult(current) || rules.ContentFlags.Contains(ContentFlag.IdentityAttack) ||
            rules.ContentFlags.Contains(ContentFlag.Threat) || rules.ContentFlags.Contains(ContentFlag.GraphicViolence) ||
            rules.ContentFlags.Contains(ContentFlag.Crime) || rules.ContentFlags.Contains(ContentFlag.SexualViolence) ||
            IsUnsafeDirective(current);
        if (selfHarmEvidence)
        {
            result = result with
            {
                Affect = UserAffect.Distressed,
                Stance = DialogueStance.Cautious,
                Policy = ResponsePolicy.Defer,
                SpeechActs = new[] { SpeechAct.Report }.Concat(result.SpeechActs).Distinct().Take(3).ToArray(),
                Domains = new[] { DialogueDomain.HealthRepair, DialogueDomain.Survival }
                    .Concat(result.Domains.Where(domain => domain != DialogueDomain.Combat)).Distinct().Take(3).ToArray(),
                Goals = new[] { DialogueGoal.Survival }.Concat(result.Goals).Distinct().Take(3).ToArray()
            };
            constraints.Add(Enforce("POLICY", "DEFER", current, "SELF_HARM_SUPPORT_BOUNDARY"));
        }
        else if (poisonEvidence)
        {
            result = result with
            {
                Affect = UserAffect.Distressed,
                Stance = DialogueStance.Cautious,
                Policy = ResponsePolicy.Answer,
                SpeechActs = new[] { SpeechAct.Report }.Concat(result.SpeechActs).Distinct().Take(3).ToArray(),
                Domains = [DialogueDomain.HealthRepair, DialogueDomain.Survival],
                Goals = [DialogueGoal.HealingRepair, DialogueGoal.Survival]
            };
            constraints.Add(Enforce("POLICY", "ANSWER", current, "POISON_SAFETY_EVENT"));
        }
        else if (refusalEvidence)
        {
            var hostileSpeech = IsDirectInsult(current) || rules.ContentFlags.Contains(ContentFlag.IdentityAttack)
                ? new[] { SpeechAct.Challenge }.Concat(result.SpeechActs).Distinct().Take(3).ToArray()
                : result.SpeechActs;
            result = result with
            {
                Affect = UserAffect.Hostile,
                Stance = DialogueStance.Hostile,
                Policy = ResponsePolicy.Refuse,
                SpeechActs = hostileSpeech,
                Domains = AddLimited([DialogueDomain.Social], result.Domains, 3)
            };
            constraints.Add(Enforce("STANCE", "HOSTILE", current, "DIRECT_HOSTILITY"));
        }
        else if (rules.ContentFlags.Contains(ContentFlag.SexualContent))
        {
            result = result with { Affect = rules.Affect, Stance = rules.Stance, Policy = ResponsePolicy.Defer };
            constraints.Add(Enforce("POLICY", "DEFER", current, "SEXUAL_CONTENT_BOUNDARY"));
        }
        else if (result.Affect == UserAffect.Hostile || result.Stance == DialogueStance.Hostile)
        {
            result = result with { Affect = rules.Affect, Stance = rules.Stance };
            constraints.Add(new PerceptionConstraint(PerceptionConstraintOperation.Veto, "AFFECT", "HOSTILE", -1.0,
                0.96, current, "NO_HOSTILE_EVIDENCE"));
        }

        var hostileEvidence = refusalEvidence;
        var unresolvedReference = IsAnaphoric(current) &&
                                  state.References is { Person: null, Place: null, Item: null, Vehicle: null, System: null };
        if (!hostileEvidence && result.Policy is ResponsePolicy.Refuse or ResponsePolicy.NoResponse)
        {
            constraints.Add(new PerceptionConstraint(PerceptionConstraintOperation.Veto, "POLICY",
                result.Policy.ToString().ToUpperInvariant(), -1.0, 0.99, current, "NO_VALIDATED_REFUSAL_OR_SILENCE_EVIDENCE"));
            result = result with { Policy = rules.Policy };
        }
        if (!unresolvedReference && result.Policy == ResponsePolicy.Clarify &&
            rules.Policy == ResponsePolicy.Answer && HasValidatedResponseShape(current, rules))
        {
            constraints.Add(new PerceptionConstraint(PerceptionConstraintOperation.Veto, "POLICY", "CLARIFY",
                -0.5, 0.97, current, "EXPLICIT_ANSWERABLE_TURN"));
            result = result with { Policy = ResponsePolicy.Answer };
        }
        if (!selfHarmEvidence && !rules.ContentFlags.Contains(ContentFlag.SexualContent) &&
            result.Policy == ResponsePolicy.Defer && rules.Policy == ResponsePolicy.Answer &&
            HasValidatedResponseShape(current, rules))
        {
            constraints.Add(new PerceptionConstraint(PerceptionConstraintOperation.Veto, "POLICY", "DEFER",
                -0.5, 0.97, current, "NO_VALIDATED_DEFERRED_CAPABILITY"));
            result = result with { Policy = ResponsePolicy.Answer };
        }
        if (result.Policy == ResponsePolicy.Clarify && rules.Policy == ResponsePolicy.Acknowledge &&
            rules.Domains.Any(domain => domain != DialogueDomain.Social))
        {
            constraints.Add(new PerceptionConstraint(PerceptionConstraintOperation.Veto, "POLICY", "CLARIFY",
                -0.5, 0.97, current, "EXPLICIT_DOMAIN_REPORT"));
            result = result with { Policy = ResponsePolicy.Acknowledge };
        }
        if (!hostileEvidence && rules.SpeechActs.Contains(SpeechAct.Report))
        {
            result = result with
            {
                SpeechActs = result.SpeechActs.Prepend(SpeechAct.Report).Distinct().Take(3).ToArray(),
                Policy = ResponsePolicy.Acknowledge
            };
            constraints.Add(Enforce("SPEECH_ACT", "REPORT", current, "EXPLICIT_EVENT_REPORT"));
        }
        if (!hostileEvidence && rules.SpeechActs.Contains(SpeechAct.Apologize))
        {
            result = result with
            {
                SpeechActs = result.SpeechActs.Prepend(SpeechAct.Apologize).Distinct().Take(3).ToArray(),
                Policy = ResponsePolicy.Acknowledge
            };
            constraints.Add(Enforce("POLICY", "ACKNOWLEDGE", current, "EXPLICIT_APOLOGY"));
        }
        var unsupportedActivity = UnsupportedActivityCommand(current, tools);
        if (!hostileEvidence && unsupportedActivity is not null)
        {
            result = result with
            {
                SpeechActs = [SpeechAct.Order],
                Domains = [DialogueDomain.Activity],
                Policy = ResponsePolicy.Defer
            };
            constraints.Add(Enforce("POLICY", "DEFER", unsupportedActivity, "UNREGISTERED_ACTIVITY_TOOL"));
        }
        if (!hostileEvidence && IsIncompleteQuestion(current))
        {
            result = result with
            {
                SpeechActs = [SpeechAct.Ask],
                Domains = [DialogueDomain.Social],
                Policy = ResponsePolicy.Clarify
            };
            constraints.Add(Enforce("POLICY", "CLARIFY", current, "INCOMPLETE_QUESTION"));
        }

        var target = rules.KnowledgeTarget != KnowledgeTarget.None
            ? rules.KnowledgeTarget
            : IsAnaphoric(current) ? state.PendingKnowledgeTarget : KnowledgeTarget.None;
        if (target != learned.KnowledgeTarget)
        {
            result = result with { KnowledgeTarget = target };
            constraints.Add(target == KnowledgeTarget.None
                ? new PerceptionConstraint(PerceptionConstraintOperation.Veto, "KNOWLEDGE_TARGET",
                    learned.KnowledgeTarget.ToString().ToUpperInvariant(), -1.0, 0.99, current,
                    "NO_EXPLICIT_OR_STATE_REFERENCE")
                : Enforce("KNOWLEDGE_TARGET", target.ToString().ToUpperInvariant(), current,
                    "EXPLICIT_OR_STATE_REFERENCE"));
        }

        if (result.SpeechActs.Count > 3) result = result with { SpeechActs = result.SpeechActs.Take(3).ToArray() };
        if (result.Domains.Count > 3) result = result with { Domains = result.Domains.Take(3).ToArray() };
        if (result.Goals.Count > 3) result = result with { Goals = result.Goals.Take(3).ToArray() };
        result = result with
        {
            ResponseCandidateId = selfHarmEvidence
                ? "SELF_HARM_SUPPORT"
                : poisonEvidence
                    ? "DISTRESS_REPLY"
                : CandidateIdFor(result.SpeechActs, result.Domains, result.Policy, result.KnowledgeTarget)
        };
        return result;

        void EnforceExact(string exact, SpeechAct act, DialogueDomain domain, ResponsePolicy policy)
        {
            if (current.TrimEnd('.', '?', '!') != exact) return;
            result = result with { SpeechActs = [act], Domains = [domain], Policy = policy };
            constraints.Add(Enforce("SPEECH_ACT", act.ToString().ToUpperInvariant(), exact, "EXACT_STRUCTURAL_UTTERANCE"));
        }
    }

    private static IReadOnlyList<SpeechAct> RuleSpeechActs(string text)
    {
        var bare = text.Trim().TrimEnd('.', '?', '!');
        var values = new List<SpeechAct>();
        if (bare is "HELLO" or "HI" or "HEY" or "GREETINGS") values.Add(SpeechAct.Greet);
        if (ContainsAny(text, "GOODBYE", "FAREWELL", "UNTIL NEXT TIME")) values.Add(SpeechAct.Farewell);
        if (text.EndsWith("?", StringComparison.Ordinal) || StartsWithAny(bare, "WHO ", "WHAT ", "WHERE ", "WHEN ", "WHY ", "HOW ", "DO ", "CAN ", "WILL ")) values.Add(SpeechAct.Ask);
        if (StartsWithAny(bare, "PLEASE ", "I NEED ", "I WANT ", "CAN YOU ", "COULD YOU ", "TELL ME ",
            "SHOW ", "FIND ", "LOCATE ", "POINT ", "CHECK ", "ESCORT ", "CAST ", "NAVIGATE ",
            "START ", "POWER ", "SEARCH ", "SEAL ", "SCAN ", "MARK ", "USE ", "WARN ", "BRING "))
            values.Add(SpeechAct.Request);
        if (ContainsAny(" " + bare + " ", " BUY ", " SELL ", " PURCHASE ", " TRADE ")) values.Add(SpeechAct.Request);
        if (StartsWithAny(bare, "FOLLOW ", "STAND ", "ATTACK ", "GO ", "OPEN ", "CLOSE ", "GIVE ",
            "FIRE ", "ESCORT ", "CAST ", "NAVIGATE ", "START ", "POWER ", "SEARCH ", "SEAL ",
            "SCAN ", "MARK ", "USE ", "WARN ", "BRING ")) values.Add(SpeechAct.Order);
        if (ContainsAny(text, "I OFFER", "MY OFFER")) values.Add(SpeechAct.Offer);
        if (ContainsAny(text, "THANK", "THANKS")) values.Add(SpeechAct.Thank);
        if (ContainsAny(text, "SORRY", "I APOLOGIZE")) values.Add(SpeechAct.Apologize);
        if (ContainsAny(text, "NO, ", "NOT WHAT", "THAT IS WRONG", "YOU'RE WRONG")) values.Add(SpeechAct.Correct);
        if (ContainsAny(text, "I REFUSE", "I WILL NOT", "I WON'T", "I AM NOT ", "I'M NOT ", "NOT GOING"))
            values.Add(SpeechAct.Refuse);
        if (ContainsAny(text, "OR ELSE", "I WILL KILL YOU", "I WILL STAB YOU", "I WILL BURN", "YOU WILL DIE")) values.Add(SpeechAct.Threaten);
        if (ContainsAny(text, "I WARN YOU", "BE CAREFUL")) values.Add(SpeechAct.Warn);
        if (ContainsAny(text, "ARE APPROACHING", "HAS TAKEN", "OPENED THE", "IS LOSING", "BREACHED THE", "ON FIRE"))
            values.Add(SpeechAct.Report);
        if (ContainsAny(text, "TRADE", "PRICE", "TERMS", "DEAL")) values.Add(SpeechAct.Negotiate);
        if (values.Count == 0) values.Add(SpeechAct.Inform);
        return values.Distinct().Take(3).ToArray();
    }

    private static IReadOnlyList<DialogueDomain> RuleDomains(string text)
    {
        var values = new List<DialogueDomain>();
        Add(DialogueDomain.TradeEconomy, "TRADE", "BUY", "SELL", "SALE", "WARES", "PRICE", "COST", "GOLD", "MONEY",
            "BALANCE");
        Add(DialogueDomain.ItemsInventory, "SWORD", "POTION", "ROPE", "ITEM", "INVENTORY", "FIREWOOD");
        Add(DialogueDomain.LocationNavigation, "WHERE", "LOCATION", "CASTLE", "INN", "MARKET", "DIRECTION", "HOW FAR",
            "PASSAGE");
        Add(DialogueDomain.Identity, "WHO ARE YOU", "YOUR NAME", "FROM?", "YOUR FAMILY", "YOUR HOME", "YOUR JOB", "YOUR FACTION");
        Add(DialogueDomain.Assistance, "HELP", "WHAT CAN YOU DO", "WHAT DO YOU DO");
        Add(DialogueDomain.Wellbeing, "HOW ARE YOU", "ARE YOU WELL", "FEELING");
        Add(DialogueDomain.QuestTask, "QUEST", "MISSION", "TASK");
        Add(DialogueDomain.Combat, "ATTACK", "KILL", "FIGHT", "ENEMY", "WEAPON", "HOSTILE DRONE", "BANDIT CAPTAIN");
        Add(DialogueDomain.Survival, "SURVIVE", "SHELTER", "HUNGER", "THIRST", "FIREWOOD", "POISON");
        Add(DialogueDomain.HealthRepair, "HEAL", "INJURY", "REPAIR", "BROKEN", "POISON");
        Add(DialogueDomain.FactionPolitics, "FACTION", "KING", "QUEEN", "POLITICS");
        Add(DialogueDomain.CrimeLaw, "STEAL", "ROBBERY", "CRIME", "GUARD", "LAW");
        Add(DialogueDomain.Magic, "MAGIC", "SPELL", "CURSE", "WIZARD");
        Add(DialogueDomain.Technology, "SYSTEM", "REACTOR", "TERMINAL", "COMPUTER", "DRONE", "DEFENSE GRID", "COLONY",
            "AIRLOCK", "KILLER FEATURE", "FIREWALL");
        Add(DialogueDomain.VehicleTravel, "SHIP", "STARSHIP", "HORSE", "VEHICLE");
        Add(DialogueDomain.Environment, "WEATHER", "STORM", "FOREST", "DESERT");
        Add(DialogueDomain.LoreWorld, "LORE", "HISTORY", "WORLD", "LEGEND");
        Add(DialogueDomain.MetaSystem, "COMMAND", "SETTING", "SAVE GAME", "CONTROL");
        Add(DialogueDomain.Activity, "KILLING TIME", "FOLLOW", "STOP", "STAY", "WAIT");
        if (values.Count == 0) values.Add(DialogueDomain.Social);
        return values.Distinct().Take(3).ToArray();

        void Add(DialogueDomain domain, params string[] needles)
        {
            if (ContainsAny(text, needles)) values.Add(domain);
        }
    }

    private static IReadOnlyList<DialogueGoal> RuleGoals(string text, IReadOnlyList<DialogueDomain> domains)
    {
        var values = new List<DialogueGoal>();
        if (ContainsAny(text, "HELLO", "HI", "GREETINGS")) values.Add(DialogueGoal.Rapport);
        if (ContainsAny(text, "GOODBYE", "FAREWELL")) values.Add(DialogueGoal.ConversationClosure);
        if (domains.Contains(DialogueDomain.LocationNavigation)) values.Add(DialogueGoal.EntityFinding);
        if (domains.Contains(DialogueDomain.TradeEconomy)) values.Add(DialogueGoal.Transaction);
        if (ContainsAny(text, "BUY", "PURCHASE")) values.Add(DialogueGoal.ItemAcquisition);
        if (ContainsAny(text, "SELL")) values.Add(DialogueGoal.ItemDisposal);
        if (domains.Contains(DialogueDomain.Combat)) values.Add(DialogueGoal.Combat);
        if (values.Count == 0) values.Add(DialogueGoal.InformationExchange);
        return values.Distinct().Take(3).ToArray();
    }

    private static UserAffect RuleAffect(string text, IReadOnlyList<ContentFlag> content)
    {
        if (IsDirectInsult(text) || content.Contains(ContentFlag.IdentityAttack)) return UserAffect.Hostile;
        if (content.Contains(ContentFlag.SelfHarm) || ContainsAny(text, "AFRAID", "WORRIED", "DYING", "HURT", "POISON"))
            return UserAffect.Distressed;
        if (ContainsAny(text, "ANGRY", "FRUSTRATED", "NOT WHAT I ASKED")) return UserAffect.Frustrated;
        if (ContainsAny(text, "HELP ME")) return UserAffect.Distressed;
        if (ContainsAny(text, "THANK", "THANKS", "FRIEND", "PLEASE", "SORRY")) return UserAffect.Friendly;
        return UserAffect.Neutral;
    }

    private static ResponsePolicy RulePolicy(string text, IReadOnlyList<SpeechAct> acts, DialogueStance stance)
    {
        if (stance == DialogueStance.Hostile) return ResponsePolicy.Refuse;
        if (acts.Contains(SpeechAct.Order)) return ResponsePolicy.Acknowledge;
        if (acts.Contains(SpeechAct.Farewell) || acts.Contains(SpeechAct.Greet) || acts.Contains(SpeechAct.Ask) || acts.Contains(SpeechAct.Request))
            return ResponsePolicy.Answer;
        if (acts.Contains(SpeechAct.Negotiate)) return ResponsePolicy.Negotiate;
        if (acts.Contains(SpeechAct.Inform) || acts.Contains(SpeechAct.Report) || acts.Contains(SpeechAct.Thank) || acts.Contains(SpeechAct.Apologize))
            return ResponsePolicy.Acknowledge;
        return ResponsePolicy.Answer;
    }

    private static KnowledgeTarget KnowledgeTargetFor(string text)
    {
        var bare = text.Trim().TrimEnd('.', '?', '!');
        if (IsClassificationQuestion(bare)) return KnowledgeTarget.None;
        if (ContainsAny(bare, "WHAT IS YOUR NAME", "YOUR NAME", "WHO ARE YOU CALLED", "WHAT NAME DO YOU ANSWER TO", "WHAT DO PEOPLE CALL YOU")) return KnowledgeTarget.Name;
        if (ContainsAny(bare, "WHO ARE YOU", "WHAT ARE YOU", "YOUR ROLE")) return KnowledgeTarget.Role;
        if (ContainsAny(bare, "WHERE ARE YOU FROM", "YOUR ORIGIN", "WHERE DID YOU COME FROM", "WHERE WERE YOU BORN")) return KnowledgeTarget.Origin;
        if (ContainsAny(bare, "WHERE DO YOU LIVE", "YOUR HOME", "A HOME HERE")) return KnowledgeTarget.Home;
        if (ContainsAny(bare, "YOUR FAMILY", "HAVE FAMILY", "ANY FAMILY", "ABOUT YOUR FAMILY")) return KnowledgeTarget.Family;
        if (ContainsAny(bare, "YOUR JOB", "YOUR OCCUPATION", "WHAT DO YOU DO", "WHAT WORK DO YOU DO")) return KnowledgeTarget.Occupation;
        if (ContainsAny(bare, "YOUR FACTION", "WHO DO YOU SERVE", "WHICH FACTION")) return KnowledgeTarget.Faction;
        if (ContainsAny(bare, "ABOUT YOURSELF", "YOUR TRAITS", "WHAT ARE YOU LIKE", "TRAITS DEFINE YOU")) return KnowledgeTarget.Traits;
        if (ContainsAny(bare, "WHAT CAN YOU DO", "HOW CAN YOU HELP", "CAN YOU TRADE", "SKILLS CAN YOU OFFER")) return KnowledgeTarget.Capabilities;
        if (bare == "BALANCE" || ContainsAny(bare, "HOW MUCH MONEY", "MY BALANCE", "HOW MUCH GOLD", "MONEY DO I HAVE",
                "DID MY BALANCE CHANGE", "CHECK BALANCE"))
            return KnowledgeTarget.Balance;
        if (ContainsAny(bare, "MY INVENTORY", "WHAT DO I CARRY", "WHAT ITEMS DO I HAVE", "ITEMS ARE IN MY PACK",
            "LIST EVERYTHING IN MY INVENTORY", "CHECK WHETHER WE HAVE")) return KnowledgeTarget.Inventory;
        if (ContainsAny(bare, "WHERE AM I", "CURRENT LOCATION")) return KnowledgeTarget.CurrentLocation;
        if (ContainsAny(bare, "WHAT WORLD FACTS DO YOU KNOW", "WHICH WORLD FACTS DO YOU KNOW", "WHAT FACTS DO YOU KNOW"))
            return KnowledgeTarget.WorldFact;
        var knownItemDescription = StartsWithAny(bare, "TELL ME ABOUT ", "WHAT DO YOU KNOW ABOUT ") &&
                                   ContainsAny(bare, "IRON SWORD", "HEALTH POTION", "ROPE", "SWORD", "POTION");
        if (!knownItemDescription && StartsWithAny(bare, "WHAT IS ", "WHO IS ", "TELL ME ABOUT ", "TELL ME A FACT ABOUT ",
            "WHAT DO YOU KNOW ABOUT ", "WHAT IS KNOWN ABOUT ", "CHECK THE WORLD RECORD FOR "))
            return KnowledgeTarget.WorldFact;
        return KnowledgeTarget.None;
    }

    private static IReadOnlyList<ContentFlag> ContentFor(string text)
    {
        var flags = new List<ContentFlag>();
        if (ContainsAny(text, "FUCK", "SHIT", "BITCH", "IDIOT", "ASSHOLE", "DAMN", "LIKE HELL")) flags.Add(ContentFlag.Profanity);
        if (ContainsAny(text, "KILL", "ATTACK", "SHOOT", "STAB", "FIGHT", "FIRE", "BURN", "GOBLIN", "DRAGON",
            "BEAST", "NECROMANCER", "WYRM", "HOSTILE DRONE", "ALIEN", "BANDIT CAPTAIN"))
            flags.Add(ContentFlag.FictionalViolence);
        if (ContainsAny(text, "GUTS", "DISEMBOWEL", "DECAPITATE", "GORE")) flags.Add(ContentFlag.GraphicViolence);
        if (ContainsAny(text, "OR ELSE", "I WILL KILL", "I WILL STAB", "I WILL BURN", "YOU WILL DIE")) flags.Add(ContentFlag.Threat);
        if (ContainsAny(text, "STEAL", "ROB", "SMUGGLE", "MURDER")) flags.Add(ContentFlag.Crime);
        if (ContainsAny(text, "FAGGOT", "NIGGER", "KIKE") || IsIdentityExclusion(text))
            flags.Add(ContentFlag.IdentityAttack);
        if (ContainsAny(text, "KILL MYSELF", "SUICIDE", "HURT MYSELF")) flags.Add(ContentFlag.SelfHarm);
        if (ContainsAny(text, "SEX", "NAKED", "FUCK ME")) flags.Add(ContentFlag.SexualContent);
        if (ContainsAny(text, "RAPE", "SEXUAL ASSAULT")) flags.Add(ContentFlag.SexualViolence);
        return flags.Distinct().ToArray();
    }

    private static IReadOnlyList<DialogueSlot> ExtractSlots(string text)
    {
        var slots = new List<DialogueSlot>();
        AddMatches(SlotType.Quantity, "\\b[0-9]+\\b", 1.0);
        var transactionPhrase = ContainsAny(text, "BUY ", "SELL ", "PURCHASE ");
        foreach (var (word, value) in new[] { ("ONE", "1"), ("TWO", "2"), ("THREE", "3"), ("FOUR", "4"), ("FIVE", "5") })
        {
            var match = Regex.Match(text, $"\\b{word}\\b", RegexOptions.CultureInvariant);
            if (transactionPhrase && match.Success)
                slots.Add(new DialogueSlot(SlotType.Quantity, BioTag.B, value, match.Index, match.Length, 1.0));
        }
        const string end = "(?=, CASE[0-9A-F]+[?.!]|[?.!]|$)";
        AddCapture(SlotType.Place, "\\bWHERE (?:IS|ARE) (?<VALUE>[A-Z0-9][A-Z0-9 '\\-]{0,31}?)" + end, 0.99);
        AddCapture(SlotType.Place, "\\bWHERE CAN I FIND (?<VALUE>[A-Z0-9][A-Z0-9 '\\-]{0,31}?)" + end, 0.99);
        AddCapture(SlotType.Place, "\\b(?:LOCATE|FIND|POINT OUT|SHOW ME) (?:THE )?(?<VALUE>[A-Z0-9][A-Z0-9 '\\-]{0,31}?)(?: FOR ME)?" + end, 0.98);
        AddCapture(SlotType.Item, "\\b(?:PRICE|COST) (?:OF )?(?<VALUE>[A-Z0-9][A-Z0-9 '\\-]{0,31}?)" + end, 0.99);
        AddCapture(SlotType.Item, "\\b(?:BUY|SELL|PURCHASE) (?:ME )?(?:(?:[0-9]+|ONE|TWO|THREE|FOUR|FIVE|A|SOME) )?(?<VALUE>[A-Z][A-Z '\\-]{0,31}?)" + end, 0.98);
        AddCapture(SlotType.Other, "\\b(?:TELL ME ABOUT|TELL ME A FACT ABOUT|WHAT DO YOU KNOW ABOUT|WHAT IS KNOWN ABOUT|WHAT IS|CHECK THE WORLD RECORD FOR) (?<VALUE>[A-Z0-9][A-Z0-9 '\\-]{0,31}?)" + end, 0.96);
        if (Regex.IsMatch(text, "\\b(?:BUY|SELL|PURCHASE) (?:ME )?(?:A|AN) ", RegexOptions.CultureInvariant) &&
            slots.All(slot => slot.Type != SlotType.Quantity))
            slots.Add(new DialogueSlot(SlotType.Quantity, BioTag.B, "1", 0, 1, 1.0));
        foreach (var item in new[] { "IRON SWORD", "HEALTH POTION", "ROPE", "SWORD", "POTION" })
        {
            var index = text.IndexOf(item, StringComparison.Ordinal);
            if (index >= 0 && slots.All(slot => slot.Type != SlotType.Item || slot.Start != index))
                slots.Add(new DialogueSlot(SlotType.Item, BioTag.B, CanonicalItem(item), index, item.Length, 1.0));
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
            var trimmed = value.Value.Trim();
            slots.Add(new DialogueSlot(type, BioTag.B, trimmed, value.Index, trimmed.Length, confidence));
        }
    }

    private static void ResolveReferences(string text, NpcDialogueState state, List<DialogueSlot> slots)
    {
        if (!IsAnaphoric(text) && state.PendingClarification is null) return;
        if (slots.All(slot => slot.Type != SlotType.Item) && state.References.Item is { } item)
            slots.Add(new DialogueSlot(SlotType.Item, BioTag.B, item, 0, item.Length, 0.96));
        if (slots.All(slot => slot.Type != SlotType.Place) && state.References.Place is { } place)
            slots.Add(new DialogueSlot(SlotType.Place, BioTag.B, place, 0, place.Length, 0.96));
        if (slots.All(slot => slot.Type != SlotType.Person) && state.References.Person is { } person)
            slots.Add(new DialogueSlot(SlotType.Person, BioTag.B, person, 0, person.Length, 0.96));
        if (slots.All(slot => slot.Type != SlotType.Vehicle) && state.References.Vehicle is { } vehicle)
            slots.Add(new DialogueSlot(SlotType.Vehicle, BioTag.B, vehicle, 0, vehicle.Length, 0.96));
        if (slots.All(slot => slot.Type != SlotType.System) && state.References.System is { } system)
            slots.Add(new DialogueSlot(SlotType.System, BioTag.B, system, 0, system.Length, 0.96));
    }

    private static void CompleteClarificationSlots(
        string text, NpcDialogueState state, List<DialogueSlot> slots)
    {
        var pending = state.PendingClarification;
        if (pending?.ToolSchema is null || pending.MissingSlots.Count == 0) return;
        var bare = text.Trim().TrimEnd('.', '?', '!');
        if (bare.Length == 0 || bare.Length > 32) return;
        if (pending.MissingSlots.Contains("QUANTITY") && slots.All(slot => slot.Type != SlotType.Quantity))
        {
            var value = bare switch
            {
                "ONE" => "1",
                "TWO" => "2",
                "THREE" => "3",
                "FOUR" => "4",
                "FIVE" => "5",
                _ when int.TryParse(bare, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number >= 0
                    => number.ToString(CultureInfo.InvariantCulture),
                _ => null
            };
            if (value is not null) slots.Add(new DialogueSlot(SlotType.Quantity, BioTag.B, value, 0, bare.Length, 1.0));
        }
        AddFragment("PLACE", SlotType.Place, bare);
        AddFragment("ITEM", SlotType.Item, CanonicalItem(bare));
        AddFragment("TOPIC", SlotType.Other, bare);

        void AddFragment(string name, SlotType type, string value)
        {
            if (!pending.MissingSlots.Contains(name) || slots.Any(slot => slot.Type == type)) return;
            slots.Add(new DialogueSlot(type, BioTag.B, value, 0, bare.Length, 0.98));
        }
    }

    private static ToolDecision SelectTool(
        string text,
        IReadOnlyList<DialogueSlot> slots,
        NpcDialogueState state,
        KnowledgeTarget target,
        GameToolRegistry tools)
    {
        var recognized = new List<string>();
        PendingDialogueAction? resumedAction = null;
        var identityOrigin = target is KnowledgeTarget.Origin or KnowledgeTarget.Home;
        if (!identityOrigin && (Regex.IsMatch(text, "\\bWHERE (?:IS|ARE|CAN I FIND)\\b", RegexOptions.CultureInvariant) ||
            ContainsAny(text, "HOW FAR", "IS IT FAR", "FAR FROM HERE", "LOCATE ", "FIND THE ", "POINT OUT ",
                "SHOW ME THE ", "GET THERE", "REACH IT", "GUIDE ME THERE")))
            recognized.Add("LOOKUP_LOCATION");
        if (ContainsAny(text, "LIST WARES", "SHOW ME YOUR WARES", "WHAT DO YOU SELL", "WHAT DO YOU HAVE FOR SALE",
            "WHAT HAVE YOU GOT FOR SALE", "SHOW WARES", "SHOW ME WHAT YOU SELL", "MERCHANT STOCK",
            "SELL ME SOME WARES")) recognized.Add("LIST_WARES");
        var itemDescription = ContainsAny(text, "TELL ME ABOUT", "WHAT DO YOU KNOW ABOUT") &&
                              ContainsAny(text, "IRON SWORD", "HEALTH POTION", "ROPE", "SWORD", "POTION");
        if (ContainsAny(text, "PRICE", "COST") || itemDescription) recognized.Add("LOOKUP_PRICE");
        if (ContainsAny(text, " BUY ", "BUY ", " PURCHASE ", "PURCHASE ")) recognized.Add("BUY");
        if (ContainsAny(text, " SELL ", "SELL ") &&
            !ContainsAny(text, "WHAT DO YOU SELL", "SHOW ME WHAT YOU SELL", "SELL ME SOME WARES")) recognized.Add("SELL");
        if (target == KnowledgeTarget.Balance || (IsAnaphoric(text) && state.LastTool is "BUY" or "SELL" && text.Contains("HOW MUCH", StringComparison.Ordinal))) recognized.Add("GET_BALANCE");
        if (target == KnowledgeTarget.Inventory) recognized.Add("LIST_INVENTORY");
        if (target == KnowledgeTarget.CurrentLocation) recognized.Add("GET_CURRENT_LOCATION");
        if (target == KnowledgeTarget.WorldFact && !itemDescription) recognized.Add("LOOKUP_WORLD_FACT");
        recognized = recognized.Distinct(StringComparer.Ordinal).ToList();
        if (recognized.Count == 0 && state.PendingClarification?.ToolSchema is { } pendingTool &&
            state.PendingClarification.MissingSlots.Any(name => name switch
            {
                "PLACE" => slots.Any(slot => slot.Type == SlotType.Place),
                "ITEM" => slots.Any(slot => slot.Type == SlotType.Item),
                "QUANTITY" => slots.Any(slot => slot.Type == SlotType.Quantity),
                "TOPIC" => slots.Any(slot => slot.Type == SlotType.Other),
                _ => false
            }))
            recognized.Add(pendingTool);
        if (recognized.Count == 0 && state.PendingActions.Count > 0 && IsContinuation(text))
        {
            resumedAction = state.PendingActions[0];
            recognized.AddRange(state.PendingActions.Where(action => action.ToolSchema is not null)
                .Select(action => action.ToolSchema!));
        }
        if (recognized.Count == 0) return ToolDecision.None;
        var name = recognized[0];
        if (!tools.TryGet(name, out var tool))
            return new(name, EmptyArguments, 1.0, false, ["CAPABILITY_UNAVAILABLE"], Additional(recognized));

        var arguments = resumedAction?.ToolSchema == name
            ? new Dictionary<string, string>(resumedAction.Arguments, StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var parameter in tool.Schema.Parameters)
        {
            var slotType = parameter.Name switch
            {
                "PLACE" => SlotType.Place,
                "ITEM" => SlotType.Item,
                "QUANTITY" => SlotType.Quantity,
                "TOPIC" => SlotType.Other,
                _ => SlotType.Other
            };
            var matching = slots.Where(slot => slot.Type == slotType).Select(slot => slot.Value).Distinct(StringComparer.Ordinal).ToArray();
            if (matching.Length == 1) arguments[parameter.Name] = parameter.Name == "ITEM" ? CanonicalItem(matching[0]) : matching[0];
        }
        var missing = tool.Schema.Parameters.Where(parameter => parameter.Required && !arguments.ContainsKey(parameter.Name))
            .Select(parameter => parameter.Name).ToArray();
        var ambiguous = tool.Schema.Parameters.Any(parameter =>
        {
            var slotType = parameter.Name switch { "PLACE" => SlotType.Place, "ITEM" => SlotType.Item, "QUANTITY" => SlotType.Quantity, _ => SlotType.Other };
            var values = slots.Where(slot => slot.Type == slotType).Select(slot => slot.Value)
                .Distinct(StringComparer.Ordinal).ToArray();
            return values.Length > 1 || parameter.Name == "PLACE" && values.Any(value => ContainsPhrase(value, "AND"));
        });
        var confidence = missing.Length > 0 || ambiguous ? 0.50 : tool.Schema.MutatesWorldState ? 0.995 : 0.98;
        var threshold = tool.Schema.MutatesWorldState ? MutatingToolPrecisionThreshold : ReadOnlyToolPrecisionThreshold;
        var canExecute = missing.Length == 0 && !ambiguous && confidence >= threshold;
        return new(name, new ReadOnlyDictionary<string, string>(arguments), confidence, canExecute,
            missing.Length > 0 ? missing : ambiguous ? ["AMBIGUOUS_SLOT"] : [], Additional(recognized));

        IReadOnlyList<PendingDialogueAction> Additional(IReadOnlyList<string> names) => names.Skip(1)
            .Take(3).Select(pendingName =>
            {
                var prior = state.PendingActions.FirstOrDefault(action => action.ToolSchema == pendingName);
                if (prior is not null) return prior;
                if (!tools.TryGet(pendingName, out var pendingTool))
                    return new PendingDialogueAction("EXECUTE_TOOL", pendingName, EmptyArguments);
                var pendingArguments = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var parameter in pendingTool.Schema.Parameters)
                {
                    var slotType = ParameterSlotType(parameter.Name);
                    var matching = slots.Where(slot => slot.Type == slotType).Select(slot => slot.Value)
                        .Distinct(StringComparer.Ordinal).ToArray();
                    if (matching.Length == 1)
                        pendingArguments[parameter.Name] = parameter.Name == "ITEM" ? CanonicalItem(matching[0]) : matching[0];
                }
                return new PendingDialogueAction("EXECUTE_TOOL", pendingName,
                    new ReadOnlyDictionary<string, string>(pendingArguments));
            }).ToArray();

        static SlotType ParameterSlotType(string parameter) => parameter switch
        {
            "PLACE" => SlotType.Place,
            "ITEM" => SlotType.Item,
            "QUANTITY" => SlotType.Quantity,
            "TOPIC" => SlotType.Other,
            _ => SlotType.Other
        };
    }

    private static bool TryRenderPersona(
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
            KnowledgeTarget.Capabilities => CapabilityText(tools),
            _ => string.Empty
        };
        return text.Length > 0;

        static string Fact(string prefix, string? value, string unknown) => value is null ? unknown + "." : $"{prefix} {value}.";
        static string WithArticle(string role) => "AEIOU".Contains(role[0]) ? "AN " + role : "A " + role;
        static string CapabilityText(GameToolRegistry registry)
        {
            var names = registry.Schemas.Select(schema => schema.Name).ToHashSet(StringComparer.Ordinal);
            var capabilities = new List<string>();
            if (names.Overlaps(["LIST_WARES", "LOOKUP_PRICE", "BUY", "SELL"])) capabilities.Add("TRADE");
            if (names.Contains("LOOKUP_LOCATION")) capabilities.Add("FIND PLACES");
            if (names.Contains("LOOKUP_WORLD_FACT")) capabilities.Add("CHECK WORLD FACTS");
            if (names.Contains("GET_BALANCE") || names.Contains("LIST_INVENTORY")) capabilities.Add("CHECK YOUR POSSESSIONS");
            return capabilities.Count == 0
                ? "I HAVE NO REGISTERED GAME CAPABILITIES."
                : $"I CAN {string.Join(", ", capabilities)}.";
        }
    }

    private static string ClarificationFor(ToolDecision decision, KnowledgeTarget target)
    {
        if (decision.Reasons.Contains("PLACE")) return "WHICH PLACE DO YOU MEAN?";
        if (decision.Reasons.Contains("ITEM")) return "WHICH ITEM DO YOU MEAN?";
        if (decision.Reasons.Contains("QUANTITY")) return "HOW MANY DO YOU MEAN?";
        if (decision.Reasons.Contains("TOPIC") && target == KnowledgeTarget.WorldFact)
            return "WHICH WORLD FACT DO YOU WANT ME TO CHECK?";
        if (decision.Reasons.Contains("TOPIC")) return "WHICH PERSON, PLACE, OR SUBJECT DO YOU MEAN?";
        if (decision.Reasons.Contains("AMBIGUOUS_SLOT")) return "PLEASE NAME ONE TARGET.";
        if (target == KnowledgeTarget.WorldFact) return "WHICH WORLD FACT DO YOU WANT ME TO CHECK?";
        return "PLEASE EXPLAIN WHAT YOU NEED.";
    }

    private static (ResponsePlanDefinition Plan, string Text)? RankResponse(
        StructuredPerception perception, string input, int seed, GameToolRegistry tools)
    {
        var plans = V11ResponseCatalog.Plans.Where(plan =>
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
        plan.Keywords.Count(keyword => input.Contains(keyword, StringComparison.Ordinal)) * 0.5 +
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

    private static NpcState ToLegacyState(NpcDialogueState state) => new(
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
        var phrase = value.Trim();
        if (phrase.Length == 0) return false;
        for (var index = text.IndexOf(phrase, StringComparison.Ordinal);
             index >= 0;
             index = text.IndexOf(phrase, index + 1, StringComparison.Ordinal))
        {
            var before = index == 0 || !Tokenizer.IsIdentifierCharacter(text[index - 1]);
            var end = index + phrase.Length;
            var after = end == text.Length || !Tokenizer.IsIdentifierCharacter(text[end]);
            if (before && after) return true;
        }
        return false;
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
