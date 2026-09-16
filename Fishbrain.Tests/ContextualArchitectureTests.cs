using Fishbrain;
using Fishbrain.Neural;

namespace Fishbrain.Tests;

internal static class ContextualArchitectureTests
{
    internal static void Run()
    {
        foreach (var input in new[] { "DO NOT BUY 2 ROPE", "IF I BUY 2 ROPE", "I DO NOT WANT TO BUY 2 ROPE", "CANCEL BUY 2 ROPE", "HE SAID BUY 2 ROPE" })
        {
            var world = new DemoWorldState();
            var brain = LegacyBrain.CreateForTesting(new BrainConfig { EmbeddingSize = 8, HeadCount = 2, MlpSize = 12, ContextLength = 128, AttentionWindow = 128, PositionPeriod = 128 });
            var result = brain.Reply(Request(input), DemoGameTools.CreateMerchant(world));
            Assert(result.Diagnostics.ToolInvocation is null && world.Balance == 100, $"Unintended mutation: {input}");
        }
        foreach (var text in new[] { "I GAVE YOU TEN GOLD.", "YOU HAVE TEN COINS.", "MY HOMETOWN IS MOONBASE.", "I AM AN ASSASSIN." })
            Assert(!ConversationalOutputValidator.IsSafe(text, NpcPersona.Default, [], out _), "Unsupported claim passed: " + text);
        var fact = new DialogueFact(DialogueParticipant.Npc, DialogueFactKind.Home, "NORTH ROAD", false, 2, 1, DialogueFactProvenance.SessionReported);
        var packedFact = StructuredInput.FactText(fact);
        Assert(packedFact.Contains("SUBJECT Npc") && packedFact.Contains("SessionReported"), "Fact ownership lost.");
        AttentionGradients(false);
        MatrixKernels();
        TransposedProjection();
        ParallelTrainingParity();
        ParallelAttentionParity();
        ScratchLifetimeParity();
        AssemblyBoundary();
        ClauseFactReduction();
        ClauseAndAgendaGradients();
        AttentionGradients(true);
        NetworkGradients();
        StructuredBoundaries();
        ContextualLearningAndResume();
        DomainPlanning();
    }

    private static void AttentionGradients(bool causal)
    {
        var random = new Random(42);
        var inputs = Enumerable.Range(0, 3).Select(_ => new Tensor(3, 4, Enumerable.Range(0, 12).Select(_ => (float)(random.NextDouble() - .5)).ToArray(), true)).ToArray();
        float Forward(bool training, bool vectorized)
        {
            var g = new TensorGraph(training, vectorized);
            var attended = g.Attention(inputs[0], inputs[1], inputs[2], 2, causal);
            var loss = g.CrossEntropy(g.Normalize(attended), 2, 1);
            if (training) g.Backward();
            return loss;
        }
        var analytic = Forward(true, true);
        Assert(Math.Abs(analytic - Forward(false, false)) < 1e-5, "Scalar and vectorized attention disagree.");
        foreach (var input in inputs)
            for (var i = 0; i < input.Data.Length; i++)
            {
                var original = input.Data[i];
                input.Data[i] = original + .001f;
                var plus = Forward(false, false);
                input.Data[i] = original - .001f;
                var minus = Forward(false, false);
                input.Data[i] = original;
                var numeric = (plus - minus) / .002f;
                Assert(Math.Abs(numeric - input.Gradient![i]) < .015f, "Attention finite difference mismatch.");
            }
    }

    private static void AssemblyBoundary()
    {
        var runtime = typeof(Brain).Assembly;
        Assert(runtime.GetReferencedAssemblies().All(x => !x.Name!.StartsWith("Fishbrain")), "Runtime depends on another project assembly.");
        Assert(runtime.GetTypes().All(t => t != typeof(ContextualTrainer) && t != typeof(LegacyBrain) && t != typeof(DemoWorldState)),
            "Training or demo implementation leaked into runtime.");
        Assert(typeof(ContextualTrainer).Assembly != runtime && typeof(DemoWorldState).Assembly != runtime, "Assemblies were not separated.");
    }

