namespace Fishbrain.Neural;

internal sealed record ParameterShape(string Name, int Rows, int Columns);
internal sealed record FrameLogits(Dictionary<string, Tensor> Fields, Tensor Start, Tensor End, Tensor Antecedent);
internal sealed record NetworkOutput(Tensor Encoded, Tensor Current, Dictionary<string, Tensor> Heads,
    Tensor Slots, Tensor FactSpans, Tensor Antecedents, FrameLogits[] Frames, Tensor[] Plans, Tensor[] PlanFrames, Tensor PlanMemory);

/// <summary>Immutable packed inference parameters. Training owns a distinct mutable parameter set.</summary>
internal sealed class ContextualNetwork
{
    internal static readonly IReadOnlyDictionary<string, int> HeadSizes = new Dictionary<string, int>
    {
        ["speechActs"] = Enum.GetValues<SpeechAct>().Length,
        ["domains"] = Enum.GetValues<DialogueDomain>().Length,
        ["goals"] = Enum.GetValues<DialogueGoal>().Length,
        ["affect"] = Enum.GetValues<UserAffect>().Length,
        ["stance"] = Enum.GetValues<DialogueStance>().Length,
        ["policy"] = Enum.GetValues<ResponsePolicy>().Length,
        ["content"] = Enum.GetValues<ContentFlag>().Length,
        ["knowledgeTarget"] = Enum.GetValues<KnowledgeTarget>().Length,
        ["discourseAct"] = Enum.GetValues<DiscourseAct>().Length,
        ["discourseSubject"] = Enum.GetValues<DialogueParticipant>().Length,
        ["discourseTarget"] = Enum.GetValues<DialogueParticipant>().Length,
        ["factKind"] = Enum.GetValues<DialogueFactKind>().Length + 1,
        ["factPolarity"] = 2,
        ["agenda"] = Enum.GetValues<AgendaKind>().Length + 1,
        ["agendaStatus"] = Enum.GetValues<AgendaStatus>().Length
    };
    private readonly Dictionary<string, float[]> _weights;
    public ContextualModelConfig Config { get; }
    public WordVocabulary Vocabulary { get; }
    public DialogueTokenizer Tokenizer { get; }
    public DialogueDomainDefinition Domain { get; }
    public IReadOnlyList<ParameterShape> Shapes { get; }
    public long ParameterCount => Shapes.Sum(x => (long)x.Rows * x.Columns);

    public ContextualNetwork(ContextualModelConfig config, WordVocabulary vocabulary, DialogueDomainDefinition domain,
        IReadOnlyDictionary<string, float[]>? weights = null, bool takeOwnership = false)
    {
        config.Validate();
        Config = config;
        Vocabulary = vocabulary;
        Tokenizer = new(vocabulary);
        Domain = domain;
        Shapes = Layout(config, vocabulary.InputSize, vocabulary.OutputSize, domain.Tools.Count + 1).ToArray();
        if (ParameterCount > 100_000_000) throw new InvalidDataException("Contextual model exceeds the parameter budget.");
        var random = new DeterministicRandom(config.Seed);
        _weights = new(StringComparer.Ordinal);
        if (weights is not null && (weights.Count != Shapes.Count || Shapes.Any(s => !weights.ContainsKey(s.Name))))
            throw new InvalidDataException("Contextual parameter names do not match the architecture.");
        foreach (var shape in Shapes)
        {
            var values = weights is null ? new float[checked(shape.Rows * shape.Columns)] : takeOwnership ? weights[shape.Name] : (float[])weights[shape.Name].Clone();
            if (values.Length != shape.Rows * shape.Columns || values.Any(x => !float.IsFinite(x))) throw new InvalidDataException("Invalid contextual parameter data.");
            if (weights is null)
            {
                var scale = shape.Name.Contains("embedding", StringComparison.Ordinal) ? 0.02 : Math.Sqrt(1.0 / shape.Rows);
                for (var i = 0; i < values.Length; i++) values[i] = (float)(random.NextGaussian() * scale);
            }
            _weights.Add(shape.Name, values);
        }
    }

    public Dictionary<string, float[]> Snapshot() => _weights.ToDictionary(x => x.Key, x => (float[])x.Value.Clone(), StringComparer.Ordinal);
    public Dictionary<string, Tensor> Parameters(bool trainable = false) => Shapes.ToDictionary(s => s.Name,
        s => new Tensor(s.Rows, s.Columns, _weights[s.Name], trainable), StringComparer.Ordinal);

