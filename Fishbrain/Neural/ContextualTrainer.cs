namespace Fishbrain.Neural;

internal enum ContextualPhase { MaskedLanguage, JointUnderstanding, JointRealization, DecoderPolish }

/// <summary>Mutable training state is owned here and is never allocated by Brain.Load.</summary>
internal sealed class ContextualTrainer
{
    public ContextualNetwork Architecture { get; }
    public Dictionary<string, Tensor> Parameters { get; }
    public Dictionary<string, float[]> FirstMoments { get; }
    public Dictionary<string, float[]> SecondMoments { get; }
    public int Step { get; private set; }
    public DeterministicRandom Random { get; }
    public Dictionary<string, int> ParameterUpdates { get; }

    public ContextualTrainer(ContextualNetwork network, int step = 0, ulong? randomState = null,
        IReadOnlyDictionary<string, float[]>? first = null, IReadOnlyDictionary<string, float[]>? second = null,
        IReadOnlyDictionary<string, int>? parameterUpdates = null)
    {
        Architecture = network;
        var weights = network.Snapshot();
        Parameters = network.Shapes.ToDictionary(s => s.Name, s => new Tensor(s.Rows, s.Columns, weights[s.Name], true));
        FirstMoments = weights.ToDictionary(x => x.Key, x => first is null ? new float[x.Value.Length] : (float[])first[x.Key].Clone());
        SecondMoments = weights.ToDictionary(x => x.Key, x => second is null ? new float[x.Value.Length] : (float[])second[x.Key].Clone());
        ParameterUpdates = weights.ToDictionary(x => x.Key, x => parameterUpdates?.GetValueOrDefault(x.Key) ?? 0);
        Step = step;
        Random = new(network.Config.Seed);
        if (randomState is { } state) Random.State = state;
    }

    public static ContextualPhase Phase(int step) => step < 40_000 ? ContextualPhase.MaskedLanguage
        : step >= 220_000 ? ContextualPhase.DecoderPolish
        : (step - 40_000) % 10 < 7 ? ContextualPhase.JointUnderstanding : ContextualPhase.JointRealization;

    public ContextualNetwork Snapshot() => new(Architecture.Config, Architecture.Vocabulary, Architecture.Domain,
        Parameters.ToDictionary(x => x.Key, x => x.Value.Data));

    public float TrainBatch(IReadOnlyList<TrainingExample> examples, ContextualPhase? overridePhase = null)
    {
        if (examples.Count == 0) throw new ArgumentException("Training needs a nonempty microbatch sequence.");
        var phase = overridePhase ?? Phase(Step);
        var gradients = Parameters.ToDictionary(x => x.Key, x => new float[x.Value.Data.Length]);
        var loss = 0f;
        foreach (var example in examples)
        {
            foreach (var p in Parameters.Values) Array.Clear(p.Gradient!);
            loss += Loss(example, phase);
            foreach (var (name, p) in Parameters)
            {
                if (!Active(name, phase)) continue;
                var accumulator = gradients[name];
                for (var i = 0; i < accumulator.Length; i++) accumulator[i] += p.Gradient![i] / examples.Count;
            }
        }
        Apply(gradients, phase);
        return loss / examples.Count;
    }