    private static void ClauseFactReduction()
    {
        var cases = ContextualAcceptance.Cases().Where(x => x.ExpectedFacts is not null).ToArray();
        foreach (var scenario in ContextualAcceptance.Cases())
        {
            var words = Tokenizer.Lex(string.Join(' ', scenario.Request.Utterances.Select(x => x.Text))).Where(x => x.Kind == LexicalTokenKind.Word).Select(x => x.Text);
            var tokenizer = new DialogueTokenizer(new WordVocabulary(words, words));
            _ = StructuredInput.Pack(scenario.Request, tokenizer, 2048, scenario.SelectedFacts ?? [], DemoDialogueDomains.Merchant);
        }
        foreach (var example in cases.Where(x => x.Id.Contains("correction", StringComparison.Ordinal)))
        {
            var actual = DialogueStateReducer.ReduceFrameFacts(example.Request.State.SessionFacts, example.Frames, 6);
            var expected = example.ExpectedFacts!;
            Assert(actual.Select(Key).ToHashSet().SetEquals(expected.Select(Key)), "Clause facts changed the wrong owner: " + example.Id);
            foreach (var status in new[] { ActionStatus.Quoted, ActionStatus.Hypothetical, ActionStatus.Question })
            {
                var suppressed = DialogueStateReducer.ReduceFrameFacts(example.Request.State.SessionFacts,
                    example.Frames.Select(x => x with { Status = status }).ToArray(), 6);
                Assert(suppressed.SequenceEqual(example.Request.State.SessionFacts), "Non-asserted clause became a session fact.");
            }
        }
        static object Key(DialogueFact f) => (f.Subject, f.Kind, f.Value, f.Negated, f.SourceUtterance, f.Provenance);
    }

    private static void ClauseAndAgendaGradients()
    {
        var test = ContextualAcceptance.Cases().First(x => x.Id == "compound-owned-correction-Player");
        var words = Tokenizer.Lex(string.Join(' ', test.Request.Utterances.Select(x => x.Text)) +
            " NAME ROLE ORIGIN HOME FAMILY OCCUPATION FACTION TRAITS UNKNOWN MOOD RAPPORT TRUST GOALS PENDING SUBJECT PREDICATE VALUE SOURCE PROVENANCE PLAYER NPC NONE NEUTRAL TRAVELER")
            .Where(x => x.Kind == LexicalTokenKind.Word).Select(x => x.Text).Distinct().ToArray();
        var vocabulary = new WordVocabulary(words, words);
        var model = new ContextualNetwork(new() { EncoderLayers = 1, DecoderLayers = 1, Width = 8, Heads = 2, FeedForwardWidth = 16 },
            vocabulary, new("FACT_TEST", []));
        var first = new DialogueAgendaEntry(AgendaKind.UnansweredQuestion, "HOME", 0, AgendaStatus.Active);
        var second = new DialogueAgendaEntry(AgendaKind.UnansweredQuestion, "OCCUPATION", 1, AgendaStatus.Active);
        var targets = new[] { first with { Status = AgendaStatus.Completed }, second with { Status = AgendaStatus.Completed } };
        var request = test.Request with { State = test.Request.State with { Agenda = [first, second] } };
        var example = new TrainingExample("", request.Utterances[^1].Text, request.Utterances.ToArray(), [], [], [],
            UserAffect.Neutral, DialogueStance.Neutral, ResponsePolicy.Answer, [], [], "NONE", "ACKNOWLEDGE", KnowledgeTarget.None,
            "PROJECT_TEST", "FACT_TEST", new HashSet<string>(), DiscourseFrame.Empty, [], [], DiscourseResponseAction.None, [], null)
        { Request = request, Contextual = new(test.Frames, test.Plan, null, targets) };
        var trainer = new ContextualTrainer(model, 100000);
        trainer.Loss(example, ContextualPhase.JointUnderstanding);
        foreach (var name in new[] { "encoder.embedding", "frame.factSpans", "frame.factSubject", "agenda.kind", "agenda.status", "agenda.subject" })
        {
            var parameter = trainer.Parameters[name];
            var index = Enumerable.Range(0, parameter.Data.Length).MaxBy(i => Math.Abs(parameter.Gradient![i]));
            Assert(Math.Abs(parameter.Gradient![index]) > 1e-7, "Missing clause/agenda gradient: " + name);
            var original = parameter.Data[index];
            parameter.Data[index] = original + .001f;
            var plus = trainer.Loss(example, ContextualPhase.JointUnderstanding, false);
            parameter.Data[index] = original - .001f;
            var minus = trainer.Loss(example, ContextualPhase.JointUnderstanding, false);
            parameter.Data[index] = original;
            Assert(Math.Abs((plus - minus) / .002f - parameter.Gradient![index]) < .04f, "Clause/agenda finite difference mismatch: " + name);
        }
        var reduced = DialogueStateReducer.ReduceAgendaPlan(request.State.Agenda, targets, test.Plan, test.Frames, null, null);
        Assert(reduced.SequenceEqual(targets), "Compound answers did not retire both agenda entries.");
        Assert(DialogueStateReducer.ReduceAgendaPlan(request.State.Agenda, [], test.Plan, [], null, null).SequenceEqual(request.State.Agenda),
            "Omitted predictions silently dropped active goals.");
    }

