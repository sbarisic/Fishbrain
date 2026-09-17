# Conversation-v4 dataset and bounded pilot

This experiment replaces response-template training with vetted public dialogue and
authored NPC conversations. It keeps the model architecture, typed authoritative
responses, caller-owned state, and dependency-free C# inference. It does not promote
a model or authorize a new full training run.

The completed pilot failed quality and resource gates. See
[measured results and retained artifacts](CONVERSATION_V4_RESULTS.md) and
[50 paired conversation rollouts](data/conversation-v4/conversations-50.md).

## Data

There is no target row count. The preparation audit reports the accepted size.
The old `data/compiled-contextual-v3` corpus and failed
`data/training/torch-run-v1/calibrated.fbm` model remain intact.

| Pool | Use |
| --- | --- |
| Public conversation | Response generation only; all semantic, memory, agenda and claim targets are masked |
| Authored NPC episodes | Explicit contextual labels and permitted conversational response portions |
| Focused supervision | Useful existing game, memory, reference and intent labels; no legacy response generation |

Public inputs reconstruct actual OpenAssistant parent-message paths and
BlendedSkillTalk exchanges. Source trees and alternate replies share an episode.
BST uses recorded dialogue, not the dataset's model suggestions. Every accepted
row records its source revision, checksum, license, episode and augmentation family.

OpenAssistant responses must be accepted, undeleted, English and nonsynthetic,
with quality >= 0.6, failure-to-answer <= 0.2, and reported spam, personal-information
and language-mismatch scores all zero. Missing scores fail eligibility. The loader
deduplicates OpenAssistant releases by message ID before selection and checks
ancestor messages. Additional filters exclude technical answers, assistant identity,
unbound biographies, service commitments and game claims. Rejections are recorded
in `rejections.jsonl`; low yield never relaxes a filter.

Responses must fit 256 characters and 64 native decoder tokens, including the end
token. Oversized responses are rejected whole. The native tokenizer and structured
input packer also validate each input; roles do not come from literal role words.

The authored source contains 260 distinct episodes covering openings, identity,
small talk, feelings, preferences, clarification, repair, topic changes, follow-ups,
memory, correction, agenda, tools and compound turns. Related correction and agenda
examples share the original episode/family. Entity changes do not count as new
episodes. Tool and persona answers use typed targets; their authoritative text is
not a decoder target. Facts retain participant, polarity, provenance and source turn.

Explicit `training` metadata controls decoder and claim-detector eligibility.
Only authored permitted responses provide positive claim labels. Selected existing
negative examples explicitly represent invented transactions or wrong-owner/world
claims. Old examples that merely dislike a stock response do not become unsupported-
claim labels. Rejected candidates mined during supervised training remain another
detector signal; deterministic text screening is not a factual guarantee.

### Attribution and licenses

