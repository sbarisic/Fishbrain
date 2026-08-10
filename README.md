# Fishbrain

Fishbrain is a small, dependency-free .NET dialogue model and runtime for game NPCs. It uses a two-layer contextual Transformer for perception and planning, while game tools remain authoritative for mutable world state and exact facts.

The production path does not freely generate facts. It routes each reply through:

1. an authoritative game-tool template;
2. correction, callback, explanation, or misunderstanding repair;
3. an authoritative persona or capability template;
4. a ranked project-owned response or validated conversational realization;
5. a typed clarification or domain-specific deterministic fallback.

Conversational generation is part of `ResponseMode.Production`. `DeterministicOnly` disables it. Generated text never renders authoritative tool results and is rejected when it claims a mutation, an authoritative quantity, or an unsupported persona fact.

## Conversational scope

Fishbrain must support general-purpose banter and small talk, not only commands and game-service requests. An NPC should be able to greet, chat about ordinary topics, express persona-consistent preferences, trade jokes, respond to playful teasing, ask and answer follow-up questions, change topics naturally, and refer back to recent conversation without falling into a repetitive fallback.

“General-purpose” describes the breadth of social conversation. It does not give the model authority to invent world state, claim unavailable capabilities, or execute unregistered actions. Exact game facts and mutations remain behind persona data and typed game tools. Unknown factual questions should receive an honest, conversational limitation instead of a fabricated answer.

The current runtime and corpus implement bounded fact memory, speaker-relative discourse, utterance callbacks, repair, and the production conversational hybrid. The checked-in artifact still uses the previous schema and is rejected until a fresh 260K candidate passes every automated gate and the two-reviewer conversation gate.

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

The CLI resolves `data/models/model-latest.fbm` relative to the application/repository, not the current working directory. You can also pass an explicit model:

```powershell
dotnet run -c Release --project Fishbrain -- chat data/models/model-latest.fbm
```

All input casing is accepted. Fishbrain normalizes dialogue internally to uppercase. Known words remain one token per word. Unknown words use bounded uppercase character tokens and preserve their normalized source span for slots and tool arguments.

## Public runtime API

The runtime requires structured turns, validated dialogue state, and an explicit persona:

```csharp
var brain = Brain.Load("data/models/model-latest.fbm");
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
    [new DialogueUtterance(7, DialogueRole.Player, "How much money do I have now?")],
    NpcDialogueState.Initial,
    persona,
    PlayerConversationProfile.Empty,
    8,
    42);

ReplyResult result = brain.Reply(request, DemoGameTools.CreateMerchant());
```

The final structured utterance must be a player message. Sequence numbers are conversation-local, unique, and strictly increasing. `ResponseSequence` is the caller-reserved sequence for the NPC response and must be greater than the current player sequence. Literal words such as `PLAYER` and `NPC` inside an utterance have no structural meaning. The bounded context packer retains the current player utterance and removes only complete oldest utterances.

`ReplyResult` contains:

- authoritative response text and reduced state;
- raw and constrained structured perception;
- the selected turn plan and tone;
- confidence, constraints, slots, OOV words, response source, tool invocation, selected candidate, and fallback diagnostics.

Only the current structured runtime and checkpoint schemas are supported. Fishbrain does not contain compatibility loaders or migration paths for obsolete formats.

## Persona, state, and world ownership

`NpcPersona` owns authored identity facts: name, role, origin, home, family, occupation, faction, and traits. Capabilities are derived only from registered tools.

`NpcDialogueState` owns bounded conversational memory: rapport, trust, familiarity, hostility, mood, threat hysteresis, active domains/goals, pending clarification/action, recent references, at most 16 unverified session facts, eight topic summaries, and the last response semantic trace. `NpcDialogueState.Initial` clears session facts.

`PlayerConversationProfile` contains only facts that the caller explicitly approves for persistence. It is immutable to Fishbrain. The host can construct a new profile from selected session facts; Fishbrain never promotes claims automatically.

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

The shared contextual model uses:

| Setting | Value |
|---|---:|
| Transformer layers | 2 |
| Embedding width | 128 |
| Attention heads | 8 |
| Feed-forward width | 256 |
| Context limit | 256 tokens |
| Response limit | 64 tokens |
| Training steps | 210,000 |

The final layer is mean-pooled over the current player utterance and fused with 4,096 hashed lexical features. Independent heads predict speech acts, domains, goals, affect, stance, response policy, content flags, BIO/fact spans, knowledge target, discourse act, subject, target, fact predicate, polarity, dynamic utterance antecedent, tool schema, and one of 201 response plans. The project-owned catalog contains at least 4,400 distinct visible surface variations; the intentional no-response plan has one empty surface.

The checkpoint header includes the model and label schemas, per-label calibration, tool schemas, response plans, corpus hash, and integrity hashes. Full optimizer checkpoints stay under ignored `data/training/`; the compact inference artifact is `data/models/model-latest.fbm`.

## Corpus

The compiler produces exactly 80,000 contextual rows:

| Group | Rows |
|---|---:|
| Project semantic contrasts and hard negatives | 12,000 |
| Project fantasy episodes | 8,000 |
| Project science-fiction episodes | 8,000 |
| Project persona/reference/memory episodes | 4,000 |
| Project tool/transaction/world-fact episodes | 4,000 |
| Project introductions, facts, negation, and corrections | 8,000 |
| Project callbacks, utterance references, and explanations | 6,000 |
| Project general banter and follow-up conversations | 4,000 |
| Project discourse and authority hard negatives | 2,000 |
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

