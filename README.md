# Fishbrain v10

Fishbrain is a small CPU-only dialogue model for video-game NPCs. It uses C# and .NET 10 without NuGet packages.

V10 separates learned perception from game authority. The model predicts speech acts, domains, goals, affect, policy, slots, content flags, tools, and response candidates. The runtime validates these predictions, runs registered game tools, and renders approved responses.

The Transformer has one layer and 64-dimensional embeddings. Production replies use learned candidate ranking and typed tool templates. Free word generation is an experimental mode.

## Run the latest model

Run these commands from the repository root:

```powershell
dotnet build Fishbrain.slnx -c Release
dotnet run -c Release --project Fishbrain -- chat
```

The chat command finds `data/models/model-v10-latest.fbm` from the application directory. The current working directory does not control model lookup.

You can also give an explicit model path:

```powershell
dotnet run -c Release --project Fishbrain -- chat data/models/model-v10-latest.fbm
```

Fishbrain accepts any input casing. It normalizes all text to uppercase before tokenization.

## Runtime API

V10 removes the flat `Reply(string, NpcState)` API. Use structured turns and `NpcDialogueState`:

```csharp
var brain = Brain.Load("data/models/model-v10-latest.fbm");
var tools = DemoGameTools.CreateMerchant();

var request = new ReplyRequest(
    ConversationId: "SESSION-17",
    TurnId: "4",
    Turns:
    [
        new DialogueTurn(DialogueRole.Player, "Where is the inn?")
    ],
    State: NpcDialogueState.Initial,
    Seed: 42);

ReplyResult result = brain.Reply(request, tools);

Console.WriteLine(result.Text);
Console.WriteLine(result.Perception.Policy);
Console.WriteLine(result.Diagnostics.ResponseSource);
```

The final structured turn must have the `Player` role. Words such as `PLAYER` and `NPC` inside the text have no structural meaning.

The runtime keeps only complete recent turns that fit the context. It always keeps the current player turn.

## Response modes

`ResponseMode.Ranked` is the production default. It masks ineligible response candidates before learned ranking.

`ResponseMode.GeneratedExperimental` uses the word-generation head. Use this mode only for evaluation and experiments.

Diagnostics report confidence, constraints, response source, candidate ID, tool invocation, slots, OOV words, and fallback reason.

## Game tools

Create an immutable `GameToolRegistry` from explicit `IGameTool` implementations:

```csharp
var registry = new GameToolRegistry(
[
    new MyLocationTool(),
    new MyMerchantTool()
]);
```

Registration authorizes Fishbrain to run each tool. A reply can run one tool. Other recognized actions enter the pending-action queue.

Each tool declares its parameters, result fields, mutation status, and response templates. The runtime copies authoritative fields into these templates. It does not generate names, quantities, prices, or locations.

Read-only tools need calibrated precision of at least 0.95. Mutating tools need precision of at least 0.99. Missing or ambiguous arguments produce a clarification.

Each invocation has an idempotency key from the conversation ID and turn ID. Game code can reject a repeated mutation.

## Tokenization

Each known word uses one token. Apostrophes and hyphens stay inside a word.

```text
PLAYER HEY I DON'T WANT TO HELP YOU, IDIOT
PLAYER | HEY | I | DON'T | WANT | TO | HELP | YOU | , | IDIOT
```

An unknown word uses a word-start token, uppercase character tokens, and a word-end token. Fishbrain keeps the normalized source span for slots and tool arguments.

Control-token IDs do not depend on enum counts. A new label does not move lexical token IDs.

## Build and test

```powershell
dotnet build Fishbrain.slnx -c Release
dotnet run -c Release --project Fishbrain -- selftest
dotnet run -c Release --project Fishbrain.Tests
dotnet run -c Release --project Fishbrain.DataGenerator.Tests
```

The executable tests cover tokenizer isolation, deterministic resume, concurrent replies, OOV copying, history bounds, tools, idempotency, and corrupt checkpoints.

## Data layout

All data files use the `data/` directory:

```text
data/
  benchmarks/       tracked 128-turn holdout benchmark
  models/           compact inference models
  raw/              downloaded source files, ignored
  compiled-v10/     generated train, validation, and test files, ignored
  training/         full resume checkpoints and logs, ignored
  telemetry/        milestone JSONL records
  sources.json      tracked source revisions, licenses, and checksums
```

The v10 compiler makes exactly 30,000 rows:

| Source | Rows | Supervision |
|---|---:|---|
| Project semantic and state contrasts | 12,000 | all v10 heads and owned responses |
| Project fantasy dialogue | 4,000 | all v10 heads and owned responses |
| Project science-fiction dialogue | 4,000 | all v10 heads and owned responses |
| Project game-grounded dialogue | 2,500 | all v10 heads and owned responses |
| Taskmaster-1 | 2,000 | native dialogue, domain, goal, and slot facets |
| CLINC150 | 500 | native intent facets |
| SLURP text | 500 | native intent and slot facets |
| English MASSIVE | 500 | native intent and slot facets |
| OASST1 | 1,000 | experimental language only |
| GoEmotions | 1,000 | affect only |
| Civil Comments | 1,000 | content labels only |
| Project social repair | 1,000 | all v10 heads and owned responses |

External text never enters the production response catalog. Sensitive rows train recognition and policy, not response imitation.

## Fetch, compile, and audit

The manifest pins every source revision and SHA-256 checksum:

```powershell
dotnet run -c Release --project Fishbrain.DataGenerator -- fetch
./scripts/prepare-civil-comments.ps1
dotnet run -c Release --project Fishbrain.DataGenerator -- compile --count 30000 --seed 42 --output data/compiled-v10
dotnet run -c Release --project Fishbrain.DataGenerator -- audit --input data/compiled-v10
```

The audit fails on count errors, changed checksums, missing provenance, label contradictions, split leakage, or benchmark contamination.

The compiler assigns full connected components to 80/10/10 splits. Components join semantic families, source conversations, equal normalized inputs, and near duplicates.

## Train and evaluate

Run the complete 40,000-step curriculum:

```powershell
dotnet run -c Release --project Fishbrain -- teach data/compiled-v10 data/training/model-v10-training.json --planned 40000 --until 40000
```

The scheduler uses structured updates for 90 percent of steps. It uses language-generation updates for 10 percent.

Fishbrain evaluates and saves every 1,000 steps. It writes full stage telemetry at 10K, 20K, 30K, and 40K.

Evaluate a checkpoint with a process exit gate:

```powershell
dotnet run -c Release --project Fishbrain -- evaluate data/compiled-v10/test.jsonl data/training/model-v10-training.json --gate release
```

`--gate stage` and `--gate release` return a nonzero exit code when their gate fails. Use `--gate none` to print results without an exit gate.

## Checkpoints

Full JSON checkpoints contain optimizer state and exact resume data. Keep them under ignored `data/training/`.

The final compact model uses the `.fbm` format. Its readable header stores schemas, calibration, catalogs, corpus hash, weight counts, and checksums.

```powershell
dotnet run -c Release --project Fishbrain -- export data/training/model-v10-training.json data/models/model-v10-latest.fbm data/compiled-v10
dotnet run -c Release --project Fishbrain -- inspect data/models/model-v10-latest.fbm
```

The 40K `teach` command exports the best structured checkpoint automatically.

## Limits

Fishbrain is for short game dialogue. It is not a general-purpose assistant or a source of world truth.

Keep inventory, currency, quests, navigation, and world state in game tools. Do not use model output as authoritative game data.

V9 checkpoints remain archives. V10 does not migrate them.

Learned multi-step reasoning is deferred to v11.
