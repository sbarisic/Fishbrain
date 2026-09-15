using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using Fishbrain.Neural;

namespace Fishbrain;

public sealed partial class Brain
{
    private readonly ContextualNetwork? _contextual;
    private readonly string _contextualCorpusHash = "UNKNOWN";
    private IReadOnlyDictionary<string, double> _executionThresholds = new Dictionary<string, double>();
    public ContextualModelConfig? ContextualConfig => _contextual?.Config;
    public DialogueDomainDefinition? Domain => _contextual?.Domain;

    public static Brain Load(string path, DialogueDomainDefinition domain)
    {
        ArgumentNullException.ThrowIfNull(domain);
        var loaded = ContextualCheckpoint.Load(path, domain);
        return new Brain(loaded.Model, loaded.Header.CompletedSteps, loaded.Header.ExecutionThresholds, loaded.Header.CorpusHash);
    }

    private Brain(ContextualNetwork network, int completedSteps, IReadOnlyDictionary<string, double>? thresholds, string corpusHash = "UNKNOWN")
    {
        _contextual = network;
        _contextualCorpusHash = corpusHash;
        _step = completedSteps;
        _curriculumPhase = "CONTEXTUAL_INFERENCE";
        _vocabulary = network.Vocabulary;
        _tokenizer = network.Tokenizer;
        _random = new(network.Config.Seed);
        _trainedTools = network.Domain.Tools.Select(x => x.Schema.Name).ToHashSet(StringComparer.Ordinal);
        _trainedExamples = [];
        _responseCatalog = [];
        _structuredHeads = null!;
        _confidenceCalibration = ModelSchemas.DefaultCalibration;
        _tokenEmbedding = _outputHead = _positionEmbedding = _intentHead = _affectHead = _expectedHead = [];
        _queryLayers = _keyLayers = _valueLayers = _attentionOutputLayers = _mlpInLayers = _mlpOutLayers = [];
        _weights = _packedGradients = _adamM = _adamV = [];
        Config = new BrainConfig
        {
            LayerCount = network.Config.EncoderLayers,
            EmbeddingSize = network.Config.Width,
            HeadCount = network.Config.Heads,
            MlpSize = network.Config.FeedForwardWidth,
            ContextLength = network.Config.ContextLength,
            AttentionWindow = network.Config.ContextLength,
            PositionPeriod = network.Config.ContextLength,
            MaximumOutputLength = network.Config.MaximumOutputTokens,
            Seed = network.Config.Seed
        };
        _executionThresholds = new ReadOnlyDictionary<string, double>(network.Domain.Tools.ToDictionary(x => x.Schema.Name,
            x => thresholds?.GetValueOrDefault(x.Schema.Name, 1.01) ?? 1.01));
    }

    internal static Brain CreateContextualForTesting(ContextualNetwork network,
        IReadOnlyDictionary<string, double>? thresholds = null) => new(network, 0, thresholds);
    internal static Brain CreateContextualForEvaluation(ContextualNetwork network, int step,
        IReadOnlyDictionary<string, double> thresholds) => new(network, step, thresholds);

