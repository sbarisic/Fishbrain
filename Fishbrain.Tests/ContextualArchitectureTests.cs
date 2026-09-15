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
            var brain = Brain.CreateForTesting(new BrainConfig { EmbeddingSize = 8, HeadCount = 2, MlpSize = 12, ContextLength = 128, AttentionWindow = 128, PositionPeriod = 128 });
            var result = brain.Reply(Request(input), DemoGameTools.CreateMerchant(world));
            Assert(result.Diagnostics.ToolInvocation is null && world.Balance == 100, $"Unintended mutation: {input}");
        }
        foreach (var text in new[] { "I GAVE YOU TEN GOLD.", "YOU HAVE TEN COINS.", "MY HOMETOWN IS MOONBASE.", "I AM AN ASSASSIN." })
            Assert(!ConversationalOutputValidator.IsSafe(text, NpcPersona.Default, [], out _), "Unsupported claim passed: " + text);
        var fact = new DialogueFact(DialogueParticipant.Npc, DialogueFactKind.Home, "NORTH ROAD", false, 2, 1, DialogueFactProvenance.SessionReported);
        var packedFact = StructuredInput.FactText(fact);
        Assert(packedFact.Contains("SUBJECT Npc") && packedFact.Contains("SessionReported"), "Fact ownership lost.");
        AttentionGradients(false);
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
        var agenda = DialogueStateReducer.ReduceAgenda([new(AgendaKind.UnansweredQuestion, "HOME", 0, AgendaStatus.Active)],
            new(2, DialogueRole.Player, "EAST TOWER"), [new(DialogueResponseAct.Answer)], AgendaKind.UnansweredQuestion, false,
            AgendaStatus.Completed, correction, null, null);
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
            var resumed = ContextualCheckpoint.Load(path, domain, true).Trainer!;
            trainer.TrainBatch(examples, ContextualPhase.JointUnderstanding);
            resumed.TrainBatch(examples, ContextualPhase.JointUnderstanding);
            Assert(trainer.Step == resumed.Step && trainer.Parameters.All(x => x.Value.Data.SequenceEqual(resumed.Parameters[x.Key].Data)), "Training resume is not bit equivalent.");
            var inferencePath = Path.Combine(directory, "inference.fbm");
            ContextualCheckpoint.Save(inferencePath, trainer.Snapshot(), "TEST", trainer.Step, new Dictionary<string, double>());
            var brain = Brain.Load(inferencePath, domain);
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
