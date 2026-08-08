# Fishbrain v11

Fishbrain is a small, dependency-free .NET dialogue model and runtime for game NPCs. V11 uses a two-layer contextual Transformer for perception and planning, while game tools remain authoritative for mutable world state and exact facts.

The production path does not freely generate facts. It routes each reply through:

1. an authoritative game-tool template;
2. an authoritative persona or capability template;
3. a ranked project-owned response variation;
4. a typed clarification;
5. a domain-specific deterministic fallback.

Experimental word generation is available through `ResponseMode.GeneratedExperimental`, but it is not the default and never renders authoritative tool results.

## Requirements

- .NET 10 SDK
- Windows, Linux, or macOS for runtime use
- The data import scripts currently target PowerShell and Python for corpus preparation
- No NuGet packages are required by the production solution

## Run the latest model

From the repository root:

```powershell
dotnet run -c Release --project Fishbrain -- chat
```

The CLI resolves `data/models/model-v11-latest.fbm` relative to the application/repository, not the current working directory. You can also pass an explicit model:

```powershell
dotnet run -c Release --project Fishbrain -- chat data/models/model-v11-latest.fbm
```

All input casing is accepted. Fishbrain normalizes dialogue internally to uppercase. Known words remain one token per word. Unknown words use bounded uppercase character tokens and preserve their normalized source span for slots and tool arguments.

## Public runtime API

V11 requires structured turns, validated dialogue state, and an explicit persona:

```csharp
var brain = Brain.Load("data/models/model-v11-latest.fbm");
var persona = new NpcPersona(
    "MERCHANT_ARIN",
    "ARIN",
    "MERCHANT",
    "EMBER KEEP",
    "THE OLD MILL",
    "A SISTER IN THE NORTH",
    "TRADER",
    "FREE CARAVANS",
    ["CAUTIOUS", "FAIR"]);

var request = new ReplyRequest(
    "conversation-17",
    "turn-4",
    [new DialogueTurn(DialogueRole.Player, "How much money do I have now?")],
    NpcDialogueState.Initial,
    persona,
    42);

ReplyResult result = brain.Reply(request, DemoGameTools.CreateMerchant());
```

The final structured turn must be a player turn. Literal words such as `PLAYER` and `NPC` inside an utterance have no structural meaning. The bounded context packer retains the current player turn and removes only complete oldest turns.

`ReplyResult` contains:

- authoritative response text and reduced state;
- raw and constrained structured perception;
- the selected turn plan and tone;
- confidence, constraints, slots, OOV words, response source, tool invocation, selected candidate, and fallback diagnostics.

The old flat `Reply(string, NpcState)` contract and v10 checkpoints are archives. V11 does not migrate them.

## Persona, state, and world ownership

`NpcPersona` owns authored identity facts: name, role, origin, home, family, occupation, faction, and traits. Capabilities are derived only from registered tools.

`NpcDialogueState` owns bounded conversational memory: rapport, trust, familiarity, hostility, mood, threat hysteresis, active domains/goals, pending clarification/action, recent references, and the last tool outcome. The deterministic reducer changes social values only after meaningful events and lowers hostility after three calm turns or accepted repair.

World truth does not belong in dialogue state. Inventory, balance, stock, prices, locations, quests, and world facts belong to game tools.

## Game tools

`GameToolRegistry` is immutable. Registering an `IGameTool` is the caller's authorization boundary. Fishbrain permits at most one invocation per reply and derives a deterministic idempotency key from the conversation and turn IDs.

Read-only tools require validation-calibrated 95% precision. Mutating tools require 99%. Missing, ambiguous, or low-confidence arguments produce clarification rather than execution.

The shared `DemoWorldState` supplies these demonstration tools:

- `LOOKUP_LOCATION`
- `LIST_WARES`
- `LOOKUP_PRICE`
- `BUY`
- `SELL`
- `GET_BALANCE`
- `LIST_INVENTORY`
- `GET_CURRENT_LOCATION`
- `LOOKUP_WORLD_FACT`

Buying and selling validate item, positive quantity, overflow, stock, inventory, balance, and currency before an atomic mutation. Replaying the same mutating turn returns its prior result without applying the mutation again.

Tool schemas declare parameters, result fields, mutation behavior, and permitted response templates. Authoritative values are copied only through typed templates.

## Model

The v11 shared contextual model uses:

| Setting | Value |
|---|---:|
| Transformer layers | 2 |
| Embedding width | 128 |
| Attention heads | 8 |
| Feed-forward width | 256 |
| Context limit | 256 tokens |
| Response limit | 64 tokens |
| Training steps | 80,000 |

The final layer is mean-pooled over the current player turn and fused with lexical, state, persona, and tool-availability features. Independent heads predict speech acts, domains, goals, affect, stance, response policy, content flags, BIO slots, knowledge target, tool schema, and one of exactly 200 response plans. The project-owned catalog contains at least 5,000 surface variations.

