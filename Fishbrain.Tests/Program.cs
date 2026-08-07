using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using Fishbrain;

var tests = new (string Name, Action Test)[]
{
    ("STRUCTURED ROLES", StructuredRoles),
    ("BOUNDED HISTORY", BoundedHistory),
    ("TOOL FIDELITY", ToolFidelity),
    ("MUTATION IDEMPOTENCY", MutationIdempotency),
    ("TOOL EXCEPTION", ToolException),
    ("CONCURRENT REPLIES", ConcurrentReplies),
    ("DETERMINISTIC SAMPLING", DeterministicSampling),
    ("OOV SLOT PRESERVATION", OovSlotPreservation),
    ("MULTI INTENT", MultiIntent),
    ("STATE CONSISTENCY", StateConsistency),
    ("V11 TRANSCRIPT REGRESSIONS", V11TranscriptRegressions),
    ("PERSONA FIDELITY", PersonaFidelity),
    ("STATE HYSTERESIS", StateHysteresis),
    ("AUTHORITATIVE DEMO WORLD", AuthoritativeDemoWorld),
    ("REFERENCE RESOLUTION", ReferenceResolution),
    ("RESPONSE CATALOG", ResponseCatalog),
    ("TOOL SCHEMA VALIDATION", ToolSchemaValidation),
    ("COMPACT CHECKPOINT", CompactCheckpoint)
};
foreach (var (name, test) in tests) { test(); Console.WriteLine($"PASS {name}"); }
Console.WriteLine($"PASS ALL {tests.Length} RUNTIME TESTS");

static Brain TestBrain() => Brain.CreateForTesting(new BrainConfig
{
    EmbeddingSize = 8, HeadCount = 2, MlpSize = 12, ContextLength = 64,
    AttentionWindow = 64, PositionPeriod = 64, MaximumOutputLength = 8, Seed = 17
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
    Assert(V11ResponseCatalog.Plans.Count == 200, "exactly 200 semantic response plans");
    Assert(V11ResponseCatalog.Plans.Sum(plan => plan.Variations.Count) >= 5000,
        "at least 5000 project-owned response variations");
}

static void ToolSchemaValidation()
{
    var threw = false;
    try { _ = new GameToolRegistry([new InvalidSchemaTool()]); }
    catch (ArgumentException) { threw = true; }
    Assert(threw, "invalid schema rejected at registry construction");
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
