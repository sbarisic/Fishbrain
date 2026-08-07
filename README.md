# Fishbrain

Fishbrain is a deliberately tiny, hackable character-level GPT written in C# and
.NET 10 for short video-game NPC conversations. It is CPU-only and has no external
NuGet dependencies. The project is based on the idea demonstrated by
[martinskuta/microgpt](https://github.com/martinskuta/microgpt), adapted to teach
explicit dialogue perception, persistent NPC behavior, and local C# tool calls.

## Revision 5

The model now learns three visible tasks:

```text
LANGUAGE -> PERCEPTION -> BEHAVIOR
```

It uses one 64-dimensional Transformer layer, four attention heads, a
128-dimensional MLP, and a 64-character attention window. Inputs and replies are
limited to 256 characters. The vocabulary accepts uppercase letters, digits,
spaces, and `. , ? ! ' - :`; normal input is canonicalized automatically.

Three dedicated perception heads predict intent, user affect, and whether the
utterance expects a response. Perception classifies only the final player turn;
response generation retains the full dialogue. C# deterministically selects the
action and updates `NpcState`. A no-response turn returns an empty string while
still persisting the state change.
Novel ordinary dialogue uses free character generation; exact memory is used only
for seen state/input pairs. Dynamic game facts remain tool-only.

Revision 5 is implemented but not yet trained through the full experiment. The
v4 baseline remains experimental: at the 32,000-step
milestone, held-out intent macro-F1 was `0.1554` and 12% of sampled replies were
invalid. The sequential curriculum exposed task interference: perception training
forgot language, while joint training repaired language and weakened intent
classification.

See [INFO.md](INFO.md) for the complete architecture and token layout, state and
tool semantics, data policy, v4 measurements, and the implemented v5 experiment.

## Build and test

```powershell
dotnet build Fishbrain.slnx
dotnet run --project Fishbrain -- selftest
dotnet run --project Fishbrain.DataGenerator -- selftest
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

The deterministic corpus contains 6,000 project-owned synthetic contrast rows,
2,000 OASST1-derived paired-response rows, 800 CLINC150 decision-only rows, and
1,200 GoEmotions decision-only rows. Accepted OASST1 pairs are expanded across
several NPC starting states; all variants from one conversation stay in the same
split. The output is:

```text
datasets/compiled/train.jsonl        8000
datasets/compiled/validation.jsonl   1000
datasets/compiled/test.jsonl         1000
datasets/compiled/review.jsonl       ambiguous rows excluded from training
```

## Teach and evaluate v5

Version 5 starts from fresh 64-dimensional weights because its dedicated
classifier heads cannot be migrated from v4 vocabulary rows. Keep older
checkpoints as archives.

```powershell
dotnet run -c Release --project Fishbrain -- teach datasets/compiled model-v5-latest.json
dotnet run -c Release --project Fishbrain -- evaluate datasets/compiled/test.jsonl model-v5-latest.json
dotnet run -c Release --project Fishbrain -- chat model-v5-latest.json
```

`teach` runs 40,000 deterministic steps: 2,000 language warmup steps, then
38,000 steps that alternate balanced perception and realization. Calling the
same command again resumes an incomplete checkpoint with its optimizer, RNG,
phase, sampler, and best-metric metadata intact.

Pause at evaluation milestones without changing the 40,000-step schedule:

```powershell
dotnet run -c Release --project Fishbrain -- teach datasets/compiled model-v5-latest.json --planned 40000 --until 2000
dotnet run -c Release --project Fishbrain -- teach datasets/compiled model-v5-latest.json --planned 40000 --until 10000
```

The checkpoint stores the planned schedule and rejects an incompatible
`--planned` value when resumed. Teaching saves every 1,000 steps and after a
requested milestone. Every save prints a flushed, absolute, copy-paste PowerShell
resume command; completed milestones also print evaluation and full-curriculum
continuation commands.

Training retains scalar autograd but skips unused query, attention, MLP, and
vocabulary work for conditioning-only tokens and uses fused cross-entropy. On the
development machine, the same 100-step Release fixture improved from 35.929 to
12.727 seconds (2.82x); full-run speed depends on the CPU and sample mix.

The chat CLI prints the NPC reply together with intent, affect, response
expectation, action, rapport, mood, topic, goal, and tone.

At each 1,000-step milestone, teaching reports task-specific perception and
realization metrics. It saves atomic `latest`, `best-perception`, and
`best-realization` checkpoint roles.

### Experimental status

- The first v5 run is paused at step 7,000. Full evaluation measured intent
  macro-F1 `0.0987`, affect macro-F1 `0.7103`, and expected-response F1 `0.9695`;
  the staged gate still fails.
- Step 24,000 is the strongest observed cognition snapshot, but it has severe
  language forgetting in the historical v4 run.
- Step 32,000 is a better language/cognition compromise, but intent performance
  regressed in v4 and the release gates still fail.

The committed v4 checkpoints preserve the historical results but are not loaded
by the v5 runtime. The next experimental action is a fresh v5 milestone run and
comparison against the staged gates in [INFO.md](INFO.md#revision-5-implementation).

## API

```csharp
var brain = Brain.Load("model-v5-latest.json");
var state = NpcState.Initial;

ReplyResult result = brain.Reply("PLAYER HELLO, HOW ARE YOU?", state);
state = result.State;

Console.WriteLine(result.Text);
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
capacity and project-scale corpus are useful for experiments, NPC barks, and
understanding the whole stack—not for factual, medical, legal, safety-critical,
or current-information answers.
