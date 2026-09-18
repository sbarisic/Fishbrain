# Causal pilot results

**Experimental candidate; not promoted.** The bounded run and its evaluation are complete. **Automated quality gates failed.** Two independent human reviews remain pending.

## Measured outcome

The unseen subset excludes both complete conversations containing previously supplied job questions. The full frozen suite and the development transcripts are also retained separately.

| Metric | Unseen result | Requirement |
|---|---:|---:|
| Exact tool and arguments | 44/128 (34.4%) | 90% |
| Tool task completion | 41/128 (32.0%) | 90% |
| Memory operation and state | 2/50 (4.0%) | 95% |
| Unintended world mutations | 0 | 0 |
| Altered authoritative fields | 0 | 0 |

Valid tool requests left unanswered or refused: **87**. Possible unsupported-claim screening flags: **13**. The latter is an imperfect lexical screen, not proof of factuality. Zero altered fields means the typed renderer preserved tool values; it does not establish that all free prose is grounded.

## Per-category results

| Category | Exact tool/arguments | Completion | Memory |
|---|---:|---:|---:|
| GET_BALANCE | 1/8 (12.5%) | 1/8 (12.5%) | not applicable |
| GET_CURRENT_LOCATION | 1/8 (12.5%) | 1/8 (12.5%) | not applicable |
| LIST_CAPABILITIES | 5/8 (62.5%) | 5/8 (62.5%) | not applicable |
| LIST_INVENTORY | 1/8 (12.5%) | 1/8 (12.5%) | not applicable |
| LIST_WARES | 1/8 (12.5%) | 1/8 (12.5%) | not applicable |
| LOOKUP_LOCATION | 6/8 (75.0%) | 6/8 (75.0%) | not applicable |
| LOOKUP_PRICE | 7/8 (87.5%) | 7/8 (87.5%) | not applicable |
| READ_PERSONA | 0/6 (0.0%) | 0/6 (0.0%) | not applicable |
| hard_negative | not applicable | not applicable | not applicable |
| memory | 11/50 (22.0%) | 8/50 (16.0%) | 2/50 (4.0%) |
| social | not applicable | not applicable | not applicable |
| transaction | 11/16 (68.8%) | 11/16 (68.8%) | not applicable |

Social relevance is reviewed from actual conversations; it is not counted as a correct tool task merely because no tool was called.

## Paired comparison

Both models received identical player turns and built their own conversation histories. Shared game tools are compared below; explicit memory tools did not exist in the preserved candidate.

| Shared game-tool metric | New candidate | Preserved candidate | Difference, percentage points (95% interval) |
|---|---:|---:|---:|
| Exact tool/arguments | 37.5% | 16.1% | +21.4 (+7.1 to +35.7) |
| Task completion | 37.5% | 16.1% | +21.4 (+7.1 to +35.7) |

Intervals use 2,000 seed-42 bootstrap samples of complete conversations. Related wording within the challenge still limits how broadly these results generalize.

Displayed-response repetition on unseen turns: 66.7%; free-response repetition: 64.8%. Repeated typed answers can be legitimate.

The separate 12-conversation stress set scored 8/30 (26.7%) on exact call sequences, 8/30 (26.7%) on completion, and 0/10 (0.0%) on checked memory states. It recorded 0 unintended world mutations and 0 unexpected memory changes.

## Time and resources

The fresh seed-42 FP32 run completed **12,000 updates**: language and specialization consumed **101.4** and **38.8 minutes** respectively. Total active training time was **2.34 hours**, including validation and checkpoints. The user-requested pause is excluded; its 165 unsaved updates were replayed from the durable update-3,000 checkpoint without resetting elapsed budgets.

Measured language preparation took 32.3 minutes. Downloads, authoring and data review are additional preparation work. Final evaluation and packaging took 13.4 minutes; they are outside the training cap.

CPU: Ryzen 5 3600XT (6 cores/12 threads), Windows 11, .NET 10. GPU training: RX 9070 XT, PyTorch 2.13.0+ROCm 10.0.0.

