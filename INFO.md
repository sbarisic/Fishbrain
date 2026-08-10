# Fishbrain engineering notes

This document records the implementation boundaries and release contract. See `README.md` for commands and API examples, and `AI_MODEL.md` for the complete neural architecture, input paths, output heads, and training data flow.

## Design boundary

Fishbrain is a compact contextual dialogue model intended to run inside a game process without external services or NuGet dependencies. Its product scope includes general-purpose banter and small talk as well as game-grounded commands, questions, and transactions.

The neural model proposes structured perception, a knowledge target, a tool schema, and a response plan. Deterministic code owns validation, constraints, state reduction, tool execution, authoritative rendering, eligibility masks, and fallbacks. This boundary prevents a generated sentence from inventing or rewriting game state.

General conversation does not weaken that authority boundary. The model may improvise social language, opinions, jokes, and persona-colored reactions, but caller-owned persona data and game tools remain the only sources for exact identity, world, inventory, currency, quest, and action facts.

## Context and tokenization

Each `Brain` owns an immutable `DialogueTokenizer`. No static lexical vocabulary is shared between models. The tokenizer accepts any input casing and normalizes internally to uppercase.

Known lexical words use one token each. An unknown word is encoded as word-start, uppercase character/digit/apostrophe/hyphen tokens, and word-end. The runtime preserves the normalized original span for slot copying and tool arguments. Control-token IDs are independent of label enum counts.

The context packer accepts structured `DialogueUtterance` values. It retains the current player turn and removes only complete oldest turns until the 256-token budget fits. Role-looking text inside an utterance does not become structure. The contextual Transformer mean-pools final-layer states for the current player turn.

## Transformer and numerical path

The shared model has two distinct Transformer layers, 128-dimensional embeddings, eight attention heads, a 256-dimensional feed-forward block, and a 256-token context. Training and inference iterate over the configured layer count.

Packed training uses contiguous arrays and `System.Numerics.Vector<double>` where applicable. Reference and optimized forward/backward paths are checked for numerical parity. Tests also cover finite gradients, deterministic initialization, and bit-equivalent save/resume behavior.

The shared contextual model is updated during the interleaved curriculum through 200K. From 200K to 245K, operational heads are frozen while discourse, coreference, fact-span, and response-ranking heads are polished. From 245K to 260K, structured heads remain frozen while response ranking and conversational realization are polished. Structured heads fuse 4,096 hashed lexical features with the 128-dimensional contextual vector.

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
- session facts are bounded to 16 and topic/callback summaries are bounded to eight;
- positive single-valued name, role, occupation, origin, and home facts replace older positive values;
- explicit negation removes only the matching positive value;
- semantic traces retain the meaning and fallback reason of the last NPC response;
- one action executes per reply and additional recognized actions enter the bounded pending queue.

## Response selection

The response catalog contains 201 plan IDs and at least 4,400 distinct visible project-owned variations. The intentional no-response plan has one empty surface. Plans declare policy, domain, knowledge target, speech acts, keywords, and variations. Metadata masks ineligible plans before scoring. The runtime retrieves the top five eligible plans, applies contextual plan/ranking scores, and uses candidate ID as the deterministic final tie-breaker.

Production response-source telemetry distinguishes tool templates, persona templates, capability templates, discourse repair, ranked variations, validated conversational generation, clarifications, and fallbacks. A recognized domain must not emit generic `I DO NOT KNOW` text.

## General conversation requirement

The production runtime must handle ordinary social conversation beyond a finite command catalog. Required behavior includes:

- multi-turn greetings, farewells, introductions, and conversational repair;
- ordinary small talk about daily life, weather-like observations, food, travel, hobbies, work, stories, and hypothetical topics;
- persona-consistent preferences, opinions, humor, playful teasing, and light disagreement;
- relevant follow-up questions, topic changes, callbacks, and pronoun/reference continuity;
- varied, natural responses that do not repeat a stock phrase or merely paraphrase the player;
- honest conversational uncertainty when a factual answer is unavailable;
- unchanged safety, capability, tool-authorization, and authoritative-field guarantees.

