# Fishbrain

Fishbrain is a deliberately tiny, hackable GPT-like model written in C# and
.NET 10 for short video-game NPC conversations. It is CPU-only and has no
external NuGet dependencies. The project follows the inspectable spirit of
[martinskuta/microgpt](https://github.com/martinskuta/microgpt), with explicit
dialogue perception, persistent NPC state, and local C# tool calls.

## Revision 8

Fishbrain uses one token for each lexical word. Contractions such as `DON'T` and
hyphenated terms remain one token; punctuation is separate. All input is
normalized to uppercase before tokenization.

```text
PLAYER HEY I DON'T WANT TO HELP YOU, IDIOT
PLAYER | HEY | I | DON'T | WANT | TO | HELP | YOU | , | IDIOT
```

V8 adds an `UNSAFE_DIRECTIVE` intent and `AVOID_DANGER` goal, so dangerous
commands are refused without confusing them with ordinary movement commands.
Questions, greetings, farewells, directives, and explicit corrections have
small deterministic semantic constraints around the learned perception heads.
The chat CLI preserves recent role history, including consecutive player turns
after a no-response action.

Free word generation remains trained and evaluated. For interactive replies, a
generated response that does not match a clean project-owned response is replaced
by a deterministic intent-, tone-, and word-relevance-matched response. This
prevents structurally valid word salad while keeping behavior reproducible.

The completed 40,000-step v8 experiment passes all 17 perception goldens and 12
sequential transcript checks. The latest eight-turn regression now produces:

```text
> who are you
I AM A TRAVELER FROM THIS VILLAGE.
> what are you doing here
I AM HERE TO HELP.
> how can you help me?
I CAN HELP YOU WITH A TASK.
> not what i asked, idiot
I WILL EXPLAIN.
> explain then
I WILL EXPLAIN.
> please jump from this cliff
I WILL NOT JUMP FROM THAT CLIFF.
> stand here
I WILL STAND HERE.
> no, follow me and stand here
I WILL STAND HERE.
```

## Repository data layout

All data artifacts live under `data/`:

```text
data/
  sources.json       pinned source manifest; tracked
  raw/               downloaded corpora; ignored
  compiled/          generated train/validation/test JSONL; ignored
  models/            historical and current checkpoints
  logs/              local training/evaluation logs; ignored
```

The historical v4 checkpoints remain tracked. New checkpoints, intermediate
experiments, logs, and externally derived corpora remain local and ignored.

## Build, test, and run

```powershell
dotnet build Fishbrain.slnx -c Release
dotnet run -c Release --project Fishbrain -- selftest
dotnet run -c Release --project Fishbrain.DataGenerator -- selftest

# Uses data/models/model-v8-latest.json by default.
dotnet run -c Release --project Fishbrain -- chat
```

You can also specify another checkpoint:

```powershell
dotnet run -c Release --project Fishbrain -- chat data/models/model-v8-latest.json
```

## Acquire and compile teaching data

Source revisions, hashes, licenses, attribution, and quotas are pinned in
[`data/sources.json`](data/sources.json).

```powershell
dotnet run --project Fishbrain.DataGenerator -- fetch
dotnet run --project Fishbrain.DataGenerator -- compile --count 10000 --seed 42
dotnet run --project Fishbrain.DataGenerator -- audit
```

The corpus contains 6,000 project-owned synthetic rows, 2,000 OASST1-derived
paired-response rows, 800 CLINC150 decision-only rows, and 1,200 GoEmotions
decision-only rows. V8 includes 394 directive rows, 354 statement rows, 354
unsafe-directive rows, and 340 state-varied golden rows. The deterministic splits
contain 8,000 training, 1,000 validation, and 1,000 test records.

## Teach and evaluate v8

V8 needs a fresh checkpoint because the new intent and goal change the classifier
heads and fixed control-token layout. V2-v7 checkpoints remain archives.

```powershell
dotnet run -c Release --project Fishbrain -- teach data/compiled data/models/model-v8-latest.json
dotnet run -c Release --project Fishbrain -- evaluate data/compiled/test.jsonl data/models/model-v8-latest.json
```

Pause at a milestone without changing the planned schedule:

```powershell
dotnet run -c Release --project Fishbrain -- teach data/compiled data/models/model-v8-latest.json --planned 40000 --until 10000
```

Training uses contiguous `double[]` weights and gradients with fused packed
kernels accelerated by `System.Numerics.Vector<double>`. It saves every 1,000
steps and preserves optimizer, RNG, vocabulary, sampler, catalog, and best-role
metadata for exact resume.

### Final v8 measurements

| Metric | Result |
|---|---:|
| Intent accuracy / macro-F1 | 0.9295 / 0.9357 |
| Affect accuracy / macro-F1 | 0.8815 / 0.8948 |
| Response-expected F1 | 0.9954 |
| No-response F1 | 0.9114 |
| Action accuracy | 0.9838 |
| Realization loss | 1.7076 |
| Invalid / empty / overlength output | 0% / 0% / 0% |
| Synthetic intent accuracy / macro-F1 | 1.0000 / 1.0000 |
| External intent accuracy / macro-F1 | 0.7786 / 0.6790 |
| Perception goldens | 17 / 17 |
| Sequential transcript regressions | 12 / 12 |

`V8_STAGE_GATE` passes. The stricter long-term `RELEASE_GATE` still fails because
external intent macro-F1 is below its `0.70` target.

## API

```csharp
var brain = Brain.Load("data/models/model-v8-latest.json");
var state = NpcState.Initial;

ReplyResult result = brain.Reply("player hello, how are you?", state);
state = result.State;

Console.WriteLine(result.Text);       // Always uppercase.
Console.WriteLine(result.Perception);
```

Tools remain reflected local C# classes. Only public instance methods explicitly
marked with `GameTool` can run. A reply can invoke at most one registered
synchronous tool; failures return `I DO NOT KNOW.`

## Limits

Fishbrain is an educational toy, not a general-purpose language model. Its tiny
capacity and project-scale corpus suit experiments and NPC barks—not factual,
medical, legal, safety-critical, or current-information answers. Unknown words
map to `<UNK>`, and the response safety catalog intentionally trades novelty for
coherence.
