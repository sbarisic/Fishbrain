# Fishbrain Project Notes

This is the detailed engineering record for Fishbrain: its goals, revision 5
architecture, data and training pipelines, historical v4 results, current
experiment, and known problems. The [README](README.md) remains the quick-start
guide.

## Purpose and boundaries

Fishbrain is a deliberately tiny, readable GPT-like system for short English NPC
conversations in small games. It is CPU-only, dependency-free beyond .NET 10, and
limited to 256-character inputs and replies. The implementation follows the spirit
of [martinskuta/microgpt](https://github.com/martinskuta/microgpt): keep the entire
learning system small enough for one person to inspect and modify.

The project began as a character-level next-token model, then added local tool
calls and explicit dialogue state. Revision 5 separates three concerns:

```text
LANGUAGE -> PERCEPTION -> BEHAVIOR

player text + previous NPC state
                |
                v
  intent + affect + response expected
                |
                v
      deterministic action selection
                |
                +----> optional registered C# tool
                |
                v
      deterministic NPC state reducer
                |
                v
       tone-conditioned text generation
```

The neural model perceives the turn and writes language. Ordinary game logic,
state transitions, action validity, and tool execution remain explicit C# code.
A tiny language model is not treated as an authoritative game-state database.

Design priorities are readability, deterministic experiments, explicit state,
and hackability—not throughput or general-purpose language ability. Dynamic facts
come from registered local tools rather than model memory.

## Repository map

```text
Fishbrain.slnx
README.md                         quick start and public overview
INFO.md                           this engineering record

Fishbrain/
  Program.cs                      CLI and dependency-free self-tests
  Brain.cs                        model, tokenizer, training, checkpoints, eval
  Cognition.cs                    public state types and deterministic reducer
  Value.cs                        scalar reverse-mode autograd
  Tools.cs                        reflected local tool registry

Fishbrain.DataGenerator/
  Program.cs                      data CLI and self-tests
  CorpusPipeline.cs               fetch, filter, compile, split, and audit
  Templates.cs                    project-owned synthetic teaching material
  MarkovChain.cs                  simple word-level variation utility
  sources.json                    pinned external-source manifest
```

Downloaded data, compiled datasets, experiment checkpoints, and intermediate
models are ignored by Git. This avoids redistributing external text and keeps the
repository small.

## Current model: checkpoint version 5

### Transformer

| Setting | Value |
|---|---:|
| Embedding dimension | 64 |
| Transformer layers | 1 |
| Attention heads | 4 |
| MLP dimension | 128 |
| Activation | ReLU |
| Total context limit | 256 tokens |
| Active causal attention window | 64 tokens |
| Positional embedding period | 64 |
| Maximum generated output | 256 characters |
| Initial learning rate | 0.005 |
| Adam beta 1 / beta 2 | 0.85 / 0.99 |
| Adam epsilon | `1e-8` |
| Default seed | 42 |
| Planned teaching steps | 40,000 |

The layer uses learned token and positional embeddings, RMSNorm, causal
multi-head attention, residual connections, and a ReLU MLP. Language output
weights are tied to token embeddings. Three independent linear heads read the
final current-turn representation for 15 intent, 5 affect, and 2 expectation
classes. There are no biases, dropout, batches, GPU paths, tensor libraries, or
external ML packages.

Autograd is scalar reverse mode. Fused dot products and stable fused softmax
cross-entropy reduce graph size. For the single layer, conditioning-only tokens
compute embeddings, normalization, keys, and values; query, attention, MLP, and
vocabulary projection run only where logits are needed. A 100-step Release
fixture improved from 35.929 to 12.727 seconds on the development machine, a
2.82x speedup.

### Tokenizer and vocabulary

The 44 visible characters are:

```text
ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 .,?!'-:
```

The v5 language vocabulary retains the 105-token v4 layout:

| IDs | Meaning |
|---:|---|
| 0-25 | `A-Z` |
| 26-35 | `0-9` |
| 36 | space |
| 37-43 | `. , ? ! ' - :` |
| 44-49 | `BOS SEP EOS TEXT CALL RESULT` |
| 50-51 | `STATE DECIDE` |
| 52-55 | four rapport levels |
| 56-59 | four moods |
| 60-74 | fifteen dialogue intents |
| 75-78 | original four response actions |
| 79-82 | four tones |
| 83-89 | seven topics |
| 90-96 | seven goals |
| 97-101 | five user-affect classes |
| 102-103 | response not expected / expected |
| 104 | no-response action |

Normalization uppercases input, collapses whitespace, canonicalizes curly
apostrophes and common Unicode dashes, repairs punctuation spacing, and collapses
runs of terminal punctuation. Unsupported symbols are rejected. Training rows
must already use canonical spelling and spacing.

Dialogue and textual tool results may use punctuation. Tool names and string
arguments remain uppercase alphanumeric identifiers.

## Stateful API and cognition

```csharp
var brain = Brain.Load("model-v5-latest.json");
var state = NpcState.Initial;

ReplyResult result = brain.Reply(
    "PLAYER HELLO, HOW ARE YOU?",
    state,
    temperature: 0.2);

state = result.State;
Console.WriteLine(result.Text);
Console.WriteLine(result.Perception);
```

`NpcState.Initial` starts with rapport 1, neutral mood, unknown last intent,
neutral last affect, no topic, and no goal. The public data is:

```text
NpcState
  Rapport          0-3
  Mood             Neutral, Friendly, Cautious, Annoyed
  LastIntent       one of 15 intents
  LastAffect       one of 5 affects
  ActiveTopic      one of 7 topics
  ActiveGoal       one of 7 goals

TurnPerception     Intent, Affect, ResponseExpected
TurnDecision       Action
ReplyResult        Text, State, Perception, Decision, Tone
```

The intents are unknown, greeting, farewell, wellbeing, identity, assistance,
clarification, activity, silence, gratitude, apology, agreement, refusal,
hostility, and game fact. User affect is neutral, friendly, distressed,
frustrated, or hostile.

Perception removes a leading `PLAYER` role marker for direct input. For history,
it finds the final `NPC ... PLAYER ...` transition and classifies only that last
player utterance. Response generation still receives the complete normalized
dialogue. Malformed role history is rejected instead of silently classifying an
NPC turn.

Action selection is constrained and deterministic:

1. No response expected produces `NO_RESPONSE`.
2. Game fact produces `CALL_TOOL`.
3. Unknown produces `CLARIFY`.
4. Refusal or hostility produces `REFUSE`.
5. Everything else produces `RESPOND`.

A no-response turn returns an empty string while still updating state. The CLI
prints it as `[NO RESPONSE]`.

The reducer updates rapport, mood, topic, goal, and tone. Hostile intent or affect
decreases rapport and produces cautious or annoyed mood. Friendly gratitude or
apology can increase rapport. Frustrated or distressed affect produces cautious
mood; distress prioritizes `HELP_PLAYER`; hostility prioritizes `DEESCALATE`.
A successful game-fact tool clears `RESOLVE_GAME_FACT`. Tone is derived directly
from mood: neutral, warm, calm, or cold.

The caller owns and persists returned state. The model has no hidden state across
calls.

Seen ordinary responses may be recalled through exact memory keyed by normalized
input, pre-turn state, perception, decision, and tone. Memory runs after cognition
and excludes tool-backed responses. Unseen ordinary dialogue uses free character
generation. Evaluation disables exact memory so it measures generalization.

## Local C# tools

```csharp
sealed class PlayerTools(PlayerState player)
{
    [GameTool("GETGOLD")]
    public int GetGold(string playerId) => player.Gold;
}

brain.Tools.Register(new PlayerTools(player));
```

Registration scans only public instance methods marked with `GameTool`. Names
must be unique uppercase alphanumeric identifiers, no longer than 32 characters,
and present in the checkpoint's trained-tool set. Parameters may be non-negative
integers or uppercase alphanumeric identifier strings. Results may be integers,
booleans, or normalized text up to 64 characters.

Only one synchronous tool call is allowed per reply. Unknown tools, malformed
arguments, exceptions, and invalid results return `I DO NOT KNOW.`. Unregistered
methods never run. Tool choice must be represented in the training corpus.

## Data pipeline

```powershell
dotnet run --project Fishbrain.DataGenerator -- fetch
dotnet run --project Fishbrain.DataGenerator -- compile --count 10000 --seed 42
dotnet run --project Fishbrain.DataGenerator -- audit
```

`sources.json` pins source revisions, URLs, SHA-256 hashes, licenses,
attributions, and quotas. Raw and externally derived records stay in ignored
`datasets/raw` and `datasets/compiled` directories.

| Source | Rows | Use |
|---|---:|---|
| Project-owned synthetic contrasts | 6,000 | perception and realization |
| OpenAssistant OASST1, Apache-2.0 | 2,000 | short conversational pairs |
| CLINC150, CC BY 3.0 | 800 | decision-only intent supervision |
| GoEmotions, Apache-2.0 | 1,200 | decision-only affect supervision |

V5 applies source-specific head masks. Synthetic and OASST1 rows supervise all
three perception heads; CLINC150 supervises intent only; GoEmotions supervises
affect only. This prevents heuristic labels from one imported ontology from
becoming authoritative targets for unrelated heads. `audit` reports counts and
examples for every split, source, intent, affect, expectation, direct/history
form, CLINC150 label family, and GoEmotions affect family.

Compilation produces deterministic 8,000/1,000/1,000 train, validation, and
test splits. Splitting occurs by source conversation, source record, or synthetic
contrast group before expansion so related paraphrases cannot leak across splits.
Ambiguous imports go to `review.jsonl` and are excluded rather than guessed.

Rows may contain a paired response, a `null` decision-only response, an empty
no-response target, or a manually authored tool example. They include source,
split, and stable group metadata. Filtering rejects unsupported characters, code,
URLs, markup, unsafe content, unresolved names, non-English text, overlength
text, and unsuitable factual/advice material. Wikipedia, dictionaries, and other
generic factual corpora are intentionally excluded; dynamic game facts use tools.

The substantive speech act outranks surface wording:

| Input | Target perception |
|---|---|
| `HELLO, HOW ARE YOU?` | wellbeing |
| `THAT IS NOT WHAT I ASKED.` | clarification + frustrated |
| `WHAT?` | clarification |
| `THANK YOU, IDIOT.` | gratitude + hostile |
| `I WAS NOT THANKING YOU.` | clarification + frustrated |
| `I AM JUST LOOKING AROUND.` | activity + no response expected |

For multi-label affect, precedence is hostile, frustrated, distressed, friendly,
then neutral.

## Teaching, checkpoints, and recovery

```powershell
dotnet run -c Release --project Fishbrain -- teach datasets/compiled model-v5-latest.json
```

The planned curriculum is:

| Steps | Phase | Sampling |
|---:|---|---|
| 1-2,000 | language warmup | realization only |
| 2,001-40,000 | interleaved | alternating 50% perception and 50% realization; periodic tools |

Each phase has a 500-step learning-rate warmup and phase-local linear decay.
Global gradient norm is clipped to 1.0. Checkpoints store Adam moments, RNG,
phase, sampler position, step, planned schedule, classifier-head state, and
separate best-perception and best-realization metadata.

Milestones pause without changing the schedule:

```powershell
dotnet run -c Release --project Fishbrain -- teach datasets/compiled model-v5-latest.json --planned 40000 --until 2000
dotnet run -c Release --project Fishbrain -- teach datasets/compiled model-v5-latest.json --planned 40000 --until 10000
```

`--planned` controls phase boundaries and decay; `--until` only pauses. A resumed
checkpoint rejects a conflicting plan. Teaching saves atomically every 1,000
steps and at a requested milestone, then flushes an absolute PowerShell resume
command. A hard crash loses at most 999 steps.

Every 1,000 steps, validation scores up to 512 supervised examples per
perception head and 64 realization samples. It reports direct/history intent
macro-F1, affect macro-F1, expectation F1, and realization loss. Intent macro-F1
selects `best-perception`; realization loss selects `best-realization`. Use the
full evaluator for milestone acceptance:

```powershell
dotnet run -c Release --project Fishbrain -- evaluate datasets/compiled/test.jsonl model-v5-latest.json
```

Evaluation disables memory and reports intent and affect accuracy/macro-F1,
response/no-response precision and recall, action accuracy, realization loss,
generation validity, source and synthetic-family breakdowns, confusion matrix,
individual golden contrasts, the staged v5 gate, and the long-term release gate.

## Revision 4 experiment record

| Metric | Step 8,000 | Step 24,000 | Step 32,000 |
|---|---:|---:|---:|
| Intent accuracy | 0.0420 | 0.2320 | 0.1680 |
| Intent macro-F1 | 0.0157 | 0.2140 | 0.1554 |
| Affect accuracy | 0.2360 | - | 0.6370 |
| Affect macro-F1 | 0.1250 | 0.6540 | 0.6607 |
| Response-expected F1 | 0.9362 | 0.9430 | 0.9485 |
| No-response F1 | 0.0000 | 0.5800 | 0.5660 |
| Action accuracy | 0.7300 | 0.6540 | 0.7200 |
| Realization loss | 1.6572 | 11.7170 | 2.2853 |
| Invalid generation | 1% | 42% | 12% |
| Empty generation | 0% | - | 1% |
| Overlength generation | 0% | - | 0% |
| Golden cases | fail | fail | fail |
| Release gate | fail | fail | fail |

At step 32,000, source intent accuracies were 0.1000 for CLINC150, 0.1000
for GoEmotions, 0.2200 for OASST1, and 0.1733 for synthetic data. Synthetic
held-out intent macro-F1 was 0.1517; external held-out macro-F1 was 0.1086.

The original long-term release gates are synthetic intent macro-F1 at least
0.85, external intent macro-F1 at least 0.70, affect macro-F1 at least 0.75,
response-expectation F1 at least 0.90, entirely valid output, and correct golden
cases. Revision 4 does not approach the intent gates.

### Conclusions

- Language-only training learned surface realization but essentially no
  perception, as expected.
- Perception training improved classification while catastrophically forgetting
  language: realization loss reached 11.717 and 42% of generations were invalid.
- Joint training repaired language to 2.285 loss and 12% invalid output, but
  intent macro-F1 regressed from 0.214 to 0.155.
- Affect and response expectation learned more reliably than 15-way intent.
- Action accuracy overstates semantic success because many intents map to the
  same deterministic action.
- Step-32 confusions collapsed toward apology, wellbeing, and assistance.
- History families were usually much worse than direct turns, often scoring zero.
- Low individual perception training losses plus poor held-out results indicate
  memorization and weak generalization, not simply too few steps.

Continuing the unchanged run from 32,000 to 40,000 may improve language further,
but there is no evidence it will recover intent. The recommended stopping point
is 32,000. Step 24,000 is the strongest observed cognition snapshot; step 32,000
is the better compromise. Checkpoints match `model.*.json`, are ignored by Git,
and should be backed up elsewhere if important.

## Revision 5 implementation

V5 changes the learning interface before increasing model capacity. The working
hypothesis is that generating classification symbols through the same tied
105-way character output matrix caused avoidable competition between perception
and language. The implementation is complete; a full 40,000-step experiment has
not yet been run.

### 1. Three dedicated perception heads

Keep the small Transformer and read a hidden representation through:

```text
current player utterance -> shared representation
                              |-> intent logits       (15)
                              |-> affect logits        (5)
                              `-> expected logits      (2)
```

Each uses fused cross-entropy. There is no action head: action remains
deterministic. Symbolic cognition tokens may still condition response generation,
but perception should no longer be trained through the language vocabulary.

### 2. Current-turn classification and full-context generation

Perception sees the latest player utterance; realization sees the full
available history, updated state, perception, action, tone, and tool result.

The string API uses deterministic role parsing and rejects malformed histories.
The original complete input is preserved for generation. Tests cover direct,
multi-turn, malformed, and ordinary uses of the word `PLAYER`. This directly
targets the direct/history performance gap without first increasing the
attention window.

### 3. Interleaved training

The first fresh-checkpoint experiment is:

1. 2,000 language warmup steps.
2. 38,000 interleaved steps with 50% perception and 50% realization.
3. Balance perception across intent, affect, and expectation buckets.
4. Ensure every short interval contains both tasks; avoid another 16,000-step
   single-task block.

If interference persists, compare one controlled variant that freezes the shared
encoder on some realization updates. Do not change capacity, corpus, and schedule
simultaneously because the cause would become unmeasurable.

### 4. Task-aware validation and checkpoint selection

The trainer evaluates a meaningful deterministic validation subset at every
milestone. It tracks direct/history intent macro-F1, affect macro-F1,
expected-response F1, and realization loss. The full evaluator adds
synthetic/external breakdowns, no-response F1, and generation validity.

It maintains atomic roles:

```text
model-v5-latest.json
model-v5-best-perception.json
model-v5-best-realization.json
```

Best perception is selected by intent macro-F1. Best realization is selected by
loss; the full milestone evaluator separately enforces valid output. The old
16-sample averaged validation loss is no longer a selection criterion.

### 5. Supervision audit

- `audit` reports counts and examples for every split, source, intent, affect,
  expectation, direct/history form, and external label family.
- CLINC150 trains only intent; GoEmotions trains only affect. Their heuristic
  cross-task labels remain in the stable schema but do not produce gradients.
- Inspect why apology, wellbeing, and assistance attract unrelated examples.
- Add contrast groups for gratitude/apology, clarification/unknown,
  identity/wellbeing, agreement/refusal, and hostile intent/hostile affect.
- Audit CLINC150 and GoEmotions mappings rather than assuming their ontologies
  align cleanly with Fishbrain.
- Keep paraphrase groups together while ensuring held-out constructions are
  learnable from training language.
- Keep ambiguous examples in review output.
- Test current-turn extraction separately from classification.
- Add linguistic variety rather than duplicating rows; duplication lowers loss
  without adding semantic coverage.

### 6. Staged acceptance gates

The evaluator keeps the original release gates as long-term goals and reports a
separate first-experiment v5 gate:

- Intent macro-F1 must beat the v4 peak of 0.214 and rise at milestones.
- History intent performance must be materially above zero and close the direct
  gap.
- Affect macro-F1 must remain at least 0.65.
- Response-expected F1 must remain at least 0.94.
- Joint realization loss should remain below 3.0.
- Invalid, unexpected-empty, and overlength rates should be 0%.
- Report every golden contrast independently.

Only after this succeeds should the project consider 96-dimensional embeddings,
a second layer, a 128-token attention window, or a larger corpus.

### 7. Checkpoint and compatibility

- Checkpoint version 5 includes head weights, Adam moments, exact sampler state,
  and separate best metrics.
- V4 checkpoints fail with an explicit fresh-start message; they remain archives.
- `Reply`, `NpcState`, the reducer, tool API, visible alphabet, and dataset schema
  are preserved.
- Deterministic uninterrupted-versus-resumed equivalence covers the new schedule
  and parameter set.

### 8. Step-7,000 milestone and SIMD investigation

The first v5 run was paused after the atomic step-7,000 checkpoint. Its trainer
validation was:

| Metric | Step 7,000 |
|---|---:|
| Intent macro-F1 | 0.0919 |
| Affect macro-F1 | 0.6658 |
| Expected-response F1 | 0.9706 |
| Direct intent macro-F1 | 0.0625 |
| History intent macro-F1 | 0.0560 |
| Realization loss | 2.4176 |
| Best realization loss so far | 2.1436 at step 2,000 |

The full 1,000-row evaluator at the same checkpoint reported intent macro-F1
`0.0987`, affect macro-F1 `0.7103`, expected-response F1 `0.9695`, no-response
F1 `0.2295`, and realization loss `2.1201`. Of 100 generated replies, 3% were
invalid and 4% were unexpectedly empty. All six golden contrasts and both
acceptance gates still failed.

The development machine reports hardware-accelerated `System.Numerics.Vector<T>`
with four `double` values per vector. Three variants were measured from the same
step-7,000 checkpoint for the same deterministic 100 updates:

| Variant | Time | Change from 15.611 s baseline |
|---|---:|---:|
| Vectorized fused dot/cross-entropy and gathered Adam buffers | 16.439 s | 5.3% slower |
| Vectorized fused dot/cross-entropy only | 17.152 s | 9.9% slower |
| Scalar graph with Adam bias correction hoisted | 15.688 s | 0.5% slower |

No experimental optimization was retained. `Value[]` stores references to scalar
objects, so SIMD requires gathering values into temporary contiguous buffers.
For the current 16-128 element operations, that work costs more than the vector
arithmetic saves. This matches Microsoft's warning that SIMD can regress when
the workload or memory layout is unsuitable.

The useful next optimization is an operation-level training tape with contiguous
`double[]` activations and gradients. Keep the scalar `Value` path as the readable
reference, then add fused matrix-vector forward/backward, RMSNorm, residual,
ReLU, and Adam kernels over spans. Those kernels can use `Vector<double>` without
gather/scatter overhead; outer matrix rows can then be evaluated for bounded
multicore parallelism. Every kernel must retain numeric-gradient tests and exact
uninterrupted-versus-resumed checkpoint tests.

References: [.NET SIMD guidance](https://learn.microsoft.com/dotnet/standard/simd),
[`Vector.IsHardwareAccelerated`](https://learn.microsoft.com/dotnet/api/system.numerics.vector.ishardwareaccelerated?view=net-10.0),
and [`Vector<T>.Count`](https://learn.microsoft.com/dotnet/api/system.numerics.vector-1.count?view=net-10.0).

## Revision history

- **v1:** uppercase character GPT for one NPC reply, using microGPT-style scalar
  autograd.
- **v2:** procedural training data experiments and one-call local C# tools.
- **v3:** punctuation, explicit NPC state, deterministic reducer, tone, and
  state-aware realization.
- **v4:** 64-dimensional model, user affect, response expectation, manifest data
  pipeline, 10,000-row curriculum, evaluation, training optimizations, and safe
  milestone resume commands.
- **v5:** dedicated perception heads, current-turn classification, interleaved
  training, task-aware validation, source-specific supervision, and richer data
  audit output. Full experiment results are pending.

## Command reference

```powershell
# Build and test
dotnet build Fishbrain.slnx
dotnet run --project Fishbrain -- selftest
dotnet run --project Fishbrain.DataGenerator -- selftest

# Corpus
dotnet run --project Fishbrain.DataGenerator -- fetch
dotnet run --project Fishbrain.DataGenerator -- compile --count 10000 --seed 42
dotnet run --project Fishbrain.DataGenerator -- audit

# Revision 5
dotnet run -c Release --project Fishbrain -- teach datasets/compiled model-v5-latest.json
dotnet run -c Release --project Fishbrain -- evaluate datasets/compiled/test.jsonl model-v5-latest.json
dotnet run -c Release --project Fishbrain -- chat model-v5-latest.json

# Generic JSONL workflow
dotnet run --project Fishbrain -- train data.jsonl model.json 10000
dotnet run --project Fishbrain -- resume data.jsonl model.json
```

## Known limitations

- Revision 4 checkpoints are historical archives and do not load in the v5 runtime.
- Revision 5 has not completed its first full 40,000-step experiment.
- One layer and 64 character dimensions provide little semantic capacity.
- The active 64-token window discards older detail despite a 256-token context.
- Character learning is simple but inefficient for word-level semantics.
- Free generation can be invalid or nonsensical; the current compromise measured
  12% invalid output.
- Exact memory can hide poor generalization, so evaluation turns it off.
- A tool result is authoritative context, but free paraphrasing may distort it;
  strict guarantees need tool-owned text or deterministic substitution.
- Tools are synchronous, local, limited to one call, and require training rows.
- Raw and derived external datasets are deliberately not committed.
- NPC state simulates behavior; it is not evidence of subjective emotion or
  consciousness.