- **OpenAssistant 1 and 2**, OpenAssistant contributors, Apache-2.0.
  [Dataset and review metadata](https://huggingface.co/datasets/OpenAssistant/oasst2/blob/main/README.md).
  Immutable revisions, file URLs and SHA-256 values are in
  [the corpus manifest](data/conversation-v4/conversation-v4.json) and `data/sources.json`.
- **BlendedSkillTalk**, Facebook Research/ParlAI contributors, CC BY 4.0.
  [Official dataset documentation](https://raw.githubusercontent.com/facebookresearch/ParlAI/main/parlai/tasks/blended_skill_talk/README.md).
  The official archive is pinned by SHA-256
  `5fbed0068ee89e2d43b93c3ecb341e784617033efa5e8e911a219d4eda6134a6`.
  Changes include filtering, canonical normalization and conversion to structured
  training rows. Per-row attribution and source URLs are retained.
- **Authored NPC episodes**, project-owned, in `scripts/conversation_data/episodes.py`
  and `annotations.py`. Retained classification/focused sources preserve their
  original provenance and license metadata; they are not decoder response sources.

### Splits, sampling and audits

Seed 42 assigns complete episode/family components approximately 80/10/10.
Published evaluation splits take precedence. Exact shared contexts, alternate
responses and near-duplicate conversations are unioned before splitting. Components
that conflict with published splits are quarantined. Near matching uses normalized
word-trigram Jaccard >= 0.85 with exact verification; short contexts use exact
matching. This is a reproducible text test, not proof of semantic independence.
Final-utterance overlap is reported separately because different histories can
legitimately require different answers to the same sentence.

Realization probability is 60% vetted public and 40% authored responses. Expected
history proportions are 25% fresh, 35% short and 40% long. These bins use utterances
that survive the **native packer**: one, two-to-five and six-or-more respectively.
Fresh examples are genuine openings or valid fresh turns; arbitrary history removal
does not manufacture openings. No exact response receives more than 0.5% total
realization probability. `sampling.json` stores each row's effective weight.

Public realization batches are homogeneous and freeze encoder/planner parameters
and their optimizer state. Authored realization batches retain contextual gradients.
Semantic updates balance authored, focused and external classification families;
targetless rows cannot enter supervised pools. The existing seven-semantic/three-
realization update cadence is preserved. Missing annotations are masked, not negative.

Preparation fails for fewer than 1,000 eligible public rows, fewer than 240 authored
episodes, missing behavior categories, infeasible sampling constraints or split
isolation failure. A recorded stratified data review must match the corpus hash
before the pilot starts. It is **agent data review, not independent human release
approval**. The accepted public pool is question-heavy; review actual conversations
before judging naturalness.

## Reproduce preparation

Use the .NET 10 SDK and the existing GPU environment described in
[the PyTorch trainer guide](scripts/torch_training/README.md). Commands below run
from the repository root. Separate CLI output avoids overwriting a DLL used by an
open chat session.

```powershell
dotnet build Fishbrain/Fishbrain.csproj -c Release -o data/training/conversation-v4-tools
$python = 'data/training/torch-env/Scripts/python.exe'
$cli = 'data/training/conversation-v4-tools/Fishbrain.dll'
& $python scripts/conversation_data/fetch.py
```

The focused source is the preserved v3 corpus. On a new checkout, reproduce its
pinned raw sources and compiler output first, then check its three split hashes
against `sourceCorpusHashes` in the v4 manifest:

```powershell
dotnet run -c Release --project Fishbrain.DataGenerator -- fetch
dotnet run -c Release --project Fishbrain.DataGenerator -- compile --output data/compiled-contextual-v3 --seed 42
```

Prepare a **new** directory; the compiler refuses to overwrite a corpus:

```powershell
& $python scripts/conversation_data/compile.py --output data/compiled-conversation-v4-release --cli $cli
& $python scripts/conversation_data/audit_details.py data/compiled-conversation-v4-release data/conversation-v4/audit-details.json
& $python scripts/conversation_data/test_data.py
```

Review `data-review.jsonl` (stratified accepted examples), `audit.json` and the
exclusion ledger. The checked-in `data-review.json` approves only its exact corpus
metadata hash. Changed source, preparation code, splits or sampler require renewed
data review; do not copy an old approval onto new content.

```powershell
dotnet $cli prepare-torch data/compiled-conversation-v4-release data/training/conversation-v4-packed-release
& $python scripts/conversation_data/test_gpu.py data/training/conversation-v4-packed-release data/training/conversation-v4-gpu-tests.json
```

Packing verifies the corpus split/sampling hashes. Checkpoints bind packed content,
tokenizer/model schema, corpus metadata, sampler and complete optimizer/RNG state.
An incompatible resume is rejected. Native `teach` intentionally rejects v4 because
its legacy sampler does not implement the required public-batch freezing and weights.

## Bounded pilot and evaluation

The frozen [challenge suite](data/conversation-v4/challenge.json) has 120 scenarios,
including 60 multi-turn contexts. Its hash is bound before training. Do not regenerate
it after seeing pilot results. It uses independent authored wording rather than
entity substitutions from training templates. Exact challenge contexts are excluded
from preparation. Controlled tests supply gold histories/state so they measure
interpretation separately from accumulated dialogue errors.

The separate 50-session export includes 40 multi-turn sessions. Those sessions use
actual model replies and reduced state, so earlier mistakes can affect later turns.
The user's transcript lives in `development.json`; it is not unseen evaluation.
The existing 17 acceptance cases are retained unchanged.

```powershell
& $python scripts/conversation_data/pilot.py `
  --corpus data/compiled-conversation-v4-release `
  --packed data/training/conversation-v4-packed-release `
  --output data/training/conversation-v4-pilot-final `
  --cli $cli `
  --baseline data/training/torch-run-v1/calibrated.fbm `
  --data-review data/conversation-v4/data-review.json
```

The runner requires a fresh output directory. It trains seed 42, batch 32:
40,000 masked-language updates then 10,000 joint updates. It evaluates at 45,000
and 50,000, saves complete checkpoints at both, and performs no decoder polishing.
Calibration uses validation families only. It compares the pilot and failed model
on identical challenge scenarios, the old acceptance suite and CPU resource checks,
then exports 50 representative conversations from each.

`progress.json` reports training updates; `pilot.json` reports the entire experiment.
An endpoint message alone does not mean quality gates passed. A nonzero final exit
can mean the completed pilot failed its quality gates; inspect the reports.
To stop safely, create `STOP` in the run directory. The trainer saves a durable
checkpoint. A later explicit resume must use the same packed data and remain within
the 50,000-update cap.

```powershell
Get-Content data/training/conversation-v4-pilot-final/progress.json
Get-Content data/training/conversation-v4-pilot-final/pilot.json
dotnet $cli chat data/training/conversation-v4-pilot-final/step-50000-calibrated.fbm
```

The required thresholds remain 90% exact semantic frames and plans, 95% memory
selection and correction state, zero unintended mutations and zero authoritative
alterations. Reports include per-category results, paired cluster-bootstrap 95%
intervals, response repetition, cold load and latency. Deterministic final-text flags
are separate from unsupported-claim review. Low loss, word overlap or passing the
screen cannot establish naturalness. Two independent human reviewers are still
required for release.

`sampler-draws.json` also records every planned seed-42 draw during the joint phase.
`robustness.py` evaluates deterministic punctuation variants and removal of unrelated
history from the frozen cases. These supplementary paired checks were defined
during masked-language pretraining, before conversational evaluation; they are not
counted as new independent unseen scenarios. The original 120-case suite stays fixed.
The separate development export repeats the user's inputs in both fresh and
continuing sessions, and is explicitly development evidence.

After the runner finishes (including a quality-failure exit), reproduce the
supplementary reports with:

```powershell
$run = 'data/training/conversation-v4-pilot-final'
$baseline = 'data/training/torch-run-v1/calibrated.fbm'
$candidate = "$run/step-50000-calibrated.fbm"
& $python scripts/conversation_data/sampler_audit.py data/training/conversation-v4-packed-release data/conversation-v4/sampler-draws.json
& $python scripts/conversation_data/robustness.py --output data/training/conversation-v4-robustness --cli $cli --baseline $baseline --pilot $candidate
& $python scripts/conversation_data/diagnostics.py "$run/challenge-45000.json" "$run/diagnostics-45000.json"
& $python scripts/conversation_data/diagnostics.py "$run/challenge-50000.json" "$run/diagnostics-50000.json"
dotnet $cli conversation-sample $baseline data/conversation-v4/development-scenarios.jsonl "$run/baseline-development.jsonl"
dotnet $cli conversation-sample $candidate data/conversation-v4/development-scenarios.jsonl "$run/development.jsonl"
& $python scripts/conversation_data/review_report.py "$run/baseline-review-50-conversations.jsonl" "$run/review-50-conversations.jsonl" data/conversation-v4/conversations-50.md
& $python scripts/conversation_data/review_report.py "$run/baseline-development.jsonl" "$run/development.jsonl" data/conversation-v4/development-conversations.md
& $python scripts/conversation_data/archive_results.py $run data/training/conversation-v4-robustness data/conversation-v4
```

Archiving copies evidence, not model weights, and does not approve human ratings.

Raw public data, full compiled rows and large model/checkpoint files stay local
under ignored `data/raw`, `data/compiled-*` and `data/training` directories. The
repository contains reproducible preparation code, source fingerprints, audit
snapshots, the frozen evaluation suite and compact results. The incompatible
checked-in shipped model is not replaced by this experiment.
