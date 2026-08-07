# Fishbrain

Fishbrain is a deliberately tiny, hackable GPT-like model written in C# and
.NET 10 for short video-game NPC conversations. It is CPU-only and has no
external NuGet dependencies. The project follows the inspectable spirit of
[martinskuta/microgpt](https://github.com/martinskuta/microgpt), with explicit
dialogue perception, persistent NPC state, and local C# tool calls.

## Revision 7

Fishbrain uses one token for each lexical word. Contractions such as `DON'T` and
hyphenated terms remain one token; punctuation is separate. All input is
normalized to uppercase before tokenization.

```text
PLAYER HEY I DON'T WANT TO HELP YOU, IDIOT
PLAYER | HEY | I | DON'T | WANT | TO | HELP | YOU | , | IDIOT
```

V7 adds dedicated `DIRECTIVE` and `STATEMENT` intents instead of forcing commands
and ordinary statements into unrelated classes. The chat CLI preserves recent
role history, including consecutive player turns after a no-response action.

Free word generation remains trained and evaluated. For interactive replies, a
generated response that does not match a clean project-owned response is replaced
by a deterministic intent-, tone-, and word-relevance-matched response. This
prevents structurally valid word salad while keeping behavior reproducible.

The completed 40,000-step v7 experiment passes all 11 perception goldens and the
full four-turn transcript regression that originally failed:

```text
> tell me something about yourself
I AM A TRAVELER FROM THIS VILLAGE.
> WHY YOU WORRY
I DO NOT WORRY.
> I WILL NOT ASK
[NO RESPONSE]
> follow me, dude!
I WILL FOLLOW YOU.
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

# Uses data/models/model-v7-latest.json by default.
dotnet run -c Release --project Fishbrain -- chat
```

You can also specify another checkpoint:

```powershell
dotnet run -c Release --project Fishbrain -- chat data/models/model-v7-latest.json
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
decision-only rows. V7 includes 380 directive rows, 380 statement rows, and 132
state-varied golden rows. The deterministic splits contain 8,000 training, 1,000
validation, and 1,000 test records.

## Teach and evaluate v7

V7 needs a fresh checkpoint because the two new intents change the classifier
heads and fixed control-token layout. V2-v6 checkpoints remain archives.

```powershell
dotnet run -c Release --project Fishbrain -- teach data/compiled data/models/model-v7-latest.json
dotnet run -c Release --project Fishbrain -- evaluate data/compiled/test.jsonl data/models/model-v7-latest.json
```

Pause at a milestone without changing the planned schedule:

```powershell
dotnet run -c Release --project Fishbrain -- teach data/compiled data/models/model-v7-latest.json --planned 40000 --until 10000
```

Training uses contiguous `double[]` weights and gradients with fused packed
kernels accelerated by `System.Numerics.Vector<double>`. It saves every 1,000
steps and preserves optimizer, RNG, vocabulary, sampler, catalog, and best-role
metadata for exact resume.

### Final v7 measurements

| Metric | Result |
|---|---:|
| Intent accuracy / macro-F1 | 0.9216 / 0.9292 |
| Affect accuracy / macro-F1 | 0.8696 / 0.8778 |
| Response-expected F1 | 1.0000 |
| No-response F1 | 1.0000 |
| Action accuracy | 1.0000 |
| Realization loss | 1.5372 |
| Invalid / empty / overlength output | 0% / 0% / 0% |
| Synthetic intent accuracy / macro-F1 | 0.9883 / 0.9880 |
| External intent accuracy / macro-F1 | 0.7786 / 0.6311 |
| Perception goldens | 11 / 11 |
| Four-turn transcript regressions | 4 / 4 |

`V7_STAGE_GATE` passes. The stricter long-term `RELEASE_GATE` still fails because
external intent macro-F1 is below its `0.70` target.

## API

```csharp
var brain = Brain.Load("data/models/model-v7-latest.json");
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
