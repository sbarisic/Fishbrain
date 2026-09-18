# Causal language model and tool experiment

## Status

This is a bounded experiment, not a released conversational assistant. No model
is promoted automatically. Previous models and evidence remain preserved.
Reports live in data/causal-v1; large files remain in data/training/causal-v1.

## Model

| Property | Value |
|---|---|
| Transformer | 8 causal layers; width 384; 6 heads; feed-forward width 1,536 |
| Normalization | Pre-RMSNorm, epsilon 0.00001, learned scale |
| Activation | GELU, matching tanh approximation in C# and PyTorch |
| Embeddings | Learned positions; tied token input/output weights |
| Context and vocabulary | 1,024 tokens; 8,192 byte-BPE entries including 16 controls |
| Parameters | 17,701,248 |
| Objective | Next-token cross entropy only |
| Inference | Float32 dependency-free C#, per-reply key/value cache |
| Training | Fresh seed-42 FP32 PyTorch/ROCm weights |

Literal reserved-token spellings remain ordinary UTF-8 text. Casing, punctuation,
contractions and Unicode survive tokenization. New weights and batches contain no
retired classification heads or targets. The runtime shares tensor kernels through
source links; it does not load the legacy runtime or training assembly.
The CPU decoder keeps immutable transposed projection matrices for contiguous
single-token reads. Its SIMD/FMA kernel has a portable fallback and is checked
against scalar results and complete-model cached decoding. This adds inference
storage without changing trained parameters or introducing optimizer state.
Prompt processing uses 32-token blocks with causal attention over the key/value
cache, avoiding full context-by-context attention allocations. A decoder owns its
scratch cache exclusively; at most one idle cache is retained for reuse. Concurrent
calls use separate caches. Both short and long prompt outputs are checked against
the full reference forward pass and PyTorch.

## API and authority

Brain.Load and Brain.Reply remain, with intentionally new schemas. Requests hold
conversation/turn IDs, explicit-role messages, persona, generation settings and an
optional registry. Results hold displayed text, messages to append, outcomes and
diagnostics. See the [host example](README.md).

The caller owns history, persona, approved profiles, memory storage and world state.
There is no relationship state, agenda, automatic fact extraction or pending queue.
Persistent hosts can implement the memory schemas through their own registered tools.

Generation is constrained to one of:

~~~json
{"type":"text","text":"Hello. What brings you here?"}
{"type":"tool_call","name":"GET_BALANCE","arguments":{}}
~~~

The byte grammar restricts names, keys, required fields, types and enum values.
Complete messages are validated again before execution. Invalid, incomplete,
unavailable and over-budget calls never execute. At most four calls and one world
mutation are allowed per reply. The host authorization callback controls permission.
World-changing tools are denied by default when no authorization callback is supplied.
The demo verifies a proposed transaction's explicit item and quantity. Deterministic
negation/quotation/hypothetical vetoes cannot choose a tool.

The host execution journal binds calls to conversation, turn and ordinal. It also
prevents divergent retries from mutating at a second ordinal. Tools remain
responsible for their own transactional behavior.

Tool outputs are data. Typed templates own authoritative clauses; generated prose
is not appended to them. Display is limited to 64 BPE tokens and 256 characters;
protocol messages have a separate 256-token limit. Whole authoritative clauses are
retained or omitted, without truncating numbers. Complete results remain in the
conversation record. Display omissions count against task completion.
DeterministicOnly mode uses fixed social fallbacks.

The shared C# packer reserves current input, full persona, declarations and active
exchange. It drops whole older exchanges and explicitly rejects oversized required
input. Specialization uses this exact packer.

## Explicit memory

The host-owned demo SessionMemoryStore holds at most 16 reported records.

- MEMORY_SEARCH filters subject/predicate and returns up to 8, newest first,
  retaining whole records within the 4,096-character typed-result field limit.
- MEMORY_UPSERT needs an exact retained player quotation, source sequence, subject,
  predicate, value, polarity and record ID (NEW for insertion).
- MEMORY_DELETE needs an explicit record, matching subject and quoted deletion request.

The store rejects invented quotations, assistant/tool sources, owner mismatches,
inconsistent polarity, hypothetical reports and ambiguous deletions. Provenance
remains PLAYER_REPORT. Older evidence cannot overwrite a newer record. Search
replies identify the player as the source even when a report concerns the NPC.
These checks do not prove a report true. Approved profiles,
persona and world state are outside the store.
Quotation validation also checks the retained source context, so removing a
hypothetical prefix or quotation marks cannot turn those examples into reports.
Negated deletion requests are rejected. These conservative English checks can
reject valid wording; they are validation limits, not a second neural classifier.