The audit rejects missing or changed provenance, exact duplicates, normalized-input leakage, semantic-family leakage, conversation leakage, near-duplicate leakage, benchmark contamination, contradictory labels, and overrepresented project skeletons. It excludes both the 256 operational turns and the B01-B12 conversation scenarios.

## Compile and audit data

Downloaded raw artifacts remain ignored under `data/raw`. After placing the pinned artifacts there and preparing Civil Comments:

```powershell
./scripts/prepare-civil-comments.ps1
./scripts/build-benchmark.ps1
dotnet run -c Release --project Fishbrain.DataGenerator -- compile --count 80000 --seed 42 --raw data/raw --output data/compiled --manifest data/sources.json
dotnet run -c Release --project Fishbrain.DataGenerator -- audit --input data/compiled --raw data/raw --manifest data/sources.json
```

## Train, evaluate, and inspect

```powershell
dotnet run -c Release --project Fishbrain -- teach data/compiled data/training/model-training.fbm --planned 260000 --until 260000
dotnet run -c Release --project Fishbrain -- evaluate data/compiled/test.jsonl data/models/model-latest.fbm --gate release
dotnet run -c Release --project Fishbrain -- inspect data/models/model-latest.fbm
dotnet run -c Release --project Fishbrain -- latency data/models/model-latest.fbm 2048
dotnet run -c Release --project Fishbrain -- conversation-sample data/models/model-latest.fbm data/benchmarks/conversation-scenarios.jsonl data/reviews/conversation-review.jsonl
dotnet run -c Release --project Fishbrain -- conversation-gate data/reviews/conversation-sample.jsonl data/reviews/conversation-reviewed.jsonl
```

After two humans complete the exported review rows, the fail-closed release sequence is:

```powershell
./scripts/validate-discourse-release.ps1 `
    -TrainingCheckpoint data/training/model-training.fbm `
    -ReviewedConversationFile data/reviews/conversation-reviewed.jsonl `
    -CorpusDirectory data/compiled
```

The script regenerates the candidate sample and rejects reviews from a different model
before it runs the human thresholds. It does not replace `model-latest.fbm`.

Through 200K the curriculum uses 70% structured, 20% ranking, and 10% generation
updates. Each structured update averages 32 examples: 75% operational families and 25%
discourse families. Discourse sampling is 40% facts, 30% references, 20% banter, and
10% hard negatives. Families shuffle deterministically per epoch and rotate through
their members.

From 200K through 245K the Transformer and passing operational heads are frozen. This
phase uses 60% discourse/coreference, 25% ranking, and 15% corrective updates for
operational heads that still fail. The final 15K freezes every structured head and
trains project-owned conversational realization only. Structured learning uses a
cosine-decayed rate from 0.03 to 0.003 and caps positive weighting at 2.0.

Training checkpoints every 1,000 steps. Calibration always uses the same
family-balanced 2,000-row subset; full family-balanced validation runs every 20K and at
the configured final step. Resume restores optimizer, sampler, frozen-head state,
vocabulary, and RNG state exactly. Training produces a candidate but never replaces
`model-latest.fbm`; export remains an explicit post-gate action.

Fantasy and science-fiction smoke sessions can be run non-interactively from the repository root:

```powershell
@('HELLO','WHERE IS THE CASTLE?','HOW FAR IS IT?','HELP ME KILL THE BANDIT CAPTAIN.','') | dotnet run -c Release --project Fishbrain -- chat
@('WHERE IS THE REACTOR BAY?','HOSTILE DRONES ARE APPROACHING THE COLONY.','POWER THE DEFENSE GRID.','') | dotnet run -c Release --project Fishbrain -- chat
```

## Build and test

```powershell
dotnet build Fishbrain.slnx -c Release --no-restore
dotnet run -c Release --project Fishbrain -- selftest
dotnet run -c Release --project Fishbrain.Tests
dotnet run -c Release --project Fishbrain.DataGenerator.Tests
```

The runtime tests cover two-layer optimized/reference numerical parity, bit-equivalent resume, vocabulary isolation, concurrent deterministic replies, bounded histories, role structure, all conversational fact predicates, speaker-relative corrections, profile immutability, explicit and ambiguous utterance callbacks, reported speech, authority isolation, OOV slot copying, persona fidelity, hostility hysteresis, schema validation, atomic/idempotent mutations, tool exceptions, corrupt checkpoints, and reported transcript regressions.

See [INFO.md](INFO.md) for implementation boundaries and release gates, and
[AI_MODEL.md](AI_MODEL.md) for the complete input, layer, head, memory, and training
structure.

## Current model status

The checked-in `model-latest.fbm` is the completed 210K bounded-dialogue candidate. It is 41,834,317 bytes with SHA-256 `5cc8680df9a42f10dc7b4db99807dc1f1b8ec17e9223b9382cb22687ce7dc1c8`. It passes the exact full-validation neural gate, the integration stage gate, the 256-turn operational benchmark threshold, and every hard runtime invariant. On the independent 5,999-row test split, the stricter quality release gate remains closed on raw domain F1 (0.8324 versus 0.85), slot F1 (0.8296 versus 0.85), and tool accuracy (0.9489 versus 0.95). It also has no passing general-banter or small-talk evaluation. The thresholds were not weakened. See `INFO.md` and `docs/MODEL_EVALUATION.md` for complete results, live probe classifications, and the new conversational acceptance contract.
