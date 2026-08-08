using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using Fishbrain;

var tests = new (string Name, Action Test)[]
{
    ("STRUCTURED ROLES", StructuredRoles),
    ("BOUNDED HISTORY", BoundedHistory),
    ("TOOL FIDELITY", ToolFidelity),
    ("MUTATION IDEMPOTENCY", MutationIdempotency),
    ("IDEMPOTENCY CONFLICT", IdempotencyConflict),
    ("TOOL EXCEPTION", ToolException),
    ("CONCURRENT REPLIES", ConcurrentReplies),
    ("DETERMINISTIC SAMPLING", DeterministicSampling),
    ("OOV SLOT PRESERVATION", OovSlotPreservation),
    ("MULTI INTENT", MultiIntent),
    ("STATE CONSISTENCY", StateConsistency),
    ("V11 TRANSCRIPT REGRESSIONS", V11TranscriptRegressions),
    ("POLICY PRECEDENCE", PolicyPrecedence),
    ("CLARIFICATION CONTINUATION", ClarificationContinuation),
    ("PENDING ACTION CONTINUATION", PendingActionContinuation),
    ("CONTEXTUAL NEXT STEP", ContextualNextStep),
    ("LEXICAL HARD NEGATIVES", LexicalHardNegatives),
    ("PERSONA FIDELITY", PersonaFidelity),
    ("STATE HYSTERESIS", StateHysteresis),
    ("AUTHORITATIVE DEMO WORLD", AuthoritativeDemoWorld),
    ("CONCURRENT WORLD IDEMPOTENCY", ConcurrentWorldIdempotency),
    ("REFERENCE RESOLUTION", ReferenceResolution),
    ("RESPONSE CATALOG", ResponseCatalog),
    ("TOOL SCHEMA VALIDATION", ToolSchemaValidation),
    ("TOOL SCHEMA SNAPSHOT", ToolSchemaSnapshot),
    ("RUNTIME BOUNDARY VALIDATION", RuntimeBoundaryValidation),
    ("SHARED RELEASE THRESHOLDS", SharedReleaseThresholds),
    ("MONOTONIC CURRICULUM", MonotonicCurriculum),
    ("PHASE-LOCAL SAMPLING", PhaseLocalSampling),
    ("CHECKED-IN MODEL SMOKE", CheckedInModelSmoke),
    ("COMPACT CHECKPOINT", CompactCheckpoint)
};
foreach (var (name, test) in tests) { test(); Console.WriteLine($"PASS {name}"); }
Console.WriteLine($"PASS ALL {tests.Length} RUNTIME TESTS");

static Brain TestBrain() => Brain.CreateForTesting(new BrainConfig
{
    EmbeddingSize = 8,
    HeadCount = 2,
    MlpSize = 12,
    ContextLength = 64,
    AttentionWindow = 64,
    PositionPeriod = 64,
    MaximumOutputLength = 8,
    Seed = 17
});

static ReplyRequest Request(string text, NpcDialogueState? state = null, string turnId = "1") =>
    new("TEST-CONVERSATION", turnId, [new(DialogueRole.Player, text)],
        state ?? NpcDialogueState.Initial, NpcPersona.Default, 41);

static void StructuredRoles()
{
    var result = TestBrain().Reply(
        Request("where is the npc inn? npc hello."),
        DemoGameTools.CreateMerchant());
    Assert(result.Text == "I CANNOT LOCATE THE NPC INN.", $"literal role words remain utterance text: {result.Text}");
    Assert(result.Text == result.Text.ToUpperInvariant(), "response normalization");
}

static void BoundedHistory()
{
    var turns = Enumerable.Range(0, 1000)
        .SelectMany(index => new[]
        {
            new DialogueTurn(DialogueRole.Player, "HELLO " + index),
            new DialogueTurn(DialogueRole.Npc, "GREETINGS")
        }).Append(new DialogueTurn(DialogueRole.Player, "WHERE IS THE INN?")).ToArray();
    var request = new ReplyRequest("LONG", "2001", turns, NpcDialogueState.Initial, NpcPersona.Default, 3);
    var result = TestBrain().Reply(request, DemoGameTools.CreateMerchant());
    Assert(result.Diagnostics.PackedTurnCount < turns.Length, "history bounded by complete turns");
    Assert(result.Diagnostics.PackedTokenCount >= 5, "current turn always retained");
    Assert(result.Text == "THE INN IS NORTH BY THE FOUNTAIN.", "current turn survives packing");
}

