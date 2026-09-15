# Contextual redesign: implementation and acceptance status

Recorded on 2026-09-15. **The full plan is still incomplete.** The implementation is
a development candidate; the checked-in model remains incompatible and unchanged.

## Completed in this continuation

- Separate runtime, training, CLI and demo-domain assemblies. The runtime has no
  project dependencies; assembly tests exclude trainers, legacy models and demo tools.
- A contextual-only `Brain`. Inference no longer initializes legacy model fields.
  `Brain.Load(path)` reads the embedded domain; the explicit-domain overload checks
  the supplied fingerprint. Optimizer execution belongs to the training assembly.
- Float32 matrix kernels with scalar forward/backward comparisons, deterministic
  parallel execution, vectorized attention and softmax, and bounded scratch reuse.
  Layer scopes release intermediate storage; the pool retains at most 128 MiB.
- Independent clause fact acts, owners, predicates, polarity and value spans.
  The reducer can correct two different participants in one turn. Hypothetical,
  quoted and question clauses cannot create asserted session facts.
- Four plan-conditioned agenda predictions with kind, subject and status.
  Omitted active entries survive; multiple completions can be applied together.
- Frame-specific memory/follow-up realization. Generated claims remain excluded from
  approved and session fact updates.
- Pending-action identifier packing, cancellation ambiguity handling, and calibration
  against the arguments recovered by the actual confirmation pipeline.
- Expanded training targets for confirmations, cancellation, ambiguous continuations,
  distant references, compound corrections and multiple agenda completions.
- Seventeen independently authored acceptance cases, excluded by the corpus audit.
  Release checks now require this suite in addition to the existing operational,
  artifact, resource, ablation and human-review gates.
- Training writer locks, a clean-stop file, atomic progress reports, complete
  checkpoints every 100 updates, and validation every 5,000 updates.

The four-layer width-256 encoder, two-layer cross-attention decoder, 512-token input,
64-token/256-character output bounds, typed authority, and caller-owned persona,
profile and world state are unchanged.

## Validation

| Check | Result |
|---|---|
| Release solution build | Pass, zero warnings/errors |
| Runtime/numerical tests with `--unit` | All 37 groups pass |
| CLI self-tests | All 9 groups pass |
| Generator tests | Pass |
| Scalar/optimized matrix and attention parity | Pass, forward and backward |
| Parallel replay and concurrent scratch ownership | Pass |
| Clause fact and agenda finite differences | Pass, gradients reach the encoder |
| Owned compound correction and multiple goal completion | Reducer checks pass |
| Identical final sentence with different histories | Small contextual model learns both |
| Separate observatory capability | Small contextual model learns OBSERVE_SKY |
| Decoder polish and checkpoint resume | Frozen understanding weights; exact continuation |
| Full-size engineering training | 20 batch-32 MLM updates complete |
| Training writer exclusion | Second process rejected before training |
| Clean stop on the final corpus | Update 12 saved; exported checkpoint loads and replies |
| Checked-in artifact smoke | Fails, as required for the incompatible artifact |
| New corpus compilation and audit | 100,000 rows, all source quotas retained |
| Authored acceptance on the 20-update model | **Fail: frame/plan/memory/correction accuracy all 0%** |
| Shipped replacement | **Not released** |

Implementation tests demonstrate numerical correctness and trainability. They do
not establish quality on unseen conversations. Self-tests of the review validator
use test ratings; actual two-person review has not occurred.

## Latest corpus and training run

Final corpus: `data/compiled-contextual-v3`.

SHA-256:
`effedd1665c689aecbb0b7041ff67b06599a62ec318fabb64f6bd5ed066eeef3`.

Splits: 80,234 training, 9,887 validation, 9,879 test rows. Existing source quotas
remain intact, including four contextual groups of 5,000 rows each.

The earlier `contextual-v2-training.fbm` reached update 20 and is retained as
engineering evidence. It uses an earlier corpus and is not resumed for the final
corpus. That run averaged 5.84 seconds per update before final checkpoint writing;
later phases and validation have different costs.

The fresh final-corpus run uses the full 260,000-update endpoint:

- Checkpoint: `data/training/contextual-v3-training.fbm`.
- Progress: `data/training/contextual-v3-training.fbm.progress.json`.
- Fixed executable snapshot and logs: `data/training/contextual-v3-job/`.
- Clean stop: create `data/training/contextual-v3-training.fbm.stop`; the current
  update finishes and complete state is saved. Remove the stop file before resume.

The live progress file reports the process, completed updates, phase and last saved
checkpoint time. Starting a run is not training completion. The job can take weeks
on this CPU. Training completion cannot promote a model.

## Resource measurements

Windows CARPPC, 12 logical processors, .NET 10.0.12; 64 measured iterations after
warmup, without a training process competing for CPU. The full-size engineering
checkpoint has 21,052,672 parameters.

| Measurement | Result |
|---|---:|
| Cold load | 819 ms |
| Short-input understanding p95 | 19.4 ms |
| Short-input understanding plus forced 64-token decoding p95 | 284.5 ms |
| Long input, 506 tokens and 8 selected facts, understanding p95 | 99.9 ms |
| Maximum input, 512 tokens and 8 selected facts, understanding p95 | **107.7 ms** |
| Maximum input plus forced 64-token decoding p95 | 324.9 ms |
| Incremental active resident memory observed | 265.2 MiB |
| Four concurrent workers, understanding p95 | 20.5 ms |
| Average allocation in short-input harness | 4.47 MB |

**Resource gate: FAIL.** The 512-token workload remains above 100 ms. The original
472-token/no-selected-memory result was 677.3 ms; the newer workload is broader,
so these measurements are not identical-workload release comparisons.

The maximum-memory probe forces eight bounded selections while still running
retrieval scoring. It measures resource coverage, not retrieval quality. Forced
decoding includes an extra unmeasured plan preparation because the public reply
does not expose tensor state. Allocation totals include all worker threads.
Compacted idle memory is diagnostic; the active resident measurement controls the gate.

Local evidence: `data/logs/contextual-v2/resources-final.json`,
`unit-tests.log`, `acceptance.json`, and `corpus-v3-audit.log`.
Generated models, corpora and logs are ignored by Git.

## Remaining acceptance work

1. Complete the fresh 260,000-update curriculum and retain the best eligible candidate.
2. Pass the full held-out metrics and authored acceptance suite. Expand coverage
   when failures expose gaps; do not add input-specific routing fixes.
3. Meet the 100 ms maximum-context understanding target and repeat resource checks
   on the trained candidate, including concurrent workloads.
4. Run lexical and memory-disabled paired comparisons with uncertainty.
5. Obtain two independent human conversation reviews, including memory, agenda
   and compound turns.
6. Pass shipped-artifact and complete release gates before packaging/promoting the
   matching model/runtime. Keep failed candidates and reports.

No thresholds have been lowered and no smoke model has replaced
`data/models/model-latest.fbm`.
