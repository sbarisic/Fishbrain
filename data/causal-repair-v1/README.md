# Focused tool-composition and conversation-repair check

This separate experiment investigates the failed causal pilot. It changes training
examples and sampling, using a copy of the existing 17.7M-parameter causal candidate.
It does not change the runtime, tokenizer, neural layout, or shipped artifact.

**The completed diagnostic found a packed-input defect.** Read
[RESULTS.md](RESULTS.md) and [packed-input-audit.json](packed-input-audit.json).
The historical corpus is now blocked from further training. Its weights and
evidence remain intact; separately cleaned rows are untrained.

## Data and isolation

The additional corpus has 181 composed episodes in 81 families, with 38 held-out
conversations. Entity substitutions are augmentation, not independent dialogue
diversity. Complete wording families stay in one split. Exact and near-duplicate
conversation checks run across splits. Two families were removed because they
overlapped earlier frozen evaluation inputs.

Every supported item has explicit price, buy, and sell targets. Other examples
cover all persona fields, the demo's location/fact entities, information repetition,
topic changes after transactions, and clarification of a previous social reply.
The same follow-up can require different tools depending on the previous turn.

Transaction quantities are 1 or 2 in the new training families and 3 in validation
and test families. Thus the held-out transaction questions test both new wording
and quantity composition. The benchmark cannot isolate those two effects.
Shared control turns are reported separately from the primary held-out formulation.

The native C# compiler executes authored tools and uses typed result templates.
The native packer produced 1,147 targets and rejected 53 oversized required inputs.
Those exclusions are reported; the context limit was not relaxed. Source plans,
the held-out challenge, and packed rows have frozen SHA-256 fingerprints.

## Bounded learning check

1. Fit 17 real packed tool/repair targets, at most 200 updates or 120 seconds.
   Exact target-sequence prediction is required. These fixture weights are discarded.
2. Reload the unchanged causal candidate and run at most 400 updates or 600 seconds.
   Use seed 42, FP32, batch 32, four-sequence microbatches, AdamW, norm-1 clipping,
   and a warmup/cosine schedule peaking at 0.0001.
3. Evaluate the final candidate through the unchanged native runtime. Do not extend
   the run, select a model using the test suite, or promote a model automatically.

Sampling weights are 50% new tool requests, 20% dialogue repair, 10% other new
responses/continuations, 10% previously approved public training responses, and
10% previous training memory targets. Validation/test rows are never replayed.
Optimizer, sampler, random state, elapsed time and fingerprints are checkpointed.
The small-check entry point refuses automatic restarts or extensions.

## Reproduce and inspect

Prerequisites: the preserved causal package, original approved packed training
corpus, .NET 10, and the existing ROCm environment described in
[CAUSAL_MODEL.md](../../CAUSAL_MODEL.md). Large files remain outside Git.

```powershell
$py = "data/training/torch-env/Scripts/python.exe"
& $py scripts/causal/repair_restore.py
& $py scripts/causal/test_repair.py
# Only for a fresh, explicitly requested check with no existing progress file:
& $py scripts/causal/repair_start.py
dotnet data/training/causal-v1/candidate-package/Fishbrain.dll evaluate data/training/causal-v1/candidate-package/candidate.fbc data/causal-repair-v1/challenge.json data/training/causal-repair-v1/baseline.jsonl
& $py scripts/causal/repair_evaluate.py
& $py scripts/causal/repair_audit.py
& $py scripts/causal/repair_report.py
```

The first preparation used `repair_data.py --prepare`. It refuses to rewrite a
frozen revision. `repair_restore.py` reconstructs the checked-in plans and verifies
the exact original corpus fingerprint instead of authoring a new test suite.
The original `repair_train.py` is retained unchanged to preserve the completed
run's code fingerprint. Use `repair_start.py` for future attempts: it applies the
packed-input gate first and currently refuses this historical corpus.
The report command packages an unpromoted copy and requires the positive-control
and checkpoint-parity evidence from this run. Existing reports/packages are not
overwritten automatically.

`repair_audit.py` writes separate `specialization-clean.jsonl` and
`prior-replay-clean.jsonl` files under the experiment's prepared directory. It
removes conflicting tool targets and explicitly annotated references whose source
exchange was discarded. It preserves complete source episodes and all previous
artifacts. These cleaned rows retain every item/action combination, but do not
fix inference context loss and have not been used to train another model.

Evaluation compares all frozen conversations, reports held-out formulation accuracy,
and resamples wording families for paired confidence intervals. Prior acceptance
and the user's transcript remain separate regression/development evidence.
Naturalness requires inspection of the replies. Agent inspection is not either of
the two independent human release reviews. The existing 90% tool/completion and
95% memory gates remain unchanged; this focused suite does not establish release
eligibility for the whole project.