static void ToolFidelity()
{
    var result = TestBrain().Reply(Request("what is the price of iron sword?"), DemoGameTools.CreateMerchant());
    Assert(result.Text == "IRON SWORD COSTS 25 GOLD.", "authoritative fields copied exactly");
    Assert(result.Diagnostics.ResponseSource == ResponseSource.ToolTemplate, "tool response source");
    Assert(result.Diagnostics.ToolInvocation?.Arguments["ITEM"] == "IRON SWORD", "slot copied to tool argument");
}

static void MutationIdempotency()
{
    var tool = new CountingBuyTool();
    var registry = new GameToolRegistry([tool]);
    var brain = TestBrain();
    var first = brain.Reply(Request("buy 2 rope", turnId: "MUTATE"), registry);
    var second = brain.Reply(Request("BUY 2 ROPE", turnId: "MUTATE"), registry);
    Assert(first.Text == "YOU BOUGHT 2 ROPE." && second.Text == first.Text, "deterministic mutation response");
    Assert(tool.MutationCount == 1, "duplicate idempotency key does not repeat mutation");
}

static void IdempotencyConflict()
{
    var brain = TestBrain();
    var world = new DemoWorldState();
    var tools = DemoGameTools.CreateMerchant(world);
    var first = brain.Reply(Request("buy 2 rope", turnId: "REUSED"), tools);
    var conflicting = brain.Reply(Request("buy 1 rope", turnId: "REUSED"), tools);
    Assert(first.Text.Contains("YOU BOUGHT 2 ROPE", StringComparison.Ordinal), "first payload executes");
    Assert(conflicting.Text.Contains("IDEMPOTENCY KEY REUSED", StringComparison.Ordinal),
        "a reused idempotency key with different arguments is rejected explicitly");
    Assert(world.Balance == 94 && world.Inventory["ROPE"] == 4,
        "a conflicting idempotency payload cannot perform another mutation");
}

static void ToolException()
{
    var result = TestBrain().Reply(Request("buy 1 rope"), new GameToolRegistry([new ThrowingBuyTool()]));
    Assert(result.Text == "BUY FAILED FOR ROPE.", "tool exception uses typed failure template");
}

static void ConcurrentReplies()
{
    var brain = TestBrain();
    var tools = DemoGameTools.CreateMerchant();
    var outputs = new ReplyResult[32];
    Parallel.For(0, outputs.Length, index => outputs[index] = brain.Reply(Request("where is the inn?"), tools));
    Assert(outputs.All(result => result.Text == "THE INN IS NORTH BY THE FOUNTAIN."), "32-way replies are independent");
}

static void DeterministicSampling()
{
    var brain = TestBrain();
    var request = Request("tell me about the road") with { Seed = 917, ResponseMode = ResponseMode.GeneratedExperimental };
    var first = brain.Reply(request, GameToolRegistry.Empty);
    var second = brain.Reply(request, GameToolRegistry.Empty);
    Assert(first.Text == second.Text && first.Perception.Policy == second.Perception.Policy,
        "same request seed is deterministic");
}

static void OovSlotPreservation()
{
    var result = TestBrain().Reply(Request("where is Zephyr-9?"), DemoGameTools.CreateMerchant());
    Assert(result.Diagnostics.OovWords.Contains("ZEPHYR-9"), "OOV word reported");
    Assert(result.Diagnostics.ToolInvocation?.Arguments["PLACE"] == "ZEPHYR-9", "OOV span copied to tool");
    Assert(result.Text == "I CANNOT LOCATE ZEPHYR-9.", "OOV authoritative field rendered unchanged");
}