    internal float Loss(TrainingExample example, ContextualPhase phase, bool backward = true)
    {
        var model = Architecture;
        var request = example.Request ?? throw new InvalidDataException("Contextual training requires complete structured requests.");
        var facts = request.PlayerProfile.Facts.Concat(request.State.SessionFacts).Distinct().ToArray();
        var selected = example.Contextual?.RelevantFacts ?? Array.Empty<DialogueFact>();
        var input = StructuredInput.Pack(request, model.Tokenizer, model.Config.ContextLength, selected, model.Domain);
        var graph = new TensorGraph(backward);
        var p = Parameters;
        var loss = 0f;
        if (phase == ContextualPhase.MaskedLanguage)
        {
            var tokens = (int[])input.Tokens.Clone();
            var segments = (int[])input.Segments.Clone();
            var positions = input.CurrentPositions.Where(_ => Random.NextDouble() < .15).ToArray();
            if (positions.Length == 0) positions = [input.CurrentPositions[Random.NextInt(input.CurrentPositions.Length)]];
            var targets = positions.Select(i => tokens[i]).ToArray();
            foreach (var position in positions) { tokens[position] = Tokenizer.Unknown; segments[position] = (int)InputSegment.Mask; }
            var encoded = model.Encode(graph, p, input with { Tokens = tokens, Segments = segments });
            var logits = graph.MatMul(graph.Gather(encoded, positions), graph.Transpose(p["encoder.embedding"]));
            for (var i = 0; i < positions.Length; i++) loss += graph.CrossEntropy(logits, i, targets[i], 1f / positions.Length);
        }
        else
        {
            // The final phase cannot backpropagate through understanding or alter its features.
            var understandingGraph = phase == ContextualPhase.DecoderPolish ? new TensorGraph(false) : graph;
            var output = model.Understand(understandingGraph, p, input, example.Contextual?.Frames, example.Contextual?.Plan);
            if (phase == ContextualPhase.JointUnderstanding)
            {
                var supervised = example.SupervisedHeads;
                Multi("speechActs", example.SpeechActs.Select(x => (int)x));
                Multi("domains", example.Domains.Select(x => (int)x));
                Multi("goals", example.Goals.Select(x => (int)x));
                Multi("content", example.ContentFlags.Select(x => (int)x));
                Exclusive("affect", (int)example.Affect); Exclusive("stance", (int)example.Stance);
                Exclusive("policy", (int)example.Policy); Exclusive("knowledgeTarget", (int)example.KnowledgeTarget);
                Exclusive("discourseAct", (int)example.Discourse.Act); Exclusive("discourseSubject", (int)example.Discourse.Subject);
                Exclusive("discourseTarget", (int)example.Discourse.Target);
                Exclusive("factKind", example.Discourse.FactKind is { } kind ? (int)kind + 1 : 0);
                Exclusive("factPolarity", example.Discourse.Negated ? 1 : 0);
                if (supervised.Contains("antecedent"))
                {
                    var pointer = Array.FindIndex(input.Utterances, x => x.Sequence == example.Discourse.AntecedentUtterance) + 1;
                    loss += graph.CrossEntropy(output.Antecedents, 0, pointer);
                }
                var sourceRows = input.CurrentPositions.Select((position, row) => (Source: input.Sources[position], Row: row))
                    .DistinctBy(x => (x.Source.Start, x.Source.Length)).ToArray();
                foreach (var (source, row) in sourceRows)
                {
                    if (supervised.Contains("slots"))
                    {
                        var slot = example.Slots.FirstOrDefault(x => source.Start >= x.Start && source.Start < x.Start + x.Length);
                        loss += graph.CrossEntropy(output.Slots, row, slot is null ? 0 : 1 + (int)slot.Type * 2 + (source.Start == slot.Start ? 0 : 1), 1f / sourceRows.Length);
                    }
                    if (supervised.Contains("factSpan"))
                    {
                        var span = example.Discourse.FactValueSpan;
                        loss += graph.CrossEntropy(output.FactSpans, row, span is not null && source.Start >= span.Start && source.Start < span.Start + span.Length
                            ? source.Start == span.Start ? 1 : 2 : 0, 1f / sourceRows.Length);
                    }
                }
                if (example.Contextual?.RelevantFacts is { } relevant)
                {
                    // Retrieval sees the same pre-selection context at training and inference.
                    var retrievalInput = StructuredInput.Pack(request, model.Tokenizer, model.Config.ContextLength, [], model.Domain);
                    var retrievalEncoding = model.Encode(graph, p, retrievalInput);
                    var scores = model.MemoryScores(graph, p, graph.Mean(graph.Gather(retrievalEncoding, retrievalInput.CurrentPositions)), facts);
                    var positives = relevant.Select(f => Array.IndexOf(facts, f) + 1).ToHashSet();
                    if (positives.Count == 0) positives.Add(0);
                    foreach (var target in positives) loss += graph.CrossEntropy(scores, 0, target, 1f / positives.Count);
                }
                if (example.Contextual?.Frames is { } frames)
                    for (var i = 0; i < 3; i++)
                    {
                        var logits = output.Frames[i];
                        loss += graph.CrossEntropy(logits.Fields["active"], 0, i < frames.Length ? 1 : 0, 1f / 3);
                        if (i >= frames.Length) continue;
                        var frame = frames[i];
                        foreach (var (name, target) in new[] { ("act", (int)frame.SpeechAct), ("subject", (int)frame.Subject), ("target", (int)frame.Target),
                            ("status", (int)frame.Status), ("tool", model.ToolIndex(frame.ToolName)) })
                            loss += graph.CrossEntropy(logits.Fields[name], 0, target, 1f / frames.Length);
                        var first = Array.FindIndex(input.CurrentPositions, position => input.Sources[position].Start >= frame.Start);
                        var last = Array.FindLastIndex(input.CurrentPositions, position => input.Sources[position].Start + input.Sources[position].Length <= frame.Start + frame.Length);
                        if (first < 0 || last < first) throw new InvalidDataException("Frame targets do not fit the packed current utterance.");
                        loss += graph.CrossEntropy(logits.Start, 0, first, 1f / frames.Length);
                        loss += graph.CrossEntropy(logits.End, 0, last, 1f / frames.Length);
                        loss += graph.CrossEntropy(logits.Antecedent, 0,
                            Array.FindIndex(input.Utterances, u => u.Sequence == frame.Antecedent) + 1, 1f / frames.Length);
                    }
                if (example.Contextual?.Plan is { } plan)
                    for (var i = 0; i < 3; i++)
                    {
                        loss += graph.CrossEntropy(output.Plans[i], 0, i < plan.Length ? (int)plan[i].Act : 0, 1f / 3);
                        loss += graph.CrossEntropy(output.PlanFrames[i], 0, i < plan.Length && plan[i].FrameIndex is { } frameIndex ? frameIndex + 1 : 0, 1f / 3);
                    }
                if (example.Contextual?.Agenda is { } agenda)
                {
                    loss += graph.CrossEntropy(output.Heads["agenda"], 0, agenda.Length == 0 ? 0 : (int)agenda[^1].Kind + 1);
                    if (agenda.Length > 0) loss += graph.CrossEntropy(output.Heads["agendaStatus"], 0, (int)agenda[^1].Status);
                }
                if (ProjectResponse(example))
                {
                    loss += graph.CrossEntropy(model.ClaimScores(graph, p, output.PlanMemory, example.Response!), 0, 0);
                    if (example.RejectedResponse is { } rejected)
                        loss += graph.CrossEntropy(model.ClaimScores(graph, p, output.PlanMemory, rejected), 0, 1);
                    if (Step % 10 == 0)
                    {
                        var candidate = Brain.GenerateContextual(model, p, output.PlanMemory, unchecked(model.Config.Seed + Step));
                        if (!string.IsNullOrWhiteSpace(candidate) && !ConversationalOutputValidator.IsSafe(candidate, request.Persona, selected, out _))
                            loss += graph.CrossEntropy(model.ClaimScores(graph, p, output.PlanMemory, candidate), 0, 1);
                    }
                }

                void Multi(string name, IEnumerable<int> targets) { if (supervised.Contains(name)) loss += graph.BinaryCrossEntropy(output.Heads[name], 0, targets.ToHashSet()); }
                void Exclusive(string name, int target) { if (supervised.Contains(name)) loss += graph.CrossEntropy(output.Heads[name], 0, target); }
            }
            else
            {
                if (!ProjectResponse(example)) throw new InvalidDataException("Realization training accepts only project-owned responses.");
                var responseTokens = model.Tokenizer.Encode(example.Response!).Append(Tokenizer.Eos).ToArray();
                if (responseTokens.Length > model.Config.MaximumOutputTokens) throw new InvalidDataException("Response target exceeds the output budget.");
                var decoderInput = new[] { Tokenizer.Bos }.Concat(responseTokens.Take(responseTokens.Length - 1)).ToArray();
                var logits = model.Decode(graph, p, decoderInput, output.PlanMemory);
                for (var i = 0; i < responseTokens.Length; i++) loss += graph.CrossEntropy(logits, i, model.Vocabulary.OutputId(responseTokens[i]), 1f / responseTokens.Length);
            }
        }
        if (backward) graph.Backward();
        if (!float.IsFinite(loss)) throw new ArithmeticException("Nonfinite contextual training loss.");
        return loss;
    }