    internal static IEnumerable<ParameterShape> Layout(ContextualModelConfig c, int inputs, int outputs, int tools)
    {
        var d = c.Width;
        yield return new("encoder.embedding", inputs, d);
        yield return new("encoder.position", c.ContextLength, d);
        yield return new("encoder.segment", Enum.GetValues<InputSegment>().Length, d);
        yield return new("decoder.embedding", inputs, d);
        yield return new("decoder.position", c.MaximumOutputTokens + 1, d);
        yield return new("decoder.output", d, outputs);
        for (var layer = 0; layer < c.EncoderLayers; layer++) foreach (var shape in Layer($"encoder.layer{layer}", false)) yield return shape;
        for (var layer = 0; layer < c.DecoderLayers; layer++) foreach (var shape in Layer($"decoder.layer{layer}", true)) yield return shape;
        foreach (var head in HeadSizes)
        {
            yield return new($"head.{head.Key}.query", 1, d);
            yield return new($"head.{head.Key}.output", d, head.Value);
        }
        yield return new("head.slots", d, 1 + 2 * Enum.GetValues<SlotType>().Length);
        yield return new("head.factSpans", d, 3);
        yield return new("head.antecedent.query", 1, d);
        yield return new("head.antecedent.none", 1, d);
        yield return new("memory.query", d, d);
        yield return new("memory.none", 1, d);
        yield return new("frame.query", 3, d);
        foreach (var field in new Dictionary<string, int> { ["active"] = 2, ["act"] = Enum.GetValues<SpeechAct>().Length, ["subject"] = 3, ["target"] = 3, ["status"] = 5, ["tool"] = tools })
            yield return new($"frame.{field.Key}", d, field.Value);
        yield return new("frame.start", d, d);
        yield return new("frame.end", d, d);
        yield return new("frame.antecedent", d, d);
        yield return new("frame.status.embedding", 5, d);
        yield return new("frame.tool.embedding", tools, d);
        yield return new("planner.query", 3, d);
        yield return new("planner.act.embedding", Enum.GetValues<DialogueResponseAct>().Length, d);
        yield return new("planner.output", d, Enum.GetValues<DialogueResponseAct>().Length);
        yield return new("planner.frame", d, 4);
        yield return new("claim.query", 1, d);
        yield return new("claim.output", d, 2);

        IEnumerable<ParameterShape> Layer(string prefix, bool cross)
        {
            foreach (var projection in new[] { "q", "k", "v", "o" }) yield return new($"{prefix}.self.{projection}", d, d);
            if (cross) foreach (var projection in new[] { "q", "k", "v", "o" }) yield return new($"{prefix}.cross.{projection}", d, d);
            yield return new($"{prefix}.in", d, c.FeedForwardWidth);
            yield return new($"{prefix}.out", c.FeedForwardWidth, d);
        }
    }

    internal Tensor Encode(TensorGraph g, IReadOnlyDictionary<string, Tensor> p, PackedInput input)
    {
        var x = g.Add(g.Gather(p["encoder.embedding"], input.Tokens),
            g.Add(g.Gather(p["encoder.position"], Enumerable.Range(0, input.Tokens.Length).ToArray()), g.Gather(p["encoder.segment"], input.Segments)));
        for (var layer = 0; layer < Config.EncoderLayers; layer++) x = Layer(g, p, x, $"encoder.layer{layer}", false, null);
        return g.Normalize(x);
    }

    internal Tensor MemoryScores(TensorGraph g, IReadOnlyDictionary<string, Tensor> p, Tensor query, IReadOnlyList<DialogueFact> facts)
    {
        var entries = new List<Tensor> { p["memory.none"] };
        foreach (var fact in facts)
            entries.Add(g.Mean(g.Gather(p["encoder.embedding"], Tokenizer.Encode(StructuredInput.FactText(fact)))));
        return g.MatMul(g.MatMul(query, p["memory.query"]), g.Transpose(g.Concat(entries.ToArray())));
    }