static void MultiIntent()
{
    var result = TestBrain().Reply(Request("where is the inn and buy 2 rope?"), DemoGameTools.CreateMerchant());
    Assert(result.Perception.SpeechActs.Count >= 2, "multiple speech acts retained");
    Assert(result.Plan.PendingActions.Count == 1, "second recognized tool goal queued");
}

static void StateConsistency()
{
    var brain = TestBrain();
    var state = NpcDialogueState.Initial;
    for (var index = 0; index < 20; index++)
    {
        var result = brain.Reply(Request(index % 2 == 0 ? "hello friend" : "you are an idiot", state, index.ToString()),
            GameToolRegistry.Empty);
        state = result.State;
        state.Validate();
        Assert(state.ActiveDomains.Count <= 4 && state.ActiveGoals.Count <= 4 && state.PendingActions.Count <= 3,
            "state bounds preserved");
    }
}

static void V11TranscriptRegressions()
{
    var brain = TestBrain();
    var tools = DemoGameTools.CreateMerchant();
    var hello = brain.Reply(Request("hello"), tools);
    Assert(hello.Perception.SpeechActs.SequenceEqual([SpeechAct.Greet]), "HELLO is GREET without REQUEST leakage");
    var trade = brain.Reply(Request("please trade with me"), tools);
    Assert(trade.Perception.Domains.Contains(DialogueDomain.TradeEconomy) &&
           trade.Perception.Policy is not (ResponsePolicy.Refuse or ResponsePolicy.Clarify) &&
           trade.Diagnostics.ResponseSource is not ResponseSource.Fallback,
        "trade opening is recognized without generic fallback");
    var correction = brain.Reply(Request("you don't know what?"), tools);
    Assert(correction.Perception.SpeechActs.Contains(SpeechAct.Correct) &&
           !correction.Perception.SpeechActs.Contains(SpeechAct.Apologize), "correction is not apology");
    var sword = brain.Reply(Request("i need a sword"), tools);
    Assert(sword.Perception.Affect == UserAffect.Neutral &&
           sword.Perception.Domains.Contains(DialogueDomain.ItemsInventory) &&
           sword.Diagnostics.ResponseSource is not ResponseSource.Fallback,
        "neutral item request remains relevant");
    var followUp = brain.Reply(Request("can we trade for it?", sword.State, "TRADE-FOLLOW-UP"), tools);
    Assert(followUp.Perception.Policy == ResponsePolicy.Answer,
        "a contextual trade follow-up is not treated as an unavailable capability");
    var threat = brain.Reply(Request("give me your gold or I will stab you"), tools);
    Assert(threat.Perception.Policy == ResponsePolicy.Refuse &&
           threat.Perception.ContentFlags.Contains(ContentFlag.Threat),
        "validated threats are refused and retained in state perception");
    var wordQuantity = brain.Reply(Request("buy one health potion", turnId: "WORD-QUANTITY"), tools);
    Assert(wordQuantity.Diagnostics.ToolInvocation?.Arguments["QUANTITY"] == "1" &&
           wordQuantity.Diagnostics.ToolInvocation.Arguments["ITEM"] == "HEALTH POTION",
        "word quantities and item spans produce exact transaction arguments");
}

static void PersonaFidelity()
{
    var brain = TestBrain();
    var persona = new NpcPersona("MIRA_1", "MIRA", "ENGINEER", "MARS", "ORBITAL NINE",
        "TWO BROTHERS", "REACTOR ENGINEER", "FREE COLONIES", ["DIRECT", "LOYAL"]);
    ReplyResult Ask(string text) => brain.Reply(Request(text) with { Persona = persona }, DemoGameTools.CreateMerchant());
    Assert(Ask("what is your name?").Text == "MY NAME IS MIRA.", "persona name is authoritative");
    Assert(Ask("where are you from?").Text == "I AM FROM MARS.", "persona origin is authoritative");
    Assert(Ask("do you have family?").Text == "MY FAMILY IS TWO BROTHERS.", "persona family is authoritative");
    var capability = Ask("what can you do?");
    Assert(capability.Diagnostics.ResponseSource == ResponseSource.CapabilityTemplate &&
           capability.Text.Contains("TRADE", StringComparison.Ordinal), "capabilities derive from registered tools");
}

