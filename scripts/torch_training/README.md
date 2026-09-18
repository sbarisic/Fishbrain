# GPU training with C# inference

This guide describes the preserved structured-model backend. Use the
[causal model guide](../../CAUSAL_MODEL.md) for the current runtime and trainer.

This optional Windows training backend keeps tensors on an AMD GPU throughout an
update. Python and PyTorch are training dependencies only. Exported `.fbm` files
load in the existing dependency-free .NET CPU runtime. The native C# trainer remains
available. Old CPU optimizer checkpoints are not converted; this run starts fresh.

For the revised dataset, use the [conversation-v4 guide](../../CONVERSATION_V4.md).
It adds explicit loss eligibility, 60/40 public/authored realization sampling,
frozen encoder/planner state for public batches and a hard 50,000-update pilot cap.
The v3 preparation/full-training commands below remain historical reproduction
instructions; they are not the conversation-v4 pilot workflow.

## Setup

Validated on Windows with Python 3.14.4, PyTorch 2.13.0+rocm10.0.0, NumPy 2.5.3,
and a Radeon RX 9070 XT (gfx1201). The wheel reports HIP 7.15.26333. Install an AMD
driver supported by the [official PyTorch installation guide](https://rocm.docs.amd.com/projects/ai-ecosystem/en/latest/frameworks/pytorch/install.html).
The device package below is specific to gfx1201.

Run from the repository root:

```powershell
py -3.14 -m venv data/training/torch-env
$python = (Resolve-Path data/training/torch-env/Scripts/python.exe).Path
& $python -m pip install -r scripts/torch_training/requirements-rocm.txt
& $python -m pip install -r scripts/torch_training/requirements.txt
& $python -c "import torch; print(torch.__version__, torch.version.hip); print(torch.cuda.get_device_name(0))"
dotnet build Fishbrain.slnx -c Release
```

PyTorch exposes ROCm devices through its `torch.cuda` API. No CUDA toolkit or NVIDIA
GPU is required for this installation. FP32 is the validated precision; the optional
BF16 switch is experimental and has not passed parity or quality gates.

## Prepare and verify

Use the audited, compiled corpus. Preparation requires an empty destination. C#
exports exact structured inputs, tokenizer IDs, utterance positions, typed targets,
and missing-supervision masks. Python does not infer roles or retokenize text.

```powershell
$cli = 'Fishbrain.LegacyCli/bin/Release/net10.0/Fishbrain.dll'
dotnet $cli prepare-torch data/compiled-contextual-v3 data/training/torch-corpus-v1
dotnet $cli torch-reference data/compiled-contextual-v3 data/training/torch-corpus-v1/initial.fbm data/training/torch-reference.jsonl
& $python scripts/torch_training/parity.py data/training/torch-corpus-v1 data/training/torch-reference.jsonl data/training/torch-parity.json
dotnet $cli artifact-smoke data/training/torch-parity.fbm
& $python scripts/torch_training/test_training.py data/training/torch-corpus-v1 data/training/torch-reference.jsonl data/training/torch-resume-tests.json
& $python scripts/torch_training/test_assess.py
& $python scripts/torch_training/validate_data.py data/training/torch-corpus-v1 data/training/torch-validation-data-tests.json
& $python scripts/torch_training/benchmark.py data/training/torch-corpus-v1 data/training/gpu-benchmark.json --cli $cli --updates 20
```

Parity covers native forward values, phase losses, sampled gradients for every
parameter tensor, padded batches, and frozen encoder/planner weights during decoder
polishing. Resume checks compare weights, Adam state, loss and RNG exactly on this
backend. Cross-backend float32 results have tolerances; CPU/GPU trajectories are
not claimed to be bit-identical. The benchmark includes batching, transfers,
backpropagation, AdamW, and rejected-output mining. It excludes initialization,
validation, checkpoint writes and final native assessment.

## Train, stop and resume

```powershell
& $python scripts/torch_training/train.py data/training/torch-corpus-v1 data/training/torch-run-v1 --cli $cli --native-corpus data/compiled-contextual-v3 --scenarios data/benchmarks/conversation-scenarios.jsonl
```

Training retains the full 260,000-update curriculum, seed 42, effective batch 32,
masked targets, norm clipping, AdamW, and phase-local schedules. GPU batches process
32 examples together. The model architecture and inference limits stay unchanged.
Rejected generated candidates are checked through the C# runtime and teach the
claim detector; generated text never becomes an approved memory fact.

```powershell
Get-Content data/training/torch-run-v1/progress.json
New-Item data/training/torch-run-v1/STOP -ItemType File
```

Ctrl+C or `STOP` finishes the current update and saves complete state. To resume,
remove only that run's `STOP` file and repeat its original command. An output-directory
lock prevents duplicate writers. Keep the same Python/PyTorch versions and code;
trainer schema, precision, corpus and initial-model fingerprints must match.
For a short smoke test, use a different output directory and `--until 100`.

- `training.pt`: complete optimizer, weights, step/phase, sampler and RNG state,
  saved every 1,000 updates and on a clean stop. Failed updates preserve the last
  durable checkpoint; progress can show more completed work than was saved.
- `latest.fbm`: immutable C# inference weights, exported every 5,000 updates and
  on a clean stop. It is a development artifact, not an approved release.
- `validation-N.json`: full teacher-forced validation losses every 5,000 updates.
- `candidates/step-N.fbm`: every validation snapshot, retained for further assessment.
- `best-PHASE.pt` / `.fbm`: phase candidates selected by validation loss. This
  development shortlist is not a claim of the best fully release-eligible model.

At 260,000 updates, the GPU is released and native assessment automatically runs.
It calibrates and scores the phase-loss shortlist plus final weights on disjoint
validation families, selects on validation, and evaluates the selected candidate
on the held-out test split. It also runs authored acceptance, CPU resources,
lexical/memory-disabled comparisons, and exports a two-reviewer conversation sample.
Native evaluation takes additional time and has its own progress status.

`native-gates.json`, `native-assessment.json`, `acceptance.json`, `resources.json`,
`comparison.json`, and `human-review.jsonl` record the results. `QUALITY_GATES_FAILED`
retains the candidate and reports. `AWAITING_HUMAN_REVIEW` still requires real human
reviews and the release workflow. Nothing automatically replaces the shipped model.

Retry completed native assessment separately, without retraining:

```powershell
& $python scripts/torch_training/assess.py data/compiled-contextual-v3 data/training/torch-run-v1 --cli $cli --scenarios data/benchmarks/conversation-scenarios.jsonl
```

Use a fixed copy of the scripts and matching CLI DLLs for a background run. Record
the source commit and hashes with its logs; rebuilding a live job's DLLs is unsafe.
The development launch uses `data/training/torch-run-v1/job` for this snapshot.
