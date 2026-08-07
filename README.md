# Fishbrain

Fishbrain is a deliberately tiny, hackable GPT-like model written in C# and
.NET 10 for short video-game NPC conversations. It is CPU-only and has no
external NuGet dependencies. The project follows the inspectable spirit of
[martinskuta/microgpt](https://github.com/martinskuta/microgpt), with explicit
dialogue perception, persistent NPC state, and local C# tool calls.

## Revision 6

Revision 6 replaces character generation with deterministic word tokens:

```text
INPUT WORDS -> PERCEPTION -> BEHAVIOR -> OUTPUT WORDS
```

Every lexical word is one token, including contractions such as `DON'T` and
hyphenated words. Punctuation is a separate fixed token. Input is always
normalized to uppercase, so `player hello` and `PLAYER HELLO` have the same
representation and result. For example:

```text
PLAYER HEY I DON'T WANT TO HELP YOU, IDIOT

PLAYER | HEY | I | DON'T | WANT | TO | HELP | YOU | , | IDIOT
```

The input and response vocabularies are built deterministically from the
training corpus and stored in the checkpoint. A separate response output head
avoids spending generation capacity on input-only words and control tokens.
Unknown words map to `<UNK>`; the model never generates that token.

The model uses one 64-dimensional Transformer layer, four attention heads, a
128-dimensional MLP, a 128-token context, and a 128-token causal attention
window. Three dedicated heads predict intent, user affect, and whether the turn
expects a response. C# then selects the action and updates `NpcState`
deterministically. Dynamic game facts remain tool-only.

The completed 40,000-step v6 experiment passes `V6_STAGE_GATE`, generates 100%
valid sampled replies, and passes all seven golden behavior cases. The exact
hostile refusal now produces:

```text
PLAYER HEY I DON'T WANT TO HELP YOU, IDIOT
THEN STEP ASIDE.
```

Its state is refusal intent, hostile affect, response expected, respond action,
zero rapport, annoyed mood, de-escalation goal, and cold tone. The same result is
produced for lowercase input.

See [INFO.md](INFO.md) for the architecture, token layout, data policy,
experiment measurements, and compatibility details.

## Build and test

```powershell
dotnet build Fishbrain.slnx -c Release
dotnet run -c Release --project Fishbrain -- selftest
dotnet run -c Release --project Fishbrain.DataGenerator -- selftest
```

## Acquire and compile teaching data

Source revisions, SHA-256 hashes, licenses, attribution, and quotas are pinned in
[`Fishbrain.DataGenerator/sources.json`](Fishbrain.DataGenerator/sources.json).
Raw downloads and derived external records remain local and are ignored by Git.

```powershell
dotnet run --project Fishbrain.DataGenerator -- fetch
dotnet run --project Fishbrain.DataGenerator -- compile --count 10000 --seed 42
dotnet run --project Fishbrain.DataGenerator -- audit
```

The deterministic corpus contains 6,000 project-owned synthetic rows, 2,000
OASST1-derived paired-response rows, 800 CLINC150 decision-only rows, and 1,200
GoEmotions decision-only rows. Related variants remain in one split. The output
contains 8,000 training, 1,000 validation, and 1,000 test rows.

## Teach and evaluate v6

V6 requires a fresh checkpoint because the word vocabularies and separate
response head are incompatible with v2-v5 parameter layouts.

```powershell
dotnet run -c Release --project Fishbrain -- teach datasets/compiled model-v6-latest.json
dotnet run -c Release --project Fishbrain -- evaluate datasets/compiled/test.jsonl model-v6-latest.json
dotnet run -c Release --project Fishbrain -- chat model-v6-latest.json
```

`teach` runs 40,000 deterministic steps: 2,000 language warmup steps followed by
38,000 steps that alternate balanced perception and realization. It saves every
1,000 steps and maintains atomic `latest`, `best-perception`, and
`best-realization` checkpoint roles. An incomplete checkpoint resumes with its
optimizer, RNG, phase, sampler, vocabulary, and best-metric metadata intact.

Pause at a milestone without changing the planned schedule:

```powershell
dotnet run -c Release --project Fishbrain -- teach datasets/compiled model-v6-latest.json --planned 40000 --until 10000
```

Training uses contiguous `double[]` weights and gradients with fused packed
kernels accelerated by `System.Numerics.Vector<double>`. The readable scalar
graph remains the inference and forward-reference implementation.

### Final v6 measurements

| Metric | Result |
|---|---:|
| Intent accuracy / macro-F1 | 0.9205 / 0.9240 |
| Affect accuracy / macro-F1 | 0.8750 / 0.8865 |
| Response-expected F1 | 0.9987 |
| Action accuracy | 0.9975 |
| Realization loss | 1.7615 |
| Invalid / empty / overlength output | 0% / 0% / 0% |
| Synthetic intent accuracy / macro-F1 | 1.0000 / 1.0000 |
| External intent accuracy / macro-F1 | 0.7500 / 0.5913 |
| Golden behavior cases | 7 / 7 |

`V6_STAGE_GATE` passes. The stricter long-term `RELEASE_GATE` remains failed
because external-source intent macro-F1 is below its 0.70 target. The recommended
interactive checkpoint is `model-v6-latest.json`; model files and compiled data
remain ignored by Git.

## API

```csharp
var brain = Brain.Load("model-v6-latest.json");
var state = NpcState.Initial;

ReplyResult result = brain.Reply("player hello, how are you?", state);
state = result.State;

Console.WriteLine(result.Text);       // Always uppercase.
Console.WriteLine(result.Perception);
```

Tools remain small reflected C# classes:

```csharp
sealed class PlayerTools(PlayerState player)
{
    [GameTool("GETGOLD")]
    public int GetGold(string playerId) => player.Gold;
}

brain.Tools.Register(new PlayerTools(player));
```

Only public instance methods explicitly marked with `GameTool` can run. A reply
can invoke at most one registered synchronous tool, and failures return
`I DO NOT KNOW.`

## Limits

Fishbrain is an educational toy, not a general-purpose language model. Its tiny
capacity and project-scale corpus suit experiments, NPC barks, and learning the
whole stack—not factual, medical, legal, safety-critical, or current-information
answers. Checkpoint vocabularies are corpus-specific, and unseen words lose their
identity through `<UNK>`.
