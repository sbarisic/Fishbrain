# Fishbrain

Fishbrain is an experimental conversational language model with validated game and
memory tools. The default CLI now uses one causal Transformer, trained from
scratch. It has no emotion, intent, policy, semantic-frame, relationship, mood,
agenda, or learned memory-selection system.

**No replacement model has been promoted.** Historical `.fbm` candidates remain
available with their matching legacy runtime. The new runtime accepts only causal
artifacts bound to the runtime's compact V2 prompt format. Older causal
weights still require their preserved matching package. See [the causal implementation and pilot](CAUSAL_MODEL.md)
for the architecture, commands and safeguards, and [the measured pilot results](CAUSAL_RESULTS.md)
for quality, resources and paired conversations.

A later [focused data-repair check](data/causal-repair-v1/RESULTS.md) improves the
reported shop conversation and identifies lost reference context in packed inputs.
It also remains unpromoted; use its separate test command to inspect that candidate.

The [second repair](data/causal-repair-v2/README.md) fixes prompt context loss and
polite transaction validation, adds authored conversations, and uses a separate
bounded training run. It does not replace the shipped model.

## Build and test

Requires .NET 10. CPU inference has no NuGet or Python dependencies.

```powershell
dotnet build Fishbrain.slnx -c Release
dotnet run --no-build -c Release --project Fishbrain.CausalTests
```

Python/PyTorch with ROCm is used only for training and data preparation.

## Chat with a causal candidate

The completed pilot failed quality gates. To inspect it locally with its matching
runtime and model package:

```powershell
dotnet data/training/causal-v1/candidate-package/Fishbrain.dll chat data/training/causal-v1/candidate-package/candidate.fbc
```

An optional third argument writes JSONL diagnostics:

```powershell
dotnet data/training/causal-v1/candidate-package/Fishbrain.dll chat data/training/causal-v1/candidate-package/candidate.fbc data/training/chat-trace.jsonl
```

Chat prints replies without the old `STATE ...` line. The demo offers wares,
prices, inventory, gold, locations, purchases, sales, persona reads and explicit
reported-memory operations. The host validates proposed transactions. Availability
of a tool does not imply the candidate has learned to use it reliably.

## Host API

Reference `Fishbrain.CausalRuntime`; the demo also uses `Fishbrain.CausalDemo`.
Do not reference the legacy and causal runtimes in the same application: they
intentionally provide different schemas under the same `Fishbrain` namespace.

```csharp
using Fishbrain;

var world = new DemoWorldState();
var memory = new SessionMemoryStore();
var tools = DemoGameTools.CreateMerchant(world)
    .WithTools(ConversationTools.Create())
    .WithTools(memory.CreateTools());
var journal = new ExecutionJournal();
var brain = Brain.Load("candidate.fbc", tools, journal, DemoAuthorization.Allow);
var history = new List<ChatMessage>
{
    new(MessageRole.Player, "What do you have for sale?", 0)
};
var result = brain.Reply(new ReplyRequest("conversation-17", "turn-1",
    history, NpcPersona.Default));
history.AddRange(result.MessagesToAppend);
Console.WriteLine(result.Text);
```

The caller owns ordered history, persona, memory storage, persistence, and world
state. Reuse the host execution journal across runtimes serving the same world.
Each new player message needs a sequence greater than every retained message.
Persist complete tool exchanges from `MessagesToAppend`.

## Preserved implementation

The old architecture and commands are documented in
[the legacy README](LEGACY_README.md). Use `--project Fishbrain.LegacyCli` for those
commands. The old public types live in `Fishbrain.Runtime`; they are not loaded by
the new production CLI. Existing binaries under `data/training/*-tools` remain
untouched for comparison.

```powershell
dotnet run --no-build -c Release --project Fishbrain.LegacyCli -- selftest
dotnet run --no-build -c Release --project Fishbrain.Tests -- --unit
```

The checked-in `data/models/model-latest.fbm` remains incompatible. Its historical
shipped-artifact smoke test still fails; this is separate from malformed-artifact
rejection tests and from the new candidate's implementation checks.
