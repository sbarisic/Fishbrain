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
        state ?? NpcDialogueState.Initial, 41);

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
    var request = new ReplyRequest("LONG", "2001", turns, NpcDialogueState.Initial, 3);
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