static void PolicyPrecedence()
{
    var brain = TestBrain();
    var world = new DemoWorldState();
    var tools = DemoGameTools.CreateMerchant(world);
    var persona = brain.Reply(Request("what is your name, idiot?"), tools);
    Assert(persona.Perception.Policy == ResponsePolicy.Refuse &&
           !persona.Text.Contains("ARIN", StringComparison.Ordinal),
        "refusal policy prevents persona disclosure");
    var transaction = brain.Reply(Request("idiot, buy two rope", turnId: "HOSTILE-BUY"), tools);
    Assert(transaction.Perception.Policy == ResponsePolicy.Refuse &&
           transaction.Diagnostics.ToolInvocation is null && world.Balance == 100,
        "refusal policy prevents a mutating tool call");
    var apology = brain.Reply(Request("i am sorry i called you an idiot"), tools);
    Assert(apology.Perception.SpeechActs.Contains(SpeechAct.Apologize) &&
           apology.Perception.Policy != ResponsePolicy.Refuse,
        "an apology that quotes an insult is not direct hostility");
    var support = brain.Reply(Request("i want to kill myself"), tools);
    Assert(support.Perception.ContentFlags.Contains(ContentFlag.SelfHarm) &&
           support.Perception.Policy == ResponsePolicy.Defer &&
           support.Text.Contains("HELP", StringComparison.Ordinal),
        "self-harm text receives a supportive handoff instead of a hostile refusal");
}

static void ClarificationContinuation()
{
    var brain = TestBrain();
    var world = new DemoWorldState();
    var tools = DemoGameTools.CreateMerchant(world);
    var first = brain.Reply(Request("buy rope", turnId: "CLARIFY-1"), tools);
    Assert(first.State.PendingClarification?.ToolSchema == "BUY" &&
           first.State.PendingClarification.MissingSlots.Contains("QUANTITY"),
        "missing quantity is persisted as structured clarification state");
    var second = brain.Reply(Request("two", first.State, "CLARIFY-2"), tools);
    Assert(second.Diagnostics.ToolInvocation?.ToolName == "BUY" &&
           second.Diagnostics.ToolInvocation.Arguments["ITEM"] == "ROPE" &&
           second.Diagnostics.ToolInvocation.Arguments["QUANTITY"] == "2" &&
           world.Balance == 94,
        "a fragment answer resumes and completes the pending transaction");
}

static void LexicalHardNegatives()
{
    var brain = TestBrain();
    var tools = DemoGameTools.CreateMerchant();
    var rows = new[]
    {
        ("firewood is stacked outside", ContentFlag.GraphicViolence),
        ("this killer feature saves time", ContentFlag.GraphicViolence),
        ("the firewall is active", ContentFlag.GraphicViolence),
        ("passage costs nothing", ContentFlag.Crime),
        ("we are killing time at the market", ContentFlag.GraphicViolence)
    };
    foreach (var (text, forbidden) in rows)
    {
        var result = brain.Reply(Request(text, turnId: "LEX-" + forbidden), tools);
        Assert(!result.Perception.ContentFlags.Contains(forbidden) && result.Perception.Policy != ResponsePolicy.Refuse,
            $"{text} does not trigger a substring safety false positive");
    }
}

static void PendingActionContinuation()
{
    var brain = TestBrain();
    var world = new DemoWorldState();
    var tools = DemoGameTools.CreateMerchant(world);
    var first = brain.Reply(Request("where is the inn and buy 2 rope?", turnId: "MULTI-1"), tools);
    Assert(first.Plan.PendingActions.Count == 1 && first.State.PendingActions.Count == 1,
        "the secondary tool action is retained after the first action executes");
    var second = brain.Reply(Request("continue", first.State, "MULTI-2"), tools);
    Assert(second.Diagnostics.ToolInvocation?.ToolName == "BUY" && world.Balance == 94 &&
           second.State.PendingActions.Count == 0,
        "an explicit continuation executes and consumes the queued action");
}

