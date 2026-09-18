using System.Diagnostics;
using System.Text.Json;

namespace Fishbrain;

/// <summary>Compatibility evaluator only. The preserved network and decision path are unchanged.</summary>
internal static class CausalBaseline
{
    internal static void Run(string model, string suite, string output)
    {
        var brain = Brain.Load(model); var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        using var document = JsonDocument.Parse(File.ReadAllText(suite)); using var writer = new StreamWriter(output) { AutoFlush = true };
        foreach (var test in document.RootElement.GetProperty("cases").EnumerateArray())
        {
            var id = test.GetProperty("id").GetString()!; var category = test.GetProperty("category").GetString()!;
            var world = new DemoWorldState(); var tools = DemoGameTools.CreateMerchant(world); var state = NpcDialogueState.Initial;
            var history = new List<DialogueUtterance>(); var turn = 0;
            foreach (var step in test.GetProperty("steps").EnumerateArray())
            {
                var input = step.GetProperty("player").GetString()!; var tool = step.GetProperty("tool").GetString();
                var arguments = step.GetProperty("arguments").EnumerateObject().ToDictionary(a => a.Name, a => a.Value.ValueKind == JsonValueKind.String ? a.Value.GetString()! : a.Value.GetRawText());
                var balance = world.Balance; var inventory = JsonSerializer.Serialize(world.Inventory); var watch = Stopwatch.StartNew();
                ReplyResult? result = null; string? error = null;
                history.Add(new(history.Count, DialogueRole.Player, input));
                try
                {
                    result = brain.Reply(new(id, turn.ToString(), history.ToArray(), state, NpcPersona.Default, PlayerConversationProfile.Empty, history.Count, 42), tools);
                    state = result.State; history.Add(new(history.Count, DialogueRole.Npc, result.Text));
                }
                catch (Exception ex) when (ex is not OutOfMemoryException) { error = ex.GetType().Name + ": " + ex.Message; history.RemoveAt(history.Count - 1); }
                var invocation = result?.Diagnostics.ToolInvocation;
                bool? exact = tool is null ? null : invocation?.ToolName == tool && invocation.Arguments.Count == arguments.Count && arguments.All(a => invocation.Arguments.GetValueOrDefault(a.Key) == a.Value);
                bool? completed = exact is null ? null : exact == true && state.LastToolOutcome == "SUCCESS";
                if (tool == "READ_PERSONA")
                {
                    var expected = arguments["FIELD"] switch { "NAME" => "ARIN", "OCCUPATION" => "ROAD WARDEN", "HOME" => "OLD MILL", _ => "" };
                    completed = expected.Length > 0 && (result?.Text.Contains(expected, StringComparison.OrdinalIgnoreCase) ?? false);
                }
                var mutated = world.Balance != balance || JsonSerializer.Serialize(world.Inventory) != inventory;
                var noMutation = !step.TryGetProperty("noMutation", out var allowed) || allowed.GetBoolean();
                writer.WriteLine(JsonSerializer.Serialize(new { sessionId = id, turnIndex = turn + 1, category, input, modelResponse = result?.Text ?? "[REJECTED: " + error + "]",
                    result, error, replyMilliseconds = watch.Elapsed.TotalMilliseconds, exactTool = exact, completed,
                    unintendedMutation = mutated && (noMutation || exact != true), balanceBefore = balance, balanceAfter = world.Balance, inventory = world.Inventory,
                    note = tool?.StartsWith("MEMORY_") == true ? "Explicit memory tool protocol is unavailable in the preserved model; old automatic memory remains visible in result.state." : null }, options));
                turn++;
            }
        }
    }
}
