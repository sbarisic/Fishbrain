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
        var slots = ExtractSlots(current).ToList();
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
        var toolDecision = SelectTool(current, slots, request.State, actionableTarget, tools);

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
            source = ResponseSource.RankedVariation;
            selectedCandidate = "NO_RESPONSE";
        }
        else if (TryRenderPersona(perception.KnowledgeTarget, request.Persona, tools, out text, out source))
        {
            selectedCandidate = "PERSONA_" + perception.KnowledgeTarget.ToString().ToUpperInvariant();
        }
        else if (request.ResponseMode == ResponseMode.GeneratedExperimental)
        {
            text = GeneratedReply(packed.Text, current, ToLegacyState(request.State), request.Seed).Text;
            source = ResponseSource.GeneratedExperimental;
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
            perception.Policy == ResponsePolicy.Clarify ? text : null);
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
            SpeechAct.Greet or SpeechAct.Farewell or SpeechAct.Refuse or SpeechAct.Threaten or SpeechAct.Report);

    private static DialogueDomain DomainForTool(string name) => name switch
    {
        "LOOKUP_LOCATION" or "GET_CURRENT_LOCATION" => DialogueDomain.LocationNavigation,
        "LIST_INVENTORY" => DialogueDomain.ItemsInventory,
        "LOOKUP_WORLD_FACT" => DialogueDomain.LoreWorld,
        _ => DialogueDomain.TradeEconomy
    };

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
        ArgumentNullException.ThrowIfNull(request.Persona);
        request.Persona.Validate();
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
            ["SPEECH_ACT"] = 0.97, ["DOMAIN"] = 0.95, ["GOAL"] = 0.93,
            ["AFFECT"] = 0.97, ["STANCE"] = 0.97, ["POLICY"] = 0.98,
            ["SLOTS"] = slots.Count == 0 ? 1.0 : slots.Min(slot => slot.Confidence),
            ["CONTENT"] = 0.99, ["TOOL"] = 0.0, ["RESPONSE_CANDIDATE"] = 0.90,
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
        var result = learned with { Slots = slots };
        var rules = RulePerception(current, slots, tools);
        var tradeEvidence = ContainsAny(current, "TRADE", "BUY", "SELL", "WARES", "PRICE", "COST", "GOLD", "MONEY");
        if (!tradeEvidence && result.Domains.Contains(DialogueDomain.TradeEconomy))
        {
            result = result with { Domains = result.Domains.Where(domain => domain != DialogueDomain.TradeEconomy).ToArray() };
            constraints.Add(new PerceptionConstraint(PerceptionConstraintOperation.Veto, "DOMAIN", "TRADE_ECONOMY",
                -1.0, 0.99, current, "NO_VALIDATED_TRADE_EVIDENCE"));
        }
        var metaEvidence = ContainsAny(current, "COMMAND", "SETTING", "SAVE GAME", "CONTROL");
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
        foreach (var domain in rules.Domains.Where(domain => domain != DialogueDomain.Social && !result.Domains.Contains(domain)))
        {
            result = result with { Domains = AddLimited(result.Domains, domain, 3) };
            constraints.Add(Boost("DOMAIN", domain.ToString().ToUpperInvariant(), current, "VALIDATED_DOMAIN_EVIDENCE", 0.35));
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

        EnforceExact("HELLO", SpeechAct.Greet, DialogueDomain.Social, ResponsePolicy.Answer);
        EnforceExact("HI", SpeechAct.Greet, DialogueDomain.Social, ResponsePolicy.Answer);
        EnforceExact("GOODBYE", SpeechAct.Farewell, DialogueDomain.Social, ResponsePolicy.Answer);

        if (ContainsAny(current, "TRADE", "BUY", "SELL", "WARES", "PRICE", "COST"))
        {
            result = result with
            {
                Domains = AddLimited(result.Domains.Where(domain => domain != DialogueDomain.MetaSystem), DialogueDomain.TradeEconomy, 3),
                Goals = AddLimited(result.Goals, DialogueGoal.Transaction, 3)
            };
            constraints.Add(Boost("DOMAIN", "TRADE_ECONOMY", "TRADE LEXEME", "HIGH_PRECISION_DOMAIN", 0.35));
        }
        if (ContainsAny(current, "SWORD", "POTION", "ROPE", "ITEM", "INVENTORY"))
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
        if (IsDirectInsult(current) || rules.ContentFlags.Contains(ContentFlag.IdentityAttack) ||
            rules.ContentFlags.Contains(ContentFlag.Threat) || IsUnsafeDirective(current))
        {
            result = result with { Affect = UserAffect.Hostile, Stance = DialogueStance.Hostile, Policy = ResponsePolicy.Refuse };
            constraints.Add(Enforce("STANCE", "HOSTILE", current, "DIRECT_HOSTILITY"));
        }
        else if (!ContainsAny(current, "AFRAID", "WORRIED", "ANGRY", "UPSET", "HATE", "IDIOT") &&
                 result.Affect == UserAffect.Hostile)
        {
            result = result with { Affect = rules.Affect, Stance = rules.Stance };
            constraints.Add(new PerceptionConstraint(PerceptionConstraintOperation.Veto, "AFFECT", "HOSTILE", -1.0,
                0.96, current, "NO_HOSTILE_EVIDENCE"));
        }

        var hostileEvidence = IsDirectInsult(current) || IsUnsafeDirective(current) ||
                              rules.ContentFlags.Contains(ContentFlag.IdentityAttack) ||
                              rules.ContentFlags.Contains(ContentFlag.Threat) ||
                              rules.ContentFlags.Contains(ContentFlag.SexualViolence);
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
        if (result.Policy == ResponsePolicy.Defer && rules.Policy == ResponsePolicy.Answer &&
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

        var target = rules.KnowledgeTarget != KnowledgeTarget.None
            ? rules.KnowledgeTarget
            : IsAnaphoric(current) ? state.PendingKnowledgeTarget : learned.KnowledgeTarget;
        if (target != learned.KnowledgeTarget && target != KnowledgeTarget.None)
        {
            result = result with { KnowledgeTarget = target };
            constraints.Add(Enforce("KNOWLEDGE_TARGET", target.ToString().ToUpperInvariant(), current, "EXPLICIT_OR_STATE_REFERENCE"));
        }

        if (result.SpeechActs.Count > 3) result = result with { SpeechActs = result.SpeechActs.Take(3).ToArray() };
        if (result.Domains.Count > 3) result = result with { Domains = result.Domains.Take(3).ToArray() };
        if (result.Goals.Count > 3) result = result with { Goals = result.Goals.Take(3).ToArray() };
        result = result with { ResponseCandidateId = CandidateIdFor(result.SpeechActs, result.Domains, result.Policy, result.KnowledgeTarget) };
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
        if (StartsWithAny(bare, "PLEASE ", "I NEED ", "I WANT ", "CAN YOU ", "COULD YOU ",
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
        Add(DialogueDomain.TradeEconomy, "TRADE", "BUY", "SELL", "WARES", "PRICE", "COST", "GOLD", "MONEY");
        Add(DialogueDomain.ItemsInventory, "SWORD", "POTION", "ROPE", "ITEM", "INVENTORY");
        Add(DialogueDomain.LocationNavigation, "WHERE", "LOCATION", "CASTLE", "INN", "MARKET", "DIRECTION", "HOW FAR");
        Add(DialogueDomain.Identity, "WHO ARE YOU", "YOUR NAME", "FROM?", "YOUR FAMILY", "YOUR HOME", "YOUR JOB", "YOUR FACTION");
        Add(DialogueDomain.Assistance, "HELP", "WHAT CAN YOU DO", "WHAT DO YOU DO");
        Add(DialogueDomain.Wellbeing, "HOW ARE YOU", "ARE YOU WELL", "FEELING");
        Add(DialogueDomain.QuestTask, "QUEST", "MISSION", "TASK");
        Add(DialogueDomain.Combat, "ATTACK", "KILL", "FIGHT", "ENEMY", "WEAPON", "HOSTILE DRONE", "BANDIT CAPTAIN");
        Add(DialogueDomain.Survival, "SURVIVE", "SHELTER", "HUNGER", "THIRST");
        Add(DialogueDomain.HealthRepair, "HEAL", "INJURY", "REPAIR", "BROKEN");
        Add(DialogueDomain.FactionPolitics, "FACTION", "KING", "QUEEN", "POLITICS");
        Add(DialogueDomain.CrimeLaw, "STEAL", "ROBBERY", "CRIME", "GUARD", "LAW");
        Add(DialogueDomain.Magic, "MAGIC", "SPELL", "CURSE", "WIZARD");
        Add(DialogueDomain.Technology, "SYSTEM", "REACTOR", "TERMINAL", "COMPUTER", "DRONE", "DEFENSE GRID", "COLONY", "AIRLOCK");
        Add(DialogueDomain.VehicleTravel, "SHIP", "STARSHIP", "HORSE", "VEHICLE");
        Add(DialogueDomain.Environment, "WEATHER", "STORM", "FOREST", "DESERT");
        Add(DialogueDomain.LoreWorld, "LORE", "HISTORY", "WORLD", "LEGEND");
        Add(DialogueDomain.MetaSystem, "COMMAND", "SETTING", "SAVE GAME", "CONTROL");
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
        if (ContainsAny(text, "ANGRY", "FRUSTRATED", "NOT WHAT I ASKED")) return UserAffect.Frustrated;
        if (ContainsAny(text, "AFRAID", "HELP ME", "DYING", "HURT")) return UserAffect.Distressed;
        if (ContainsAny(text, "THANK", "FRIEND", "PLEASE", "SORRY")) return UserAffect.Friendly;
        return UserAffect.Neutral;
    }

    private static ResponsePolicy RulePolicy(string text, IReadOnlyList<SpeechAct> acts, DialogueStance stance)
    {
        if (stance == DialogueStance.Hostile) return ResponsePolicy.Refuse;
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
        if (ContainsAny(bare, "WHAT IS YOUR NAME", "YOUR NAME", "WHO ARE YOU CALLED", "WHAT NAME DO YOU ANSWER TO", "WHAT DO PEOPLE CALL YOU")) return KnowledgeTarget.Name;
        if (ContainsAny(bare, "WHO ARE YOU", "WHAT ARE YOU", "YOUR ROLE")) return KnowledgeTarget.Role;
        if (ContainsAny(bare, "WHERE ARE YOU FROM", "YOUR ORIGIN", "WHERE DID YOU COME FROM", "WHERE WERE YOU BORN")) return KnowledgeTarget.Origin;
        if (ContainsAny(bare, "WHERE DO YOU LIVE", "YOUR HOME", "A HOME HERE")) return KnowledgeTarget.Home;
        if (ContainsAny(bare, "YOUR FAMILY", "HAVE FAMILY", "ANY FAMILY", "ABOUT YOUR FAMILY")) return KnowledgeTarget.Family;
        if (ContainsAny(bare, "YOUR JOB", "YOUR OCCUPATION", "WHAT DO YOU DO", "WHAT WORK DO YOU DO")) return KnowledgeTarget.Occupation;
        if (ContainsAny(bare, "YOUR FACTION", "WHO DO YOU SERVE", "WHICH FACTION")) return KnowledgeTarget.Faction;
        if (ContainsAny(bare, "ABOUT YOURSELF", "YOUR TRAITS", "WHAT ARE YOU LIKE", "TRAITS DEFINE YOU")) return KnowledgeTarget.Traits;
        if (ContainsAny(bare, "WHAT CAN YOU DO", "HOW CAN YOU HELP", "CAN YOU TRADE", "SKILLS CAN YOU OFFER")) return KnowledgeTarget.Capabilities;
        if (ContainsAny(bare, "HOW MUCH MONEY", "MY BALANCE", "HOW MUCH GOLD", "MONEY DO I HAVE", "DID MY BALANCE CHANGE")) return KnowledgeTarget.Balance;
        if (ContainsAny(bare, "MY INVENTORY", "WHAT DO I CARRY", "WHAT ITEMS DO I HAVE", "ITEMS ARE IN MY PACK",
            "LIST EVERYTHING IN MY INVENTORY", "CHECK WHETHER WE HAVE")) return KnowledgeTarget.Inventory;
        if (ContainsAny(bare, "WHERE AM I", "CURRENT LOCATION")) return KnowledgeTarget.CurrentLocation;
        if (StartsWithAny(bare, "WHAT IS ", "WHO IS ", "TELL ME ABOUT ", "TELL ME A FACT ABOUT ",
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
        if (ContainsAny(text, "FAGGOT", "NIGGER", "KIKE")) flags.Add(ContentFlag.IdentityAttack);
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
        if (!IsAnaphoric(text)) return;
        if (slots.All(slot => slot.Type != SlotType.Item) && state.References.Item is { } item)
            slots.Add(new DialogueSlot(SlotType.Item, BioTag.B, item, 0, item.Length, 0.96));
        if (slots.All(slot => slot.Type != SlotType.Place) && state.References.Place is { } place)
            slots.Add(new DialogueSlot(SlotType.Place, BioTag.B, place, 0, place.Length, 0.96));
        if (slots.All(slot => slot.Type != SlotType.Person) && state.References.Person is { } person)
            slots.Add(new DialogueSlot(SlotType.Person, BioTag.B, person, 0, person.Length, 0.96));
    }

    private static ToolDecision SelectTool(
        string text,
        IReadOnlyList<DialogueSlot> slots,
        NpcDialogueState state,
        KnowledgeTarget target,
        GameToolRegistry tools)
    {
        var recognized = new List<string>();
        var identityOrigin = target is KnowledgeTarget.Origin or KnowledgeTarget.Home;
        if (!identityOrigin && (Regex.IsMatch(text, "\\bWHERE (?:IS|ARE|CAN I FIND)\\b", RegexOptions.CultureInvariant) ||
            ContainsAny(text, "HOW FAR", "IS IT FAR", "FAR FROM HERE", "LOCATE ", "FIND THE ", "POINT OUT ",
                "SHOW ME THE ", "GET THERE", "REACH IT", "GUIDE ME THERE")))
            recognized.Add("LOOKUP_LOCATION");
        if (ContainsAny(text, "LIST WARES", "SHOW ME YOUR WARES", "WHAT DO YOU SELL", "SHOW WARES",
            "SHOW ME WHAT YOU SELL", "MERCHANT STOCK", "SELL ME SOME WARES")) recognized.Add("LIST_WARES");
        if (ContainsAny(text, "PRICE", "COST")) recognized.Add("LOOKUP_PRICE");
        if (ContainsAny(text, " BUY ", "BUY ", " PURCHASE ", "PURCHASE ")) recognized.Add("BUY");
        if (ContainsAny(text, " SELL ", "SELL ") &&
            !ContainsAny(text, "WHAT DO YOU SELL", "SHOW ME WHAT YOU SELL", "SELL ME SOME WARES")) recognized.Add("SELL");
        if (target == KnowledgeTarget.Balance || (IsAnaphoric(text) && state.LastTool is "BUY" or "SELL" && text.Contains("HOW MUCH", StringComparison.Ordinal))) recognized.Add("GET_BALANCE");
        if (target == KnowledgeTarget.Inventory) recognized.Add("LIST_INVENTORY");
        if (target == KnowledgeTarget.CurrentLocation) recognized.Add("GET_CURRENT_LOCATION");
        if (target == KnowledgeTarget.WorldFact) recognized.Add("LOOKUP_WORLD_FACT");
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
            return slots.Where(slot => slot.Type == slotType).Select(slot => slot.Value).Distinct(StringComparer.Ordinal).Count() > 1;
        });
        var confidence = missing.Length > 0 || ambiguous ? 0.50 : tool.Schema.MutatesWorldState ? 0.995 : 0.98;
        var threshold = tool.Schema.MutatesWorldState ? MutatingToolPrecisionThreshold : ReadOnlyToolPrecisionThreshold;
        var canExecute = missing.Length == 0 && !ambiguous && confidence >= threshold;
        return new(name, new ReadOnlyDictionary<string, string>(arguments), confidence, canExecute,
            missing.Length > 0 ? missing : ambiguous ? ["AMBIGUOUS_SLOT"] : [], Additional(recognized));

        static IReadOnlyList<PendingDialogueAction> Additional(IReadOnlyList<string> names) => names.Skip(1)
            .Take(3).Select(name => new PendingDialogueAction("EXECUTE_TOOL", name, EmptyArguments)).ToArray();
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
    private static bool ContainsAny(string text, params string[] values) =>
        values.Any(value => text.Contains(value, StringComparison.Ordinal));
    private static bool StartsWithAny(string text, params string[] values) =>
        values.Any(value => text.StartsWith(value, StringComparison.Ordinal));
    private static bool IsAnaphoric(string text)
    {
        var words = Tokenizer.Lex(text).Where(token => token.Kind == LexicalTokenKind.Word)
            .Select(token => token.Text).ToHashSet(StringComparer.Ordinal);
        return words.Overlaps(["IT", "THAT", "THERE", "NOW"]);
    }
    private static bool IsDirectInsult(string text) => ContainsAny(text,
        "IDIOT", "FAGGOT", "NIGGER", "KIKE", "SHUT UP", "I HATE YOU", "YOU ARE USELESS", "YOU'RE USELESS");
    private static bool IsUnsafeDirective(string text) => ContainsAny(text,
        "JUMP OFF", "HELP ME KILL", "ATTACK THE ", "KILL YOURSELF", "KILL MYSELF");
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
                plan.ToolSchema is null ? [] : ["REQUIRED_ARGUMENT"])
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
            perception.Domains.FirstOrDefault(), perception.KnowledgeTarget,
            plan.ToolSchema ?? state.LastTool,
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