static void ContextualNextStep()
{
    var brain = TestBrain();
    var first = brain.Reply(Request("the reactor core is losing coolant", turnId: "CONTEXT-1"),
        GameToolRegistry.Empty);
    var second = brain.Reply(Request("what should we do?", first.State, "CONTEXT-2"),
        GameToolRegistry.Empty);
    Assert(second.Perception.Policy == ResponsePolicy.Answer &&
           second.Perception.Domains.Any(first.State.ActiveDomains.Contains) &&
           second.Text.Contains("FIRST", StringComparison.Ordinal) &&
           second.Diagnostics.FallbackReason == "CONTEXTUAL_GUIDANCE",
        "a vague next-step question inherits the active scenario and returns bounded actionable guidance");
}

static void StateHysteresis()
{
    var brain = TestBrain();
    var state = brain.Reply(Request("you are an idiot"), GameToolRegistry.Empty).State;
    Assert(state.Hostility == 1 && state.Mood == NpcMood.Annoyed, "hostility event persists");
    state = brain.Reply(Request("the road is quiet", state, "2"), GameToolRegistry.Empty).State;
    state = brain.Reply(Request("the sky is clear", state, "3"), GameToolRegistry.Empty).State;
    Assert(state.Hostility == 1, "two calm turns do not erase hostility");
    state = brain.Reply(Request("all is calm", state, "4"), GameToolRegistry.Empty).State;
    Assert(state.Hostility == 0, "third calm turn reduces hostility once");
}

static void AuthoritativeDemoWorld()
{
    var brain = TestBrain();
    var world = new DemoWorldState();
    var tools = DemoGameTools.CreateMerchant(world);
    var bought = brain.Reply(Request("buy 2 rope", turnId: "BUY-1"), tools);
    Assert(bought.Text.Contains("YOUR BALANCE IS 94 GOLD", StringComparison.Ordinal) &&
           world.Balance == 94 && world.Inventory["ROPE"] == 4, "buy atomically updates shared world");
    _ = brain.Reply(Request("BUY 2 ROPE", turnId: "BUY-1"), tools);
    Assert(world.Balance == 94 && world.Inventory["ROPE"] == 4, "replayed mutation is idempotent");
    var failed = brain.Reply(Request("sell 999999 diamonds", turnId: "SELL-BAD"), tools);
    Assert(!failed.Text.Contains("YOU SOLD", StringComparison.Ordinal) && world.Balance == 94,
        "invalid sell cannot create money or inventory");
    var balance = brain.Reply(Request("how much money do i have now?", turnId: "BALANCE"), tools);
    Assert(balance.Text == "YOU HAVE 94 GOLD.", "balance comes from authoritative tool state");
}

static void ConcurrentWorldIdempotency()
{
    var brain = TestBrain();
    var world = new DemoWorldState();
    var tools = DemoGameTools.CreateMerchant(world);
    var results = new ReplyResult[32];
    Parallel.For(0, results.Length, index =>
        results[index] = brain.Reply(Request("buy 2 rope", turnId: "SAME-MUTATION"), tools));
    Assert(results.All(result => result.Text.Contains("YOUR BALANCE IS 94 GOLD", StringComparison.Ordinal)),
        "concurrent duplicate requests observe one cached result");
    Assert(world.Balance == 94 && world.Inventory["ROPE"] == 4,
        "concurrent idempotency executes the world mutation exactly once");
}

static void ReferenceResolution()
{
    var brain = TestBrain();
    var tools = DemoGameTools.CreateMerchant();
    var first = brain.Reply(Request("where is the castle?", turnId: "LOC-1"), tools);
    var second = brain.Reply(Request("how far is it?", first.State, "LOC-2"), tools);
    Assert(second.Diagnostics.ToolInvocation?.Arguments["PLACE"] is "THE CASTLE" or "CASTLE" &&
           second.Text.Contains("ON THE HILL", StringComparison.Ordinal),
        $"unique place reference fills follow-up slot: REF={first.State.References.Place} TEXT={second.Text} TOOL={second.Diagnostics.ToolInvocation}");
}