    private static void ParallelAttentionParity()
    {
        foreach (var causal in new[] { false, true })
        {
            var random = new Random(92);
            var inputs = Enumerable.Range(0, 3).Select(_ => Enumerable.Range(0, 67 * 32)
                .Select(_ => (float)(random.NextDouble() - .5)).ToArray()).ToArray();
            (float[] Output, float[][] Gradients) Run(bool optimized)
            {
                var tensors = inputs.Select(x => new Tensor(67, 32, (float[])x.Clone(), true)).ToArray();
                var graph = new TensorGraph(true, optimized);
                var output = graph.Attention(tensors[0], tensors[1], tensors[2], 8, causal);
                for (var row = 0; row < output.Rows; row++) graph.CrossEntropy(output, row, row % 32);
                graph.Backward();
                return (output.Data, tensors.Select(x => x.Gradient!).ToArray());
            }
            var fast = Run(true);
            var reference = Run(false);
            Assert(fast.Output.Zip(reference.Output).All(x => Math.Abs(x.First - x.Second) < 2e-6), "Parallel attention forward differs from scalar reference.");
            for (var i = 0; i < 3; i++)
                Assert(fast.Gradients[i].Zip(reference.Gradients[i]).All(x => Math.Abs(x.First - x.Second) < 2e-6), "Parallel attention gradient differs from scalar reference.");
            var replay = Run(true);
            Assert(fast.Output.SequenceEqual(replay.Output) && fast.Gradients.Zip(replay.Gradients).All(x => x.First.SequenceEqual(x.Second)),
                "Parallel attention is not deterministic.");
        }
    }

    private static void ScratchLifetimeParity()
    {
        var vocabulary = new WordVocabulary(["ALPHA", "BETA"], ["ALPHA", "BETA"]);
        var model = new ContextualNetwork(new() { Width = 32, Heads = 8, EncoderLayers = 2, DecoderLayers = 2, FeedForwardWidth = 128 },
            vocabulary, new("SCRATCH_TEST", []));
        var tokens = Enumerable.Range(0, 128).Select(i => vocabulary.InputId(i % 2 == 0 ? "ALPHA" : "BETA")).ToArray();
        var input = new PackedInput(tokens, new int[tokens.Length], tokens.Select((_, i) => new TokenSource(0, i * 6, 5, InputSegment.Player)).ToArray(),
            [new(0, DialogueRole.Player, string.Join(' ', Enumerable.Repeat("ALPHA", 128)))], Enumerable.Range(0, 128).ToArray(), []);
        float[] Run(bool pooled)
        {
            using var scratch = pooled ? new InferenceScratch() : null;
            var parameters = model.Parameters();
            var encoded = model.Encode(new TensorGraph(false), parameters, input);
            var decode = Enumerable.Range(0, 32).Select(i => tokens[i]).Prepend(Tokenizer.Bos).ToArray();
            var full = model.Decode(new TensorGraph(false), parameters, decode, encoded);
            var cache = model.CreateDecoderSession(parameters, encoded);
            for (var row = 0; row < decode.Length; row++)
            {
                var incremental = cache.Next(decode[row]);
                Assert(incremental.Data.Zip(full.Data.Skip(row * full.Columns).Take(full.Columns))
                    .All(x => Math.Abs(x.First - x.Second) < 2e-5), "Scratch reuse corrupted decoder cache.");
            }
            return full.Data.ToArray();
        }
        var expected = Run(false);
        Assert(Run(true).SequenceEqual(expected) && Run(true).SequenceEqual(expected), "Pooled inference differs from unpooled inference.");
        var concurrent = Enumerable.Range(0, 4).AsParallel().Select(_ => Run(true)).ToArray();
        Assert(concurrent.All(x => x.SequenceEqual(expected)), "Concurrent scratch scopes share active buffers.");
    }

