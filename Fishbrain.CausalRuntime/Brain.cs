using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Fishbrain;

/// <summary>Host-owned execution journal. Share it with all runtimes that serve the same world.</summary>
public sealed class ExecutionJournal
{
    private readonly ConcurrentDictionary<string, (string Fingerprint, Lazy<GameToolResult> Result)> _calls = new();
    private readonly ConcurrentDictionary<string, string> _worldTurns = new();
    internal GameToolResult Execute(GameToolInvocation invocation, Func<GameToolResult> action, string? worldTurnKey = null)
    {
        if (worldTurnKey is not null && _worldTurns.GetOrAdd(worldTurnKey, invocation.IdempotencyKey) != invocation.IdempotencyKey)
            return new(false, new Dictionary<string, string>(), "WORLD_ACTION_LIMIT");
        var fingerprint = invocation.ToolName + JsonDefaults.Write(invocation.Arguments.OrderBy(a => a.Key).ToArray());
        var entry = _calls.GetOrAdd(invocation.IdempotencyKey, _ => (fingerprint, new(action, LazyThreadSafetyMode.ExecutionAndPublication)));
        return entry.Fingerprint == fingerprint ? entry.Result.Value : new(false, new Dictionary<string, string>(), "IDEMPOTENCY_CONFLICT");
    }
}

public sealed class Brain
{
    private readonly CausalNetwork _model;
    private readonly ByteBpe _tokenizer;
    private readonly CausalHeader _header;
    private readonly GameToolRegistry _tools;
    private readonly ExecutionJournal _journal;
    private readonly Func<ReplyRequest, GameToolInvocation, bool> _authorize;
    private readonly Func<PackedPrompt, GameToolRegistry, GenerationSettings, (string Text, int Count)>? _testGenerator;
    private Brain(CausalHeader header, CausalNetwork model, ByteBpe tokenizer, GameToolRegistry tools, ExecutionJournal journal,
        Func<ReplyRequest, GameToolInvocation, bool>? authorize,
        Func<PackedPrompt, GameToolRegistry, GenerationSettings, (string, int)>? testGenerator = null)
    {
        _header = header; _model = model; _tokenizer = tokenizer; _tools = tools; _journal = journal;
        _authorize = authorize ?? ((request, call) => request.Tools!.TryGet(call.ToolName, out var tool) && !tool.Schema.MutatesWorldState);
        _testGenerator = testGenerator;
    }
    public static Brain Load(string path, GameToolRegistry? tools = null, ExecutionJournal? journal = null,
        Func<ReplyRequest, GameToolInvocation, bool>? authorize = null)
    {
        var artifact = CausalArtifact.Load(path);
        return new(artifact.Header, artifact.Model, artifact.Tokenizer, tools ?? GameToolRegistry.Empty, journal ?? new(), authorize);
    }
    internal static Brain Fixture(CausalHeader h, CausalNetwork n, ByteBpe t, GameToolRegistry tools, ExecutionJournal journal,
        Func<PackedPrompt, GameToolRegistry, GenerationSettings, (string, int)> generator,
        Func<ReplyRequest, GameToolInvocation, bool>? authorize = null) => new(h, n, t, tools, journal, authorize, generator);