    private ReplyResult ContextualReply(ReplyRequest request, GameToolRegistry tools)
    {
        ValidateRequest(request);
        var timer = Stopwatch.StartNew();
        var model = _contextual!;
        var parameters = model.Parameters();
        var candidates = request.PlayerProfile.Facts.Concat(request.State.SessionFacts).Distinct().ToArray();
        var initial = StructuredInput.Pack(request, _tokenizer, model.Config.ContextLength, [], model.Domain);
        var retrievalGraph = new TensorGraph(false);
        var initialEncoding = model.Encode(retrievalGraph, parameters, initial);
        var query = retrievalGraph.Mean(retrievalGraph.Gather(initialEncoding, initial.CurrentPositions));
        var memoryScores = TensorGraph.Probabilities(model.MemoryScores(retrievalGraph, parameters, query, candidates));
        var memory = candidates.Select((fact, i) => new MemorySelection(fact, memoryScores[i + 1]))
            .Where(x => x.Score > memoryScores[0]).OrderByDescending(x => x.Score).ThenByDescending(x => x.Fact.SourceUtterance).Take(8).ToArray();
        var packed = StructuredInput.Pack(request, _tokenizer, model.Config.ContextLength, memory.Select(x => x.Fact).ToArray(), model.Domain);
        memory = memory.Where(x => packed.Facts.Contains(x.Fact)).ToArray();
        var graph = new TensorGraph(false);
        var output = model.Understand(graph, parameters, packed, encodedInput: memory.Length == 0 ? initialEncoding : null);
        var current = DialogueText.Normalize(request.Utterances[^1].Text);
        var slots = DecodeSlots(output.Slots, packed, current);
        var factSpan = DecodeFactSpan(output.FactSpans, packed, current);
        var pointer = ContextualNetwork.ArgMax(output.Antecedents);
        long? antecedent = pointer == 0 ? null : packed.Utterances[pointer - 1].Sequence;
        var discourse = new DiscourseFrame(Exclusive<DiscourseAct>("discourseAct"), Exclusive<DialogueParticipant>("discourseSubject"),
            Exclusive<DialogueParticipant>("discourseTarget"), ContextualNetwork.ArgMax(output.Heads["factKind"]) is var kind && kind > 0 ? (DialogueFactKind?)(kind - 1) : null,
            factSpan, ExclusiveIndex("factPolarity") == 1, antecedent, Confidence("discourseAct"), "LEARNED_CONTEXTUAL");
        var frames = DecodeFrames(model, output, packed, current, slots);
        var acts = output.Plans.Select((x, i) => new PlannedResponseAct((DialogueResponseAct)ContextualNetwork.ArgMax(x),
            ContextualNetwork.ArgMax(output.PlanFrames[i]) is var framePointer && framePointer > 0 && framePointer <= frames.Length ? framePointer - 1 : null,
            (DialogueResponseAct)ContextualNetwork.ArgMax(x) == DialogueResponseAct.AskFollowUp
                ? discourse.FactKind?.ToString().ToUpperInvariant() : null)).TakeWhile(x => x.Act != DialogueResponseAct.None).ToArray();
        var planConfidence = output.Plans.Take(Math.Max(1, acts.Length)).Concat(output.PlanFrames.Take(acts.Length))
            .Select(x => (double)TensorGraph.Probabilities(x).Max()).Min();
        var confidence = output.Heads.ToDictionary(x => x.Key.ToUpperInvariant(), x => (double)TensorGraph.Probabilities(x.Value).Max());
        confidence["PLAN"] = planConfidence;
        var perception = new StructuredPerception(Multi<SpeechAct>("speechActs", 3), Multi<DialogueDomain>("domains", 3),
            Multi<DialogueGoal>("goals", 3), Exclusive<UserAffect>("affect"), Exclusive<DialogueStance>("stance"),
            Exclusive<ResponsePolicy>("policy"), slots, Multi<ContentFlag>("content", 9), null, null,
            Exclusive<KnowledgeTarget>("knowledgeTarget"), confidence, discourse);
        var firstPlannedTool = acts.Where(a => a.Act == DialogueResponseAct.ExecuteTool && a.FrameIndex is not null)
            .Select(a => frames[a.FrameIndex!.Value].ToolName).FirstOrDefault();
        var raw = perception with { ToolSchema = firstPlannedTool };
        var vetoes = new List<string>();
        var actionCandidates = new List<ValidatedActionCandidate>();
        GameToolInvocation? invocation = null;
        GameToolResult? toolResult = null;
        var pending = request.State.PendingActions.ToList();
        var text = "";
        string? fallback = null;
        var source = ResponseSource.Fallback;
        var action = DiscourseResponseAction.None;
        var unsupported = 0.0;
        var selectedTool = (string?)null;
        var clarified = false;
        var orderedFrameIndices = acts.Where(a => a.Act == DialogueResponseAct.ExecuteTool && a.FrameIndex is not null)
            .Select(a => a.FrameIndex!.Value).Concat(Enumerable.Range(0, frames.Length)).Distinct().ToArray();
        foreach (var index in orderedFrameIndices)
        {
            var frame = frames[index];
            if (frame.ToolName is null) continue;
            if (frame.Status == ActionStatus.Negated && frame.SpeechAct == SpeechAct.Refuse)
                pending.RemoveAll(a => a.ToolSchema == frame.ToolName);
            var binding = model.Domain.Tools.Single(x => x.Schema.Name == frame.ToolName);
            // Include the whole surrounding sentence so a span prediction cannot omit a preceding negation.
            var sentenceStart = frame.Start == 0 ? 0 : current.LastIndexOfAny(['.', '?', '!', ';'], frame.Start - 1) + 1;
            var frameEnd = frame.Start + frame.Length;
            var sentenceEnd = ".?!;".Contains(current[frameEnd - 1]) ? frameEnd - 1 : current.IndexOfAny(['.', '?', '!', ';'], frameEnd);
            var clause = current[sentenceStart..(sentenceEnd < 0 ? current.Length : sentenceEnd + 1)];
            var eligible = frame.Status == ActionStatus.Affirmative || !binding.Schema.MutatesWorldState && frame.Status == ActionStatus.Question;
            if (!eligible || frame.Subject != DialogueParticipant.Player || ActionLanguage.ExecutionVeto(clause) is { })
            { vetoes.Add($"FRAME_{index}_NON_EXECUTABLE"); continue; }
            if (!acts.Any(x => x.Act == DialogueResponseAct.ExecuteTool && x.FrameIndex == index))
            { vetoes.Add($"FRAME_{index}_PLAN_DID_NOT_AUTHORIZE_TOOL"); continue; }
            if (!tools.TryGet(frame.ToolName, out var tool)) { vetoes.Add("CAPABILITY_UNAVAILABLE"); continue; }
            if (!SchemaEquals(tool.Schema, binding.Schema)) { vetoes.Add("CAPABILITY_SCHEMA_MISMATCH"); continue; }
            var arguments = new Dictionary<string, string>(StringComparer.Ordinal);
            var resumable = pending.Where(a => a.ToolSchema == frame.ToolName && a.Action == "EXECUTE_TOOL").ToArray();
            PendingDialogueAction? resumed = null;
            if (frame.SpeechAct is SpeechAct.Confirm or SpeechAct.Accept && frame.Status == ActionStatus.Affirmative &&
                resumable.Length == 1 && resumable[0].SourceUtterance is { } origin && frame.Antecedent == origin)
            {
                resumed = resumable[0];
                foreach (var argument in resumed.Arguments) arguments[argument.Key] = argument.Value;
            }
            foreach (var parameter in binding.Schema.Parameters)
            {
                var values = frame.Arguments.Where(x => x.Type == binding.Parameters[parameter.Name]).Select(x => x.Value).Distinct().ToArray();
                if (values.Length == 1) arguments[parameter.Name] = binding.Parameters[parameter.Name] == SlotType.Quantity
                    ? NormalizeQuantity(values[0]) : model.Domain.CanonicalEntity(values[0]);
            }
            var missing = binding.Schema.Parameters.Where(p => p.Required && !arguments.ContainsKey(p.Name)).Select(p => p.Name).ToArray();
            if (missing.Length > 0)
            {
                vetoes.Add("MISSING_OR_AMBIGUOUS_ARGUMENTS");
                if (invocation is null) { text = $"PLEASE SPECIFY {string.Join(" AND ", missing)}."; selectedTool = frame.ToolName; clarified = true; }
                break;
            }
            try { GameToolRegistry.ValidateArguments(binding.Schema, arguments); }
            catch (ArgumentException) { vetoes.Add("INVALID_ARGUMENTS"); break; }
            var executionConfidence = Math.Min(frame.Confidence, planConfidence);
            actionCandidates.Add(new(index, frame.ToolName, new ReadOnlyDictionary<string, string>(arguments), executionConfidence));
            if (executionConfidence < _executionThresholds.GetValueOrDefault(frame.ToolName, 1.01))
            { vetoes.Add("UNCALIBRATED_OR_LOW_CONFIDENCE"); break; }
            if (invocation is not null)
            {
                if (resumed is null) pending.Add(new("EXECUTE_TOOL", frame.ToolName, new ReadOnlyDictionary<string, string>(arguments))
                { SourceUtterance = request.Utterances[^1].Sequence });
                continue;
            }
            try
            {
                if (resumed is not null) pending.Remove(resumed);
                invocation = new(frame.ToolName, new ReadOnlyDictionary<string, string>(arguments), GameToolRegistry.IdempotencyKey(request.ConversationId, request.TurnId));
                toolResult = GameToolRegistry.InvokeValidated(tool, invocation);
                text = GameToolRegistry.Render(binding.Schema, toolResult);
                source = ResponseSource.ToolTemplate;
                selectedTool = frame.ToolName;
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidDataException)
            {
                // A host tool may already have mutated state before returning malformed data.
                // Preserve the attempted invocation and never attempt a second action this turn.
                vetoes.Add("INVALID_TOOL_RESULT_AFTER_INVOCATION");
                text = "THE ACTION WAS SENT, BUT I COULD NOT VALIDATE ITS RESULT.";
                source = ResponseSource.Fallback;
                fallback = "INVALID_TOOL_RESULT_AFTER_INVOCATION";
            }
        }
        var understandingMilliseconds = timer.Elapsed.TotalMilliseconds;
        if (invocation is null && text.Length == 0)
        {
            if (vetoes.Count > 0)
            { text = "I HAVE NOT TAKEN THAT ACTION. PLEASE CLARIFY YOUR REQUEST."; source = ResponseSource.ClarificationTemplate; fallback = vetoes[0]; clarified = true; }
            else if (acts.Any(x => x.Act == DialogueResponseAct.Refuse) || perception.Policy == ResponsePolicy.Refuse)
            { text = "I WILL NOT DO THAT."; source = ResponseSource.Fallback; }
            else if (perception.KnowledgeTarget == KnowledgeTarget.Capabilities)
            {
                var available = model.Domain.Tools.Where(x => tools.TryGet(x.Schema.Name, out var registered) && SchemaEquals(x.Schema, registered.Schema))
                    .Select(x => x.Capability).ToArray();
                text = available.Length == 0 ? "I HAVE NO REGISTERED GAME CAPABILITIES." : $"I CAN {string.Join(", ", available)}.";
                source = ResponseSource.CapabilityTemplate;
            }
            else if (TryRenderPersona(perception.KnowledgeTarget, request.Persona, tools, out text, out source)) { }
            else if (acts.Any(x => x.Act == DialogueResponseAct.Clarify))
            { text = "COULD YOU EXPLAIN WHAT YOU MEAN?"; source = ResponseSource.ClarificationTemplate; clarified = true; }
            else if (request.ResponseMode == ResponseMode.Production && perception.ContentFlags.Count == 0 && _step > 0)
            {
                text = GenerateContextual(model, parameters, output.PlanMemory, request.Seed);
                unsupported = TensorGraph.Probabilities(model.ClaimScores(new TensorGraph(false), parameters, output.PlanMemory, text))[1];
                if (unsupported < .5 && (!text.Contains('?') || HasGroundedQuestion(request, acts, discourse)) &&
                    ConversationalOutputValidator.IsSafe(text, request.Persona, memory.Select(m => m.Fact).ToArray(), out fallback))
                    source = ResponseSource.ConversationalGenerated;
                else { text = ContextualFallback(request, acts, discourse, memory); fallback ??= "UNSUPPORTED_OR_UNPLANNED_GENERATION"; source = ResponseSource.Fallback; }
            }
            else { text = ContextualFallback(request, acts, discourse, memory); fallback = "NO_VALIDATED_REALIZATION"; source = ResponseSource.Fallback; }
        }
        if (invocation is not null && source == ResponseSource.ToolTemplate && acts.Any(a => a.Act == DialogueResponseAct.Correct))
        {
            var combined = "THANK YOU FOR THE CORRECTION. " + text;
            if (combined.Length <= 256 && _tokenizer.Encode(combined).Length <= 64) text = combined;
        }
        if (text.Length > 256 || text.Length == 0 || !DialogueText.IsCanonical(text) || _tokenizer.Encode(text).Length > 64)
        { text = "COULD YOU REPHRASE THAT?"; fallback = "INVALID_REALIZATION"; source = ResponseSource.Fallback; }
        var policy = invocation is not null ? ResponsePolicy.ExecuteTool : clarified ? ResponsePolicy.Clarify : perception.Policy;
        perception = perception with { Policy = policy, ToolSchema = selectedTool };
        var plan = new TurnPlan(policy, selectedTool, null, perception.KnowledgeTarget, pending.Take(3).ToArray(),
            clarified ? text : null, clarified && selectedTool is not null ? model.Domain.Tools.Single(x => x.Schema.Name == selectedTool).Schema.Parameters.Select(x => x.Name).ToArray() : [], action, antecedent);
        var reduced = DialogueStateReducer.Apply(request.State, request.PlayerProfile, request.Utterances[^1], request.ResponseSequence, perception, plan, toolResult, text, fallback);
        // Generated prose is not a memory source. Only interpreted input frames update session facts.
        var facts = DialogueStateReducer.ReduceFacts(request.State.SessionFacts, discourse, request.Utterances[^1].Sequence);
        var agenda = DialogueStateReducer.ReduceAgenda(request.State.Agenda, request.Utterances[^1], acts,
            (AgendaKind)Math.Max(0, ExclusiveIndex("agenda") - 1), ExclusiveIndex("agenda") > 0,
            Exclusive<AgendaStatus>("agendaStatus"), discourse, selectedTool, toolResult);
        reduced = reduced with { SessionFacts = facts, Agenda = agenda };
        reduced.Validate();
        return new ReplyResult(text, reduced, raw, perception, plan, Cognition.ToneFor(reduced.Mood),
            new ReplyDiagnostics(confidence, [], source, null, invocation, slots, _tokenizer.UnknownWords(current), fallback, packed.Utterances.Length, packed.Tokens.Length))
        {
            Contextual = new(memory, frames, acts,
                vetoes, agenda, planConfidence, unsupported, understandingMilliseconds)
            { ActionCandidates = actionCandidates.AsReadOnly() }
        };

        int ExclusiveIndex(string name) => ContextualNetwork.ArgMax(output.Heads[name]);
        T Exclusive<T>(string name) where T : struct, Enum => (T)Enum.ToObject(typeof(T), ExclusiveIndex(name));
        double Confidence(string name) => TensorGraph.Probabilities(output.Heads[name]).Max();
        T[] Multi<T>(string name, int maximum) where T : struct, Enum => output.Heads[name].Data.Select((x, i) => (x, i))
            .Where(x => x.x >= 0).OrderByDescending(x => x.x).Take(maximum).Select(x => (T)Enum.ToObject(typeof(T), x.i)).ToArray();
    }