| Measurement | Result | Target |
|---|---:|---:|
| Cold model load | 267.8 ms | Report only |
| Prompt processing p95 (572 tokens) | 862.1 ms | Report only |
| Raw cached 64-token generation p95 | 529.4 ms | ≤1,000 ms |
| Incremental steady resident memory | 172.2 MiB | ≤512 MiB |
| Per-call allocation p95 | 13.7 MiB | Report only |
| Four concurrent prompts plus 64-token decodes, total | 2007.0 ms | Report separately |
| Actual challenge reply p95, including tools and grammar | 3029.7 ms | Separate end-to-end measurement |

Raw decoding excludes prompt processing and schema checks. The complete reply measurement includes them and may contain several tool exchanges. Cold load uses a fresh process without flushing the filesystem cache; twenty steady samples follow one warm-up. The separate understanding/planning metric is retired.

During sequential real challenge replies, the maximum sampled incremental resident memory was 210.9 MiB and per-reply allocated memory p95 was 31.9 MiB. These samples include retained scratch and execution journals; they do not measure a within-call peak.

Retained experiment storage: **9.29 GiB**, below the 12-GiB cap. 26 complete checkpoints are indexed with checksums. Existing datasets, candidates and comparison binaries were preserved.

## Implementation and data evidence

- One 17,701,248-parameter causal model; no retired classification heads or learned relationship state.
- C#/GPU forward, gradient, exported-weight and cached-decoding checks passed. Exact checkpoint restoration was probed with discarded updates.
- Runtime checks cover malformed artifacts, schemas, tool budgets, concurrent retries, idempotency, Unicode, quoted evidence and memory ownership.
- Language training has 129,362,702 WikiText and 375,744,555 TinyStories training tokens. Published evaluation splits remain separate.
- The public pool has 2,335 Blended Skill Talk and 15 OpenAssistant episodes. This is a substantial source imbalance.
- The 300 composed authored episodes use 264 families and reusable components. They are not 300 independently collected human conversations; entity and social variety remain limited.
- The packed corpus has 3,882 targets. Packing excluded 127 oversized required exchanges and 23 oversized responses. Approximate deduplication is not an exhaustive proof of uniqueness.
- No automatic extension, full training run, old-weight migration, or model promotion occurred.

## Inspect and run

- [Fifty paired conversations](data/causal-v1/results/conversations-50.md)
- [Quality metrics and definitions](data/causal-v1/results/quality.json)
- [Supplemental stress results](data/causal-v1/results/supplemental-results.jsonl)
- [Resource measurements](data/causal-v1/results/resources.json)
- [Delivery and timing record](data/causal-v1/results/delivery.json)
- [Reproduction, migration and human-review instructions](CAUSAL_MODEL.md)

```powershell
dotnet data/training/causal-v1/candidate-package/Fishbrain.dll chat data/training/causal-v1/candidate-package/candidate.fbc
```

The package contains matching runtime/model files, source notices and a checksum manifest. Its shipped-candidate smoke test is separate from deliberate rejection of old `.fbm` artifacts. Large checkpoints and datasets remain outside Git.

## Runtime memory correction

The first full evaluation sampled 876.0 MiB of incremental resident memory, exceeding the 512-MiB target. The final runtime processes prompts in 32-token blocks and reuses an exclusively owned KV cache. The repeated full evaluation sampled 210.9 MiB.

This trades prompt speed for bounded temporary storage: the same 572-token benchmark changed from 333.1 to 862.1 ms p95. Raw 64-token generation remains separately measured above. No process-wide forced collection is used.

The 389 replayed turns across challenge, preserved acceptance, development and history-removal suites have comparison status **BEHAVIOR_UNCHANGED** for displayed text, returned messages, tools, world values and memory. Trained weights and all fingerprint-bound input code remain unchanged. Long-prefix C#/GPU parity also passed.

Both complete evaluation passes took 20.8 minutes in total; isolated probes and engineering work are additional. The original package and reports remain under `data/training/causal-v1/before-memory-fix`. See [the recorded comparison](data/causal-v1/results/runtime-comparison.json).

## Conversation inspection and conclusion

I inspected all 50 paired conversations and the 23 development turns. Direct prices and some transactions work, but social replies remain malformed, irrelevant or repetitive. Persona fields are confused after shopping, and corrections often repeat an old value or fail to write memory. The candidate is not ready for game conversations.

See [the agent inspection](data/causal-v1/results/agent-inspection.md) for concrete examples and limitations. This does not replace either human review. No further training or promotion was started.
