# Second repair results

**The runtime defects are fixed, but this is still not a reliable conversational
model. Quality gates failed. No model was promoted.**

The candidate improves transactions and some references. It also makes more
unrequested read-only tool calls on the broader suite. The focused gains do not
establish general conversational competence.

## Changes and verification

- System input shrank from 558 to 382 tokens, with the same complete persona and
  registered tools. Compact tool-history arrays retain values and source IDs.
  The formerly lost sword-price antecedent now survives a brief distraction.
- Training excludes targets whose declared source history is no longer present.
  The frozen corpus has no identical-input tool conflicts, mixed text/tool
  labels, or exact/near-duplicate conversations across evaluation splits.
- Polite purchase requests and customer “sell me” requests pass authorization.
  Negated, quoted, conditional, and cancelled actions remain vetoed.
- Missing prompt-format bindings fail in the new runtime. Old candidate packages
  remain intact.
- 428 C# checks passed with SIMD and with hardware intrinsics disabled. Four new
  Python checks and the five previous repair checks passed. The solution built
  without warnings or errors.
- C#/GPU forward and cached-decoding error stayed below 0.000044, against a
  0.0005 tolerance. The 16-target fixture overfit in 40 updates / 23.4 seconds;
  its weights were discarded.
- The exported 17,701,248 parameters match the final checkpoint bit for bit.
  Optimizer steps, sampler counts, and saved RNG state were inspected. An
  interrupted training resume was not exercised.
- The restored corpus matches SHA-256
  9d60f164d5fae0b0a6bc689113f31019f4d11665a90216f0bf1c80f515ab1592.

## Bounded training

The run started from a copy of the first repair candidate. It completed exactly
1,200 updates, batch 32 / microbatch four, seed 42, in **752.0 seconds
(12 minutes 32 seconds)**, within the 900-second cap. No further training is
running. Complete checkpoints at updates 600 and 1,200 remain outside Git.

The corpus has 4,840 targets, including 211 new episodes across 73 families.
Thirty social conversations were written individually; 150 episodes are marked
as augmentation in the canonical source plans. Training excluded 593 targets
with evicted dependencies and 21 oversized responses. The remaining public
dialogue and old authored examples come only from preserved training splits.
See [the sampling audit](sampling-audit.json) for effective weights and repetition.

The separate 16 six-turn follow-up episodes were authored after the smoke test.
Their 166 accepted targets cover late name writes, ownership, corrections, and
wares-to-price topic changes. They are **prepared, not trained**. Seven dependent
targets were excluded. They do not change this candidate or its frozen corpus.

## Paired actual-runtime evaluation

Both models received the same player turns using their matching runtime packages:
28 new conversations, the full 120-case frozen suite, 17 preserved cases, and the
user's 12-turn development transcript. This is **422 turns per model**.

| Measure | Previous candidate | New candidate |
|---|---:|---:|
| Focused exact tool/task completion | 22/48 (45.8%) | 31/48 (64.6%) |
| Focused memory correctness | 0/6 | 1/6 |
| Frozen exact tool accuracy | 48/130 (36.9%) | 54/130 (41.5%) |
| Frozen task completion | 46/130 (35.4%) | 54/130 (41.5%) |
| Frozen memory correctness | 3/50 (6%) | 4/50 (8%) |
| Frozen valid requests unanswered | 84 | 76 |
| Frozen unrequested tool-call turns | 9 | **44** |
| Preserved applicable task completion | 0/3 | 0/3 |
| User transcript task completion (development) | 3/10 | 7/10 |
| Unintended world mutations, all replays | 0 | 0 |
| Altered authoritative fields, all replays | 0 | 0 |

The focused completion difference is +18.75 percentage points; the paired
category-cluster bootstrap interval is +3.0 to +29.0 points. The broader frozen
completion difference is +6.15 points, with an interval of -0.8 to +20.0 points.
These intervals use only five and ten category clusters respectively and are
descriptive. Entity variants are not treated as independent evidence. The user
transcript is development data; its improvement is not unseen evaluation.

All 12 focused purchase/sale turns completed, including quantity three and all
three items. All three price recalls after a distraction worked. All six new
hard negatives left the world unchanged.

Memory and unfamiliar wording remain poor. All three focused name-recall
questions failed. Identity after a transaction often becomes unrelated social
text. On the old frozen suite, ordinary journey small talk repeatedly triggers
a village-fact tool call. The drop in lexical unsupported-claim flags does not
prove factuality: wrong but unchanged typed values, such as answering a player
name question with “Arin”, are still wrong answers.

The 17 preserved cases have only three applicable tool assertions and no memory
assertions in their retained schema. They are not broad memory evidence.

## Direct inspection

The old model reproduced the user's reported transcript, including “hiange.”
The new model begins “Hi!”, gives its name, reports gold and location correctly,
and completes both ten-rope purchases:

~~~text
sell me 10 rope
YOU BOUGHT 10 ROPE FOR 30 GOLD. YOUR BALANCE IS 70 GOLD.

I would like to buy 10 rope
YOU BOUGHT 10 ROPE FOR 30 GOLD. YOUR BALANCE IS 40 GOLD.
~~~

However, “how much money for iron sword?” repeats the wares answer in that
history. “My name is Steve” proposes the right value and quotation but cites
source turn 0 after a long conversation. The memory tool correctly rejects
that missing evidence. In the separate package smoke, “What is my name?”
then incorrectly reads the NPC persona.

[51 representative paired conversations](results/conversations.md) and the
[package smoke trace](results/package-smoke.jsonl) are retained for review.
Agent inspection does not replace the two-human release review.

## Resources

The standalone benchmark ran after the paired replays and training had finished.
Its 20 steady samples followed one warm-up. Cold load used a fresh process with
the operating-system filesystem cache left intact.

| Measure | Result |
|---|---:|
| Cold model load | 328 ms |
| Prompt processing p95, 396 tokens | 569 ms |
| Raw cached 64-token generation p95 | 502 ms |
| Incremental steady resident memory | 164 MiB |
| Steady raw-call allocation p95 | 14.0 MB |
| Four concurrent raw generations, total | 1,474 ms |
| Actual reply p95, frozen suite | 3,229 ms |

Raw decoding and steady memory meet their engineering targets. Actual
schema-constrained replies remain slower than one second. Baseline replay
overlapped GPU training, so its latency is diagnostic, not a controlled speed
comparison. Preparation, preflight, and final evaluation are outside the
reported 752-second training time. Exact evaluation elapsed times are recorded
in results/candidate-timing.json and results/baseline-timing.json.

## Test this candidate

~~~powershell
dotnet data/training/causal-repair-v2/candidate-package/Fishbrain.dll chat data/training/causal-repair-v2/candidate-package/candidate.fbc
~~~

Candidate SHA-256:
04c4506f1cf82474e138975c56005c1e240803ebc565eb3f4f0d204b642eab8f.

The package manifest binds the matching runtime files and model. This remains
an experimental candidate: the 90% tool/task and 95% memory gates failed, and
naturalness failed agent inspection. No thresholds were lowered, no shipped
artifact was replaced, and no run was extended.

The final package also includes conservative defaults for unannotated source
plans and stricter parsing of malformed quantities in the public authorization
helper. The exact training-bound sources and initial package remain preserved.
The inference assembly and weights are byte-identical to those evaluated; valid
generated integers already satisfy the stricter parser. The final package smoke
is compared with the original smoke to check displayed-reply parity.
