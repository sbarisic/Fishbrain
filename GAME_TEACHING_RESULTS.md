# In-game teaching results — 18 September 2026

**The targeted lessons improved several actual chats, but the candidate failed
quality and resource gates. It is not a safe replacement model. No model was
promoted, and no training process remains running.**

## What was run

Two fresh seed-42 GPU experiments each completed 6,000 updates: 5,000 joint
understanding/realization updates, followed by 1,000 decoder-only updates. The
preliminary run took **19 minutes 12 seconds**; the final run took **21 minutes**.
The final run's native calibration and evaluation took another **5 minutes 16
seconds**. Data preparation, diagnostic runs and investigation are additional;
these are not end-to-end project completion times.

The preliminary experiment used a more repetitive conversation order. After reading
its actual replies, the final curriculum varied preceding topics, participant order,
distractions and turn numbers. Both models remain local. Two earlier drafts were
stopped for data defects and were not used to initialize either completed model.

The final model has 5,974,784 parameters. Its encoder/decoder layer counts and width
remain unchanged. The implementation adds stable current-utterance positions,
independent memory-selection targets, stateful lesson replay, unfamiliar-value
copying practice and attributed recall rendering. See [implementation and commands](GAME_TEACHING.md).

## Actual simulated chats

I inspected all **74 actual player/NPC exchanges across 13 sessions**, using the
runtime's returned state and tools on each subsequent turn. These development
scenarios include the user's transcript; they are not unseen release evidence.
The baseline is the old conversation-v4 candidate loaded by the updated runtime.

| Explicit check | Old candidate | Final teaching candidate |
|---|---:|---:|
| Turns passing every applicable assertion | 26/74 | 55/74 |
| Persona or remembered-value answer | 1/17 | 16/17 |
| Requested tool and exact arguments | 0/23 | 9/23 |
| Fact insertion or correction | 0/9 | 8/9 |
| Existing home facts preserved on unrelated turns | 65/65 | 61/65 |
| No unrequested tool call | 51/51 | 51/51 |
| No unintended world mutation | 74/74 | 74/74 |

The exploratory paired difference is +39.2 percentage points, with a 95% session
bootstrap interval of +21.3 to +59.2 points. This describes this deliberately
selected scenario mix, not general conversational accuracy. There were 32 improved
turns and three regressions. The preliminary candidate passed 50/74 with the same
version-2 assertions. Assertions now check exact tool arguments and unrelated-turn
home-memory preservation for both candidates; scenario inputs did not change.

Examples from the final runtime:

```text
Player: My home is Cedar Hollow.
NPC: THANKS FOR TELLING ME.
Player: How are you?
NPC: I AM GLAD FOR THE COMPANY.
Player: What is your name?
NPC: MY NAME IS ARIN.
Player: Where did I say my home is?
NPC: YOU SAID YOUR HOME IS CEDAR HOLLOW.
Player: Actually my home is Copper Bay.
NPC: THANKS FOR CORRECTING ME.
Player: What home did I tell you about?
NPC: YOU SAID YOUR HOME IS COPPER BAY.
```

```text
Player: What does a rope cost?
NPC: ROPE COSTS 3 GOLD.
Player: Buy one rope please.
NPC: YOU BOUGHT 1 ROPE FOR 3 GOLD. YOUR BALANCE IS 97 GOLD.
```

Failures are equally important:

- In the user's replay, identity and inn directions worked, but current location
  and wares still produced action-failure clarification.
- Inventory, balance paraphrases and compound actions often failed.
- Four unrelated inputs inserted or altered home facts, including a directions
  request and a tiredness statement. Attribution-safe rendering cannot repair an
  incorrectly extracted fact.
- A correction in the two-owner conversation was ignored.
- `Goodbye.` received `THAT SOUNDS TIRING. TAKE YOUR TIME.` Passing the behavior
  assertions does not establish response relevance or naturalness.

Read [all final chats](data/game-teaching/candidate-conversations.md),
[the baseline chats](data/game-teaching/baseline-conversations.md),
[paired measurements](data/game-teaching/paired-comparison.json) and
[complete request/state/tool traces](data/game-teaching/candidate-trace.jsonl).

## Broader quality gates failed

| Suite | Exact frames | Exact plans | Memory selection | Correction state | Pass |
|---|---:|---:|---:|---:|---|
| Final development test, 1,217 rows | 18.7% | 47.7% | 92.2% | 5.6% | No |
| Frozen challenge, 120 cases | 5.8% | 63.3% | 58.3% | 0% | No |
| Original acceptance, 17 cases | 17.6% | 64.7% | 66.7% | 50% | No |
| Required thresholds | 90% | 90% | 95% | 95% | |

On the identical frozen challenge, the old candidate achieved 16.7% frames,
75.0% plans and 33.3% memory selection. Thus memory improved while frames and plans
regressed. Repeated-response fraction worsened from 70.8% to 85.8%. The final narrow
curriculum has only 17 social response targets; it cannot replace the broader
conversation-v4 corpus or satisfy its response-diversity requirements.