static void ResponseCatalog()
{
    Assert(V11ResponseCatalog.Plans.Count >= 200, "at least 200 semantic response plans");
    Assert(V11ResponseCatalog.Plans.SelectMany(plan => plan.Variations).Distinct(StringComparer.Ordinal).Count() >= 4400,
        "at least 4400 distinct project-owned response variations");
    Assert(V11ResponseCatalog.Find("NO_RESPONSE")?.Variations.SequenceEqual([string.Empty]) == true,
        "no-response has one intentional empty surface, not duplicate padding");
}

static void ToolSchemaValidation()
{
    var threw = false;
    try { _ = new GameToolRegistry([new InvalidSchemaTool()]); }
    catch (ArgumentException) { threw = true; }
    Assert(threw, "invalid schema rejected at registry construction");
}

static void ToolSchemaSnapshot()
{
    var tool = new MutableSchemaTool();
    var registry = new GameToolRegistry([tool]);
    tool.Parameters.Clear();
    tool.Templates.Clear();
    var schema = registry.Schemas.Single();
    Assert(schema.Parameters.Count == 2 && schema.PermittedResponseTemplates.Count == 1,
        "registry owns an immutable snapshot of a custom tool schema");
    var result = TestBrain().Reply(Request("buy 1 rope", turnId: "SCHEMA-SNAPSHOT"), registry);
    Assert(result.Text == "YOU BOUGHT 1 ROPE.", "execution continues through the validated schema snapshot");
}

static void CompactCheckpoint()
{
    var directory = Path.Combine(Path.GetTempPath(), "fishbrain-v10-checkpoint-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
        var path = Path.Combine(directory, "model.fbm");
        TestBrain().ExportInference(path, new string('a', 64));
        var loaded = Brain.Load(path);
        var result = loaded.Reply(Request("where is Zephyr-9?"), DemoGameTools.CreateMerchant());
        Assert(result.Text == "I CANNOT LOCATE ZEPHYR-9.", "compact checkpoint roundtrip");
        var bytes = File.ReadAllBytes(path);
        bytes[^1] ^= 0x5a;
        File.WriteAllBytes(path, bytes);
        var corruptRejected = false;
        try { _ = Brain.Load(path); }
        catch (InvalidDataException) { corruptRejected = true; }
        Assert(corruptRejected, "corrupt compact checkpoint rejected");
    }
    finally { Directory.Delete(directory, true); }
}

static void RuntimeBoundaryValidation()
{
    var brain = TestBrain();
    var invalidIdRejected = false;
    try { _ = brain.Reply(Request("hello", turnId: "BAD\u001fID"), GameToolRegistry.Empty); }
    catch (ArgumentException) { invalidIdRejected = true; }
    Assert(invalidIdRejected, "control characters cannot create ambiguous idempotency identifiers");

    var invalidModeRejected = false;
    try
    {
        _ = brain.Reply(Request("hello") with { ResponseMode = (ResponseMode)999 }, GameToolRegistry.Empty);
    }
    catch (ArgumentOutOfRangeException) { invalidModeRejected = true; }
    Assert(invalidModeRejected, "unknown response modes are rejected");

    var duplicateClarificationRejected = false;
    try
    {
        (NpcDialogueState.Initial with
        {
            PendingClarification = new PendingClarification("HOW MANY?", "BUY", ["QUANTITY", "QUANTITY"])
        }).Validate();
    }
    catch (ArgumentException) { duplicateClarificationRejected = true; }
    Assert(duplicateClarificationRejected, "duplicate nested clarification slots are rejected");

    var nonFiniteConfigRejected = false;
    try { _ = Brain.CreateForTesting(new BrainConfig { LearningRate = double.NaN }); }
    catch (InvalidDataException) { nonFiniteConfigRejected = true; }
    Assert(nonFiniteConfigRejected, "non-finite optimizer settings are rejected");

    var oversizedConfigRejected = false;
    try
    {
        _ = Brain.CreateForTesting(new BrainConfig
        {
            LayerCount = 8,
            EmbeddingSize = 1024,
            HeadCount = 64,
            MlpSize = 8192,
            ContextLength = 4096,
            AttentionWindow = 4096,
            PositionPeriod = 4096
        });
    }
    catch (InvalidDataException) { oversizedConfigRejected = true; }
    Assert(oversizedConfigRejected, "aggregate model parameter limits are enforced before allocation");

    var oldBinaryPath = Path.Combine(Path.GetTempPath(), "fishbrain-old-binary-" + Guid.NewGuid().ToString("N") + ".fbm");
    try
    {
        File.WriteAllBytes(oldBinaryPath, "FISHBRN10\n"u8.ToArray());
        var oldBinaryRejected = false;
        try { _ = Brain.Load(oldBinaryPath); }
        catch (InvalidDataException exception)
        {
            oldBinaryRejected = exception.Message.Contains("older binary", StringComparison.Ordinal);
        }
        Assert(oldBinaryRejected, "old binary checkpoints produce a clear compatibility error");
    }
    finally
    {
        if (File.Exists(oldBinaryPath)) File.Delete(oldBinaryPath);
    }
}