The response catalog remains useful for exact policies and high-risk boundaries, but catalog ranking alone is not sufficient for this breadth. The production hybrid includes deterministic conversational repair and a realization path conditioned on current input, bounded history, persona, approved persistent facts, unverified session facts, resolved discourse, and an antecedent utterance. Exact facts are still inserted only after deterministic validation, and free-form text never rewrites a tool result.

## Corpus integrity

The compiler produces exactly 80,000 rows and records full conversations, initial state and player profile, persona, discourse frame, fact delta, antecedent, response action, acceptable response constraints, rejected response, structured targets, slots, tool arguments, response plan, and provenance. Project-owned data contributes 56,000 rows; compatible external data contributes 24,000. The added 20,000 reviewed project-owned rows contain 8,000 fact/correction rows, 6,000 callback/explanation rows, 4,000 general conversation rows, and 2,000 discourse hard negatives.

Complete conversations and semantic families are assigned to an approximately 80/10/10
split, stratified by band, discourse act, predicate, polarity, antecedent type, and
ambiguity. The 20,000 discourse rows contain at least 2,000 fact families, 1,500
reference families, 1,000 banter families, and 1,000 hard-negative families. Expansion
is capped at four rows per family, or two for hard negatives. Model-visible discourse
text contains no serial markers.

The audit requires:

- all source license, revision, URL, checksum, and attribution fields;
- only project-owned, MIT, Apache-2.0, CC0, or CC BY input artifacts;
- no exact duplicates, contradictions, split leakage, or benchmark contamination;
- at least 2,000 project-owned normalized input skeletons;
- no skeleton above 0.25% of the full corpus;
- exact exclusion of the 256 operational prompts and B01-B12 conversation prompts;
- exactly 80,000 records and every declared source quota.

The audited corpus hash for the current source manifest and seed 42 is
`ad92057bf82800c0f1ff95602e01ee44dff87d49b55035602d5301ba076a6425`.
Its split sizes are 64,217 train, 7,885 validation, and 7,898 test rows.

## Curriculum and checkpoints

The fixed 260,000-step schedule has three phases:

- steps 0-200K interleave seven structured, two ranking, and one generation update per
  ten steps. Each structured update averages 32 examples, with 75% operational and 25%
  discourse selection;
- steps 200K-245K freeze the Transformer and passing operational heads, then use 60%
  discourse/coreference, 25% ranking, and 15% corrective failing-operational updates;
- steps 245K-260K freeze every structured head and train project-owned conversational
  realization only.

Families are shuffled deterministically for each epoch and rotate through at most four
members. Discourse selection is 40% facts, 30% references, 20% banter, and 10% hard
negatives. The structured learning rate decays from 0.03 to 0.003 with a cosine schedule,
and positive weighting is capped at 2.0.

Rolling checkpoints and telemetry are written every 1,000 steps. Calibration uses one
fixed family-balanced 2,000-row subset and never a noisy 128-row milestone sample. Full
family-balanced validation runs every 20K and at the configured final step.
`best-production` is selected only when every raw neural release minimum passes;
`best-generation` is retained separately. Training never replaces `model-latest.fbm`
automatically. The candidate must pass every automated and human gate before export.

The inference format starts with `FISHBRAIN`, stores a readable JSON metadata header followed by float32 weights, and ends with an integrity checksum. It includes the label schema, per-label calibration, tool schemas, response catalog, corpus hash, and weights hash. There is no format-version field, compatibility loader, or migration path; only the current schema is accepted.

## Release gates

The release evaluator reports raw neural and constrained production metrics separately. It also reports response-source counts, tool argument exact match, authoritative-field fidelity, unexpected empty/invalid/overlength output, benchmark failures, hashes, throughput, and environment details.

