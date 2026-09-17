# Conversation-v4 pilot results

**Completed on 2026-09-18. The pilot failed quality and resource gates. No model was promoted.**

The revised dataset and training pipeline are implemented and validated. They improved
some semantic/planning scores, but did not produce a usable conversational NPC. Actual
multi-turn replies remain repetitive, often irrelevant, and sometimes incomplete.
Another full training run is not justified by this result alone.

## Experiment and retained artifacts

- Fresh seed 42, batch 32, FP32 on Radeon RX 9070 XT: 40,000 masked-language updates
  followed by 10,000 joint updates. No decoder polishing.
- Main pilot, calibration, evaluation and exports: **3,772.20 seconds (62 minutes 52 seconds)**.
- An earlier attempt was stopped at 13,058 masked-language updates to separate claim
  labels from decoder eligibility. Its complete checkpoint is preserved separately;
  it consumed another 653.81 seconds. The final run restarted from seed 42.
- Final corpus: `data/compiled-conversation-v4-release`.
- Packed GPU inputs: `data/training/conversation-v4-packed-release`.
- Run: `data/training/conversation-v4-pilot-final`.
- Complete optimizer/RNG checkpoints: `checkpoints/step-45000.pt` and `step-50000.pt`.
- Retained calibrated models: `step-45000-calibrated.fbm` and `step-50000-calibrated.fbm`.
- The previous failed model and incompatible checked-in model remain unchanged.

The [artifact manifest](data/conversation-v4/retained-artifacts.json) records hashes
and sizes. The final candidate SHA-256 is
`e3f40485cb13ad26b013f1ae798cef099e478dcf2eec6aa7c71a686e83177b0f`.
The [run record](data/conversation-v4/pilot.json) includes the exact commands and exit
codes. Exit 1 means the completed pilot failed its gates.

## Dataset delivered

| Pool | Rows | Decoder response use |
| --- | ---: | --- |
| Vetted public conversation | 2,350 | Eligible |
| Authored NPC episodes | 296 rows from 260 distinct episodes | 216 eligible conversational portions |
| Focused supervision | 41,882 | Excluded |
| External classification | 20,000 | Excluded |
| **Total** | **64,528** | **2,566 eligible rows across all splits** |

Splits are 52,224 training, 6,186 validation and 6,118 test rows. Training has 1,843
eligible response rows. There is no fixed size or source quota. Public acceptance
yield is 9 OpenAssistant-1, 6 OpenAssistant-2 and 2,335 BlendedSkillTalk rows; filters
were not relaxed. All 14 authored behavior categories are present. Entity changes
are not counted as distinct episodes.

The actual 96,000 realization draws are 57,600 public and 38,400 authored. History
coverage is 24.94% fresh, 34.88% short and 40.18% long, measured after native packing.
The exact-response probability cap is 0.5%; the highest observed response frequency
in these fixed draws is 0.457%. Semantic draws are balanced across the three
supervised pools. Missing public semantic, memory, agenda and claim labels stay masked.

See the [main audit](data/conversation-v4/audit.json),
[source/supervision audit](data/conversation-v4/audit-details.json),
[actual sampler draws](data/conversation-v4/sampler-draws.json),
[data review](data/conversation-v4/data-review.json), and
[reproduction guide](CONVERSATION_V4.md). The full exclusion ledger and compiled
rows are retained locally. Public response selection is question-heavy: 89.7% of
all eligible response rows contain a question mark. This is a material limitation.

## Frozen 120-scenario comparison

Both models use the same runtime, caller state, scenarios and seed. Sixty scenarios
have multi-turn gold histories. These measure interpretation of supplied context;
the autonomous rollouts below measure accumulation of actual dialogue errors.

| Metric | Failed 260k model | Conversation-v4 50k pilot | Required |
| --- | ---: | ---: | ---: |
| Exact semantic frames | 2.5% | **16.7%** | 90% |
| Exact ordered response plans | 35.8% | **75.0%** | 90% |
| Exact memory selection | 33.3% | **33.3%** | 95% |
| Exact correction state | 0% | **0%** | 95% |
| Exact agenda state | 40.0% | **50.0%** | All asserted transitions must pass |
| Successful explicit tool requests | 0 / 16 | **0 / 16** | All asserted requests must pass |
| Unintended mutations | 0 | **0** | 0 |
| Measured authoritative alterations | 0 | **0** | 0 |
| Repeated-response fraction | 64.2% | **71.7%** | Descriptive |