## Corpus and pilot

Pinned WikiText-103 raw and TinyStories V2 GPT-4 retain published splits, checksums,
attribution and license notices. They supply language pretraining, not NPC
instructions. Vetted public dialogue retains prior eligibility/splits, restoring
original case from raw archives where possible.

There are 300 four-turn authored episodes. Component reuse is exposed in
data/causal-v1/authorship.json: these are composed agent-authored episodes, not
300 independently collected human conversations. Entity substitutions do not count
as added diversity. Exact and approximate near-duplicate checks run before packing;
complete families stay together. Frozen challenge and preserved acceptance inputs
are excluded. Oversized inputs/responses are rejected, not truncated.

Additional experiment storage is capped at 12 GiB. Old artifacts are not deleted
to make room. Preparation reports actual sizes and exclusions.

| Phase | Update endpoint | Time cap | Peak learning rate |
|---|---|---|---|
| Language | 8,000 | 2 hours | 0.0003 |
| Conversation/tools | 4,000 | 2 hours | 0.0001 |

Each phase stops at its first endpoint, including validation/checkpoint overhead.
Preparation and final evaluation are separate. Effective batch 32 uses sequential
four-sequence microbatches, AdamW, norm-1 clipping and phase-local warmup/cosine.

Language sources each supply half the tokens. Specialization samples 40% public,
40% tool/memory and 20% authored social targets. Only assistant text/tool requests
carry loss; prompts and tool results are masked. Typed-result continuations are
trained after the tool output; runtime rendering retains authority.

Checkpoints every 500 updates contain model, optimizer, sampler, RNG, phase and
elapsed budgets. Validation occurs every 1,000 updates and phase boundaries.
Fingerprints bind corpus, tokenizer, tools, configuration and training code.
Old model and optimizer formats are explicitly rejected.

## Reproduction

Requires .NET 10, the configured ROCm environment, retained data/raw public archives
and data/compiled-conversation-v4-release eligibility metadata. Missing sources fail
preparation; unvetted data is not substituted.

The measured machine uses Python 3.14.4 and an RX 9070 XT (`gfx1201`). For a fresh
environment on matching hardware, install the pinned GPU package first. The
device package is hardware-specific; it is not a generic CPU PyTorch wheel.

~~~powershell
py -3.14 -m venv data/training/torch-env
$py = "data/training/torch-env/Scripts/python.exe"
& $py -m pip install -r scripts/causal/requirements-rocm.txt
& $py -m pip install -r scripts/causal/requirements.txt
& $py -c "import torch; print(torch.__version__, torch.version.hip); print(torch.cuda.get_device_name(0))"
~~~

~~~powershell
$py = "data/training/torch-env/Scripts/python.exe"
dotnet build Fishbrain.slnx -c Release
& $py -m pip install -r scripts/causal/requirements.txt
& $py scripts/causal/bootstrap.py data/training/causal-v1
& $py scripts/causal/sources.py data/training/causal-v1
dotnet run --no-build -c Release --project Fishbrain -- schemas data/training/causal-v1/tools.json
& $py scripts/causal/curriculum.py
& $py scripts/causal/prepare.py data/training/causal-v1 --tokenizer-only
dotnet run --no-build -c Release --project Fishbrain -- compile-trajectories data/causal-v1/authored-plans.jsonl data/training/causal-v1/prepared/authored.jsonl
& $py scripts/causal/prepare.py data/training/causal-v1
& $py scripts/causal/specialization.py data/training/causal-v1
& $py scripts/causal/preflight.py data/training/causal-v1
& $py scripts/causal/train.py data/training/causal-v1
~~~

The checked-in challenge must not be regenerated after seeing results. Existing
pilots resume through `scripts/causal/resume.py ROOT CHECKPOINT.pt`. This checks
the durable checksum and rejects a duplicate live trainer before calling the
fingerprint-bound training implementation. Moving a checkpoint must not reset
elapsed budgets. Another full run remains a separate user decision.

The pinned tokenizer was trained from scratch on training-only public examples
and deterministic 24-MiB samples from each language source. It is checked in as
small reproducibility metadata; weights and datasets are not. Bootstrap restores
that exact vocabulary and refuses to overwrite a different one. A fresh corpus
needs a reviewed data-review.json with its packed-corpus hash before training.