Required thresholds are:

| Metric | Threshold |
|---|---:|
| Speech-act macro-F1 | 0.85 |
| Domain macro-F1 | 0.84 |
| Goal macro-F1 | 0.80 |
| Affect accuracy | 0.85 |
| Policy accuracy | 0.90 |
| Content macro-F1 | 0.90 |
| Slot span F1 | 0.85 |
| Knowledge-target accuracy | 0.90 |
| Tool selection | 0.95 |
| Mutating-tool precision | 0.97 |
| Tool argument exact match | 0.90 |
| Response-plan top-1 | 0.85 |
| Response-plan top-3 | 0.95 |
| Variation Recall@10 | 0.95 |
| Variation MRR | 0.80 |
| Discourse-act accuracy | 0.90 |
| Speaker attribution | 0.95 |
| Fact span F1 | 0.90 |
| Antecedent accuracy | 0.90 |
| Correction-state accuracy | 0.95 |
| 256-turn semantic assertions | 0.90 |

Tool fidelity, mutation safety, persona fidelity, OOV preservation, parser/state invariants, and structural invariants require 100%. Unexpected empty, invalid, overlength, generic known-domain fallback, duplicate mutation, and altered authoritative-field counts must remain zero.

General conversation has a separate held-out gate over B01-B12. `conversation-sample` exports the exact model responses for review. `conversation-gate` compares that fresh sample with the reviewed file, rejects stale or mismatched model outputs and incomplete ratings, and requires exactly two distinct human reviews per turn. It requires at least 90% appropriate turns, 90% continuity, 95% persona consistency, 90% relevant/complete responses, 90% graceful topic switching, and zero unsupported factual claims, authority violations, or safety violations. An automated, stale, or single-reviewer file cannot pass.

The final 210K artifact measured 2.7463/4.1461 ms median/p95 over 2,048 replies on the development machine. This remains a recorded measurement rather than a relative compatibility claim; future performance comparisons should build the relevant Git revisions independently instead of adding old-format loaders to the current runtime.

## Checked-in artifact and replacement status

The checked-in artifact below uses the previous schema and is deliberately rejected by the current runtime. It remains in the worktree until a fresh candidate passes every gate; schema compatibility code is not retained.

The completed 210,000-step run is stored at `data/models/model-latest.fbm`. The repacked artifact is 41,834,317 bytes, has SHA-256 `5cc8680df9a42f10dc7b4db99807dc1f1b8ec17e9223b9382cb22687ce7dc1c8`, weights hash `1ebc66026560e813b992a57099f02a2784392e5645f9d2b3921125b72bc2040a`, and corpus hash `0d2ec57cc86b20b8a1bb23eb9479367788202aebe352813e1eea3f4dded3ede3`.

The full 6,001-row validation stage passes every exact raw neural minimum. Its composite is 0.9077; representative passing values are domain F1 0.8561, slot F1 0.8568, tool accuracy 0.9551, mutating-tool precision 0.9907, response top-1 0.8539, and response top-3 0.9669.

The independent 5,999-row test evaluation passes the stage gate and every hard runtime invariant. It records 99.22% semantic assertion success on the 256-turn operational benchmark, 100% tool fidelity, 100% tool-argument exact match, 100% mutating-tool precision, and zero invalid, unexpected-empty, overlength, or generic known-domain fallback outputs. The quality release gate remains closed because raw domain macro-F1 is 0.8324, slot span F1 is 0.8296, and tool selection accuracy is 0.9489. The other raw neural thresholds pass, including response top-1/top-3 at 0.8596/0.9636 and variation Recall@10/MRR at 0.9818/0.9146. No general-conversation gate has been run, so the artifact is not accepted for general-purpose banter or small talk. These misses are reported as failures; `--gate release` returns a nonzero exit code and does not weaken or bypass the thresholds.