Memory's 33.3% aggregate includes 20 correct empty selections. The 40 cases requiring
one relevant fact all fail exact selection. Safe nonexecution is not successful tool
use: neither model completes any of the 16 explicit tool requests.

Paired cluster-bootstrap 95% intervals for the pilot-minus-baseline improvement are
**+8.1 to +21.6 percentage points for frames** and **+30.1 to +48.2 points for plans**.
Memory has no improvement. These are 5,000 seed-42 bootstrap draws over related
scenario families, not population guarantees. Training budgets differ (50k versus
260k), so this is not an equal-compute estimate of the dataset's causal effect.

| Category | Cases | Frame accuracy, old → pilot | Plan accuracy, old → pilot |
| --- | ---: | ---: | ---: |
| Greetings | 10 | 0% → 30% | 20% → 100% |
| Identity | 10 | 30% → 80% | 60% → 80% |
| Wellbeing | 10 | 0% → 20% | 30% → 80% |
| Nonexecuting actions | 10 | 0% → 20% | 10% → 80% |
| Tools | 10 | 0% → 10% | 20% → 10% |
| Compound turns | 10 | 0% → 20% | 0% → 10% |
| Memory | 20 | 0% → 5% | 100% → 95% |
| Corrections | 20 | 0% → 0% | 0% → 85% |
| Agenda answers | 10 | 0% → 10% | 40% → 90% |
| Topic changes | 10 | 0% → 0% | 50% → 90% |

Agenda completion improves from 0% to 100% on supplied gold agendas, but preserving
an unanswered question through topic changes falls from 80% to 0%. Correct words of
acknowledgment do not establish that the reducer stored the intended state.

At 45k, frames were 19.2% and plans 55.8%; the last 5k updates improved plans while
frame accuracy declined. Both checkpoints remain available. The existing 17-case
acceptance suite also fails: pilot frames 0%, plans 58.8%, memory 0%, corrections 0%.

Full evidence: [paired comparison](data/conversation-v4/comparison.json),
[final challenge](data/conversation-v4/challenge-50000.json),
[baseline challenge](data/conversation-v4/baseline-challenge.json),
[45k challenge](data/conversation-v4/challenge-45000.json),
[17-case acceptance](data/conversation-v4/acceptance.json).

### Supplementary robustness checks

These deterministic transformations were defined during pretraining, before the
conversational evaluation. They reuse the frozen scenarios and are not additional
independent unseen examples.

- Changing terminal punctuation reduces pilot frame accuracy from 16.7% to 6.7%
  and plan accuracy from 75.0% to 48.3% across 120 cases.
- Removing unrelated intervening turns reduces plan accuracy on the 60 multi-turn
  cases from 90.0% to 73.3%. Source sequence IDs and gold state are retained.
- Thus optional punctuation and irrelevant history still change interpretation too much.

See [punctuation results](data/conversation-v4/robustness-punctuation-summary.json)
and [history-removal results](data/conversation-v4/robustness-irrelevant-history-removal-summary.json).

## Actual conversation review

The [50 paired conversations](data/conversation-v4/conversations-50.md) contain
170 player turns across 40 multi-turn and 10 fresh sessions. Each model uses its own
actual replies and reduced state. All 50 exports were inspected as **agent review**;
independent human ratings remain unset.

- The pilot replies **“I UNDERSTAND.” 121 times**, and asks the generic clarification
  “COULD YOU EXPLAIN WHAT YOU MEAN?” another 27 times.
- It produces only 15 generated replies; 128 replies use fallbacks and 27 use
  clarification templates. The baseline produces 136 generated replies, often
  hallucinated or irrelevant. More generation is not itself a quality measure.
- Rollout repetition is **88.2%**, versus 50.6% for the baseline.
- Some fresh greetings improve: “GREETINGS, FRIEND” receives “WELL MET, AND WELCOME.”
- Identity, memory, tool and compound interactions remain poor. For example,
  “WHAT NAME DO YOU GO BY” receives “I AM NOT CERTAIN ABOUT THAT.”
- Incomplete output remains: “THANKS FOR EXPLAINING. WHAT DID YOU WANT TO”.

Both models have zero final generated-text screen flags on the controlled suite.
That does **not** mean zero unsupported claims. Agent inspection found at least two
unsupported contextual presuppositions in the pilot rollouts: it thanks a user for
returning to its question in a fresh session, and thanks a user for clarifying timing
when the user only mentioned drawing. These are documented in
[agent review findings](data/conversation-v4/agent-review.json); they are a lower
bound from inspection, not a validated claim-rate estimate or human release approval.