    public ReplyResult Reply(ReplyRequest request)
    {
        ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(request.Messages);
        var watch = Stopwatch.StartNew(); var settings = request.Generation ?? new();
        if (settings.MaximumToolCalls is < 0 or > 4 || !float.IsFinite(settings.Temperature) || settings.Temperature is < 0 or > 2 || !Enum.IsDefined(settings.Mode))
            throw new ArgumentException("Invalid generation settings.");
        request = request with { Messages = request.Messages.ToArray(), Tools = request.Tools ?? _tools };
        if (request.Messages.Any(m => m is null || m.Sequence < 0 || m.Sequence > int.MaxValue - 9L))
            throw new ArgumentException("Messages require non-negative source sequences with room for a complete tool exchange.");
        foreach (var schema in request.Tools.Schemas)
        {
            var trained = _header.Tools.SingleOrDefault(s => s.Name == schema.Name);
            if (trained is null || JsonDefaults.Write(trained) != JsonDefaults.Write(schema))
                throw new ArgumentException($"Tool '{schema.Name}' does not match this artifact's bound definition.");
        }
        var appended = new List<ChatMessage>(); var outcomes = new List<ToolOutcome>(); var events = new List<string>();
        var clauses = new List<string>(); var mutated = false; var generated = 0; var promptTokens = 0;
        var sequence = request.Messages.LastOrDefault()?.Sequence ?? 0;
        for (var ordinal = 0; ; ordinal++)
        {
            PackedPrompt packed;
            try { packed = PromptPacker.Pack(_tokenizer, _model.Config, request, appended); }
            catch (ArgumentException) when (appended.Count > 0) { events.Add("ACTIVE_EXCHANGE_EXCEEDS_CONTEXT"); clauses.Add("Please continue in another turn."); break; }
            promptTokens = Math.Max(promptTokens, packed.Tokens.Length);
            ProtocolMessage message;
            try
            {
                var output = _testGenerator is null ? Generate(packed, request.Tools, settings) : _testGenerator(packed, request.Tools, settings);
                generated += output.Count;
                message = ProtocolGrammar.Parse(output.Text, request.Tools);
            }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException or System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException)
            { events.Add("INVALID_OR_INCOMPLETE_PROTOCOL"); if (clauses.Count == 0) clauses.Add("Could you clarify your request?"); break; }
            if (message.Type == "text")
            {
                if (clauses.Count == 0) clauses.Add(settings.Mode == ResponseMode.DeterministicOnly ? "Please ask about an available tool." : Fit(message.Text!));
                break;
            }
            var name = message.Name!; var arguments = message.Arguments!;
            var key = GameToolRegistry.IdempotencyKey(request.ConversationId, request.TurnId, ordinal);
            var invocation = new GameToolInvocation(name, arguments, key);
            request.Tools.TryGet(name, out var tool);
            string? veto = ordinal >= settings.MaximumToolCalls ? "TOOL_BUDGET" : tool.Schema.MutatesWorldState && mutated ? "WORLD_ACTION_LIMIT" :
                tool.Schema.MutatesWorldState ? ActionVeto(request.Messages[^1].Text) : null;
            if (veto is null && !_authorize(request, invocation)) veto = "HOST_DENIED";
            if (veto is not null)
            {
                outcomes.Add(new(name, arguments, null, veto, key)); events.Add(veto);
                clauses.Add(veto == "WORLD_ACTION_LIMIT" ? "Only one world-changing action is allowed per reply." : "I have not taken that action. Please clarify your request."); break;
            }
            if (tool.Schema.MutatesWorldState) mutated = true;
            var callText = ProtocolText.Call(name, arguments, tool.Schema);
            appended.Add(new(MessageRole.AssistantToolCall, callText, ++sequence, name));
            var result = _journal.Execute(invocation, () => GameToolRegistry.InvokeValidated(tool, invocation, new(request, packed.Retained)),
                tool.Schema.MutatesWorldState ? GameToolRegistry.IdempotencyKey(request.ConversationId, request.TurnId, -1) : null);
            outcomes.Add(new(name, arguments, result, null, key));
            appended.Add(new(MessageRole.ToolResult, JsonDefaults.Write(new { name, result }), ++sequence, name));
            clauses.Add(result.ErrorCode == "WORLD_ACTION_LIMIT" ? "Only one world-changing action is allowed per reply." : GameToolRegistry.Render(tool.Schema, result));
            if (ordinal + 1 >= settings.MaximumToolCalls) { events.Add("TOOL_BUDGET_REACHED"); break; }
        }
        var display = string.Join(" ", clauses);
        if (display.Length > 256 || _tokenizer.Encode(display).Length > 64)
        {
            // Never truncate a number or an authoritative clause mid-sentence.
            var bounded = new List<string>();
            foreach (var clause in clauses) { var candidate = string.Join(" ", bounded.Append(clause)); if (candidate.Length > 256 || _tokenizer.Encode(candidate).Length > 64) break; bounded.Add(clause); }
            display = bounded.Count == 0 ? "The tool result is available in the conversation record." : string.Join(" ", bounded);
            events.Add("DISPLAY_LIMIT");
        }
        appended.Add(new(MessageRole.Assistant, display, ++sequence));
        return new(display, appended.ToArray(), outcomes.ToArray(), new(promptTokens, generated, watch.Elapsed.TotalMilliseconds, events.ToArray()));
    }
    private string Fit(string text) => string.IsNullOrWhiteSpace(text) || text.Length > 256 || _tokenizer.Encode(text).Length > 64
        ? "Could you clarify your request?" : text;
    internal static string? ActionVeto(string text)
    {
        text = text.Replace('’', '\'');
        // Veto only: this never chooses a tool or supplies arguments.
        return Regex.IsMatch(text, "(?:\\b(?:not|never|don't|dont|do not|won't|wouldn't|can't|cannot|cancel|stop|nevermind|never mind|forget it|scratch that|hold off|withdraw|retract|" +
            "if|unless|when|until|provided|assuming|suppose|imagine|pretend|hypothetical|hypothetically|theoretical|theoretically|in theory|" +
            "would|could|might|quoted|kidding|joking|example|wait|changed my mind|second thought)\\b|[\"“”‘`])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            ? "NON_AFFIRMATIVE_ACTION" : null;
    }
    private (string Text, int Count) Generate(PackedPrompt packed, GameToolRegistry tools, GenerationSettings settings)
    {
        using var session = new CausalNetwork.Session(_model); var logits = session.Prefill(packed.Tokens);
        var grammar = new ProtocolGrammar(tools.Schemas); var ids = new List<int>(); var random = new Random(settings.Seed);
        for (var step = 0; step < 256; step++)
        {
            if (grammar.Complete) return (_tokenizer.Decode(ids), ids.Count);
            var best = -1; var score = double.NegativeInfinity;
            for (var id = ByteBpe.Reserved; id < logits.Length; id++)
            {
                var value = logits[id]; if (!float.IsFinite(value)) continue;
                var sampled = settings.Temperature == 0 ? value : value / settings.Temperature - Math.Log(-Math.Log(Math.Clamp(random.NextDouble(), 1e-12, 1 - 1e-12)));
                if (sampled > score && grammar.Allows(_tokenizer.Piece(id))) { best = id; score = sampled; }
            }
            if (best < 0) throw new InvalidDataException("No valid protocol continuation.");
            grammar.Append(_tokenizer.Piece(best)); ids.Add(best);
            if (!grammar.Complete && step < 255) logits = session.Next(best);
        }
        if (grammar.Complete) return (_tokenizer.Decode(ids), ids.Count);
        throw new InvalidDataException("Protocol token budget exhausted.");
    }
}

internal static class ProtocolText
{
    internal static string Call(string name, IReadOnlyDictionary<string, string> args, ToolSchema schema)
    {
        var typed = new Dictionary<string, object>();
        foreach (var p in schema.Parameters) if (args.TryGetValue(p.Name, out var value)) typed[p.Name] = p.Type switch
        { ToolValueType.Integer => int.Parse(value), ToolValueType.Boolean => bool.Parse(value), _ => value };
        return JsonDefaults.Write(new { type = "tool_call", name, arguments = typed });
    }
}
