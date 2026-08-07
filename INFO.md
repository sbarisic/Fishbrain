# Fishbrain v10 engineering notes

This file records the v10 design and its implementation boundaries. See `README.md` for commands and the public API.

## Runtime flow

```text
structured turns
      |
      v
bounded complete-turn packer
      |
      v
raw compositional heads
      |
      v
confidence and deterministic constraints
      |
      +----> one authorized game tool
      |             |
      |             v
      |       typed result template
      |
      v
eligible candidate mask
      |
      v
learned response ranking
      |
      v
deterministic state reducer
```

The immutable `Brain` owns its vocabulary, tokenizer, Transformer weights, structured heads, catalogs, and calibration. Each reply owns its scratch state and deterministic random source.

The caller owns `NpcDialogueState`. The model has no hidden conversation state.

## Model

The language model uses one Transformer layer with these settings:

| Setting | Value |
|---|---:|
| Embedding dimension | 64 |
| Attention heads | 4 |
| MLP dimension | 128 |
| Context length | 128 tokens |
| Attention window | 128 tokens |
| Maximum generated output | 64 tokens |
| Planned steps | 40,000 |
| Structured updates | 90 percent |
| Generation updates | 10 percent |

The packed trainer stores weights and gradients in contiguous `double[]` arrays. It uses `System.Numerics.Vector<double>` for optimizer updates and hot numeric loops.

The structured model uses hashed sparse features. Independent heads use the correct loss for each target type.

- Speech acts, domains, goals, and content flags use sigmoid with binary cross-entropy.
- Affect, stance, response policy, tool schema, and candidate ID use softmax cross-entropy.
- Slots use token-level BIO softmax cross-entropy.

Validation calibrates confidence thresholds. Production clarifies when a required decision is below its threshold.

## Perception schema

Speech acts are multi-label: greet, farewell, ask, request, order, offer, inform, report, confirm, correct, accept, refuse, warn, threaten, apologize, thank, challenge, and negotiate.

Domains are multi-label. They cover social, identity, wellbeing, assistance, activity, navigation, trade, inventory, quests, combat, survival, repair, factions, crime, magic, technology, vehicles, environment, lore, and systems.

Goals are multi-label. They cover rapport, closure, information, finding, access, items, transactions, tasks, coordination, travel, combat, survival, repair, influence, concealment, negotiation, systems, emotion, and clarification.

Slots use BIO labels for person, place, item, faction, quantity, currency, time, direction, vehicle, system, credential, action, proposition, and other.

Content flags cover profanity, fictional violence, graphic violence, threats, crime, identity attacks, self-harm, sexual content, and sexual violence.

## State ownership

`NpcDialogueState` stores bounded dialogue state only:

- Rapport, trust, familiarity, and hostility use values from zero through three.
- Active domains and goals contain at most four values each.
- Pending actions contain at most three ordered values.
- One clarification and one transaction can be active.
- Person, place, item, vehicle, and system references contain at most 32 normalized characters.

The reducer owns every transition. Tools own inventory, money, quest truth, navigation, and other world facts.

## Tool boundary

`GameToolRegistry` is immutable after construction. It accepts explicit `IGameTool` objects and validates each schema.

Registration is the authorization boundary. Fishbrain can run mutating tools after registration.

The runtime permits one invocation per reply. The idempotency key uses the conversation ID and turn ID.

Read-only tools require 0.95 precision. Mutating tools require 0.99 precision. Missing or ambiguous slots always produce a clarification.

Tool results contain typed fields. Only declared templates can render these fields.

The repository includes location, ware list, price, buy, and sell demo tools. Merchant behavior depends on registered tools, not a persona flag.

## Response ownership

The project owns all production response candidates. Each candidate has a stable ID, policies, domains, tones, requirements, and template fields.

The runtime masks ineligible candidates before ranking. It selects the highest eligible candidate above the calibrated threshold.

If no candidate passes, the runtime uses a typed clarification or deterministic fallback. It records the selected response source in diagnostics.

Exact-example memory does not run in the v10 production API. The old v9 path remains internal for archived tests only.

## Corpus rules

The compiler creates exactly 30,000 rows. It keeps external sources under their native supervision masks.

Only project-owned rows can supervise production candidates. Identity attacks and other sensitive bands supervise recognition and response policy only.

The source manifest permits commercial-use licenses only. It rejects noncommercial, research-only, and unclear licenses.

The compiler joins rows into split components by four relations:

1. The rows share a semantic family.
2. The rows share a source conversation.
3. The rows have equal normalized input.
4. The rows are near duplicates.

It assigns each complete component to one split. The targets are 24,000 train rows, 3,000 validation rows, and 3,000 test rows.

The tracked benchmark contains 128 turns from 64 fantasy and science-fiction scenarios. The compiler excludes its exact text and semantic families.

## Checkpoint version 10

The full training checkpoint stores these items:

- The fixed v10 version and model configuration.
- The immutable word vocabulary.
- Transformer and structured-head weights.
- Adam moments and deterministic random state.
- Completed step, sampler position, and best-role metadata.
- Label schemas, calibration, tool schemas, and candidate catalog.
- Corpus hash and an integrity checksum.

The compact `.fbm` model removes optimizer state and exact memory. It stores a readable JSON header, float32 weights, and SHA-256 integrity data.

The loader rejects v2 through v9 checkpoints. It also rejects corrupt v10 headers, schemas, counts, weights, and checksums.

## Evaluation

The evaluator reports raw neural metrics and constrained production metrics separately. It also reports response-source counts and experimental generation results.

The release gate uses these minimum values:

| Metric | Minimum |
|---|---:|
| Speech-act macro-F1 | 0.85 |
| Domain macro-F1 | 0.85 |
| Goal macro-F1 | 0.80 |
| Affect accuracy | 0.85 |
| Policy accuracy | 0.90 |
| Content macro-F1 | 0.90 |
| Slot span F1 | 0.85 |
| Tool accuracy | 0.95 |
| Mutating-tool precision | 0.99 |
| Tool argument exact match | 0.90 |
| Candidate top-1 | 0.80 |
| Candidate top-3 | 0.95 |
| Benchmark semantic assertions | 0.90 |

The release gate also requires zero invalid output, unexpected empty output, overlength output, duplicate mutation, benchmark contamination, and altered authoritative tool fields.

Telemetry includes the corpus hash, checkpoint hash, environment, vector width, throughput, losses, raw metrics, constrained metrics, response sources, and gate results.

## Deferred work

V10 does not include learned multi-step reasoning. The `THINK -> STATE -> THINK -> ACTION` design belongs to v11.

Do not increase Transformer capacity until v10 evaluation shows a capacity bottleneck.