    private static void ParallelTrainingParity()
    {
        var request = Request("HELLO FRIEND");
        var model = new ContextualNetwork(new() { Width = 8, Heads = 2, EncoderLayers = 1, DecoderLayers = 1, FeedForwardWidth = 16 },
            new WordVocabulary(["HELLO", "FRIEND"], ["HELLO", "FRIEND"]), new DialogueDomainDefinition("EMPTY", []));
        var example = new TrainingExample("HELLO FRIEND", "HELLO FRIEND", request.Utterances.ToArray(), [SpeechAct.Greet], [DialogueDomain.Social], [],
            UserAffect.Neutral, DialogueStance.Neutral, ResponsePolicy.Answer, [], [], "NONE", "ACKNOWLEDGE", KnowledgeTarget.None,
            "PROJECT_TEST", "PARALLEL", new HashSet<string> { "domains" }, DiscourseFrame.Empty, [], [], DiscourseResponseAction.None, [], null)
        { Request = request, Response = "HELLO FRIEND" };
        foreach (var workers in new[] { 3, 6 })
        {
            var single = new ContextualTrainer(model, 100000);
            var parallel = new ContextualTrainer(model, 100000, sampleWorkers: workers);
            foreach (var phase in Enum.GetValues<ContextualPhase>().Concat(Enum.GetValues<ContextualPhase>()))
            {
                // Seven samples also exercise the partially filled final worker group.
                var examples = Enumerable.Repeat(example, 7).ToArray();
                var loss = single.TrainBatch(examples, phase);
                Assert(loss == parallel.TrainBatch(examples, phase), "Parallel training loss changed.");
                Assert(single.Random.State == parallel.Random.State, "Parallel masking changed RNG position.");
                Assert(single.Parameters.All(x => x.Value.Data.SequenceEqual(parallel.Parameters[x.Key].Data)), "Parallel training changed weights.");
                Assert(single.FirstMoments.All(x => x.Value.SequenceEqual(parallel.FirstMoments[x.Key])) &&
                    single.SecondMoments.All(x => x.Value.SequenceEqual(parallel.SecondMoments[x.Key])), "Parallel training changed optimizer state.");
            }
        }
    }

    private static void TransposedProjection()
    {
        foreach (var (rows, width, vocabulary) in new[] { (1, 7, 11), (5, 32, 37), (7, 64, 4096) })
        {
            var random = new Random(42);
            var a = new Tensor(rows, width, Enumerable.Range(0, rows * width).Select(_ => (float)(random.NextDouble() - .5)).ToArray(), true);
            var b = new Tensor(vocabulary, width, Enumerable.Range(0, vocabulary * width).Select(_ => (float)(random.NextDouble() - .5)).ToArray(), true);
            var reference = new TensorGraph(true, false);
            var expected = reference.MatMul(a, reference.Transpose(b));
            for (var row = 0; row < rows; row++) reference.CrossEntropy(expected, row, row % vocabulary);
            reference.Backward();
            var da = a.Gradient!.ToArray(); var db = b.Gradient!.ToArray();
            Array.Clear(a.Gradient!); Array.Clear(b.Gradient!);
            var graph = new TensorGraph(true);
            var actual = graph.MatMulRightTranspose(a, b);
            // A tied embedding also receives gradients through its input gather.
            var gathered = graph.Gather(b, [0, 0]);
            graph.CrossEntropy(gathered, 0, 1);
            graph.CrossEntropy(gathered, 1, 2);
            for (var row = 0; row < rows; row++) graph.CrossEntropy(actual, row, row % vocabulary);
            graph.Backward();
            var gatherGraph = new TensorGraph(true, false);
            var isolated = new Tensor(b.Rows, b.Columns, b.Data, true);
            var isolatedGather = gatherGraph.Gather(isolated, [0, 0]);
            gatherGraph.CrossEntropy(isolatedGather, 0, 1); gatherGraph.CrossEntropy(isolatedGather, 1, 2);
            gatherGraph.Backward();
            Assert(actual.Data.Zip(expected.Data).All(p => Math.Abs(p.First - p.Second) < 2e-5), "Transposed projection forward mismatch.");
            Assert(a.Gradient!.Zip(da).All(p => Math.Abs(p.First - p.Second) < 2e-5), "Transposed projection input gradient mismatch.");
            Assert(b.Gradient!.Select((v, i) => Math.Abs(v - db[i] - isolated.Gradient![i])).Max() < 2e-5, "Tied embedding gradient accumulation mismatch.");
        }
    }

