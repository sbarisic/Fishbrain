# Focused data-repair results

**Experimental candidate; not promoted.** The focused tool gate failed. This small check does not establish full release eligibility.

## What changed

Added 181 composed tool/repair episodes with complete item/action coverage. The model architecture, tokenizer, prompt packer, native runtime, schemas and authorization remain unchanged. No routing patch was added.

The 17-target fitting check passed after 80 updates (50.1 seconds). Its weights were discarded. The separate data intervention started from the preserved causal candidate and stopped after 400 updates in 4.27 minutes.

## Packed-input defect found during inspection

The source-level split checks passed, but the model-input audit found **40 training rows in four groups with identical packed prompts and different tool targets**. Whole-exchange truncation had removed the antecedent. For example, a price repetition request lost its item, and otherwise identical rows required either a sword price or a potion price. The previous corpus also has four conflicting memory rows.

The new gate checks packed token prefixes and author-annotated reference dependencies. It excludes 56 rows across training/validation/test from a separate untrained clean corpus, leaving 1,091 rows with full item/action coverage. The original corpus, weights, frozen evaluation and expected answers remain unchanged.

This is a data/packing defect, not evidence that additional layers would recover information absent from the input. The next implementation step should compact tool declarations/exchanges or otherwise preserve required context, then regenerate and audit the packed corpus. Merely repeating training is blocked. See [the audit](packed-input-audit.json).

## Held-out formulation results

The primary turn is the held-out question/order, excluding repeated controls. Sale questions follow an inventory-stocking purchase. Transaction quantities are 3 in validation/test and 1 or 2 in the new training families. New wording and unseen quantity composition are tested together.

| Metric | Before | After | Required |
|---|---:|---:|---:|
| Exact tool and arguments | 25.0% | 19/36 (52.8%) | 90% |
| Task completion | 25.0% | 19/36 (52.8%) | 90% |
| Unintended world mutations, all held-out turns | 0 | 0 | 0 |
| Altered authoritative fields, all held-out turns | 0 | 0 | 0 |

Refused or unanswered valid requests across all held-out turns: 77 before, 43 after.

| Category | Exact tool/arguments | Completion |
|---|---:|---:|
| buy-style | 0/6 (0.0%) | 0/6 (0.0%) |
| global-GET_BALANCE | 1/1 (100.0%) | 1/1 (100.0%) |
| global-GET_CURRENT_LOCATION | 0/1 (0.0%) | 0/1 (0.0%) |
| global-LIST_CAPABILITIES | 1/1 (100.0%) | 1/1 (100.0%) |
| global-LIST_INVENTORY | 1/1 (100.0%) | 1/1 (100.0%) |
| global-LIST_WARES | 0/1 (0.0%) | 0/1 (0.0%) |
| persona | 2/5 (40.0%) | 2/5 (40.0%) |
| price-style | 6/6 (100.0%) | 6/6 (100.0%) |
| repair-social | not applicable | not applicable |
| sell-style | 0/6 (0.0%) | 0/6 (0.0%) |
| world-LOOKUP_LOCATION | 5/5 (100.0%) | 5/5 (100.0%) |
| world-LOOKUP_WORLD_FACT | 3/3 (100.0%) | 3/3 (100.0%) |

Paired 95% intervals for the change resample wording families (2,000 seed-42 draws), keeping entity substitutions together:

- Exact tool/arguments: +27.8 percentage points; interval +3.6 to +50.0; 14 families.
- Completion: +27.8 percentage points; interval +3.8 to +52.4; 14 families.

## Previous acceptance and implementation checks

| Previous 17-case suite | Before | After |
|---|---:|---:|
| Exact tool/arguments | 0/3 (0.0%) | 0/3 (0.0%) |
| Completion | 0/3 (0.0%) | 0/3 (0.0%) |
| Memory operations/state | not applicable | not applicable |

- C#/GPU full forward and native cached/prefill parity passed with the new weights.
- Exported weights equal the complete final checkpoint bitwise.
- The frozen corpus was independently reconstructed with the same SHA-256.
- The packaged runtime/model completed an actual chat smoke test.
- Packaging smoke checks loading and response bounds, not answer correctness. Its shorter Hello → What? → sword-price sequence still returns a greeting for the price question; inspect package-smoke.jsonl.
- Baseline replay overlapped training; recorded latencies are diagnostic, not a controlled performance comparison. The runtime itself is unchanged.
- Existing full quality/resource requirements and two-human review remain in force. No full pilot was started.

## Inspect and test

[All 38 paired conversations](results/conversations.md) · [Machine-readable results](results/evaluation.json) · [Agent inspection](results/inspection.md)

From the project directory:

```powershell
dotnet data/training/causal-repair-v1/candidate-package/Fishbrain.dll chat data/training/causal-repair-v1/candidate-package/candidate.fbc
```

The previous candidate package remains unchanged. Large model/checkpoint files stay outside Git.