    internal NetworkOutput Understand(TensorGraph g, IReadOnlyDictionary<string, Tensor> p, PackedInput input,
        IReadOnlyList<SemanticFrame>? teacherFrames = null, IReadOnlyList<PlannedResponseAct>? teacherPlan = null, Tensor? encodedInput = null)
    {
        var encoded = encodedInput ?? Encode(g, p, input);
        var current = g.Gather(encoded, input.CurrentPositions);
        if (current.Rows == 0) throw new ArgumentException("Current utterance has no tokens.");
        var heads = new Dictionary<string, Tensor>(StringComparer.Ordinal);
        foreach (var name in HeadSizes.Keys)
            heads[name] = g.MatMul(g.Attention(p[$"head.{name}.query"], current, current, 1, false), p[$"head.{name}.output"]);
        var antecedents = new List<Tensor> { p["head.antecedent.none"] };
        foreach (var utterance in input.Utterances)
        {
            var positions = Enumerable.Range(0, input.Sources.Length).Where(i => input.Sources[i].Utterance == utterance.Sequence && input.Sources[i].Length > 0).ToArray();
            antecedents.Add(g.Mean(g.Gather(encoded, positions)));
        }
        var pointerQuery = g.Attention(p["head.antecedent.query"], current, current, 1, false);
        var pointer = g.MatMul(pointerQuery, g.Transpose(g.Concat(antecedents.ToArray())));
        var frames = new FrameLogits[3];
        var frameStates = new List<Tensor>();
        Tensor? previous = null;
        for (var i = 0; i < frames.Length; i++)
        {
            var query = g.Gather(p["frame.query"], [i]);
            if (previous is not null) query = g.Add(query, previous);
            var frame = g.Attention(query, encoded, encoded, Config.Heads, false);
            var fields = new[] { "active", "act", "subject", "target", "status", "tool" }.ToDictionary(name => name, name => g.MatMul(frame, p[$"frame.{name}"]));
            frames[i] = new(fields, g.MatMul(g.MatMul(frame, p["frame.start"]), g.Transpose(current)),
                g.MatMul(g.MatMul(frame, p["frame.end"]), g.Transpose(current)),
                g.MatMul(g.MatMul(frame, p["frame.antecedent"]), g.Transpose(g.Concat(antecedents.ToArray()))));
            var teacher = teacherFrames is not null && i < teacherFrames.Count ? teacherFrames[i] : null;
            var status = teacher is null ? ArgMax(fields["status"]) : (int)teacher.Status;
            var tool = teacher is null ? ArgMax(fields["tool"]) : ToolIndex(teacher.ToolName);
            previous = g.Add(frame, g.Add(g.Gather(p["frame.status.embedding"], [status]), g.Gather(p["frame.tool.embedding"], [tool])));
            frameStates.Add(previous);
        }
        var memory = g.Concat(encoded, g.Concat(frameStates.ToArray()));
        var plans = new Tensor[3];
        var planFrames = new Tensor[3];
        var planStates = new List<Tensor>();
        previous = null;
        for (var i = 0; i < plans.Length; i++)
        {
            var query = g.Gather(p["planner.query"], [i]);
            if (previous is not null) query = g.Add(query, previous);
            var state = g.Attention(query, memory, memory, Config.Heads, false);
            plans[i] = g.MatMul(state, p["planner.output"]);
            planFrames[i] = g.MatMul(state, p["planner.frame"]);
            var act = teacherPlan is null ? ArgMax(plans[i]) : i < teacherPlan.Count ? (int)teacherPlan[i].Act : 0;
            previous = g.Add(state, g.Gather(p["planner.act.embedding"], [act]));
            planStates.Add(previous);
        }
        return new(encoded, current, heads, g.MatMul(current, p["head.slots"]), g.MatMul(current, p["head.factSpans"]),
            pointer, frames, plans, planFrames, g.Concat(memory, g.Concat(planStates.ToArray())));
    }

    internal Tensor Decode(TensorGraph g, IReadOnlyDictionary<string, Tensor> p, IReadOnlyList<int> inputTokens, Tensor memory)
    {
        var x = g.Add(g.Gather(p["decoder.embedding"], inputTokens), g.Gather(p["decoder.position"], Enumerable.Range(0, inputTokens.Count).ToArray()));
        for (var layer = 0; layer < Config.DecoderLayers; layer++) x = Layer(g, p, x, $"decoder.layer{layer}", true, memory);
        return g.MatMul(g.Normalize(x), p["decoder.output"]);
    }

    internal DecoderSession CreateDecoderSession(IReadOnlyDictionary<string, Tensor> parameters, Tensor memory) => new(this, parameters, memory);

    /// <summary>Per-reply KV cache. Encoder projections and previous decoder tokens are computed once.</summary>
    internal sealed class DecoderSession
    {
        private readonly ContextualNetwork _model;
        private readonly IReadOnlyDictionary<string, Tensor> _parameters;
        private readonly Tensor?[] _keys;
        private readonly Tensor?[] _values;
        private readonly Tensor[] _memoryKeys;
        private readonly Tensor[] _memoryValues;
        private int _position;