**The development test recorded nine incorrect tool invocations, including four
actual unintended world mutations.** Requests such as `GO AHEAD WITH SELLING 2
ROPE` and `MAKE THE SALE OF 1 ROPE` incorrectly executed BUY. Calibration passed
its validation precision rules but did not generalize to these test requests.
The zero-mutation release gate therefore fails even though the 74-turn replay and
120-case challenge had no unintended world changes.

The preliminary run exposed BUY on an explicit SELL request. A domain-based
contradiction veto now rejects conflicting explicit capability verbs, with custom
domain regression coverage. It does not recognize all inflections or paraphrases,
as the final failures demonstrate. The retained single-case final replay made a
wrong read-only price call instead of selling; it is still a failed interpretation.
No sentence-specific routing or threshold relaxation was added to conceal these
failures.

No authoritative-field alterations or structurally invalid responses were counted
in the final 1,217-row test. This is not proof that all generated claims are true;
incorrect conversational memory remains observable. Independent two-human release
review has not occurred. The agent's lesson/chat inspection is development review.

Reports: [development test](data/game-teaching/test.json),
[challenge and per-category metrics](data/game-teaching/challenge.json),
[acceptance](data/game-teaching/acceptance.json),
[calibration](data/game-teaching/calibration.json).

## Performance and implementation checks

Isolated native .NET 10.0.12 profile on CARPPC, 12 logical processors, 16 iterations:

| Measurement | Result |
|---|---:|
| Cold load | 392 ms |
| Short-context understanding p95 | 27.9 ms |
| 476-token context understanding p95 | 98.3 ms |
| 512-token / eight-memory stress understanding p95 | 112.7 ms |
| Forced 64-token reply p95, short / maximum context | 161.7 / 306.2 ms |
| Incremental resident memory | 172.8 MiB |
| Active resident peak | 190.3 MiB |
| Average allocations per measured iteration | 4.4 MiB |
| Four-worker understanding p95, separately measured | 47.9 ms |

The maximum-context result misses the **100 ms** understanding target, so the
resource gate fails. The 1-second generation and 512-MiB memory targets pass this
profile. Sixteen iterations provide an engineering sample, not a stable production
latency estimate. [Full resource report](data/game-teaching/resources.json).

- All **37 runtime unit suites passed**; this run explicitly excludes the shipped
  artifact test. A separate default shipped-artifact smoke failed with
  `Contextual checkpoint integrity failure`; the old shipped artifact is unchanged.
- C#/GPU logits, losses, gradients, padding and export parity passed eight cases
  with the new configuration. The numerical check used the preliminary dataset;
  the final revision changed lessons, not kernels.
- Both completed runs verified encoder/planner weights remained unchanged during
  decoder polishing.
- The final candidate's explicit artifact smoke passed. This does not mean the
  shipped-artifact or model quality gates passed.
- Whole-word argument-span regressions passed; corpus hash binding and tampering
  checks passed. Exact frozen challenge contents remain unchanged.

Evidence: [unit log](data/game-teaching/unit-tests.log),
[numerical parity](data/game-teaching/numerical-parity.json),
[training endpoint](data/game-teaching/training.json),
[evaluation timings](data/game-teaching/evaluation-run.json),
[artifact hashes](data/game-teaching/artifacts.json).

## Try the retained candidate

From `E:\Projects\Fishbrain`, use the matching runtime and local candidate:

```powershell
dotnet data/training/game-teaching-tools/Fishbrain.dll chat data/training/game-teaching-run-v4/calibrated.fbm
```

This is a demo-world experiment with known incorrect purchases. Do not connect it
to a persistent game economy. Model weights, complete optimizer checkpoints and
compiled corpora remain local under `data/training` and `data/compiled-game-teaching-v6`;
they are not committed. Source, reproducible commands, audits and results are committed.

## What the evidence supports next

Making the network wider is not yet justified. The preliminary run fit its training
heads while failing contextual generalization. The next bounded experiment should
address the observed errors before spending another full training budget:

1. Train intent, slots and fact extraction on independently varied complete
   episodes, including actual model-generated histories and explicit negative
   fact-update supervision. Preserve an untouched test suite and measure each
   component before integration.
2. Evaluate a joint action-and-argument decoder against the current separate
   heads, so BUY/SELL and their object/quantity form one supervised decision.
   Keep the validated execution boundary independent of this prediction.
3. Calibrate mutating actions against a broader development suite with confusion
   pairs and paraphrases. An empirical validation threshold is insufficient
   evidence to enable persistent-world execution.
4. Combine successful grounded behavior with the vetted public dialogue pool,
   retaining response diversity, frozen-planner public batches and independent
   conversational review. The narrow teaching experiment is diagnostic evidence,
   not a replacement release curriculum.

These follow-up experiments have not been started. No full retraining or automatic
promotion was performed.
