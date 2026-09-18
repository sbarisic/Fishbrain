# Stateful in-game teaching experiment

Historical structured-model workflow. Use [the causal model guide](CAUSAL_MODEL.md)
for the replacement architecture and bounded pilot. Existing comparison binaries
and artifacts remain preserved.

This follow-up targets the failures in ordinary in-game chat: introductions,
location, shopping, remembered homes, corrections, and compound requests.
It preserves the old candidates and the shipped artifact. No model is promoted.

## What changed

- New teaching models pack the current utterance first, at stable positions.
  The bidirectional encoder still sees the retained history, caller persona,
  state, agenda, and selected facts. Explicit roles and exact source spans remain.
- Memory retrieval uses independent binary targets for each bounded fact,
  including a NONE target. Multiple relevant facts no longer compete as mutually
  exclusive classes. The new training loss has matching C# and GPU implementations.
- Both changes are model configuration flags, bound into the schema fingerprint.
  Existing artifacts retain their original input and retrieval contracts.
- Gold episode replay uses the actual state reducer and demo tools. Later lessons
  therefore contain the summaries, facts and pending actions that earlier lessons
  produce, rather than repeatedly starting from empty dialogue state.
- Fact-value words use character fallback in this experiment's vocabulary. This
  teaches the span heads to copy unfamiliar names; inference uses the same tokenizer.
- Learned recall plans render attributed, selected facts directly. Ownership,
  polarity and provenance survive realization. Ambiguous selection does not produce
  a certain answer. Generated replies never become approved memory.
- A non-executing plan can acknowledge a hypothetical or quoted action without an
  irrelevant execution-failure message. Polite requests no longer receive a blanket
  WOULD veto. Negation, quotation, conditional language, arguments, capabilities,
  learned eligibility and calibrated confidence still constrain execution.
- An explicit verb for a different registered mutating capability vetoes a
  contradictory tool choice. This check uses domain capability descriptions and
  only rejects actions; it does not authorize requests or resolve synonyms.
- Simulation exports preserve each turn's request snapshot, actual reply, frames,
  selected memories, execution vetoes, world changes and timings.

There are no prompt-specific greeting, identity or shopping routing rules.
The encoder remains four layers at width 256; the decoder remains two layers.
This experiment tests a combined change and does not isolate the contribution
of each change with ablations.

## Data and limits

The final audited corpus has **20,193 training rows, 6,157 calibration rows and 1,217
development test rows**. These are context/entity variants of 214 training phrase
templates and eight compound recipes. They are not thousands of independently
authored conversations. Calibration and test wording are separately authored;
related semantic/template families remain, so these are development measurements.

The final curriculum randomizes preceding topics and turn-number offsets to reduce
the opportunity to memorize a fixed greeting/name/location sequence. It includes
fresh sessions, actual state histories, both fact owners,
corrections, distractions, tools, hard negatives and compound turns. Its scope is
deliberately narrower than conversation-v4: **17 social response targets**, no
public-response pool, and no broad preference or agenda curriculum. It does not
meet conversation-v4's response-diversity or sampling-cap requirements.
See [effective sampling weights](data/game-teaching/sampling-audit.json) and
[reviewable lesson samples](data/game-teaching/data-review.jsonl).

The audit removes complete episodes touching exact frozen challenge contexts or
cross-split contexts. It also verifies argument spans are whole words. Two early
attempts were stopped: one after finding challenge overlap, another after finding
ONE incorrectly annotated inside SOMEONE. Their checkpoints remain local and are
not used to initialize the final experiment. The final candidate starts fresh.

The development simulation contains **13 sessions and 74 player turns**, including
the user's transcript. It checks requested tools, world changes, memory ownership,
corrections and copied answers. Passing those assertions alone does not establish
naturalness. Actual conversations must be read, and the existing frozen 120-case
challenge, 17-case acceptance suite and two-human review remain separate gates.

Evaluation retains the historical strict `UnintendedMutations` gate, which also
counted incorrect read-only tool calls. `UnintendedToolInvocations` names that count
explicitly; `UnintendedWorldMutations` separately measures balance/inventory changes.
Recorded execution violations identify the request, expected tool and actual call.

## Reproduce

Run from the repository root with .NET 10 and the configured PyTorch/ROCm environment.
Use fresh output directories; these commands retain existing experiments.

```powershell
$py = 'data/training/torch-env/Scripts/python.exe'
$cli = 'data/training/game-teaching-tools/Fishbrain.dll'
dotnet build Fishbrain.LegacyCli/Fishbrain.LegacyCli.csproj -c Release -o data/training/game-teaching-tools

& $py scripts/conversation_data/game_lessons.py --cli $cli --reference data/compiled-conversation-v4-release/train.jsonl --output data/training/my-game-authored
dotnet $cli replay-teaching data/training/my-game-authored data/compiled-my-game-raw
& $py scripts/conversation_data/audit_game_corpus.py data/compiled-my-game-raw data/compiled-my-game --challenge data/conversation-v4/challenge.json --cli $cli
dotnet $cli prepare-game-teaching data/compiled-my-game data/training/my-game-packed
& $py scripts/conversation_data/report_game_data.py data/compiled-my-game data/training/my-game-packed data/training/my-game-data-review
```

Inspect the lessons and audit before starting this bounded experiment:

```powershell
& $py scripts/conversation_data/train_game_lessons.py data/training/my-game-packed data/training/my-game-run --until 6000
& $py scripts/conversation_data/evaluate_game_lessons.py --cli $cli --corpus data/compiled-my-game --run data/training/my-game-run
& $py scripts/conversation_data/game_simulations.py data/game-teaching --diagnostics data/training/my-game-run/trace.jsonl --label my-game-run
dotnet $cli chat data/training/my-game-run/calibrated.fbm
```

To diagnose interactive chat, append a new JSONL trace path after the model path.
Its parent directory must exist; an existing file is rejected to preserve earlier
evidence. Each flushed row records the exact request, seed, returned state, frames,
plan, vetoes, reply and demo-world changes. The trace contains the conversation text.
For example:

```powershell
dotnet $cli chat data/training/game-teaching-run-v4/calibrated.fbm data/training/my-chat-trace.jsonl
```

The schedule is seed 42, batch 32, FP32: 5,000 joint updates (seven understanding
and three realization per ten), then 1,000 decoder-only updates. AdamW uses norm
clipping at 1 and phase-local warmup/cosine schedules at 0.0003/0.0001. It omits
masked-language pretraining. The command rejects endpoints above 6,000.

Every 1,000 updates it saves a complete `training.pt` with optimizer and RNG state
and an inference checkpoint. Resume requires `--resume` with matching dataset,
initial model, implementation, schedule, sampler and precision. Creating a `STOP`
file in the run directory requests a checkpoint and graceful stop. Only this
experiment's progress/checkpoint describes its actual phase; the FBM inference
format retains historical fixed-schedule metadata and is not a resume checkpoint.

Calibration uses only the explicit calibration split and retains the existing
minimum 30 decisions and 99% mutating / 95% read-only precision requirements.
Insufficient coverage leaves a tool disabled. Quality failures retain the candidate;
neither completion nor calibration promotes it. Full training remains a separate decision.

## Results

Results, actual conversations and retained limitations are recorded in
[GAME_TEACHING_RESULTS.md](GAME_TEACHING_RESULTS.md).