        internal DecoderSession(ContextualNetwork model, IReadOnlyDictionary<string, Tensor> parameters, Tensor memory)
        {
            _model = model;
            _parameters = parameters;
            _keys = new Tensor?[model.Config.DecoderLayers];
            _values = new Tensor?[model.Config.DecoderLayers];
            _memoryKeys = new Tensor[model.Config.DecoderLayers];
            _memoryValues = new Tensor[model.Config.DecoderLayers];
            var graph = new TensorGraph(false);
            for (var layer = 0; layer < model.Config.DecoderLayers; layer++)
            {
                _memoryKeys[layer] = graph.MatMul(memory, parameters[$"decoder.layer{layer}.cross.k"]);
                _memoryValues[layer] = graph.MatMul(memory, parameters[$"decoder.layer{layer}.cross.v"]);
            }
        }

        internal Tensor Next(int inputToken)
        {
            if (_position > _model.Config.MaximumOutputTokens) throw new InvalidOperationException("Decoder cache exceeded its token budget.");
            var g = new TensorGraph(false);
            var p = _parameters;
            var x = g.Add(g.Gather(p["decoder.embedding"], [inputToken]), g.Gather(p["decoder.position"], [_position++]));
            for (var layer = 0; layer < _model.Config.DecoderLayers; layer++)
            {
                var prefix = $"decoder.layer{layer}";
                var norm = g.Normalize(x);
                var key = g.MatMul(norm, p[prefix + ".self.k"]);
                var value = g.MatMul(norm, p[prefix + ".self.v"]);
                _keys[layer] = _keys[layer] is { } oldKeys ? g.Concat(oldKeys, key) : key;
                _values[layer] = _values[layer] is { } oldValues ? g.Concat(oldValues, value) : value;
                x = g.Add(x, g.MatMul(g.Attention(g.MatMul(norm, p[prefix + ".self.q"]), _keys[layer]!, _values[layer]!, _model.Config.Heads, false), p[prefix + ".self.o"]));
                x = g.Add(x, g.MatMul(g.Attention(g.MatMul(g.Normalize(x), p[prefix + ".cross.q"]), _memoryKeys[layer], _memoryValues[layer], _model.Config.Heads, false), p[prefix + ".cross.o"]));
                x = g.Add(x, g.MatMul(g.Relu(g.MatMul(g.Normalize(x), p[prefix + ".in"])), p[prefix + ".out"]));
            }
            return g.MatMul(g.Normalize(x), p["decoder.output"]);
        }
    }

    internal Tensor ClaimScores(TensorGraph g, IReadOnlyDictionary<string, Tensor> p, Tensor memory, string text)
    {
        var tokens = Tokenizer.Encode(text);
        if (tokens.Length == 0) tokens = [global::Fishbrain.Tokenizer.Eos];
        var response = g.Gather(p["encoder.embedding"], tokens);
        var query = g.Add(p["claim.query"], g.Mean(response));
        return g.MatMul(g.Attention(query, memory, memory, Config.Heads, false), p["claim.output"]);
    }

    private Tensor Layer(TensorGraph g, IReadOnlyDictionary<string, Tensor> p, Tensor input, string prefix, bool causal, Tensor? memory)
    {
        var norm = g.Normalize(input);
        var x = g.Add(input, Attention("self", norm, norm, causal));
        if (memory is not null) x = g.Add(x, Attention("cross", g.Normalize(x), memory, false));
        return g.Add(x, g.MatMul(g.Relu(g.MatMul(g.Normalize(x), p[prefix + ".in"])), p[prefix + ".out"]));
        Tensor Attention(string kind, Tensor query, Tensor source, bool mask) =>
            g.MatMul(g.Attention(g.MatMul(query, p[$"{prefix}.{kind}.q"]), g.MatMul(source, p[$"{prefix}.{kind}.k"]),
                g.MatMul(source, p[$"{prefix}.{kind}.v"]), Config.Heads, mask), p[$"{prefix}.{kind}.o"]);
    }

    internal int ToolIndex(string? name) => name is null or "NONE" ? 0 : Domain.Tools.Select((x, i) => (x, i)).FirstOrDefault(x => x.x.Schema.Name == name, (null!, -1)).i + 1;
    internal static int ArgMax(Tensor tensor, int row = 0)
    {
        var best = 0;
        for (var c = 1; c < tensor.Columns; c++) if (tensor.Data[row * tensor.Columns + c] > tensor.Data[row * tensor.Columns + best]) best = c;
        return best;
    }
}