    internal static SemanticFrame[] DecodeFrames(ContextualNetwork model, NetworkOutput output, PackedInput packed, string current,
        IReadOnlyList<DialogueSlot> slots)
    {
        var frames = new List<SemanticFrame>();
        foreach (var logits in output.Frames)
        {
            if (ContextualNetwork.ArgMax(logits.Fields["active"]) == 0) break;
            var startSource = packed.Sources[packed.CurrentPositions[ContextualNetwork.ArgMax(logits.Start)]];
            var endSource = packed.Sources[packed.CurrentPositions[ContextualNetwork.ArgMax(logits.End)]];
            var end = endSource.Start + endSource.Length;
            if (end <= startSource.Start || end > current.Length) break;
            var tool = ContextualNetwork.ArgMax(logits.Fields["tool"]);
            var reference = ContextualNetwork.ArgMax(logits.Antecedent);
            long? antecedent = reference == 0 ? null : packed.Utterances[reference - 1].Sequence;
            var probability = logits.Fields.Values.Select(x => TensorGraph.Probabilities(x).Max()).Append(TensorGraph.Probabilities(logits.Start).Max())
                .Append(TensorGraph.Probabilities(logits.End).Max()).Append(TensorGraph.Probabilities(logits.Antecedent).Max()).Min();
            frames.Add(new(startSource.Start, end - startSource.Start, (SpeechAct)ContextualNetwork.ArgMax(logits.Fields["act"]),
                (DialogueParticipant)ContextualNetwork.ArgMax(logits.Fields["subject"]), (DialogueParticipant)ContextualNetwork.ArgMax(logits.Fields["target"]),
                tool == 0 ? null : model.Domain.Tools[tool - 1].Schema.Name,
                slots.Where(s => s.Start >= startSource.Start && s.Start + s.Length <= end).ToArray(), antecedent,
                (ActionStatus)ContextualNetwork.ArgMax(logits.Fields["status"]), probability));
        }
        return frames.ToArray();
    }

