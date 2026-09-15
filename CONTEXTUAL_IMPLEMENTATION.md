# Contextual redesign: implementation and acceptance status

Recorded on 2026-09-15. **This is a development implementation, not a completed
replacement-model release.** The shipped artifact is unchanged and incompatible.

## Delivered code

- Four-layer bidirectional encoder and two-layer causal cross-attention decoder,
  width 256, eight heads, feed-forward width 1,024, and a 512-token input budget.
- Native C# float32 forward/backward operations, finite-difference reference checks,
  learned task pooling, contextual slot/fact spans, independent frame references,
  and ordered response acts with explicit frame associations.
- Shared structured packing with role metadata and exact normalized source offsets.
  Oversized mandatory input is rejected; retained history consists of whole turns.
- Bounded fact retrieval, ownership/provenance encoding, topic summaries, agenda
  reduction, and pending-action metadata. Retrieval training uses the same context
  available before memory selection at inference.
- Plan-driven execution checks, typed tool/persona rendering, one attempted tool
  invocation per reply, confidence calibration, and execution-veto diagnostics.
- Social decoder generation with per-reply key/value caches, a trained claim head,
  deterministic screening, grounded-question checks, and plan-specific fallbacks.
- Decoder-only polishing that leaves encoder and planner weights unchanged.
- Immutable inference config and domain definitions; an observatory fixture exercises
  a distinct capability outside the merchant domain.
- Packed inference checkpoints without gradient/optimizer allocation. Checkpoints
  bind vocabulary order, schema, domain, corpus, calibration, phase, sampler and RNG;
  training snapshots also retain Adam moments and parameter update counts.
- A reusable runtime library and CLI project boundary, plus deterministic corpus,
  training, evaluation, resource profiling, ablation and release commands.
- Original source quotas retained in a 100,000-row corpus. Four new groups contain
  5,000 rows each; existing authored single-act rows also gain frame/plan targets.
- Existing operational release thresholds retained. Catalog retrieval metrics are
  explicitly retired. Human-review scenarios and ratings now cover memory, agenda,
  and compound turns. The release script packages a matching model/runtime only
  after its gates pass.

## Regression fixes

Legacy purchase routing now vetoes negated, conditional and quoted/reported actions.
Conversational screening rejects the reproduced unsupported quantity, hometown and
role claims. Conditioning retains fact ownership/provenance. The duplicate training
step increment was removed. The contextual runtime also retains an attempted
invocation when a tool returns malformed output, preventing a second invocation.

The shipped-artifact smoke no longer treats incompatible-model rejection as success.
`Fishbrain.Tests --unit` explicitly excludes that integration test. Default runtime
tests and the release script still require the actual shipped/candidate artifact.

## Validation completed

| Check | Result |
|---|---|
| Release solution build | Pass, zero warnings/errors |
| Runtime/numerical checks with `--unit` | 37 groups pass |
| CLI self-tests | 9 groups pass |
| Generator tests | Pass |
| Corpus compilation and independent audit | Pass, 100,000 rows |
| Identical final sentence, different histories | Small model learns both interpretations |
| History-learning loss | 3.9392 to 0.1303 in the final small-model check |
| Distinct observatory domain | Small model learns and invokes OBSERVE_SKY |
| Concurrent contextual replies | Same outputs and deterministic invocation keys |
| Decoder cache | Agrees with full causal decode |
| Decoder polishing | Encoder/planner weights remain unchanged |
| Training save/resume | Bit-equivalent continuation on the small model |
| Fresh full-size training | One 32-example masked-language update completes |
| Full-size first-update time | 12.679 seconds, excluding loading/preflight |
| Full-size smoke artifact load/reply | Pass |
| Checked-in artifact load/reply | **Fail: incompatible label schema** |

Corpus SHA-256:
`afb6af9bfd1be205946b3bfbc60a695ed61f1a0afb16f2780059c9bfac965230`.
Splits: 80,226 training, 9,891 validation, 9,883 test rows.

Current local engineering artifacts:

- `data/training/contextual-stage-validation.fbm`: complete training state at update 1.
- `data/training/contextual-stage-inference.fbm`: matching inference-only export.
- `data/logs/contextual-implementation/resources.json`: isolated performance run.
- `data/logs/contextual-implementation/quality-smoke.json`: focused evaluator smoke.

These generated artifacts are ignored by Git. They are not release candidates with
proven conversational quality. No full training job is left running.

## Measured resource failure

Windows development machine CARPPC, 12 logical processors, .NET 10.0.12; 16 measured
iterations after warmup; 21,037,568 parameters. The final measurement ran without
the runtime test process competing for CPU.

| Measurement | Result |
|---|---:|
| Cold checkpoint load | 831 ms |
| Short-input understanding p95 | 84.7 ms |
| Short-input understanding plus forced 64-token decode p95 | 304.0 ms |
| Long-input understanding p95, 472 tokens | **677.3 ms** |
| Incremental active resident memory observed | 299.5 MiB |
| Four concurrent workers, understanding p95 | 110.3 ms |

**Resource gate: FAIL.** Long-context understanding exceeds the 100 ms target.
The untrained retrieval head selected no facts from the 16 supplied candidates;
this does not establish worst-case performance with eight retrieved facts. The
allocation report includes extra plan preparation in the forced-decoding harness.
Compacted idle memory is reported separately and is not used to claim a gate pass.

## Quality status

The full-size model has completed only one masked-language update. A focused
32-row evaluator smoke (eight rows from each new source group) correctly fails:
frame exact accuracy 0%, plan exact accuracy 0%, correction accuracy 0%, and zero
tool invocations/authoritative alterations. These numbers test the evaluator and
disabled uncalibrated execution. They are not a trained-model quality comparison.

The full held-out run, lexical and memory-disabled ablations with paired uncertainty,
and actual two-person conversation review have not been performed. Passing unit
tests or fitting a tiny dataset does not satisfy those gates.

## Remaining work before the requested plan is complete

1. Finish responsibility separation: new and legacy training code and demo-domain
   implementations still coexist in the runtime assembly. The runtime/CLI split
   is delivered; separate training/demo assemblies are not.
2. Expand and validate pending-confirmation, cancellation, distant-reference and
   compound-correction training beyond the present fixtures. The current agenda
   reducer applies one learned transition per turn. Multi-fact compound corrections
   still use a turn-level fact prediction for memory updates.
3. Add a broader independently authored, machine-annotated contextual acceptance
   suite. The new corpus groups remain template-generated; held-out families alone
   do not prove generalization beyond those templates. The additional conversation
   review scenarios are authored separately but need human ratings.
4. Optimize and verify long-context CPU kernels and measure maximum retrieved-memory
   and concurrent workloads with a trained model. Keep the 100 ms target unchanged.
5. Complete the 260,000-update curriculum. At the measured initial update rate this
   is a weeks-long CPU job; later phases have different costs. Do not treat a one-step
   smoke checkpoint as the requested trained candidate.
6. Run all held-out operational/conversational gates, comparisons and two-human review.
   Retain failed candidates and reports; package/promote only a fully eligible model.

Until these steps pass, milestones 3-5 and the complete architectural plan remain
unaccepted. The repository's `model-latest.fbm` must not be replaced by a smoke model.