static void SharedReleaseThresholds()
{
    var passing = new StructuredMetrics(0.85, 0.85, 0.80, 0.85, 0.0, 0.90, 0.90, 0.85,
        0.95, 0.99, 0.90, 0.85, 0.95, 0.95, 0.80, 0.0);
    Assert(CompositionalHeadModel.MeetsReleaseNeuralThresholds(passing),
        "training accepts the evaluator's exact neural minima");
    Assert(!CompositionalHeadModel.MeetsReleaseNeuralThresholds(passing with { DomainMacroF1 = 0.8499 }),
        "best-production selection rejects a model below any release neural minimum");
}

static void MonotonicCurriculum()
{
    var at40K = Brain.CurriculumLearningRate(40_000, 0.14);
    var at80K = Brain.CurriculumLearningRate(80_000, 0.14);
    var at120K = Brain.CurriculumLearningRate(120_000, 0.14);
    Assert(at40K > at80K && at80K > at120K,
        "extending a completed curriculum cannot raise the absolute-step learning rate");
}

static void PhaseLocalSampling()
{
    var structured = Enumerable.Range(0, 20).Where(step => step % 10 <= 6)
        .Select(Brain.StructuredCurriculumIndex).ToArray();
    var ranking = Enumerable.Range(0, 20).Where(step => step % 10 is 7 or 8)
        .Select(Brain.RankingCurriculumIndex).ToArray();
    Assert(structured.SequenceEqual(Enumerable.Range(0, 14)),
        "structured schedule visits consecutive family ordinals without residue gaps");
    Assert(ranking.SequenceEqual(Enumerable.Range(0, 4)),
        "ranking schedule visits consecutive family ordinals without residue gaps");
    var polishStructured = Enumerable.Range(160_000, 20).Where(step => step % 10 <= 7)
        .Select(step => Brain.HeadPolishStructuredIndex(step, 160_000)).ToArray();
    var polishRanking = Enumerable.Range(160_000, 20).Where(step => step % 10 >= 8)
        .Select(step => Brain.HeadPolishRankingIndex(step, 160_000)).ToArray();
    Assert(polishStructured.SequenceEqual(Enumerable.Range(0, 16)),
        "head-polish structured sampling uses consecutive phase-local ordinals");
    Assert(polishRanking.SequenceEqual(Enumerable.Range(0, 4)),
        "head-polish ranking sampling uses consecutive phase-local ordinals");
    var isolatedBuildAnchor = Path.Combine(Environment.CurrentDirectory, "data", "logs", "nested", "output");
    var benchmark = Fishbrain.Program.ResolveRepositoryFileFrom([isolatedBuildAnchor],
        "data", "benchmarks", "v11-256.jsonl");
    Assert(File.Exists(benchmark) && Path.GetFileName(benchmark) == "v11-256.jsonl",
        "repository data discovery climbs from isolated build directories");
}