    private static bool HasGroundedQuestion(ReplyRequest request, IReadOnlyList<PlannedResponseAct> acts, DiscourseFrame discourse) =>
        acts.Any(a => a.Act == DialogueResponseAct.Clarify || a.Act == DialogueResponseAct.AskFollowUp && a.Subject is { } subject &&
            (request.State.Agenda.Any(goal => goal.Status == AgendaStatus.Active && goal.Subject == subject) ||
                discourse.FactKind?.ToString().ToUpperInvariant() == subject && discourse.FactValueSpan is not null));

    private static string ContextualFallback(ReplyRequest request, IReadOnlyList<PlannedResponseAct> acts,
        DiscourseFrame discourse, IReadOnlyList<MemorySelection> memory)
    {
        var clauses = new List<string>();
        foreach (var act in acts)
        {
            var clause = act.Act switch
            {
                DialogueResponseAct.Correct => "THANK YOU FOR THE CORRECTION.",
                DialogueResponseAct.Acknowledge => discourse.FactValueSpan is { } value ? $"YOU MENTIONED {value.NormalizedValue}." : "I UNDERSTAND.",
                DialogueResponseAct.Refuse => "I WILL NOT DO THAT.",
                DialogueResponseAct.Farewell => "SAFE TRAVELS.",
                DialogueResponseAct.Clarify => "COULD YOU CLARIFY WHAT YOU MEAN?",
                DialogueResponseAct.AskFollowUp when HasGroundedQuestion(request, [act], discourse) =>
                    $"WHAT ELSE WOULD YOU LIKE TO DISCUSS ABOUT {discourse.FactValueSpan?.NormalizedValue ?? act.Subject}?",
                DialogueResponseAct.Answer when discourse.Act == DiscourseAct.ReferBack => Recall(),
                DialogueResponseAct.Answer => "I AM NOT CERTAIN ABOUT THAT.",
                _ => null
            };
            if (clause is not null && !clauses.Contains(clause) && string.Join(' ', clauses.Append(clause)).Length <= 256) clauses.Add(clause);
        }
        return clauses.Count == 0 ? "I AM NOT SURE HOW TO RESPOND TO THAT." : string.Join(' ', clauses);

        string Recall()
        {
            var owner = discourse.Target == DialogueParticipant.None ? discourse.Subject : discourse.Target;
            var selected = memory.Where(m => m.Fact.Subject == owner && m.Fact.Kind == discourse.FactKind).Select(m => m.Fact).Distinct().ToArray();
            if (selected.Length != 1) return "I CANNOT IDENTIFY ONE CLEAR MEMORY ABOUT THAT.";
            var fact = selected[0];
            var subject = fact.Subject == DialogueParticipant.Player ? "YOU" : "ME";
            return $"{(fact.Provenance == DialogueFactProvenance.CallerApproved ? "YOUR PROFILE" : "OUR CONVERSATION")} RECORDS THIS ABOUT {subject}: {fact.Kind.ToString().ToUpperInvariant()} {(fact.Negated ? "NOT " : "")}{fact.Value}.";
        }
    }