    private static void MatrixKernels()
    {
        foreach (var (rows, inner, columns) in new[] { (1, 23, 37), (7, 29, 35), (16, 64, 32), (16, 32, 43), (64, 384, 512) })
        {
            var random = new Random(42);
            var a = new Tensor(rows, inner, Enumerable.Range(0, rows * inner).Select(_ => (float)(random.NextDouble() - .5)).ToArray(), true);
            var b = new Tensor(inner, columns, Enumerable.Range(0, inner * columns).Select(_ => (float)(random.NextDouble() - .5)).ToArray(), true);
            var referenceGraph = new TensorGraph(true, false);
            var expected = referenceGraph.MatMul(a, b);
            referenceGraph.CrossEntropy(expected, rows - 1, columns - 1);
            referenceGraph.Backward();
            var expectedA = a.Gradient!.ToArray(); var expectedB = b.Gradient!.ToArray();
            Array.Clear(a.Gradient!); Array.Clear(b.Gradient!);
            var fastGraph = new TensorGraph(true);
            var actual = fastGraph.MatMul(a, b);
            fastGraph.CrossEntropy(actual, rows - 1, columns - 1);
            fastGraph.Backward();
            Assert(expected.Data.Zip(actual.Data).All(p => Math.Abs(p.First - p.Second) <= 2e-4 * (1 + Math.Abs(p.First))), "Blocked matrix forward disagrees with scalar reference.");
            Assert(expectedA.Zip(a.Gradient!).All(p => Math.Abs(p.First - p.Second) < 2e-5), "Blocked matrix left gradient disagrees with reference.");
            Assert(expectedB.Zip(b.Gradient!).All(p => Math.Abs(p.First - p.Second) < 2e-5), "Blocked matrix right gradient disagrees with reference.");
            var replay = new float[rows * columns];
            TensorKernels.Multiply(a.Data, b.Data, replay, rows, inner, columns);
            Assert(replay.SequenceEqual(actual.Data), "Parallel matrix execution is nondeterministic.");
        }
    }

    private static void NetworkGradients()
    {
        var vocab = new WordVocabulary(["HELLO", "FRIEND", "WAIT"], ["HELLO", "FRIEND", "WAIT"]);
        var model = new ContextualNetwork(new ContextualModelConfig { EncoderLayers = 1, DecoderLayers = 1, Width = 4, Heads = 2, FeedForwardWidth = 8, ContextLength = 32, MaximumOutputTokens = 4 }, vocab, new DialogueDomainDefinition("EMPTY", []));
        var parameters = model.Parameters(true);
        var input = new PackedInput([vocab.InputId("HELLO"), vocab.InputId("FRIEND")], [0, 0],
            [new(0, 0, 5, InputSegment.Player), new(0, 6, 6, InputSegment.Player)], [new(0, DialogueRole.Player, "HELLO FRIEND")], [0, 1], []);
        float Forward(bool training)
        {
            var g = new TensorGraph(training, false);
            var encoded = model.Encode(g, parameters, input);
            var logits = model.Decode(g, parameters, [Tokenizer.Bos, vocab.InputId("HELLO")], encoded);
            var loss = g.CrossEntropy(logits, 1, vocab.OutputId(vocab.InputId("FRIEND")));
            if (training) g.Backward();
            return loss;
        }
        Forward(true);
        foreach (var name in new[] { "encoder.embedding", "encoder.layer0.self.q", "encoder.layer0.in", "decoder.layer0.cross.k", "decoder.layer0.cross.v", "decoder.output" })
        {
            var p = parameters[name];
            var index = Enumerable.Range(0, p.Data.Length).MaxBy(i => Math.Abs(p.Gradient![i]));
            var original = p.Data[index];
            p.Data[index] = original + .001f;
            var plus = Forward(false);
            p.Data[index] = original - .001f;
            var minus = Forward(false);
            p.Data[index] = original;
            Assert(Math.Abs((plus - minus) / .002f - p.Gradient![index]) < .025f, "Network finite difference mismatch: " + name);
        }
        Assert(parameters["encoder.embedding"].Gradient!.Any(x => Math.Abs(x) > 1e-5), "Decoder loss cannot reach encoder.");
        Assert(model.Parameters().Values.All(x => x.Gradient is null), "Inference allocated gradients.");
        var inferenceGraph = new TensorGraph(false);
        var encoded = model.Encode(inferenceGraph, parameters, input);
        var decodeTokens = new[] { Tokenizer.Bos, vocab.InputId("HELLO"), vocab.InputId("FRIEND") };
        var full = model.Decode(inferenceGraph, parameters, decodeTokens, encoded);
        var cache = model.CreateDecoderSession(parameters, encoded);
        for (var row = 0; row < decodeTokens.Length; row++)
        {
            var incremental = cache.Next(decodeTokens[row]);
            Assert(incremental.Data.Select((x, i) => Math.Abs(x - full.Data[row * full.Columns + i])).Max() < 1e-4,
                "Cached decoder diverges from full causal decoding.");
        }
    }

