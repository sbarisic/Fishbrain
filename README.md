# Fishbrain

Fishbrain is a C# NPC dialogue runtime with dependency-free .NET CPU inference.
The contextual replacement uses a bidirectional encoder, bounded memory, semantic
frames, an ordered dialogue planner, and a separate generative decoder.

**Release status:** the replacement is implemented as a development candidate.
The checked-in `data/models/model-latest.fbm` is incompatible. It has not been
replaced. Small learning tests and full-size training updates pass; the 260,000
update training run, held-out quality gates, and two-human review remain unfinished.
Long-context inference currently exceeds the 100 ms engineering target.

See [implementation status](CONTEXTUAL_IMPLEMENTATION.md) for evidence and remaining
work, [model architecture](AI_MODEL.md) for the neural design, and
[engineering boundaries](INFO.md) for ownership and release rules.

## Build and verify

Requires the .NET 10 SDK. There are no runtime NuGet dependencies.

```powershell
dotnet build Fishbrain.slnx -c Release
dotnet run --no-build -c Release --project Fishbrain -- selftest
dotnet run --no-build -c Release --project Fishbrain.Tests -- --unit
dotnet run --no-build -c Release --project Fishbrain.DataGenerator.Tests
```

The `--unit` option explicitly excludes the shipped-artifact smoke test.
Running `Fishbrain.Tests` without it must load and exercise the shipped model;
rejection is a failure. Corrupt/incompatible artifact rejection is a separate check.

## Host API

Reference `Fishbrain.Runtime/Fishbrain.Runtime.csproj` or the matching runtime DLL.
This merchant example also references `Fishbrain.DemoDomain`.

```csharp
using Fishbrain;

var brain = Brain.Load("candidate.fbm", DemoDialogueDomains.Merchant);
var tools = DemoGameTools.CreateMerchant();
var request = new ReplyRequest(
    "conversation-17", "turn-4",
    [new DialogueUtterance(7, DialogueRole.Player, "How much money do I have?")],
    NpcDialogueState.Initial, NpcPersona.Default,
    PlayerConversationProfile.Empty, 8, 42);

var result = brain.Reply(request, tools);
// Persist result.State through the host's own session storage.
```

`Brain.Load(path)` reads the domain embedded in the model. The explicit-domain
overload requires the exact immutable `DialogueDomainDefinition` used for training.
Registering an `IGameTool` does not teach a new capability.

The host supplies persona, approved player facts, tools, history, and session
state. The runtime returns new state; it never approves or writes persistent
player facts. Share one loaded brain across calls, but give each session its own
state and keep request collections stable during a call.

`result.Contextual` exposes selected memories, clause frames, response acts,
validated action candidates, confidence, execution vetoes, and agenda changes.
`ResponseMode.DeterministicOnly` disables social generation.

Input is normalized to uppercase. Known words use word tokens; unknown words use
character fallback with exact normalized source offsets. The current utterance
and required persona/state must fit the input budget; an oversized request throws
instead of silently truncating its meaning.

## Compile and train

The source manifest and existing preparation scripts govern source provenance.
After the raw data is available:

```powershell
dotnet run -c Release --project Fishbrain.DataGenerator -- compile --output data/compiled-contextual --seed 42
dotnet run -c Release --project Fishbrain.DataGenerator -- audit --input data/compiled-contextual
dotnet run -c Release --project Fishbrain -- teach data/compiled-contextual data/training/contextual-training.fbm --planned 260000 --until 260000
```

An existing matching training checkpoint resumes exactly. Use a smaller `--until`
for a bounded engineering run. Ctrl+C finishes the current update and saves.
For a background job, create `CHECKPOINT.fbm.stop` to request the same clean stop.
Remove that stop file before resuming. Progress is in `CHECKPOINT.fbm.progress.json`.
Complete state is saved every 100 updates, and validation runs every 5,000 updates.
A file lock prevents concurrent writers to the same checkpoint.
Checkpoints bind weights, token order, schema, domain, corpus, calibration, phase,
sampler, optimizer, and RNG state. Old weights are not migrated.

Training is CPU-intensive. The first 20 updates with the optimized kernels averaged
5.84 seconds per update; later phases can take different amounts of time. A complete
run can still take weeks on the development CPU.

## Evaluate and release

```powershell
dotnet run -c Release --project Fishbrain -- export data/training/contextual-training.fbm data/training/candidate.fbm data/compiled-contextual
dotnet run -c Release --project Fishbrain -- evaluate data/compiled-contextual/test.jsonl data/training/candidate.fbm --gate release
dotnet run -c Release --project Fishbrain -- profile-contextual data/training/candidate.fbm 32
dotnet run -c Release --project Fishbrain -- acceptance-contextual data/training/candidate.fbm data/logs/acceptance.json
dotnet run -c Release --project Fishbrain -- compare-contextual data/compiled-contextual data/training/candidate.fbm data/logs/comparison.json
dotnet run -c Release --project Fishbrain -- conversation-sample data/training/candidate.fbm data/benchmarks/conversation-scenarios.jsonl data/logs/conversation-sample.jsonl
```

Two people must complete the exported review. Reviews include memory, agenda, and
compound-turn ratings where applicable. Then run:

```powershell
./scripts/validate-discourse-release.ps1 -TrainingCheckpoint data/training/contextual-training.fbm -ReviewedConversationFile data/logs/reviewed.jsonl
```

The script checks numerical/runtime tests, corpus provenance, held-out metrics,
human review, artifact compatibility, resources, and ablations before packaging
the matching runtime and model. It does not overwrite the repository's shipped
artifact. A completed training run alone is never sufficient for release.

## Project layout

- `Fishbrain.Runtime`: dependency-free contextual inference and host-facing API.
- `Fishbrain.Training`: trainers, optimizer state, evaluation and the legacy lexical baseline.
- `Fishbrain.DemoDomain`: merchant tools, world fixture and domain definitions.
- `Fishbrain/Neural`: float32 network, differentiation, optimizer, checkpoint code.
- `Fishbrain`: CLI plus runtime/training source linked into the library.
- `Fishbrain.DataGenerator`: deterministic corpus compilation and audits.
- `Fishbrain.Tests`, `Fishbrain.DataGenerator.Tests`: numerical, runtime, and data checks.

Legacy scalar/lexical code remains for baseline comparisons and regression tests.
The contextual production path does not allocate its parameters or optimizer.
The runtime has no project references to training, CLI or demo-domain assemblies.
Assembly-boundary tests check this separation. `Brain.Load(path)` uses the immutable
domain embedded in the checkpoint; the explicit-domain overload verifies its fingerprint.