    internal static DialogueSlot[] DecodeSlots(Tensor logits, PackedInput input, string text)
    {
        var result = new List<DialogueSlot>();
        foreach (var (source, row) in input.CurrentPositions.Select((p, i) => (input.Sources[p], i)).DistinctBy(x => (x.Item1.Start, x.Item1.Length)))
        {
            var label = ContextualNetwork.ArgMax(logits, row);
            if (label == 0) continue;
            var type = (SlotType)((label - 1) / 2);
            if ((label - 1) % 2 == 1 && result.LastOrDefault() is { } prior && prior.Type == type &&
                text.AsSpan(prior.Start + prior.Length, source.Start - (prior.Start + prior.Length)).Trim().Length == 0)
            {
                var length = source.Start + source.Length - prior.Start;
                result[^1] = prior with { Length = length, Value = text.Substring(prior.Start, length) };
            }
            else result.Add(new(type, BioTag.B, text.Substring(source.Start, source.Length), source.Start, source.Length, TensorGraph.Probabilities(logits, row)[label]));
        }
        return result.ToArray();
    }

    internal static DialogueTextSpan? DecodeFactSpan(Tensor logits, PackedInput input, string text)
    {
        int? start = null;
        var end = 0;
        foreach (var (source, row) in input.CurrentPositions.Select((p, i) => (input.Sources[p], i)).DistinctBy(x => (x.Item1.Start, x.Item1.Length)))
        {
            var label = ContextualNetwork.ArgMax(logits, row);
            if (start is null && label == 1) start = source.Start;
            else if (start is not null && label != 2) break;
            if (start is not null) end = source.Start + source.Length;
        }
        return start is { } first && end - first <= 128 ? new(text.Substring(first, end - first), first, end - first) : null;
    }

