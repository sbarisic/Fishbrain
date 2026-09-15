using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fishbrain;

internal sealed class TrainingData
{
    internal const int ConditioningLength = 96;
    internal const int TargetChunkLength = 32;
    internal const int MaximumSampleLength = ConditioningLength + TargetChunkLength;
    private static readonly HashSet<string> KnownStructuredHeads =
        ModelSchemas.Labels.Keys.Concat(["tool", "responseCandidate"]).ToHashSet(StringComparer.Ordinal);
    private static readonly HashSet<string> KnownStructuredTools =
        DemoGameTools.CreateMerchant().Schemas.Select(schema => schema.Name).Append("NONE").ToHashSet(StringComparer.Ordinal);
    private static readonly HashSet<string> KnownResponseCandidates =
        global::Fishbrain.ResponseCatalog.Plans.Select(plan => plan.Id).ToHashSet(StringComparer.Ordinal);

    private TrainingData(
        List<TrainingSample> samples,
        List<TrainingExample> structuredSamples,
        HashSet<string> toolNames,
        Dictionary<string, string> examples,
        Dictionary<string, string[]> responseCatalog)
    {
        Samples = samples;
        StructuredSamples = structuredSamples;
        ToolNames = toolNames;
        Examples = examples;
        ResponseCatalog = responseCatalog;
    }

    public IReadOnlyList<TrainingSample> Samples { get; }
    public IReadOnlyList<TrainingExample> StructuredSamples { get; }
    public IReadOnlySet<string> ToolNames { get; }
    public IReadOnlyDictionary<string, string> Examples { get; }
    public IReadOnlyDictionary<string, string[]> ResponseCatalog { get; }
    public IReadOnlyList<TrainingSample> LanguageSamples => Samples.Where(x => x.Task == TrainingTask.Language).ToArray();
    public IReadOnlyList<TrainingSample> PerceptionSamples => Samples.Where(x => x.Task == TrainingTask.Perception).ToArray();
    public IReadOnlyList<TrainingSample> ToolSamples => Samples.Where(x => x.Task == TrainingTask.Tool).ToArray();

