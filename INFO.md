# Fishbrain Project Notes

This is the detailed engineering record for Fishbrain: its goals, revision 8
architecture, data and training pipelines, historical experiments, current
results, and known problems. The [README](README.md) remains the quick-start
guide.

## Purpose and boundaries

Fishbrain is a deliberately tiny, readable GPT-like system for short English NPC
conversations in small games. It is CPU-only, dependency-free beyond .NET 10, and
limited to short inputs and replies. The implementation follows the spirit
of [martinskuta/microgpt](https://github.com/martinskuta/microgpt): keep the entire
learning system small enough for one person to inspect and modify.

The project began as a character-level next-token model, then added local tool
calls and explicit dialogue state. Revision 8 separates three concerns:

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
  WordVocabulary.cs               deterministic input and response vocabularies
  PackedTrainer.cs                contiguous SIMD training forward/backward
  Cognition.cs                    public state types and deterministic reducer
  Value.cs                        scalar inference and forward reference graph
  Tools.cs                        reflected local tool registry

Fishbrain.DataGenerator/
  Program.cs                      data CLI and self-tests
  CorpusPipeline.cs               fetch, filter, compile, split, and audit
  Templates.cs                    project-owned synthetic teaching material
  MarkovChain.cs                  simple word-level variation utility
data/
  sources.json                    pinned external-source manifest
  raw/                            ignored downloaded corpora
  compiled/                       ignored generated JSONL splits
  models/                         checkpoints and experiment roles
  logs/                           ignored experiment logs
```

Downloaded data, compiled datasets, experiment checkpoints, and intermediate
models are ignored by Git. This avoids redistributing external text and keeps the
repository small.

## Current model: checkpoint version 8

### Transformer

| Setting | Value |
|---|---:|
| Embedding dimension | 64 |
| Transformer layers | 1 |
| Attention heads | 4 |
| MLP dimension | 128 |
| Activation | ReLU |
| Total context limit | 128 tokens |
| Active causal attention window | 128 tokens |
| Positional embedding period | 128 |
| Maximum generated output | 64 tokens |
| Initial learning rate | 0.005 |
| Adam beta 1 / beta 2 | 0.85 / 0.99 |
| Adam epsilon | `1e-8` |
| Default seed | 42 |
| Planned teaching steps | 40,000 |

The layer uses learned token and positional embeddings, RMSNorm, causal
multi-head attention, residual connections, and a ReLU MLP. A separate response
output head projects hidden states into the output vocabulary. Three independent linear heads read the
final current-turn representation for 18 intent, 5 affect, and 2 expectation
classes. There are no biases, dropout, batches, GPU paths, tensor libraries, or
external ML packages.

Autograd is scalar reverse mode. Fused dot products and stable fused softmax
cross-entropy reduce graph size. For the single layer, conditioning-only tokens
compute embeddings, normalization, keys, and values; query, attention, MLP, and
vocabulary projection run only where logits are needed. A 100-step Release
fixture improved from 35.929 to 12.727 seconds on the development machine, a
2.82x speedup.

### Tokenizer and vocabulary

V8 uses one token for every lexical word. Apostrophes and hyphens inside a word
remain part of that word, so `DON'T` and `SELF-AWARE` each use one token.
`. , ? ! :` are standalone punctuation tokens. Control, cognition, and state
tokens occupy fixed IDs; corpus words begin at ID 72.

Input and response vocabularies are built in deterministic ordinal order from
the training split and saved in the checkpoint. The response vocabulary contains
only words that can occur in a response, tool selection, tool arguments, or tool
results. Input targets are mapped to response IDs during language training.
Unknown corpus words map to `<UNK>`, which is never eligible for generation.

Normalization uppercases input invariantly, collapses whitespace, canonicalizes
curly apostrophes and common Unicode dashes, repairs punctuation spacing, and
collapses runs of terminal punctuation. Unsupported symbols are rejected.

Dialogue and textual tool results may use punctuation. Tool names and string
arguments remain uppercase alphanumeric identifiers.

## Stateful API and cognition

```csharp
var brain = Brain.Load("data/models/model-v8-latest.json");
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
  LastIntent       one of 18 intents
  LastAffect       one of 5 affects
  ActiveTopic      one of 7 topics
  ActiveGoal       one of 8 goals

TurnPerception     Intent, Affect, ResponseExpected
TurnDecision       Action
ReplyResult        Text, State, Perception, Decision, Tone
```

The intents are unknown, greeting, farewell, wellbeing, identity, assistance,
clarification, activity, silence, gratitude, apology, agreement, refusal,
hostility, game fact, directive, statement, and unsafe directive. User affect is
neutral, friendly, distressed, frustrated, or hostile.

Perception removes a leading `PLAYER` role marker for direct input. For history,
it finds the final `NPC ... PLAYER ...` transition and classifies only that last
player utterance. Response generation still receives the complete normalized
dialogue. Malformed role history is rejected instead of silently classifying an
NPC turn.

Action selection is constrained and deterministic:

1. No response expected produces `NO_RESPONSE`.
2. Game fact produces `CALL_TOOL`.
3. Unknown produces `CLARIFY`.
4. Hostility produces `REFUSE`; a player's refusal produces `RESPOND`.
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
and excludes tool-backed responses. Unseen ordinary dialogue uses free word
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

`data/sources.json` pins source revisions, URLs, SHA-256 hashes, licenses,
attributions, and quotas. Raw and externally derived records stay in ignored
`data/raw` and `data/compiled` directories.

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
dotnet run -c Release --project Fishbrain -- teach data/compiled data/models/model-v8-latest.json
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
dotnet run -c Release --project Fishbrain -- teach data/compiled data/models/model-v8-latest.json --planned 40000 --until 2000
dotnet run -c Release --project Fishbrain -- teach data/compiled data/models/model-v8-latest.json --planned 40000 --until 10000
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
dotnet run -c Release --project Fishbrain -- evaluate data/compiled/test.jsonl data/models/model-v8-latest.json
```

Evaluation disables memory and reports intent and affect accuracy/macro-F1,
response/no-response precision and recall, action accuracy, realization loss,
generation validity, source and synthetic-family breakdowns, confusion matrix,
individual golden contrasts, the v8 stage gate, and the long-term release gate.

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

### 8. Completed v5 run and SIMD training kernel

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
with four `double` values per vector. Three initial variants were measured from
the same step-7,000 checkpoint for the same deterministic 100 updates:

| Variant | Time | Change from 15.611 s baseline |
|---|---:|---:|
| Vectorized fused dot/cross-entropy and gathered Adam buffers | 16.439 s | 5.3% slower |
| Vectorized fused dot/cross-entropy only | 17.152 s | 9.9% slower |
| Scalar graph with Adam bias correction hoisted | 15.688 s | 0.5% slower |

Those variants were reverted. `Value[]` stores references to scalar objects, so
SIMD first required gathering data into temporary arrays and lost more time than
the arithmetic saved.

`PackedTrainer` implements the required layout change. Model weights, gradients,
and Adam state remain in contiguous `double[]` arrays while training. Fused
matrix-vector forward/backward, attention, RMSNorm, residual, ReLU,
cross-entropy, and Adam kernels use `Vector<double>` directly. Scalar `Value`
inference is synchronized only when validation or interactive use needs it.
Checkpoint version 5 and parameter ordering did not change.

The identical step-7,000 to step-7,100 fixture now measures:

| Variant | Time | Relative speed |
|---|---:|---:|
| Original scalar graph | 15.611 s | 1.00x |
| Packed SIMD training kernel | 2.164 s | 7.21x |

Language and perception gradients have finite-difference coverage. The packed
forward loss is also checked against the scalar forward path. Deterministic
uninterrupted-versus-resumed checkpoint tests continue to pass.

The saved step-7,000 checkpoint was then resumed through the planned step 40,000.
Final trainer validation was:

| Metric | Step 40,000 |
|---|---:|
| Intent macro-F1 | 0.6338 |
| Affect macro-F1 | 0.7832 |
| Expected-response F1 | 0.9970 |
| Direct intent macro-F1 | 0.6577 |
| History intent macro-F1 | 0.6682 |
| Realization loss | 2.0685 |

The independent 1,000-row test evaluation reported:

| Metric | Result |
|---|---:|
| Intent accuracy / macro-F1 | 0.6205 / 0.6184 |
| Affect accuracy / macro-F1 | 0.7902 / 0.8045 |
| Expected-response precision / recall / F1 | 1.0000 / 0.9874 / 0.9936 |
| No-response precision / recall / F1 | 0.4444 / 1.0000 / 0.6154 |
| Action accuracy | 0.8850 |
| Realization loss, 100 samples | 1.8091 |
| Invalid / empty / overlength generation | 2% / 0% / 0% |
| Direct / history intent macro-F1 | 0.6047 / 0.6489 |
| Synthetic / external intent macro-F1 | 0.6720 / 0.3358 |

The first-experiment cognition thresholds pass, but `V5_STAGE_GATE` and
`RELEASE_GATE` still fail. One of six golden contrasts passes, and the 2% invalid
generation rate misses the required 0%. The finished checkpoint is suitable for
interactive testing, not a release claim.

References: [.NET SIMD guidance](https://learn.microsoft.com/dotnet/standard/simd),
[`Vector.IsHardwareAccelerated`](https://learn.microsoft.com/dotnet/api/system.numerics.vector.ishardwareaccelerated?view=net-10.0),
and [`Vector<T>.Count`](https://learn.microsoft.com/dotnet/api/system.numerics.vector-1.count?view=net-10.0).

## Revision 6 implementation and experiment

V6 addresses the remaining v5 output and sparse-construction failures without
increasing the Transformer width or depth.

- Character tokens were replaced by deterministic word vocabularies. A lexical
  word, contraction, or hyphenated term is one token; punctuation stays separate.
- The input embedding and response output head are no longer tied. The trained
  corpus produced 3,582 input words, 923 output words, and 339,392 parameters.
- Context, attention, and positional periods are all 128 tokens. Conditioning is
  limited to 96 tokens and a response target to 32 during teaching.
- Output constraints reject `<UNK>`, punctuation as the first token, consecutive
  punctuation, three identical tokens, repeated trigrams, and an incomplete
  comma or colon ending.
- Player refusal is distinct from NPC refusal: it selects `RESPOND`, preserves
  hostile state handling, and learns short acknowledgement responses.
- The corpus contains 84 state-varied golden anchors covering seven behaviors,
  including `PLAYER HEY I DON'T WANT TO HELP YOU, IDIOT`.
- Checkpoint version 6 stores both vocabularies and the separate output head.
  Versions 2 through 5 fail with an explicit fresh-start message.

The final fresh 40,000-step checkpoint reported these trainer-validation values:

| Metric | Step 40,000 |
|---|---:|
| Intent macro-F1 | 0.9068 |
| Affect macro-F1 | 0.9208 |
| Expected-response F1 | 0.9970 |
| Direct / history intent macro-F1 | 1.0000 / 1.0000 |
| Realization loss | 2.4161 |

The independent 1,000-row test evaluation of `model-v6-latest.json` reported:

| Metric | Result |
|---|---:|
| Intent accuracy / macro-F1 | 0.9205 / 0.9240 |
| Affect accuracy / macro-F1 | 0.8750 / 0.8865 |
| Expected-response F1 | 0.9987 |
| No-response F1 | 0.8889 |
| Action accuracy | 0.9975 |
| Realization loss | 1.7615 |
| Invalid / empty / overlength generation | 0% / 0% / 0% |
| Synthetic intent accuracy / macro-F1 | 1.0000 / 1.0000 |
| External intent accuracy / macro-F1 | 0.7500 / 0.5913 |
| Direct / history intent macro-F1 | 0.9007 / 1.0000 |
| Golden behavior cases | 7 / 7 |

`V6_STAGE_GATE` passes. The long-term `RELEASE_GATE` still fails because external
intent macro-F1 remains below 0.70. `model-v6-latest.json` is the recommended
interactive checkpoint: the best-perception and best-realization snapshots have
better isolated selection metrics but regress one or more golden behaviors.

The required hostile-refusal probe is deterministic for uppercase and lowercase
ASCII input:

```text
PLAYER HEY I DON'T WANT TO HELP YOU, IDIOT
THEN STEP ASIDE.

RAPPORT=0 MOOD=ANNOYED INTENT=REFUSAL AFFECT=HOSTILE EXPECTED=TRUE
ACTION=RESPOND TOPIC=RELATIONSHIP GOAL=DEESCALATE TONE=COLD
```

## Revision 7 implementation and experiment

The first interactive v6 test showed that benchmark gains did not guarantee a
usable conversation. It misclassified an identity request as silence, a follow
command as farewell, and `I WILL NOT ASK` as refusal. Generated replies could be
structurally valid but grammatically incoherent.

V7 deliberately breaks checkpoint compatibility to address those failures:

- `DIRECTIVE` and `STATEMENT` expand the intent head from 15 to 17 classes.
- Fixed control tokens now occupy IDs 0-69; corpus words begin at ID 70.
- Refusal annotation requires an actual refused action rather than matching every
  occurrence of `WILL NOT`.
- Identity and wellbeing constructions cover `TELL ME SOMETHING ABOUT YOURSELF`
  and `WHY YOU WORRY` directly and through paraphrases.
- The chat CLI retains role history. Missing terminal punctuation is repaired in
  the stored history, and the parser selects the last player turn even when a
  no-response action creates consecutive player markers.
- Checkpoints store a clean response catalog built only from project-owned
  synthetic responses. A generated reply outside that catalog falls back to a
  deterministic candidate selected by intent, tone, and lexical overlap.
- The evaluator replays the complete four-turn failure transcript. Direct
  one-line checks are not sufficient for stage-gate acceptance.
- Every data artifact now lives below `data/`; only the source manifest and
  historical v4 checkpoints are tracked.

The regenerated 10,000-row corpus contains 380 directive rows, 380 statement
rows, and 132 state-varied golden rows. The fresh 40,000-step v7 run ended with
these trainer-validation values:

| Metric | Step 40,000 |
|---|---:|
| Intent macro-F1 | 0.9112 |
| Affect macro-F1 | 0.8815 |
| Expected-response F1 | 1.0000 |
| Direct / history intent macro-F1 | 1.0000 / 0.9683 |
| Realization loss | 1.6876 |

Independent evaluation on 1,000 test records reported:

| Metric | Result |
|---|---:|
| Intent accuracy / macro-F1 | 0.9216 / 0.9292 |
| Affect accuracy / macro-F1 | 0.8696 / 0.8778 |
| Expected-response F1 | 1.0000 |
| No-response F1 | 1.0000 |
| Action accuracy | 1.0000 |
| Realization loss | 1.5372 |
| Invalid / empty / overlength generation | 0% / 0% / 0% |
| Synthetic intent accuracy / macro-F1 | 0.9883 / 0.9880 |
| External intent accuracy / macro-F1 | 0.7786 / 0.6311 |
| Perception golden cases | 11 / 11 |
| Sequential transcript cases | 4 / 4 |

The real CLI replay is:

```text
TELL ME SOMETHING ABOUT YOURSELF -> I AM A TRAVELER FROM THIS VILLAGE.
WHY YOU WORRY                    -> I DO NOT WORRY.
I WILL NOT ASK                   -> [NO RESPONSE]
FOLLOW ME, DUDE!                 -> I WILL FOLLOW YOU.
```

`V7_STAGE_GATE` passes. `RELEASE_GATE` still fails because external intent
macro-F1 `0.6311` remains below the long-term `0.70` requirement.

## Revision 8 implementation and experiment

V8 addresses the next interactive transcript: assistance word order, activity
questions, short clarification follow-ups, dangerous commands, and compound
movement directives.

- `UNSAFE_DIRECTIVE` expands perception to 18 intents. Its deterministic action
  is `REFUSE`, and its state transition selects the new `AVOID_DANGER` goal.
- Questions, greetings, farewells, directives, and explicit corrections have
  runtime semantic constraints. Explicit corrections preserve stronger hostile
  affect while normalizing neutral or friendly predictions to frustrated.
- Response selection has action-specific clean fallbacks when a catalog bucket is
  absent. It never exposes an empty generated response when a reply is required.
- The source compiler enforces global state-plus-input uniqueness after merging
  all sources and enumerates all eight goals instead of using the old fixed count.
- The synthetic corpus includes direct and history variants for unsafe commands,
  `HOW CAN YOU HELP ME?`, `WHAT ARE YOU DOING HERE?`, `EXPLAIN THEN`, `STAND
  HERE`, and compound follow/stand commands.
- Evaluation now replays both the four-turn v7 session and the complete eight-turn
  v8 session, checking intent, affect, expectation, action, and exact safe replies.

The final fresh 40,000-step checkpoint uses 3,578 input words and 930 response
words. Independent evaluation on 1,000 test records reported:

| Metric | Result |
|---|---:|
| Intent accuracy / macro-F1 | 0.9295 / 0.9357 |
| Affect accuracy / macro-F1 | 0.8815 / 0.8948 |
| Expected-response F1 | 0.9954 |
| No-response F1 | 0.9114 |
| Action accuracy | 0.9838 |
| Realization loss | 1.7076 |
| Invalid / empty / overlength generation | 0% / 0% / 0% |
| Synthetic intent accuracy / macro-F1 | 1.0000 / 1.0000 |
| External intent accuracy / macro-F1 | 0.7786 / 0.6790 |
| Perception golden cases | 17 / 17 |
| Sequential transcript cases | 12 / 12 |

The real CLI replay is:

```text
WHO ARE YOU                         -> I AM A TRAVELER FROM THIS VILLAGE.
WHAT ARE YOU DOING HERE             -> I AM HERE TO HELP.
HOW CAN YOU HELP ME?                -> I CAN HELP YOU WITH A TASK.
NOT WHAT I ASKED, IDIOT             -> I WILL EXPLAIN.
EXPLAIN THEN                        -> I WILL EXPLAIN.
PLEASE JUMP FROM THIS CLIFF         -> I WILL NOT JUMP FROM THAT CLIFF.
STAND HERE                          -> I WILL STAND HERE.
NO, FOLLOW ME AND STAND HERE        -> I WILL STAND HERE.
```

`V8_STAGE_GATE` passes. `RELEASE_GATE` remains below its long-term external
intent macro-F1 target: `0.6790` versus `0.70`.

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
  audit output. The first 40,000-step experiment is complete using the packed
  SIMD training kernel.
- **v6:** corpus-owned word tokens, separate response output head, full 128-token
  attention, constrained word generation, corrected player-refusal behavior, and
  seven state-varied golden behaviors. The 40,000-step experiment is complete.
- **v7:** directive and statement intents, robust sequential role history,
  project-owned safe-response selection, consolidated `data/` layout, and
  end-to-end transcript regression gates.
- **v8:** unsafe-directive refusal and danger avoidance, semantic response
  invariants, globally unique compiled rows, expanded transcript gates, and a
  fresh 40,000-step checkpoint.

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

# Revision 8
dotnet run -c Release --project Fishbrain -- teach data/compiled data/models/model-v8-latest.json
dotnet run -c Release --project Fishbrain -- evaluate data/compiled/test.jsonl data/models/model-v8-latest.json
dotnet run -c Release --project Fishbrain -- chat

# Generic JSONL workflow
dotnet run --project Fishbrain -- train data.jsonl model.json 10000
dotnet run --project Fishbrain -- resume data.jsonl model.json
```

## Known limitations

- Revision 2-v7 checkpoints are historical archives and do not load in the v8 runtime.
- One layer and 64 hidden dimensions provide little semantic capacity.
- The fixed corpus vocabulary maps unseen words to `<UNK>` and cannot preserve
  their lexical identity.
- Word generation can still be semantically weak despite passing structural
  validity constraints.
- Exact memory can hide poor generalization, so evaluation turns it off.
- A tool result is authoritative context, but free paraphrasing may distort it;
  strict guarantees need tool-owned text or deterministic substitution.
- Tools are synchronous, local, limited to one call, and require training rows.
- Raw and derived external datasets are deliberately not committed.
- NPC state simulates behavior; it is not evidence of subjective emotion or
  consciousness.
