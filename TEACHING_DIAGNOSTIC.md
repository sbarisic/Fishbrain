# Teaching Fishbrain with simulated conversations

This experiment asks a narrow question: can the existing network learn a small,
fully labelled set of ordinary NPC exchanges? It does not replace conversation-v4,
its frozen challenge suite, the retained pilot, or the shipped model.

## Lessons

Read [the desired conversations](data/teaching-diagnostic/expected.md).
There are eight training episodes (32 player turns), with a target for **every**
player turn. Four additional episodes (16 turns) are development paraphrase probes.
The probes are related to the lessons and are not independent unseen release tests.

Each lesson supplies:

- Explicit player/NPC roles and complete preceding dialogue.
- Speech act, clause frame, response plan, and applicable tool/argument targets.
- Caller-owned persona values, or attributed memory with its source turn.
- A social response target where appropriate. Identity and world answers are
  supplied through typed targets and gold histories, never decoder targets.
- An explicitly false transaction response as a negative claim example.

The lessons cover greetings, wellbeing, identity, current location, directions,
wares, negated/hypothetical/quoted purchases, and one reported-home correction.
The training set intentionally contains the user's development examples. Matching
those examples is not evidence of unseen performance.

Example desired exchange:

> Player: Hi.
>
> NPC: Hi there.
>
> Player: Where am I?
>
> NPC: You are at Village Market. *(From the current-location tool.)*
>
> Player: Tell me your name.
>
> NPC: My name is Arin. *(From the caller's persona.)*
>
> Player: Where is the inn?
>
> NPC: Inn is north by the fountain. *(From the location tool.)*

These are desired replies, not claims about the model's actual performance. The
desired authority wording illustrates the demo values; the typed renderer may use
different formatting.

## Experiment

- Same four-layer, width-256 encoder and two-layer causal decoder architecture.
- Fresh seed-42 weights; vocabulary built only from the 32 training turns.
- 2,000 joint updates (seven understanding, three authored realization per ten),
  followed by 500 decoder-only updates. Batch size 32, FP32, AdamW, clipping at 1.
- Phase-local 100-update warmup and cosine decay; peak learning rates 0.0003 and
  0.0001 respectively. No masked-language pretraining in this capacity diagnostic.
- Optimizer, RNG and sampler state retained every 500 updates. The diagnostic
  schedule and dataset are fingerprinted separately from the release curriculum.
- Encoder/planner weights checked for exact equality across decoder-only training.
- **All tools disabled in the exported diagnostic model.** These few probes cannot
  satisfy calibration coverage. Learned tool labels and actual execution are
  different measurements; disabling tools does not establish useful execution.
- Endpoint fixed at 2,500 updates. No promotion or continuation into full training.

The small vocabulary and repeated lessons make this much easier than general
conversation. Success establishes limited learning capacity, not sufficient capacity
for every future domain. A failed probe does not by itself establish a capacity limit.

## Recorded result

The run completed all 2,500 updates in **5 minutes 36 seconds**; training plus native
evaluation and exports took **5 minutes 47 seconds**. No model was promoted.

| Measurement | Taught rows (32) | Development probes (16) |
|---|---:|---:|
| Exact native semantic frames | 31/32 (96.9%) | 0/16 |
| Exact native response plans | 32/32 (100%) | 7/16 (43.8%) |

The GPU check matched every categorical training target and all 107 teacher-forced
response tokens. That does not imply correct generated conversations. The native
checks include decoding, predicted memory selection and runtime plan validation;
their results differ from raw-head fit. All three native evaluation reports fail
the existing complete gates.

Read the [48 paired actual turns](data/teaching-diagnostic/results/conversations.md).
Some taught social exchanges and persona questions improve, but even taught sessions
break under the model's own state and replies. For example, in the arrival replay,
"Where am I?" receives "My name is Arin." Novel wording remains poor. This model
is a diagnostic artifact, not a usable replacement.

A [post-training state probe](data/teaching-diagnostic/results/state-probes.json)
holds the taught "Hi" / "Hi there" / "Where am I?" dialogue fixed. The original
state gives the correct frame and knowledge target. Changing only mood to friendly
or adding a rapport goal preserves both. Adding a valid SOCIAL topic summary breaks
both, although the current question and its intended meaning have not changed.
The runtime adds topic summaries as conversation develops; the lessons used empty
summaries. This demonstrates state sensitivity in this diagnostic, not proof of its
cause or proof that every v4 error has the same cause.

**Recommendation:** generate lessons from complete state transitions and include
valid variations in state, histories, wording, and NPC replies. Keep authoritative
values typed. Check actual rollouts before increasing dataset size or network depth.
The next curriculum should also teach recovery from earlier mistakes. This run did
not add those state variations to training after observing the probe results.

Limits: only one taught home correction, no multi-owner or multi-fact retrieval
challenge, no affirmative transaction lesson, no tool calibration, no independent
human review, and a much smaller vocabulary than the v4 pilot. Reported zero
mutations with disabled tools is not evidence of safe, useful tool execution.

Evidence includes complete optimizer/RNG/sampler checkpoints locally and their hashes
in `data/teaching-diagnostic/results/summary.json`. The explicitly supplied diagnostic
artifact passed the native load/reply smoke check. This did not test or replace the
incompatible shipped artifact. Decoder-only training preserved all non-decoder
weights exactly. Native packing, frozen-data hashes, authority-target masking and
Python syntax checks passed.

## Reproduce

From the repository root in PowerShell, using the existing matching CLI and ROCm
Python environment. Choose new output paths for each run:

```powershell
dotnet data/training/conversation-v4-tools/Fishbrain.dll prepare-torch data/teaching-diagnostic data/training/teaching-diagnostic-packed-replay

data/training/torch-env/Scripts/python.exe scripts/conversation_data/learn_diagnostic.py --packed data/training/teaching-diagnostic-packed-replay --lessons data/teaching-diagnostic --cli data/training/conversation-v4-tools/Fishbrain.dll --output data/training/teaching-diagnostic-replay

dotnet data/training/conversation-v4-tools/Fishbrain.dll conversation-sample data/training/conversation-v4-pilot-final/step-50000-calibrated.fbm data/teaching-diagnostic/rollouts.jsonl data/training/teaching-diagnostic-replay/baseline-conversations.jsonl

data/training/torch-env/Scripts/python.exe scripts/conversation_data/report_diagnostic.py --lessons data/teaching-diagnostic --run data/training/teaching-diagnostic-replay
```

The reporting command writes the paired evidence under `data/teaching-diagnostic/results`;
use a copy of the lessons directory when retaining a second result set. The trainer
refuses existing run directories and does not resume a release checkpoint.

The checked-in lessons are the exact training inputs for the recorded run. To author
a new lesson set, edit `scripts/conversation_data/teaching_diagnostic.py`, generate a
new directory using `--cli`, `--reference` (a native training JSONL for its schema),
and `--output`, then pack that directory. Do not relabel development probes as an
unseen release test after using their results to guide changes.

`generator-snapshot.py` preserves the source whose hash appears in these frozen
lessons. After generation, the rollout export was corrected to use the CLI's
one-based turn indexes. Training inputs and their fingerprints were unchanged;
the current generator includes that export fix. Expected-lesson indexes remain
zero-based and the pairing report accounts for the difference.

The native artifact format still derives `NextPhase` from the original 260,000-step
schedule. For this diagnostic, read the actual schedule in `experiment.json` and
`training.pt`, and completion in `progress.json`. The diagnostic inference artifact
must not be used to resume the standard trainer.

To inspect this diagnostic interactively (all tools remain disabled):

```powershell
dotnet data/training/conversation-v4-tools/Fishbrain.dll chat data/training/teaching-diagnostic-run/diagnostic.fbm
```

To repeat the state-sensitivity probe:

```powershell
data/training/torch-env/Scripts/python.exe scripts/conversation_data/probe_teaching_state.py --cli data/training/conversation-v4-tools/Fishbrain.dll --model data/training/teaching-diagnostic-run/diagnostic.fbm --lessons data/teaching-diagnostic --report data/training/teaching-diagnostic-run/state-probes.json
```

## What an architecture change should address

Use exact taught-example fit, development probes, and actual C# rollouts separately:

1. If taught examples cannot be learned, inspect labels, loss masks, gradient paths
   and inference parity before adding layers.
2. If taught examples work but paraphrases fail, expand varied, annotated episodes
   and measure generalization. Train on complete episodes and useful recovery turns,
   including histories where the NPC previously misunderstood the player.
3. If semantic predictions work but replies fail, inspect plan-conditioned decoding,
   rejection diagnostics and tool calibration independently.
4. The previously measured memory mismatch warrants a targeted design change:
   train and calibrate selection of a **set** of facts. The current categorical
   retrieval objective and runtime comparison with the NONE score do not express
   that decision consistently. This experiment does not implement that redesign.

If state-aware teaching still leaves strong sensitivity to inserted context, compare
the current absolute position embeddings with relative-position attention in a
controlled experiment. T5 describes one established relative-position approach
([paper, section 2.1](https://jmlr.org/papers/volume21/20-074/20-074.pdf)). That is a
hypothesis to test here, not evidence that changing positions will fix Fishbrain.

Any memory-head change must update C# and PyTorch together, verify numerical and
gradient parity, and use separate calibration and acceptance data. Making the whole
Transformer larger is not a substitute for fixing that mismatch.