    internal static ReplyRequest Request(string text) => new("ARCHITECTURE", "1", [new(0, DialogueRole.Player, text)], NpcDialogueState.Initial, NpcPersona.Default, PlayerConversationProfile.Empty, 1, 42);
    private static void StructuredBoundaries()
    {
        var domain = DemoDialogueDomains.Observatory;
        var tokenizer = new DialogueTokenizer(new WordVocabulary(["NPC", "PLAYER", "HELLO"], ["HELLO"]));
        var request = Request("NPC PLAYER ZQXTRON") with
        {
            Utterances = [new(0, DialogueRole.Npc, "PLAYER NPC"), new(1, DialogueRole.Player, "NPC PLAYER ZQXTRON")],
            ResponseSequence = 2
        };
        var packed = StructuredInput.Pack(request, tokenizer, 512, [], domain);
        Assert(packed.Sources.Where(s => s.Utterance == 0).All(s => s.Segment == InputSegment.Npc), "Literal role words changed structural NPC role.");
        Assert(packed.Sources.Where(s => s.Utterance == 1).All(s => s.Segment == InputSegment.Player), "Literal role words changed structural player role.");
        Assert(packed.Sources.Where(s => s.Utterance == 1 && s.Start == 11).All(s => s.Length == 7), "OOV fallback lost exact word offsets.");
        try { StructuredInput.Pack(Request(string.Join(' ', Enumerable.Repeat("ZQXTRON", 100))), tokenizer, 512, [], domain); throw new InvalidOperationException("Oversized current turn was silently truncated."); }
        catch (ArgumentException) { }
        var source = new DialogueFact(DialogueParticipant.Player, DialogueFactKind.Home, "NORTH TOWER", false, 0, 1, DialogueFactProvenance.SessionReported);
        var other = source with { Subject = DialogueParticipant.Npc, Value = "SOUTH TOWER" };
        var correction = new DiscourseFrame(DiscourseAct.Correct, DialogueParticipant.Player, DialogueParticipant.Npc, DialogueFactKind.Home,
            new("EAST TOWER", 0, 10), false, 0, 1, "TEST");
        var facts = DialogueStateReducer.ReduceFacts([source, other], correction, 2);
        Assert(facts.Contains(other) && facts.Single(f => f.Subject == DialogueParticipant.Player).Value == "EAST TOWER", "Correction changed another participant's fact.");
        var agenda = DialogueStateReducer.ReduceAgendaPlan([new(AgendaKind.UnansweredQuestion, "HOME", 0, AgendaStatus.Active)],
            [new(AgendaKind.UnansweredQuestion, "HOME", 0, AgendaStatus.Completed)], [new(DialogueResponseAct.Answer)], [], null, null);
        Assert(agenda.Single().Status == AgendaStatus.Completed, "An answered agenda entry was not retired.");
    }

