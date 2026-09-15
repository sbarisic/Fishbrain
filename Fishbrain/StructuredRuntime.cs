using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Fishbrain;

public sealed partial class LegacyBrain
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
        var packed = PackTurns(request.Utterances);
        var currentUtterance = request.Utterances[^1];
        var current = DialogueText.Normalize(currentUtterance.Text);
        var classificationQuestion = IsClassificationQuestion(current);
        var unsupportedActivity = UnsupportedActivityCommand(current, tools);
        var slots = ExtractSlots(current).ToList();
        slots.RemoveAll(slot => slot.Type == SlotType.Place && IsDeicticPlace(slot.Value));
        CompleteClarificationSlots(current, request.State, slots);
        ResolveReferences(current, request.State, slots);
        slots.RemoveAll(slot => slot.Type == SlotType.Place && IsDeicticPlace(slot.Value));

        var learned = _structuredHeads.Updates > 0
            ? _structuredHeads.Predict(packed.Text, slots, ContextVector(packed.Text), current, packed.Utterances)
            : RulePerception(current, slots, tools);
        var raw = learned;
        var constraints = new List<PerceptionConstraint>();
        var perception = ApplyConstraints(learned, current, slots, request.State, tools, constraints);
        var discourse = DiscourseResolver.Resolve(request, packed.Utterances, current, perception.Discourse);
        discourse = RejectIncompatibleLearnedDiscourse(discourse, current, constraints);
        perception = perception with { Discourse = discourse };
        perception = ApplyDiscourseFrameConstraints(perception, discourse, current, constraints);
        if (discourse.Evidence != "NONE" &&
            !discourse.Evidence.StartsWith("LEARNED_", StringComparison.Ordinal))
        {
            constraints.Add(new PerceptionConstraint(
                PerceptionConstraintOperation.Enforce,
                "DISCOURSE",
                discourse.Act.ToString().ToUpperInvariant(),
                1.0,
                discourse.Confidence,
                discourse.Evidence,
                "DETERMINISTIC_DISCOURSE_CONSTRAINT"));
        }

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
        var discourseAction = DiscourseResponseAction.None;
        var pendingActions = toolDecision.Reasons.Contains("CONFIRM_PURCHASE") && toolDecision.Name is { } pendingTool
            ? new List<PendingDialogueAction>
            {
                new("EXECUTE_TOOL", pendingTool, toolDecision.Arguments)
            }
            : toolDecision.Name is null
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
            text = ContextualizeCurrentLocation(text, current, toolDecision.Name, toolResult);
            source = ResponseSource.ToolTemplate;
        }
        else if (perception.Policy is not (ResponsePolicy.Refuse or ResponsePolicy.Defer) &&
                 ConversationalResponder.TryRespond(
                     request,
                     discourse,
                     out text,
                     out discourseAction,
                     out fallbackReason))
        {
            source = ResponseSource.ConversationalRepair;
            selectedCandidate = "DISCOURSE_" + discourseAction.ToString().ToUpperInvariant();
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
        else
        {
            var ranked = RankResponse(perception, current, request.Seed, tools);
            var conversationalRealization = request.ResponseMode == ResponseMode.Production &&
                CanGenerateConversation(perception) &&
                (ranked is null || perception.ResponseCandidateId == "ACKNOWLEDGE" ||
                 ranked.Value.Plan.Id == "ACKNOWLEDGE");
            if (conversationalRealization)
            {
                var conditioned = ConversationConditioning.Build(request.Persona, request.PlayerProfile,
                    request.State, discourse, packed.Utterances, discourseAction, []);
                text = GeneratedReply(conditioned, current, ToModelState(request.State), request.Seed).Text;
                if (ConversationalOutputValidator.IsSafe(
                        text,
                        request.Persona,
                        request.State.SessionFacts,
                        out var generationFailure))
                {
                    source = ResponseSource.ConversationalGenerated;
                }
                else
                {
                    text = "I AM NOT CERTAIN ENOUGH TO CLAIM THAT. WHAT DO YOU THINK?";
                    source = ResponseSource.Fallback;
                    fallbackReason = generationFailure;
                }
            }
            else if (ranked is null)
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

        var invalidConversationalText = text.Length > 256 ||
            text.Length > 0 && !DialogueText.IsCanonical(text);
        if (invalidConversationalText && source is
            (ResponseSource.ConversationalRepair or ResponseSource.ConversationalGenerated))
        {
            text = "I AM NOT CERTAIN ENOUGH TO RESPOND TO THAT. COULD YOU REPHRASE IT?";
            source = ResponseSource.Fallback;
            selectedCandidate = null;
            discourseAction = DiscourseResponseAction.RepairMisunderstanding;
            fallbackReason = "INVALID_CONVERSATIONAL_RESPONSE";
        }

        if (text.Length > 256) throw new InvalidDataException("Runtime produced an overlength response.");
        if (text.Length > 0 && !DialogueText.IsCanonical(text))
            throw new InvalidDataException("Runtime produced noncanonical response text.");

        var plan = new TurnPlan(perception.Policy, perception.ToolSchema, selectedCandidate,
            perception.KnowledgeTarget, pendingActions,
            perception.Policy == ResponsePolicy.Clarify ? text : null,
            perception.Policy == ResponsePolicy.Clarify ? toolDecision.Reasons : [],
            discourseAction, discourse.AntecedentUtterance);
        var state = LegacyDialogueStateReducer.Apply(request.State, request.PlayerProfile, currentUtterance,
            request.ResponseSequence, perception, plan, toolResult, text, fallbackReason);
        var tone = Cognition.ToneFor(state.Mood);
        var diagnostics = new ReplyDiagnostics(
            perception.Confidence, constraints, source, selectedCandidate, invocation,
            slots, _tokenizer.UnknownWords(current), fallbackReason, packed.TurnCount, packed.TokenCount);
        return new ReplyResult(text, state, raw, perception, plan, tone, diagnostics);
    }

    private static bool CanGenerateConversation(StructuredPerception perception) =>
        perception.Policy is ResponsePolicy.Answer or ResponsePolicy.Acknowledge &&
        perception.ToolSchema is null && perception.KnowledgeTarget == KnowledgeTarget.None &&
        perception.ContentFlags.Count == 0 && perception.Domains.All(domain => domain is
             DialogueDomain.Social or DialogueDomain.Identity or DialogueDomain.Wellbeing or DialogueDomain.Activity);

    private static bool IsDeicticPlace(string value) => value is
        "I" or "ME" or "WE" or "US" or "YOU" or "HERE" or "THERE" or "THIS PLACE" or "CURRENT LOCATION";

    private static string ContextualizeCurrentLocation(
        string rendered,
        string current,
        string toolName,
        GameToolResult result)
    {
        if (toolName != "GET_CURRENT_LOCATION" || !result.Success ||
            !result.Fields.TryGetValue("LOCATION", out var location))
        {
            return rendered;
        }

        var bare = current.Trim().TrimEnd('.', '?', '!');
        if (bare is "WHERE ARE WE" or "WERE ARE WE")
        {
            return $"WE ARE AT {location}.";
        }

        if (bare is "WHERE ARE YOU" or "WERE ARE YOU")
        {
            return $"I AM AT {location}.";
        }

        return rendered;
    }

    private static DiscourseFrame RejectIncompatibleLearnedDiscourse(
        DiscourseFrame frame,
        string current,
        ICollection<PerceptionConstraint> constraints)
    {
        if (!frame.Evidence.StartsWith("LEARNED_", StringComparison.Ordinal))
        {
            return frame;
        }

        if (frame.FactValueSpan is { } factValueSpan)
        {
            try
            {
                factValueSpan.Validate(current);
            }
            catch (ArgumentException)
            {
                constraints.Add(new PerceptionConstraint(
                    PerceptionConstraintOperation.Veto,
                    "FACT_VALUE",
                    factValueSpan.NormalizedValue,
                    -1.0,
                    frame.Confidence,
                    current,
                    "INVALID_LEARNED_FACT_SPAN"));
                return DiscourseFrame.Empty;
            }
        }

        var ruleActs = RuleSpeechActs(current);
        var compatible = frame.Act switch
        {
            DiscourseAct.Inform =>
                frame.Subject != DialogueParticipant.None &&
                frame.FactKind is not null &&
                frame.FactValueSpan is not null &&
                ruleActs.Any(act => act is SpeechAct.Inform or SpeechAct.Report or SpeechAct.Correct),
            DiscourseAct.Correct or DiscourseAct.RejectAssumption => ruleActs.Contains(SpeechAct.Correct),
            DiscourseAct.AskExplanation => ruleActs.Contains(SpeechAct.Ask) && ContainsAny(current,
                "MEAN", "EXPLAIN THAT", "YOU SAID", "TALKING ABOUT", "SUPPOSED TO", "EARLIER REMARK",
                "REFER TO"),
            DiscourseAct.ReferBack => ruleActs.Contains(SpeechAct.Ask) && ContainsAny(current,
                "REMEMBER", "BEFORE", "EARLIER", "YOU SAID", "YOU TOLD", "REFER BACK", "CALL BACK"),
            DiscourseAct.None => true,
            _ => false
        };
        if (compatible)
        {
            return frame;
        }

        constraints.Add(new PerceptionConstraint(
            PerceptionConstraintOperation.Veto,
            "DISCOURSE",
            frame.Act.ToString().ToUpperInvariant(),
            -1.0,
            frame.Confidence,
            current,
            "NO_COMPATIBLE_CURRENT_TURN_DISCOURSE_EVIDENCE"));
        return DiscourseFrame.Empty;
    }

    private static StructuredPerception ApplyDiscourseFrameConstraints(
        StructuredPerception perception,
        DiscourseFrame frame,
        string current,
        ICollection<PerceptionConstraint> constraints)
    {
        if (frame.Act == DiscourseAct.None || IsDirectInsult(current) ||
            perception.ContentFlags.Contains(ContentFlag.IdentityAttack))
        {
            return perception;
        }

        var speechAct = frame.Act switch
        {
            DiscourseAct.Correct or DiscourseAct.RejectAssumption => SpeechAct.Correct,
            DiscourseAct.AskExplanation or DiscourseAct.ReferBack => SpeechAct.Ask,
            _ => SpeechAct.Inform
        };
        var domain = frame.FactKind switch
        {
            DialogueFactKind.Name or DialogueFactKind.Role or DialogueFactKind.Occupation or
                DialogueFactKind.Origin or DialogueFactKind.Home or DialogueFactKind.Family =>
                DialogueDomain.Identity,
            DialogueFactKind.Activity => DialogueDomain.Activity,
            _ => DialogueDomain.Social
        };
        constraints.Add(new PerceptionConstraint(
            PerceptionConstraintOperation.Enforce,
            "SPEECH_ACT",
            speechAct.ToString().ToUpperInvariant(),
            1.0,
            frame.Confidence,
            frame.Evidence,
            "UNAMBIGUOUS_DISCOURSE_FRAME"));
        constraints.Add(new PerceptionConstraint(
            PerceptionConstraintOperation.Enforce,
            "DOMAIN",
            domain.ToString().ToUpperInvariant(),
            1.0,
            frame.Confidence,
            frame.Evidence,
            "UNAMBIGUOUS_DISCOURSE_FRAME"));
        return perception with
        {
            SpeechActs = [speechAct],
            Domains = [domain],
            Goals = [DialogueGoal.InformationExchange],
            Affect = UserAffect.Neutral,
            Stance = DialogueStance.Neutral,
            Policy = ResponsePolicy.Answer
        };
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
        if (request.Utterances is null || request.Utterances.Count == 0)
            throw new ArgumentException("At least one structured utterance is required.", nameof(request));
        if (!Enum.IsDefined(request.ResponseMode))
            throw new ArgumentOutOfRangeException(nameof(request), "ResponseMode is invalid.");
        if (request.Utterances.Any(turn => turn is null || !Enum.IsDefined(turn.Speaker) || turn.Sequence < 0 ||
            string.IsNullOrWhiteSpace(turn.Text) || turn.Text.Length > 4_096))
            throw new ArgumentException("Structured utterances must have a valid sequence, speaker, and 1-4096 text characters.", nameof(request));
        if (request.Utterances.Zip(request.Utterances.Skip(1)).Any(pair => pair.First.Sequence >= pair.Second.Sequence))
            throw new ArgumentException("Utterance sequences must be unique and strictly increasing.", nameof(request));
        if (request.Utterances[^1].Speaker != DialogueRole.Player)
            throw new ArgumentException("The final structured utterance must be a player utterance.", nameof(request));
        if (request.ResponseSequence <= request.Utterances[^1].Sequence ||
            request.Utterances.Any(utterance => utterance.Sequence == request.ResponseSequence))
            throw new ArgumentException(
                "ResponseSequence must be unused and greater than the current player sequence.",
                nameof(request));
        ArgumentNullException.ThrowIfNull(request.State);
        request.State.Validate();
        ArgumentNullException.ThrowIfNull(request.Persona);
        request.Persona.Validate();
        ArgumentNullException.ThrowIfNull(request.PlayerProfile);
        request.PlayerProfile.Validate();

        static bool ValidRequestId(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 128 &&
            value == value.Trim() && value.All(character => !char.IsControl(character));
    }

    private (string Text, int TurnCount, int TokenCount, IReadOnlyList<DialogueUtterance> Utterances) PackTurns(
        IReadOnlyList<DialogueUtterance> turns)
    {
        var retained = new List<(DialogueUtterance Utterance, string Text, int Tokens)>();
        var count = 0;
        for (var index = turns.Count - 1; index >= 0; index--)
        {
            var normalized = DialogueText.Normalize(turns[index].Text);
            var role = turns[index].Speaker == DialogueRole.Player ? "PLAYER" : "NPC";
            var complete = role + " " + DialogueText.TerminateTurn(normalized);
            var tokens = _tokenizer.Encode(complete).Length;
            if (index == turns.Count - 1 && tokens > Config.ContextLength)
                throw new ArgumentException(
                    $"The current turn requires {tokens} tokens, but the model context allows {Config.ContextLength}.", nameof(turns));
            if (index != turns.Count - 1 && count + tokens > Config.ContextLength) break;
            retained.Add((turns[index], complete, tokens));
            count += tokens;
        }
        retained.Reverse();
        return (string.Join(' ', retained.Select(item => item.Text)), retained.Count, count,
            retained.Select(item => item.Utterance).ToArray());
    }
}
