# Fishbrain v11 engineering notes

This document records the v11 implementation boundaries and release contract. See `README.md` for commands and API examples.

## Design boundary

Fishbrain is not a general-purpose LLM. It is a compact contextual perception, planning, retrieval, and optional generation model intended to run inside a game process without external services or NuGet dependencies.

The neural model proposes structured perception, a knowledge target, a tool schema, and a response plan. Deterministic code owns validation, constraints, state reduction, tool execution, authoritative rendering, eligibility masks, and fallbacks. This boundary prevents a generated sentence from inventing or rewriting game state.

## Context and tokenization

Each `Brain` owns an immutable `DialogueTokenizer`. No static lexical vocabulary is shared between models. The tokenizer accepts any input casing and normalizes internally to uppercase.

Known lexical words use one token each. An unknown word is encoded as word-start, uppercase character/digit/apostrophe/hyphen tokens, and word-end. The runtime preserves the normalized original span for slot copying and tool arguments. Control-token IDs are independent of label enum counts.

The context packer accepts structured `DialogueTurn` values. It retains the current player turn and removes only complete oldest turns until the 256-token budget fits. Role-looking text inside an utterance does not become structure. The contextual Transformer mean-pools final-layer states for the current player turn.

## Transformer and numerical path

The shared model has two distinct Transformer layers, 128-dimensional embeddings, eight attention heads, a 256-dimensional feed-forward block, and a 256-token context. Training and inference iterate over the configured layer count.

Packed training uses contiguous arrays and `System.Numerics.Vector<double>` where applicable. Reference and optimized forward/backward paths are checked for numerical parity. Tests also cover finite gradients, deterministic initialization, and bit-equivalent save/resume behavior.

All structured, ranking, and generation curriculum phases update the shared contextual model. Structured heads fuse 512 hashed lexical features with the 128-dimensional contextual vector. The production response-plan reranker uses the same contextual representation and hard negatives.

## Perception heads

V11 predicts independent heads for:

- multi-label speech acts, domains, goals, and content flags;
- single-label affect, stance, response policy, and knowledge target;
- token-level BIO slots;
- tool schema and response-plan softmax outputs.

Multi-label heads use sigmoid/BCE. Exclusive heads and slots use softmax cross-entropy. Thresholds are calibrated per label. Ordinary speech-act, domain, and goal outputs are limited to three; a third label must exceed its threshold by 0.15. Content flags are not capped.

Constraints are typed as `ENFORCE`, `VETO`, or `BOOST`. Structural and validation-proven rules may enforce or veto. Lexical hints normally boost. Reply diagnostics preserve the operation, label, evidence, score change, and final confidence.

## Knowledge and authority

`KnowledgeTarget` separates questions about name, role, origin, home, family, occupation, faction, traits, capabilities, balance, inventory, location, and world facts.

Persona facts are caller-authored and rendered through typed templates. Capabilities come only from the registered tools. Dialogue state contains conversational memory but never owns inventory, currency, stock, prices, location, quest truth, or world facts.

The demo tools share one `DemoWorldState`. Mutations validate their complete precondition set before changing state. Their idempotency cache is keyed by the deterministic invocation key, so a replay returns the prior result. Tool exceptions become typed failure results; authoritative fields still pass schema validation before rendering.

## State reducer

The reducer owns all `NpcDialogueState` changes:

- hostility rises only after validated insults, threats, attacks, or betrayals;
- neutral turns do not lower hostility immediately;
- hostility lowers after three calm turns or accepted social repair;
- rapport and trust change only for meaningful events;
- recent person, place, item, vehicle, and system references are bounded to 32 normalized characters;
- ambiguous reference phrases clarify instead of guessing;
- one action executes per reply and additional recognized actions enter the bounded pending queue.

## Response selection

The response catalog contains exactly 200 plan IDs and at least 5,000 project-owned variations. Plans declare policy, domain, knowledge target, speech acts, keywords, and variations. Metadata masks ineligible plans before scoring. The runtime retrieves the top five eligible plans, applies contextual plan/ranking scores, and uses candidate ID as the deterministic final tie-breaker.

Production response-source telemetry distinguishes tool templates, persona templates, capability templates, ranked variations, clarifications, fallbacks, and experimental generation. A recognized domain must not emit generic `I DO NOT KNOW` text.

## Corpus integrity