static void CheckedInModelSmoke()
{
    var modelPath = Fishbrain.Program.ResolveRepositoryFileFrom([Environment.CurrentDirectory, AppContext.BaseDirectory],
        "data", "models", "model-v11-latest.fbm");
    var brain = Brain.Load(modelPath);
    var world = new DemoWorldState();
    var tools = DemoGameTools.CreateMerchant(world);
    var balance = brain.Reply(Request("balance", turnId: "MODEL-BALANCE"), tools);
    Assert(balance.Text == "YOU HAVE 100 GOLD." && balance.Diagnostics.ToolInvocation?.ToolName == "GET_BALANCE",
        "short balance request uses authoritative world state");
    var farewell = brain.Reply(Request("goodbye", balance.State, "MODEL-GOODBYE"), tools);
    Assert(farewell.Perception.SpeechActs.SequenceEqual([SpeechAct.Farewell]) &&
           farewell.Diagnostics.ResponseSource != ResponseSource.PersonaTemplate &&
           !farewell.Text.Contains("MY NAME", StringComparison.Ordinal),
        "farewell cannot render a stale learned persona target");
    var apology = brain.Reply(Request("i am sorry i called you an idiot", farewell.State, "MODEL-APOLOGY"), tools);
    Assert(apology.Perception.SpeechActs.Contains(SpeechAct.Apologize) &&
           apology.Perception.Policy == ResponsePolicy.Acknowledge &&
           apology.Diagnostics.ResponseSource != ResponseSource.PersonaTemplate &&
           apology.Text.Contains("APOLOG", StringComparison.Ordinal) &&
           !apology.Text.Contains("MY NAME", StringComparison.Ordinal),
        "apology cannot render a stale learned persona target");
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException("Assertion failed: " + message);
}

sealed class CountingBuyTool : IGameTool
{
    private readonly ConcurrentDictionary<string, GameToolResult> _results = new(StringComparer.Ordinal);
    public int MutationCount;
    public ToolSchema Schema { get; } = BuySchema();
    public GameToolResult Execute(GameToolInvocation invocation) => _results.GetOrAdd(invocation.IdempotencyKey, _ =>
    {
        Interlocked.Increment(ref MutationCount);
        return new(true, Fields(("ITEM", invocation.Arguments["ITEM"]), ("QUANTITY", invocation.Arguments["QUANTITY"])));
    });

    internal static ToolSchema BuySchema() => new("BUY",
        [new("ITEM", ToolValueType.String), new("QUANTITY", ToolValueType.Integer)],
        [new("ITEM", ToolValueType.String), new("QUANTITY", ToolValueType.Integer)], true,
        [new("OK", "YOU BOUGHT {QUANTITY} {ITEM}.", ["QUANTITY", "ITEM"]),
         new("ERROR", "BUY FAILED FOR {ITEM}.", ["ITEM"], false)]);

    internal static IReadOnlyDictionary<string, string> Fields(params (string Name, string Value)[] fields) =>
        new ReadOnlyDictionary<string, string>(fields.ToDictionary(field => field.Name, field => field.Value));
}

sealed class MutableSchemaTool : IGameTool
{
    public List<ToolParameter> Parameters { get; } = [new("ITEM", ToolValueType.String), new("QUANTITY", ToolValueType.Integer)];
    public List<ToolResponseTemplate> Templates { get; } =
        [new("DONE", "YOU BOUGHT {QUANTITY} {ITEM}.", ["QUANTITY", "ITEM"])];
    public ToolSchema Schema => new("BUY", Parameters,
        [new("ITEM", ToolValueType.String), new("QUANTITY", ToolValueType.Integer)], true, Templates);
    public GameToolResult Execute(GameToolInvocation invocation) => new(true,
        CountingBuyTool.Fields(("ITEM", invocation.Arguments["ITEM"]), ("QUANTITY", invocation.Arguments["QUANTITY"])));
}

sealed class ThrowingBuyTool : IGameTool
{
    public ToolSchema Schema { get; } = CountingBuyTool.BuySchema();
    public GameToolResult Execute(GameToolInvocation invocation) => throw new InvalidOperationException("DEMO FAILURE");
}

sealed class InvalidSchemaTool : IGameTool
{
    public ToolSchema Schema { get; } = new("bad name", [], [], false, []);
    public GameToolResult Execute(GameToolInvocation invocation) => new(true, new Dictionary<string, string>());
}
