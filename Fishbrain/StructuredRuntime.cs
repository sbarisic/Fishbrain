using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Fishbrain;

public sealed partial class Brain
{
    private const double ReadOnlyToolPrecisionThreshold = 0.95;
    private const double MutatingToolPrecisionThreshold = 0.99;

    internal static IReadOnlyList<ResponseCandidate> ResponseCandidates { get; } = ResponseCatalog.Plans
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
            text = GeneratedReply(packed.Text, current, ToModelState(request.State), request.Seed).Text;
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
}