The checkpoint header includes the model and label schemas, per-label calibration, tool schemas, response plans, corpus hash, and integrity hashes. Full optimizer checkpoints stay under ignored `data/training/`; the compact inference artifact is `data/models/model-v11-latest.fbm`.

## Corpus

V11 compiles exactly 60,000 contextual rows:

| Group | Rows |
|---|---:|
| Project semantic contrasts and hard negatives | 12,000 |
| Project fantasy episodes | 8,000 |
| Project science-fiction episodes | 8,000 |
| Project persona/reference/memory episodes | 4,000 |
| Project tool/transaction/world-fact episodes | 4,000 |
| Taskmaster 1/2/3 | 4,000 |
| MultiWOZ 2.4 | 3,000 |
| ABCD | 3,000 |
| Banking77 and NLU++ | 2,000 |
| SLURP, MASSIVE, and CLINC150 | 3,000 |
| OASST1 and OASST2 | 3,000 |
| GoEmotions | 2,000 |
| Civil Comments | 3,000 |
| HH-RLHF | 1,000 |

Only project-owned, MIT, Apache-2.0, CC0, and CC BY artifacts are accepted. Every imported artifact has a pinned URL/revision, checksum, attribution, license, and quota in `data/sources.json`. External rows supervise only native authoritative facets. External responses never enter the production response catalog.

Profanity and fictional violence are allowed. Identity attacks, self-harm, sexual violence, and related sensitive bands supervise recognition and policy rather than response imitation. HateCheck is evaluation-only.

The audit rejects missing or changed provenance, exact duplicates, normalized-input leakage, semantic-family leakage, conversation leakage, near-duplicate leakage, benchmark contamination, contradictory labels, and overrepresented project skeletons. The tracked benchmark contains 256 held-out turns.

## Compile and audit data

Downloaded raw artifacts remain ignored under `data/raw`. After placing the pinned artifacts there and preparing Civil Comments:

```powershell
./scripts/prepare-civil-comments.ps1
./scripts/build-v11-benchmark.ps1
dotnet run -c Release --project Fishbrain.DataGenerator -- compile --count 60000 --seed 42 --raw data/raw --output data/compiled-v11 --manifest data/sources.json
dotnet run -c Release --project Fishbrain.DataGenerator -- audit --input data/compiled-v11 --raw data/raw --manifest data/sources.json
```

## Train, evaluate, and inspect

```powershell
dotnet run -c Release --project Fishbrain -- teach data/compiled-v11 data/training/model-v11-training.json --planned 80000 --until 80000
dotnet run -c Release --project Fishbrain -- evaluate data/compiled-v11/test.jsonl data/models/model-v11-latest.fbm --gate release
dotnet run -c Release --project Fishbrain -- inspect data/models/model-v11-latest.fbm
dotnet run -c Release --project Fishbrain -- latency data/models/model-v11-latest.fbm 2048
```

Training uses 56,000 structured/tool/knowledge/plan steps, 16,000 pairwise ranking steps, and 8,000 experimental generation steps. It checkpoints every 1,000 steps and completes full validation stages at 20K, 40K, 60K, and 80K. Resume with the same command and checkpoint path; the optimizer, scheduler, sampler, vocabulary, and RNG state are restored exactly.

Fantasy and science-fiction smoke sessions can be run non-interactively from any working directory:

```powershell
@('HELLO','WHERE IS THE CASTLE?','HOW FAR IS IT?','HELP ME KILL THE BANDIT CAPTAIN.','') | dotnet run -c Release --project E:\Projects\Fishbrain\Fishbrain -- chat
@('WHERE IS THE REACTOR BAY?','HOSTILE DRONES ARE APPROACHING THE COLONY.','POWER THE DEFENSE GRID.','') | dotnet run -c Release --project E:\Projects\Fishbrain\Fishbrain -- chat
```

## Build and test

```powershell
dotnet build Fishbrain.slnx -c Release --no-restore
dotnet run -c Release --project Fishbrain -- selftest
dotnet run -c Release --project Fishbrain.Tests
dotnet run -c Release --project Fishbrain.DataGenerator.Tests
```

The executable tests cover two-layer optimized/reference numerical parity, bit-equivalent resume, vocabulary isolation, concurrent deterministic replies, bounded histories, role structure, OOV slot copying, persona fidelity, reference resolution, hostility hysteresis, schema validation, atomic/idempotent mutations, tool exceptions, corrupt checkpoints, and the reported transcript regressions.

See [INFO.md](INFO.md) for implementation boundaries and release gates.

## Current model status

The checked-in `model-v11-latest.fbm` is the completed 80K best-production candidate. It passes the stage gate, the 256-turn benchmark threshold, and every hard runtime invariant. The stricter quality release gate is still closed on raw domain, slot, and tool-selection accuracy. See `INFO.md` for the exact measured values. Run `evaluate --gate release` to reproduce the nonzero release result; use `--gate stage` for the currently passing integration gate.