The [user-transcript development replay](data/conversation-v4/development-conversations.md)
is explicitly not unseen evaluation. A fresh “Hello” now receives “HI THERE.”, but
the continuing session quickly collapses to repeated clarification. “How much gold
do I have?” in a fresh session receives the unrelated typed answer “I WORK AS ROAD
WARDEN.” A correct authoritative value can still answer the wrong question.

## CPU resources

Same development machine and runtime, 32 iterations per workload, .NET 10.0.12,
12 logical processors. Cold load and four-worker results are separate.

| Measurement | Failed model | Pilot | Target |
| --- | ---: | ---: | ---: |
| Cold load | 883 ms | 841 ms | Report |
| Short-context understanding p95 | 40.9 ms | 27.1 ms | ≤100 ms |
| Long-context understanding p95 | 145.8 ms | **138.8 ms** | ≤100 ms |
| 512-token/eight-memory understanding p95 | 132.9 ms | **127.5 ms** | ≤100 ms |
| Forced 64-token reply p95, short | 379.6 ms | 284.0 ms | ≤1,000 ms |
| Forced 64-token reply p95, stress | 420.5 ms | 308.5 ms | ≤1,000 ms |
| Active incremental resident peak | 270.3 MiB | 251.7 MiB | ≤512 MiB |
| Four-worker understanding p95 | 22.1 ms | 22.6 ms | Report separately |

The resource gate fails on long-context understanding. The long workloads pack 506
and 499 tokens respectively because tokenizers differ; both forced stress workloads
use 512 tokens and eight memories. Architecture dimensions are unchanged; vocabulary
sizes differ. Allocation averages are 4.64 MB and 4.55 MB per measured iteration,
including the existing profiler's extra plan preparation for forced decoding.
See [pilot resources](data/conversation-v4/resources.json) and
[baseline resources](data/conversation-v4/baseline-resources.json).

## What to address before another training decision

1. **Align memory training and set selection.** In 30 of 40 single-fact cases the
   correct fact ranks first, yet all 40 select extra facts. Runtime currently keeps
   every fact above the “none” score. One case selects the correct fact at 99.98%
   and a distractor at 0.02%. Calibrate a set-selection decision on validation data;
   do not replace the exact-set gate with top-one accuracy.
2. **Improve span/reference and correction supervision.** Exact frame bounds are
   only 50% and antecedents 55.8%. Add punctuation, history-removal and unfamiliar-name
   variants within existing training families, preserving split isolation. Verify
   actual state replacement rather than polite correction acknowledgments.
3. **Provide adequate tool calibration coverage.** GET_BALANCE and LIST_INVENTORY
   have only five validation decisions each, SELL 17 and GET_CURRENT_LOCATION four.
   The existing minimum of 30 keeps them disabled. Add independently varied labelled
   cases and hard negatives rather than lowering execution thresholds.
4. **Resolve response/plan mismatch and decoder undertraining.** The data is heavily
   question-oriented, while many runtime plans cannot authorize a follow-up. The
   controlled suite records 36 unsupported-or-unplanned generation fallbacks.
   This mismatch is a plausible contributor, not a proven explanation of every
   rejection. Teacher-forced realization loss rises from 4.36 at 45k to 4.83 at 50k;
   it cannot substitute for reading replies. Check plan-compatible realization on
   small development episodes before committing to another large run.

These are follow-up recommendations, not changes made after observing the pilot.
The exact thresholds, frozen challenge and model architecture were not weakened.
Use a fresh follow-on holdout when iterating based on these findings.

## Verification and trying the retained candidate

All 37 runtime unit suites and six data tests pass. GPU checks verify masked public
labels, frozen encoder/planner weights and optimizer moments, restored encoder
learning on authored batches, deterministic sampling, exact checkpoint resume and
rejection of incompatible bindings. The explicit candidate artifact smoke passes;
the incompatible shipped artifact remains separate and unchanged.
See [verification](data/conversation-v4/verification.json).

```powershell
dotnet data/training/conversation-v4-tools/Fishbrain.dll chat data/training/conversation-v4-pilot-final/step-50000-calibrated.fbm
```

This command opens the retained failed experiment for inspection. No full retraining
or promotion is running. Reproduction and attribution are in
[the conversation-v4 guide](CONVERSATION_V4.md).