    public static TrainingData Load(string path, DialogueTokenizer tokenizer, DialogueDomainDefinition? domain = null)
    {
        ArgumentNullException.ThrowIfNull(tokenizer);
        var samples = new List<TrainingSample>();
        var structuredSamples = new List<TrainingExample>();
        var tools = new HashSet<string>(StringComparer.Ordinal);
        var examples = new Dictionary<string, string>(StringComparer.Ordinal);
        var responseCatalog = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var rows = new Dictionary<string, string>(StringComparer.Ordinal);
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseUpper));
        var lineNumber = 0;
        foreach (var line in File.ReadLines(path))
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var row = JsonSerializer.Deserialize<TrainingRow>(line, options)
                    ?? throw new InvalidDataException("Empty object.");
                AddRow(row, samples, structuredSamples, tools, examples, responseCatalog, rows, tokenizer,
                    domain?.Tools.Select(t => t.Schema.Name).Append("NONE").ToHashSet(StringComparer.Ordinal) ?? KnownStructuredTools);
            }
            catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidDataException)
            {
                throw new InvalidDataException($"Invalid training data on line {lineNumber}: {exception.Message}", exception);
            }
        }
        if (samples.Count == 0) throw new InvalidDataException("Training data contains no examples.");
        return new TrainingData(samples, structuredSamples, tools, examples, responseCatalog
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .ToDictionary(
                item => item.Key,
                item => item.Value.Order(StringComparer.Ordinal).ToArray(),
                StringComparer.Ordinal));
    }

    private static void AddRow(
        TrainingRow row,
        List<TrainingSample> samples,
        List<TrainingExample> structuredSamples,
        HashSet<string> tools,
        Dictionary<string, string> examples,
        Dictionary<string, HashSet<string>> responseCatalog,
        Dictionary<string, string> rows,
        DialogueTokenizer tokenizer, IReadOnlySet<string> knownTools)
    {
        if (row.Input is null || row.State is null || row.Perception is null || row.Action is null)
            throw new InvalidDataException("Input, state, perception, and action are required.");
        var input = Tokenizer.Normalize(row.Input);
        if (input != row.Input || input.Length is < 1 or > 1024)
            throw new InvalidDataException("Input must be canonical and contain 1-1024 characters.");
        row.State.Validate();
        var perception = row.Perception;
        var decision = new TurnDecision(row.Action.Value);
        if (decision.Action != Cognition.ActionFor(perception))
            throw new InvalidDataException("Action is invalid for the supplied perception.");
        var source = string.IsNullOrWhiteSpace(row.Source) ? "manual" : row.Source;
        var bucket = $"{perception.Intent}|{perception.Affect}|{perception.ResponseExpected}";

        var hasAnyTool = row.Tool is not null || row.Arguments is not null || row.Result is not null;
        var hasAllTool = row.Tool is not null && row.Arguments is not null && row.Result is not null;
        if (hasAnyTool != hasAllTool) throw new InvalidDataException("Tool, arguments, and result must be supplied together.");
        if (hasAllTool != (decision.Action == ResponseAction.CallTool))
            throw new InvalidDataException("CALL_TOOL rows require all tool fields and other rows must omit them.");
        if (decision.Action == ResponseAction.NoResponse && row.Response is not null && row.Response != string.Empty)
            throw new InvalidDataException("NO_RESPONSE rows require an explicitly empty response.");

        string? response = null;
        if (row.Response is not null)
        {
            response = Tokenizer.Normalize(row.Response);
            if (response != row.Response || response.Length > 256)
                throw new InvalidDataException("Response must be canonical and no longer than 256 characters.");
            if (decision.Action != ResponseAction.NoResponse && response.Length == 0)
                throw new InvalidDataException("Response-producing rows cannot contain an empty response.");
        }

        var rowKey = DialogueKeys.StateInput(input, row.State);
        var rowValue = $"{perception}|{decision.Action}|{response ?? "<NULL>"}";
        if (rows.TryGetValue(rowKey, out var existing) && existing != rowValue)
            throw new InvalidDataException("The same state and input cannot have competing supervision.");
        rows[rowKey] = rowValue;

        samples.Add(CreatePerceptionSample(input, perception, bucket, source, row.Family, tokenizer));
        if (row.StructuredPerception is not null)
        {
            var structured = row.StructuredPerception;
            if (structured.SpeechActs is null || structured.Domains is null || structured.Goals is null ||
                structured.Slots is null || structured.ContentFlags is null || structured.Confidence is null)
                throw new InvalidDataException("Structured perception collections cannot be null.");
            if (structured.SpeechActs.Count > 3 || structured.Domains.Count > 3 || structured.Goals.Count > 3 ||
                structured.SpeechActs.Any(value => !Enum.IsDefined(value)) ||
                structured.Domains.Any(value => !Enum.IsDefined(value)) ||
                structured.Goals.Any(value => !Enum.IsDefined(value)) ||
                structured.ContentFlags.Any(value => !Enum.IsDefined(value)) ||
                !Enum.IsDefined(structured.Affect) || !Enum.IsDefined(structured.Stance) ||
                !Enum.IsDefined(structured.Policy) || !Enum.IsDefined(structured.KnowledgeTarget))
                throw new InvalidDataException("Structured perception contains an unknown label.");
            if (string.IsNullOrWhiteSpace(row.SemanticFamilyId))
                throw new InvalidDataException("Structured rows require semanticFamilyId.");
            var supervisedNames = row.SupervisedHeads ?? ModelSchemas.Labels.Keys.Concat(["tool", "responseCandidate"]).ToArray();
            if (supervisedNames.Distinct(StringComparer.Ordinal).Count() != supervisedNames.Length ||
                supervisedNames.Any(head => !KnownStructuredHeads.Contains(head)))
                throw new InvalidDataException("Structured supervision contains an unknown or duplicate head.");
            var supervised = supervisedNames.ToHashSet(StringComparer.Ordinal);
            var toolName = structured.ToolSchema ?? "NONE";
            if (supervised.Contains("tool") && !knownTools.Contains(toolName))
                throw new InvalidDataException($"Unknown structured tool target '{toolName}'.");
            var candidateName = structured.ResponseCandidateId ?? "ACKNOWLEDGE";
            if (supervised.Contains("responseCandidate") && !KnownResponseCandidates.Contains(candidateName))
                throw new InvalidDataException($"Unknown response candidate target '{candidateName}'.");
            var currentTurn = LegacyBrain.ExtractCurrentPlayerTurn(input);
            var currentOffset = input.LastIndexOf(currentTurn, StringComparison.Ordinal);
            var normalizedSlots = structured.Slots.Select(slot => NormalizeSlot(slot, currentTurn, currentOffset)).ToArray();
            structured.Discourse?.FactValueSpan?.Validate(currentTurn);
            var turns = row.Turns is { Length: > 0 }
                ? row.Turns.Select(turn => turn is null
                    ? throw new InvalidDataException("Structured turns cannot contain null entries.")
                    : new DialogueUtterance(turn.Sequence, turn.Speaker, DialogueText.Normalize(turn.Text))).ToArray()
                : new[] { new DialogueUtterance(0, DialogueRole.Player, currentTurn) };
            if (turns.Any(turn => !Enum.IsDefined(turn.Speaker) || string.IsNullOrWhiteSpace(turn.Text)) ||
                turns.Zip(turns.Skip(1)).Any(pair => pair.First.Sequence >= pair.Second.Sequence) ||
                turns[^1].Speaker != DialogueRole.Player)
                throw new InvalidDataException(
                    "Structured turns must have increasing sequences and end with a player turn.");
            var initialState = row.InitialDialogueState ?? NpcDialogueState.Initial;
            initialState.Validate();
            var initialProfile = row.InitialPlayerProfile ?? PlayerConversationProfile.Empty;
            initialProfile.Validate();
            var responseAction = row.DiscourseResponseAction ?? DiscourseResponseAction.None;
            if (!Enum.IsDefined(responseAction) || row.AcceptableResponseConstraints is { } constraints &&
                (constraints.Length > 8 || constraints.Any(constraint =>
                    string.IsNullOrWhiteSpace(constraint) || constraint.Length > 128 ||
                    constraint != DialogueText.Normalize(constraint))))
            {
                throw new InvalidDataException("Conversational response supervision is invalid.");
            }
            if (row.RejectedResponse is { } rejected &&
                (rejected.Length > 256 || rejected != DialogueText.Normalize(rejected) || rejected == response))
            {
                throw new InvalidDataException("Rejected conversational response is invalid.");
            }
            var expectedFactState = row.FactDelta ?? initialState.SessionFacts.ToArray();
            foreach (var fact in expectedFactState)
            {
                PlayerConversationProfile.ValidateFact(fact, DialogueFactProvenance.SessionReported);
            }

            structuredSamples.Add(new TrainingExample(
                input, currentTurn, turns, structured.SpeechActs.ToArray(), structured.Domains.ToArray(),
                structured.Goals.ToArray(), structured.Affect, structured.Stance, structured.Policy,
                normalizedSlots, structured.ContentFlags.ToArray(), toolName,
                candidateName, structured.KnowledgeTarget,
                source, row.SemanticFamilyId, supervised, structured.Discourse ?? DiscourseFrame.Empty,
                initialState.SessionFacts.ToArray(), expectedFactState, responseAction,
                row.AcceptableResponseConstraints ?? [], row.RejectedResponse)
            {
                Request = new ReplyRequest(row.GroupId ?? source, row.SemanticFamilyId ?? source, turns, initialState,
                    row.Persona ?? NpcPersona.Default, initialProfile, turns[^1].Sequence + 1, 42),
                Contextual = row.Contextual,
                Response = response
            });
            row.Contextual?.Validate(currentTurn);
        }

        var transition = Cognition.Apply(row.State, perception, decision, hasAllTool);
        if (!hasAllTool)
        {
            if (!string.IsNullOrEmpty(response))
            {
                examples[DialogueKeys.Example(input, row.State, perception, decision, transition.Tone)] = response;
                if (source.Equals("SYNTHETIC", StringComparison.OrdinalIgnoreCase))
                {
                    var catalogKey = DialogueKeys.Catalog(perception.Intent, transition.Tone);
                    if (!responseCatalog.TryGetValue(catalogKey, out var responses))
                        responseCatalog.Add(catalogKey, responses = new HashSet<string>(StringComparer.Ordinal));
                    responses.Add(response);
                }
                var conditioned = row.Turns is { Length: > 0 } && row.Persona is not null
                    ? ConversationConditioning.Build(
                        row.Persona,
                        row.InitialPlayerProfile ?? PlayerConversationProfile.Empty,
                        row.InitialDialogueState ?? NpcDialogueState.Initial,
                        row.StructuredPerception?.Discourse,
                        row.Turns,
                        row.DiscourseResponseAction ?? DiscourseResponseAction.None,
                        row.AcceptableResponseConstraints ?? [])
                    : input;
                AddSamples(SerializeResponse(conditioned, transition.State, perception, decision, transition.Tone,
                        response, row.RejectedResponse, tokenizer),
                    samples, TrainingTask.Language, bucket, source);
            }
            return;
        }

        var tool = row.Tool!;
        if (tool.Length is < 1 or > 32 || tool.Any(c => !Tokenizer.IsIdentifierCharacter(c)))
            throw new InvalidDataException("Tool names must be uppercase alphanumeric identifiers.");
        var arguments = row.Arguments!;
        if (arguments.Any(x => x.Length is < 1 or > 32 || x.Any(c => !Tokenizer.IsIdentifierCharacter(c))))
            throw new InvalidDataException("Tool arguments must be uppercase alphanumeric identifiers.");
        var result = Tokenizer.Normalize(row.Result!);
        if (result != row.Result || result.Length is < 1 or > 64)
            throw new InvalidDataException("Tool results must be canonical and contain 1-64 characters.");
        if (string.IsNullOrEmpty(response)) throw new InvalidDataException("Tool rows require a response.");
        tools.Add(tool);
        AddSamples(SerializeToolCall(input, row.State, perception, decision, tool, arguments, tokenizer),
            samples, TrainingTask.Tool, bucket, source);
        AddSamples(SerializeToolResult(input, transition.State, perception, decision, transition.Tone,
            tool, arguments, result, response, tokenizer), samples, TrainingTask.Language, bucket, source);
    }

    private static DialogueSlot NormalizeSlot(DialogueSlot slot, string currentTurn, int currentOffset)
    {
        ArgumentNullException.ThrowIfNull(slot);
        if (!Enum.IsDefined(slot.Type) || !Enum.IsDefined(slot.Tag) || !double.IsFinite(slot.Confidence) ||
            slot.Confidence is < 0 or > 1 || slot.Value.Length == 0 || slot.Length != slot.Value.Length)
            throw new InvalidDataException("Structured slot metadata is invalid.");
        var normalizedValue = DialogueText.Normalize(slot.Value);
        if (normalizedValue != slot.Value)
            throw new InvalidDataException("Structured slot values must already be normalized.");
        if (Matches(slot.Start)) return slot;
        var relative = slot.Start - currentOffset;
        if (Matches(relative)) return slot with { Start = relative };
        throw new InvalidDataException($"Structured slot '{slot.Value}' does not match its source span.");

        bool Matches(int start) => start >= 0 && start + slot.Length <= currentTurn.Length &&
            currentTurn.AsSpan(start, slot.Length).SequenceEqual(slot.Value.AsSpan());
    }

    private static TrainingSample CreatePerceptionSample(
        string input, TurnPerception perception, string bucket, string source, string? family,
        DialogueTokenizer tokenizer)
    {
        var currentTurn = LegacyBrain.ExtractCurrentPlayerTurn(input);
        var encoded = tokenizer.Encode(currentTurn);
        var maximumText = 254;
        if (encoded.Length > maximumText) encoded = encoded[^maximumText..];
        var tokens = new List<int>(encoded.Length + 2) { Tokenizer.Bos };
        tokens.AddRange(encoded);
        tokens.Add(Tokenizer.Sep);
        var fields = source switch
        {
            "CLINC150" => PerceptionFields.Intent,
            "GOEMOTIONS" => PerceptionFields.Affect,
            _ => PerceptionFields.All
        };
        return new TrainingSample(
            tokens.ToArray(), 0, 1, TrainingTask.Perception, bucket, source, perception, family ?? "", fields);
    }

    private static SerializedStream SerializeResponse(
        string input, NpcState state, TurnPerception perception, TurnDecision decision,
        ResponseTone tone, string response, string? rejectedResponse, DialogueTokenizer tokenizer)
    {
        var tokens = Start(input, tokenizer);
        LegacyBrain.AppendState(tokens, state);
        tokens.Add(Tokenizer.Decide);
        AddPerception(tokens, perception, decision);
        tokens.Add(Tokenizer.Tone(tone));
        var target = tokens.Count;
        tokens.Add(Tokenizer.Text);
        var accepted = tokenizer.Encode(response);
        tokens.AddRange(accepted);
        tokens.Add(Tokenizer.Eos);
        if (string.IsNullOrWhiteSpace(rejectedResponse))
        {
            return new(tokens.ToArray(), target);
        }

        var rejected = tokenizer.Encode(rejectedResponse);
        var divergence = 0;
        while (divergence < accepted.Length && divergence < rejected.Length &&
               accepted[divergence] == rejected[divergence])
        {
            divergence++;
        }

        var rejectedToken = divergence < rejected.Length ? rejected[divergence] : Tokenizer.Eos;
        return new(tokens.ToArray(), target, target + 1 + divergence, rejectedToken);
    }

    private static SerializedStream SerializeToolCall(
        string input, NpcState state, TurnPerception perception, TurnDecision decision,
        string tool, IReadOnlyList<string> arguments, DialogueTokenizer tokenizer)
    {
        var tokens = Start(input, tokenizer);
        LegacyBrain.AppendState(tokens, state);
        tokens.Add(Tokenizer.Decide);
        AddPerception(tokens, perception, decision);
        tokens.Add(Tokenizer.Call);
        var target = tokens.Count;
        AddCallBody(tokens, tool, arguments, tokenizer);
        tokens.Add(Tokenizer.Eos);
        return new(tokens.ToArray(), target);
    }

    private static SerializedStream SerializeToolResult(
        string input, NpcState state, TurnPerception perception, TurnDecision decision,
        ResponseTone tone, string tool, IReadOnlyList<string> arguments, string result, string response,
        DialogueTokenizer tokenizer)
    {
        var tokens = Start(input, tokenizer);
        LegacyBrain.AppendState(tokens, state);
        tokens.Add(Tokenizer.Decide);
        AddPerception(tokens, perception, decision);
        tokens.Add(Tokenizer.Tone(tone));
        tokens.Add(Tokenizer.Call);
        AddCallBody(tokens, tool, arguments, tokenizer);
        tokens.Add(Tokenizer.Result);
        tokens.AddRange(tokenizer.Encode(result));
        var target = tokens.Count;
        tokens.Add(Tokenizer.Text);
        tokens.AddRange(tokenizer.Encode(response));
        tokens.Add(Tokenizer.Eos);
        return new(tokens.ToArray(), target);
    }

    private static List<int> Start(string input, DialogueTokenizer tokenizer)
    {
        var tokens = new List<int> { Tokenizer.Bos };
        tokens.AddRange(tokenizer.Encode(input));
        tokens.Add(Tokenizer.Sep);
        return tokens;
    }

    private static void AddPerception(List<int> tokens, TurnPerception perception, TurnDecision decision)
    {
        tokens.Add(Tokenizer.Intent(perception.Intent));
        tokens.Add(Tokenizer.Affect(perception.Affect));
        tokens.Add(perception.ResponseExpected ? Tokenizer.ExpectedTrue : Tokenizer.ExpectedFalse);
        tokens.Add(Tokenizer.Action(decision.Action));
    }

    private static void AddCallBody(
        List<int> tokens, string tool, IReadOnlyList<string> arguments, DialogueTokenizer tokenizer)
    {
        tokens.AddRange(tokenizer.Encode(tool));
        foreach (var argument in arguments)
        {
            tokens.Add(Tokenizer.ArgumentSeparator);
            tokens.AddRange(tokenizer.Encode(argument));
        }
    }

    private static void AddSamples(
        SerializedStream stream, List<TrainingSample> samples, TrainingTask task, string bucket, string source)
    {
        for (var targetStart = stream.FirstTargetIndex;
             targetStart < stream.Tokens.Length;
             targetStart += TargetChunkLength)
        {
            var start = Math.Max(0, targetStart - ConditioningLength);
            var end = Math.Min(stream.Tokens.Length, targetStart + TargetChunkLength);
            int? unlikelihoodIndex = stream.UnlikelihoodIndex is { } absolute &&
                                    absolute >= targetStart && absolute < end
                ? absolute - start
                : null;
            samples.Add(new TrainingSample(
                stream.Tokens[start..end], start, targetStart - start, task, bucket, source,
                UnlikelihoodTargetIndex: unlikelihoodIndex,
                UnlikelihoodToken: unlikelihoodIndex is null ? null : stream.UnlikelihoodToken,
                UnlikelihoodWeight: unlikelihoodIndex is null ? 0.0 : 0.2));
        }
    }

    private sealed record SerializedStream(
        int[] Tokens,
        int FirstTargetIndex,
        int? UnlikelihoodIndex = null,
        int? UnlikelihoodToken = null);
    private sealed class TrainingRow
    {
        public ContextualSupervision? Contextual { get; set; }
        public string? Input { get; set; }
        public NpcState? State { get; set; }
        public TurnPerception? Perception { get; set; }
        public ResponseAction? Action { get; set; }
        public string? Response { get; set; }
        public string? Source { get; set; }
        public string? Split { get; set; }
        public string? GroupId { get; set; }
        public string? Family { get; set; }
        public string? SemanticFamilyId { get; set; }
        public DialogueUtterance[]? Turns { get; set; }
        public StructuredPerception? StructuredPerception { get; set; }
        public NpcDialogueState? InitialDialogueState { get; set; }
        public NpcPersona? Persona { get; set; }
        public PlayerConversationProfile? InitialPlayerProfile { get; set; }
        public DialogueFact[]? FactDelta { get; set; }
        public DiscourseResponseAction? DiscourseResponseAction { get; set; }
        public string[]? AcceptableResponseConstraints { get; set; }
        public string? RejectedResponse { get; set; }
        public string[]? SupervisedHeads { get; set; }
        public string? Tool { get; set; }
        public string[]? Arguments { get; set; }
        public string? Result { get; set; }
    }
}
