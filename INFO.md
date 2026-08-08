# Fishbrain engineering notes

This document records the implementation boundaries and release contract. See `README.md` for commands and API examples.

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

The shared contextual model is updated during the interleaved curriculum through 160K. Later head-polish phases freeze that representation while the structured and response heads converge. Structured heads fuse 4,096 hashed lexical features with the 128-dimensional contextual vector. The production response-plan reranker uses the same contextual representation and hard negatives.

## Perception heads

Fishbrain predicts independent heads for:

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

The response catalog contains 201 plan IDs and at least 4,400 distinct visible project-owned variations. The intentional no-response plan has one empty surface. Plans declare policy, domain, knowledge target, speech acts, keywords, and variations. Metadata masks ineligible plans before scoring. The runtime retrieves the top five eligible plans, applies contextual plan/ranking scores, and uses candidate ID as the deterministic final tie-breaker.

Production response-source telemetry distinguishes tool templates, persona templates, capability templates, ranked variations, clarifications, fallbacks, and experimental generation. A recognized domain must not emit generic `I DO NOT KNOW` text.

## Corpus integrity

The compiler produces exactly 60,000 rows and records full turns, initial state, persona, structured targets, slots, tool arguments, response plan, positive/rejected variation IDs, and provenance. Project-owned data contributes 36,000 rows; compatible external data contributes 24,000.

Complete conversations and connected semantic families are assigned as components to an approximately 80/10/10 split. Component edges include semantic family, source conversation, normalized-input equality, and near-duplicate signatures.

The audit requires:

- all source license, revision, URL, checksum, and attribution fields;
- only project-owned, MIT, Apache-2.0, CC0, or CC BY input artifacts;
- no exact duplicates, contradictions, split leakage, or benchmark contamination;
- at least 2,000 project-owned normalized input skeletons;
- no skeleton above 0.25% of the full corpus;
- exactly 60,000 records and every declared source quota.

The compiled corpus hash for the current source manifest and seed 42 is `0d2ec57cc86b20b8a1bb23eb9479367788202aebe352813e1eea3f4dded3ede3`.

## Curriculum and checkpoints

The deterministic completed 210,000-step schedule has three phases:

- steps 0-160K interleave seven contextual structured updates, two pairwise response-ranking updates, and one experimental generation update per ten steps;
- steps 160K-200K freeze the shared encoder and polish structured/ranking heads with phase-local sampling, rare-domain families, explicit and hard-negative tool families, independent response rows, and auxiliary slot passes;
- steps 200K-210K freeze every passing structured head and train only response-plan classification and ranking.

Rolling checkpoints and telemetry are written every 1,000 steps. Ordinary checkpoints use one fixed source-stratified validation sample. Full validation runs every 20K and at the configured final step. `best-production` is selected only when every raw neural release minimum passes; `best-generation` is retained separately. The full configured schedule runs even if the best checkpoint occurs earlier. A completed plan may be extended to a larger explicit endpoint; changing an in-progress plan remains forbidden.

The inference format starts with `FISHBRAIN`, stores a readable JSON metadata header followed by float32 weights, and ends with an integrity checksum. It includes the label schema, per-label calibration, tool schemas, response catalog, corpus hash, and weights hash. There is no format-version field, compatibility loader, or migration path; only the current schema is accepted.

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

The final 210K artifact measured 2.7463/4.1461 ms median/p95 over 2,048 replies on the development machine. This remains a recorded measurement rather than a relative compatibility claim; future performance comparisons should build the relevant Git revisions independently instead of adding old-format loaders to the current runtime.

## Current trained artifact

The completed 210,000-step run is stored at `data/models/model-latest.fbm`. The repacked artifact is 41,834,317 bytes, has SHA-256 `5cc8680df9a42f10dc7b4db99807dc1f1b8ec17e9223b9382cb22687ce7dc1c8`, weights hash `1ebc66026560e813b992a57099f02a2784392e5645f9d2b3921125b72bc2040a`, and corpus hash `0d2ec57cc86b20b8a1bb23eb9479367788202aebe352813e1eea3f4dded3ede3`.

The full 6,001-row validation stage passes every exact raw neural minimum. Its composite is 0.9077; representative passing values are domain F1 0.8561, slot F1 0.8568, tool accuracy 0.9551, mutating-tool precision 0.9907, response top-1 0.8539, and response top-3 0.9669.

The independent 5,999-row test evaluation passes the stage gate and every hard runtime invariant. It records 99.22% semantic assertion success on the 256-turn benchmark, 100% tool fidelity, 100% tool-argument exact match, 100% mutating-tool precision, and zero invalid, unexpected-empty, overlength, or generic known-domain fallback outputs. The quality release gate remains closed because raw domain macro-F1 is 0.8324, slot span F1 is 0.8296, and tool selection accuracy is 0.9489. The other raw neural thresholds pass, including response top-1/top-3 at 0.8596/0.9636 and variation Recall@10/MRR at 0.9818/0.9146. These misses are reported as failures; `--gate release` returns a nonzero exit code and does not weaken or bypass the thresholds.