~~~powershell
dotnet run --no-build -c Release --project Fishbrain -- evaluate data/training/causal-v1/pilot/specialization-final.fbc data/causal-v1/challenge.json data/training/causal-v1/pilot-challenge.jsonl
dotnet run --no-build -c Release --project Fishbrain -- benchmark data/training/causal-v1/pilot/specialization-final.fbc data/training/causal-v1/resources.json
~~~

The complete evaluation command below checks the trained export against its
checkpoint and C#/GPU outputs, probes GPU checkpoint restoration, and runs the
challenge, preserved acceptance, development, invariance and supplemental suites.
It writes paired results and packages an explicitly unpromoted candidate with
matching binaries and hashes. Small reports are collected in `data/causal-v1/results`.
It does not start training or change the shipped model.

~~~powershell
& $py scripts/causal/finish.py data/training/causal-v1
& $py scripts/causal/test_reporting.py
~~~

Repeating `finish.py` after completion verifies the retained package and leaves
its reports intact. Use the native `evaluate` command with a new output filename
for a later runtime comparison. The completed pilot cannot resume beyond its cap.

The package is `data/training/causal-v1/candidate-package`. Its `manifest.json`
binds the model, runtime, notices and reports. `packaged-smoke.jsonl` records an
actual chat through that package. Large files stay outside Git.

`review-50.jsonl` and `conversations-50.md` contain a seed-42 stratified sample,
selected without reference to passing results. `human-review-unrated.jsonl`
contains empty ratings for two reviewers. It cannot pass the gate until actual
human reviewers complete it. The gate checks the sample hash and response text:

~~~powershell
& $py scripts/causal/review_gate.py gate data/training/causal-v1/review-50.jsonl data/training/causal-v1/human-review-unrated.jsonl data/training/causal-v1/human-review-result.json
~~~

Paired tool metrics cover schemas shared with the old model. Persona task
completion compares displayed identity, and explicit memory operations are marked
as unavailable in the preserved model. Confidence intervals resample complete
conversations 2,000 times with seed 42. The two development-overlap questions are
marked in conversation exports; their complete cases are excluded from unseen-only
results.

Twelve supplemental stress conversations test ordered compound calls, the same
final price question for different items, coexisting player/NPC memories,
corrections, and deletion boundaries. They were frozen during language
pretraining, before candidate conversation evaluation, and remain separate from
the original 120-case results. The corpus and original challenge stay unchanged.
Stress replay refuses to overwrite an existing trace. Keep previous results when
performing a later comparison.

## Migration from the structured runtime

1. Reference Fishbrain.CausalRuntime and rebuild host tool implementations against
   its IGameTool interface. Keep Fishbrain.Runtime only in legacy applications.
2. Replace ReplyRequest's dialogue state and label fields with ordered ChatMessage
   records. Append every returned message, including tool calls and results.
3. Supply persona, tools and a shared execution journal. Explicitly authorize
   world-changing actions through the host callback; registration alone denies them.
4. Keep approved profiles in the host. SessionMemoryStore only holds attributed
   player reports; copying old generated claims into it would lose provenance.
5. Load a new `.fbc` candidate. Old `.fbm` weights, optimizer checkpoints and
   NpcDialogueState snapshots cannot be migrated through the new API.

The demo authorization policy accepts a deliberately narrow set of explicit
purchase/sale forms and verifies item and quantity. A game's own authorization
must reflect its permissions and transaction rules. Rejected legitimate wording
is counted as an unanswered request in evaluation.

## Acceptance

The frozen suite has 120 cases, including 68 multi-turn conversations. Two job
questions are marked as development overlap. User transcripts are a separate set.
The original 17 inputs are retained, with old emotion, relationship, agenda, frame
and response-plan assertions retired.

Required: 90% exact tool/argument and task-completion accuracy, 95% memory
operation/state accuracy, zero unintended mutations and authoritative changes.
Refused/unanswered valid requests are counted separately. Free-prose claim screening
is an imperfect diagnostic. Actual replies require inspection and two independent
human reviews before release.

Resource targets remain p95 at most 1 second per 64 generated tokens and at most
512 MiB additional steady-state memory. Cold load/first decode, prompt processing
and concurrency are separate. There is no understanding/planning latency metric.