The compiler produces exactly 60,000 rows and records full turns, initial state, persona, structured targets, slots, tool arguments, response plan, positive/rejected variation IDs, and provenance. Project-owned data contributes 36,000 rows; compatible external data contributes 24,000.

Complete conversations and connected semantic families are assigned as components to an approximately 80/10/10 split. Component edges include semantic family, source conversation, normalized-input equality, and near-duplicate signatures.

The audit requires:

- all source license, revision, URL, checksum, attribution, and transformation fields;
- only project-owned, MIT, Apache-2.0, CC0, or CC BY input artifacts;
- no exact duplicates, contradictions, split leakage, or benchmark contamination;
- at least 2,000 project-owned normalized input skeletons;
- no skeleton above 0.25% of the full corpus;
- exactly 60,000 records and every declared source quota.

The compiled v11 corpus hash for the current source manifest and seed 42 is `c240725fa1f316fa02b8a8968269056b72fa123f4bca1fad5b4534385c8834fd`.

## Curriculum and checkpoints

The deterministic 80,000-step schedule is:

- 56,000 contextual structured/tool/knowledge/plan updates;
- 16,000 pairwise response-ranking updates;
- 8,000 experimental generation updates.

Rolling checkpoints and telemetry are written every 1,000 steps. Ordinary checkpoints use one fixed source-stratified validation sample. Full validation runs at 20K, 40K, 60K, and 80K. `best-production` is selected by the structured composite subject to finite metrics and mutating-tool precision. `best-generation` is retained separately. The full 80K schedule runs even if the best checkpoint occurs earlier.

The inference format starts with `FISHBRN11`, uses format version 11, stores a readable JSON metadata header followed by float32 weights, and ends with an integrity checksum. It includes the label schema, per-label calibration, tool schemas, response catalog, corpus hash, and weights hash. Versions 2 through 10 are rejected rather than migrated.

## Release gates

The release evaluator reports raw neural and constrained production metrics separately. It also reports response-source counts, tool argument exact match, authoritative-field fidelity, unexpected empty/invalid/overlength output, benchmark failures, hashes, throughput, and environment details.

Required thresholds are:

| Metric | Threshold |
|---|---:|
| Speech-act macro-F1 | 0.85 |
| Domain macro-F1 | 0.85 |
| Goal macro-F1 | 0.80 |
| Affect accuracy | 0.85 |
| Policy accuracy | 0.90 |
| Content macro-F1 | 0.90 |
| Slot span F1 | 0.85 |
| Knowledge-target accuracy | 0.90 |
| Tool selection | 0.95 |
| Mutating-tool precision | 0.99 |
| Tool argument exact match | 0.90 |
| Response-plan top-1 | 0.85 |
| Response-plan top-3 | 0.95 |
| Variation Recall@10 | 0.95 |
| Variation MRR | 0.80 |
| 256-turn semantic assertions | 0.90 |

Tool fidelity, mutation safety, persona fidelity, OOV preservation, parser/state invariants, and structural invariants require 100%. Unexpected empty, invalid, overlength, generic known-domain fallback, duplicate mutation, and altered authoritative-field counts must remain zero.

The 2x128 release build's median and p95 reply latency must remain within four times the v10 baseline measured on the same machine. The tracked 2,048-reply measurement on the development machine records v10 at 0.6728/1.2559 ms median/p95 and v11 at 0.9227/2.1951 ms. The v11 ratios are 1.3714x/1.7478x, so the latency gate passes. The measurement environment and checkpoint hashes are stored in `data/benchmarks/v10-v11-latency.json`.

## Current trained artifact

The completed 80,000-step run exported `data/models/model-v11-latest.fbm`. The artifact is 36,939,894 bytes and contains corpus hash `c240725fa1f316fa02b8a8968269056b72fa123f4bca1fad5b4534385c8834fd`.

The full 5,999-row held-out evaluation passes the stage gate and all hard runtime invariants. It records 99.61% semantic assertion success on the 256-turn benchmark, 100% tool fidelity, 100% tool-argument exact match, and zero invalid, unexpected-empty, overlength, or generic known-domain fallback outputs. The one preserved semantic-benchmark miss is the legacy `HELLO?` row whose expected content band is `PROFANITY`; the runtime correctly leaves it unflagged. The quality release gate remains closed because raw domain macro-F1 is 0.7966, slot span F1 is 0.8220, and tool selection accuracy is 0.9257. These misses are reported as failures; the evaluator does not weaken or bypass their thresholds.