    internal static string GenerateContextual(ContextualNetwork model, IReadOnlyDictionary<string, Tensor> p, Tensor memory, int seed)
    {
        var tokens = new List<int> { Tokenizer.Bos };
        var outputs = new List<int>();
        var random = new Random(seed);
        var session = model.CreateDecoderSession(p, memory);
        for (var step = 0; step < model.Config.MaximumOutputTokens; step++)
        {
            var logits = session.Next(tokens[^1]);
            var distribution = TensorGraph.Probabilities(logits);
            var eligible = model.Tokenizer.GeneratedTextOutputs.OrderByDescending(i => distribution[i]).Take(8).ToArray();
            var sum = eligible.Sum(i => distribution[i]);
            var sample = random.NextDouble() * sum;
            var selected = eligible[^1];
            foreach (var candidate in eligible) { sample -= distribution[candidate]; if (sample <= 0) { selected = candidate; break; } }
            var token = model.Vocabulary.InputIdFromOutput(selected);
            if (token == Tokenizer.Eos) break;
            outputs.Add(selected);
            tokens.Add(token);
        }
        return model.Tokenizer.DetokenizeOutput(outputs);
    }

    internal static string NormalizeQuantity(string value) => value switch
    {
        "A" or "AN" or "ONE" => "1",
        "TWO" => "2",
        "THREE" => "3",
        "FOUR" => "4",
        "FIVE" => "5",
        _ => value
    };
}