    private static void DomainPlanning()
    {
        var domain = DemoDialogueDomains.Observatory;
        var input = "OBSERVE THE SKY.";
        var vocabulary = new WordVocabulary(Tokenizer.Lex(input + " THE SKY IS CLEAR. NAME ROLE ORIGIN HOME FAMILY OCCUPATION FACTION TRAITS UNKNOWN MOOD RAPPORT TRUST GOALS PENDING NEUTRAL TRAVELER")
            .Where(t => t.Kind == LexicalTokenKind.Word).Select(t => t.Text), ["THE", "SKY", "IS", "CLEAR"]);
        var config = new ContextualModelConfig { EncoderLayers = 1, DecoderLayers = 1, Width = 8, Heads = 2, FeedForwardWidth = 16, ContextLength = 512 };
        var model = new ContextualNetwork(config, vocabulary, domain);
        var frame = new SemanticFrame(0, input.Length, SpeechAct.Request, DialogueParticipant.Player, DialogueParticipant.Npc,
            "OBSERVE_SKY", [], null, ActionStatus.Affirmative, 1);
        var example = new TrainingExample(input, input, Request(input).Utterances.ToArray(), [SpeechAct.Request], [DialogueDomain.Environment], [],
            UserAffect.Neutral, DialogueStance.Neutral, ResponsePolicy.ExecuteTool, [], [], "OBSERVE_SKY", "ACKNOWLEDGE", KnowledgeTarget.None,
            "PROJECT_OBSERVATORY", "OBSERVATORY", new HashSet<string>(), DiscourseFrame.Empty, [], [], DiscourseResponseAction.None, [], null)
        { Request = Request(input), Contextual = new([frame], [new(DialogueResponseAct.ExecuteTool, 0)]) };
        var trainer = new ContextualTrainer(model, 100_000);
        for (var i = 0; i < 1800; i++) trainer.TrainBatch([example], ContextualPhase.JointUnderstanding);
        var inference = trainer.Snapshot();
        var before = inference.Parameters().ToDictionary(x => x.Key, x => x.Value.Data.ToArray());
        var brain = Brain.CreateContextualForTesting(inference, new Dictionary<string, double> { ["OBSERVE_SKY"] = 0 });
        var tool = new ObservatoryTool();
        var registry = new GameToolRegistry([tool]);
        var reply = brain.Reply(Request(input), registry);
        Assert(reply.Diagnostics.ToolInvocation?.ToolName == "OBSERVE_SKY" && reply.Text == "THE SKY IS CLEAR.", "Distinct domain did not learn its semantic plan: " + reply.Text);
        var concurrent = Enumerable.Range(0, 8).AsParallel().Select(_ => brain.Reply(Request(input), registry)).ToArray();
        Assert(concurrent.All(r => r.Text == reply.Text && r.Diagnostics.ToolInvocation?.IdempotencyKey == reply.Diagnostics.ToolInvocation!.IdempotencyKey), "Concurrent contextual replies diverged.");
        Assert(brain.Reply(Request(input), GameToolRegistry.Empty).Diagnostics.ToolInvocation is null, "Unregistered capability executed.");
        Assert(brain.Reply(Request(input), DemoGameTools.CreateMerchant()).Diagnostics.ToolInvocation is null, "Observatory model invoked a merchant capability.");
        var weights = inference.Parameters();
        Assert(weights.All(p => p.Value.Data.SequenceEqual(before[p.Key]) && p.Value.Gradient is null), "Inference modified weights or allocated optimizer state.");
        Console.WriteLine("PASS DISTINCT OBSERVATORY DOMAIN LEARNING AND CONCURRENT CONTEXTUAL REPLIES");
    }