    internal static bool ProjectResponse(TrainingExample example) => example.Source.StartsWith("PROJECT_", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(example.Response);
    private static bool Active(string name, ContextualPhase phase) => phase switch
    {
        ContextualPhase.MaskedLanguage => name.StartsWith("encoder.", StringComparison.Ordinal),
        ContextualPhase.DecoderPolish => name.StartsWith("decoder.", StringComparison.Ordinal),
        ContextualPhase.JointUnderstanding => !name.StartsWith("decoder.", StringComparison.Ordinal),
        _ => true
    };

    private void Apply(Dictionary<string, float[]> gradients, ContextualPhase phase)
    {
        double norm = 0;
        foreach (var (name, values) in gradients)
            if (Active(name, phase)) foreach (var value in values) { if (!float.IsFinite(value)) throw new ArithmeticException("Nonfinite training gradient."); norm += (double)value * value; }
        var scale = (float)(norm > 1 ? 1 / Math.Sqrt(norm) : 1);
        var start = Step < 40_000 ? 0 : Step < 220_000 ? 40_000 : 220_000;
        var length = start == 40_000 ? 180_000 : 40_000;
        var local = Step - start;
        var peak = phase == ContextualPhase.DecoderPolish ? .0001 : .0003;
        var rate = (float)(peak * Math.Min(1, (local + 1) / 2000.0) * (.1 + .9 * .5 * (1 + Math.Cos(Math.PI * Math.Min(1, local / (double)length)))));
        foreach (var (name, parameter) in Parameters)
        {
            if (!Active(name, phase)) continue;
            var update = ++ParameterUpdates[name];
            var firstCorrection = 1 - Math.Pow(.9, update);
            var secondCorrection = 1 - Math.Pow(.999, update);
            var m = FirstMoments[name]; var v = SecondMoments[name]; var gradient = gradients[name];
            for (var i = 0; i < parameter.Data.Length; i++)
            {
                var value = gradient[i] * scale;
                m[i] = .9f * m[i] + .1f * value;
                v[i] = .999f * v[i] + .001f * value * value;
                parameter.Data[i] -= rate * ((float)(m[i] / firstCorrection / (Math.Sqrt(v[i] / secondCorrection) + 1e-8)) + .01f * parameter.Data[i]);
            }
        }
        Step++;
    }
}