    private sealed class ObservatoryTool : IGameTool
    {
        public ToolSchema Schema => DemoDialogueDomains.Observatory.Tools[0].Schema;
        public GameToolResult Execute(GameToolInvocation invocation) => new(true, new Dictionary<string, string> { ["PHASE"] = "CLEAR" });
    }
    private static void ContextualLearningAndResume()
    {
        var sentences = new[] { "WE ARE DISCUSSING SPELLS.", "WE ARE DISCUSSING MERCHANTS.", "TELL ME MORE.", "THAT SOUNDS INTERESTING." };
        var structural = "NAME ROLE ORIGIN HOME FAMILY OCCUPATION FACTION TRAITS UNKNOWN MOOD RAPPORT TRUST GOALS PENDING SUBJECT PREDICATE VALUE SOURCE PROVENANCE PLAYER NPC NONE NEUTRAL TRAVELER";
        var words = Tokenizer.Lex(string.Join(' ', sentences) + " " + structural).Where(x => x.Kind == LexicalTokenKind.Word).Select(x => x.Text).Distinct().ToArray();
        var vocabulary = new WordVocabulary(words, words);
        var domain = new DialogueDomainDefinition("TEST_DOMAIN", []);
        var config = new ContextualModelConfig { Width = 8, Heads = 2, FeedForwardWidth = 16, EncoderLayers = 1, DecoderLayers = 1, ContextLength = 512, MaximumOutputTokens = 16 };
        var model = new ContextualNetwork(config, vocabulary, domain);
        var examples = new[] { Example(0, DialogueDomain.Magic), Example(1, DialogueDomain.TradeEconomy) };
        var trainer = new ContextualTrainer(model, 100_000);
        var initial = examples.Sum(x => trainer.Loss(x, ContextualPhase.JointUnderstanding, false));
        for (var i = 0; i < 2500; i++) trainer.TrainBatch(examples, ContextualPhase.JointUnderstanding);
        var final = examples.Sum(x => trainer.Loss(x, ContextualPhase.JointUnderstanding, false));
        Assert(final < initial * .65, $"Contextual loss did not improve: {initial} -> {final}");
        foreach (var example in examples)
        {
            var packed = StructuredInput.Pack(example.Request!, model.Tokenizer, 512, [], domain);
            var prediction = model.Understand(new TensorGraph(false), trainer.Parameters, packed);
            Assert(ContextualNetwork.ArgMax(prediction.Heads["domains"]) == (int)example.Domains[0], $"Identical current input did not learn distinct historical meanings: {initial} -> {final}, expected {example.Domains[0]}, predicted {(DialogueDomain)ContextualNetwork.ArgMax(prediction.Heads["domains"])}.");
        }
        var snapshot = trainer.Parameters.ToDictionary(x => x.Key, x => (float[])x.Value.Data.Clone());
        trainer.TrainBatch([examples[0]], ContextualPhase.DecoderPolish);
        Assert(trainer.Parameters.Where(x => !x.Key.StartsWith("decoder.", StringComparison.Ordinal)).All(x => x.Value.Data.SequenceEqual(snapshot[x.Key])), "Decoder polish changed understanding weights.");
        var directory = Path.Combine(Path.GetTempPath(), "FishbrainContextualTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "training.fbm");
            ContextualCheckpoint.Save(path, trainer.Snapshot(), "TEST", trainer.Step, new Dictionary<string, double>(), trainer);
            var resumed = ContextualTrainer.Restore(ContextualCheckpoint.Load(path, domain, true), sampleWorkers: 3);
            trainer.TrainBatch(examples, ContextualPhase.JointUnderstanding);
            resumed.TrainBatch(examples, ContextualPhase.JointUnderstanding);
            Assert(trainer.Step == resumed.Step && trainer.Parameters.All(x => x.Value.Data.SequenceEqual(resumed.Parameters[x.Key].Data)), "Training resume is not bit equivalent.");
            var inferencePath = Path.Combine(directory, "inference.fbm");
            ContextualCheckpoint.Save(inferencePath, trainer.Snapshot(), "TEST", trainer.Step, new Dictionary<string, double>());
            var brain = Brain.Load(inferencePath);
            Assert(brain.Domain.Fingerprint == domain.Fingerprint, "Embedded domain did not round trip.");
            var reply = brain.Reply(examples[0].Request! with { ResponseMode = ResponseMode.DeterministicOnly }, GameToolRegistry.Empty);
            Assert(reply.Contextual is not null && reply.Diagnostics.ToolInvocation is null, "Loaded model did not use contextual inference.");
            var corrupt = File.ReadAllBytes(inferencePath); corrupt[^40] ^= 1; File.WriteAllBytes(inferencePath, corrupt);
            try { Brain.Load(inferencePath, domain); throw new InvalidOperationException("Corrupt contextual checkpoint accepted."); }
            catch (InvalidDataException) { }
        }
        finally { Directory.Delete(directory, true); }
        Console.WriteLine($"PASS CONTEXTUAL HISTORY LEARNING LOSS {initial:F4} -> {final:F4}, FROZEN POLISH, BIT-EQUIVALENT RESUME");

        TrainingExample Example(int index, DialogueDomain expected)
        {
            var request = Request(sentences[2]) with { Utterances = [new(0, DialogueRole.Npc, sentences[index]), new(1, DialogueRole.Player, sentences[2])], ResponseSequence = 2 };
            return new TrainingExample(sentences[index] + sentences[2], sentences[2], request.Utterances.ToArray(), [SpeechAct.Ask], [expected], [],
                UserAffect.Neutral, DialogueStance.Neutral, ResponsePolicy.Answer, [], [], "NONE", "ACKNOWLEDGE", KnowledgeTarget.None,
                "PROJECT_TEST", "HISTORY_" + index, new HashSet<string> { "domains" }, DiscourseFrame.Empty, [], [], DiscourseResponseAction.None, [], null)
            { Request = request, Response = sentences[3] };
        }
    }
    internal static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
