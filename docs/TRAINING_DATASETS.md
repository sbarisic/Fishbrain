# Training Data Expansion Plan

This plan adds game-grounded dialogue, task semantics, social goals, and mature
language without treating a generic assistant corpus as the target product.
Licenses were reviewed from primary project pages on 2026-08-07, but this is an
engineering screen rather than legal advice. Pin and archive the exact license
shipped with every downloaded revision before importing any row.

## Recommendation

Use three kinds of data:

1. Project-owned generated game interactions for exact behavior, state, tool,
   and response-policy supervision.
2. Licensed external corpora for wording diversity and source-native auxiliary
   labels.
3. Mature/toxic corpora for recognition and held-out robustness, not automatic
   NPC response imitation.

The next balanced 30,000-row experiment should remain majority project-owned.
External assistant data should provide paraphrases, not decide game behavior.

## Existing sources

| Source | Current role | Keep/change |
|---|---|---|
| Project synthetic | 6,000 game-like rows | Expand with the intent catalog, state contrasts, and typed tools. |
| OASST1 | 2,000 short response pairs | Keep a capped quota; it is helpful language but not game-grounded policy. |
| CLINC150 | 800 intent-only rows | Keep as auxiliary NLU; do not map unrelated labels by keywords. |
| GoEmotions | 1,200 affect-only rows | Keep source-native emotion supervision. |

## High-value import candidates

### Tier A - prepare an importer

| Dataset | What it contributes | License screen | Fishbrain use |
|---|---|---|---|
| [LIGHT and LIGHT WILD](https://parl.ai/projects/light/) | Fantasy characters that both speak and act; the project page reports 41,131 WILD training dialogue episodes. | Verify the exact downloaded data bundle; do not infer a data license only from the archived code repository's MIT license. | Highest domain fit. Import dialogue/action structure, character role, location, object, and emote metadata. Keep original episode groups intact. |
| [SLURP](https://github.com/pswietojanski/slurp) text | Natural task requests with action and entity annotations across 18 domains. | Text is CC BY 4.0; audio is CC BY-NC 4.0. Import text only unless noncommercial audio is deliberately accepted. | Map source actions/entities into speech-act and slot auxiliary heads; rewrite none of its targets as fantasy replies. |
| [MASSIVE](https://github.com/alexa/massive) | More than 1M utterances, 52 languages, 60 intents, and 55 slot types. | The dataset archive says CC BY 4.0 and carries its own `LICENSE`; the code repository also has separate notices. | Start with capped English utterances for intent/slot diversity. Later use other locales only if Fishbrain becomes multilingual. |
| [Taskmaster](https://github.com/google-research-datasets/Taskmaster) | More than 55,000 spoken/written task-oriented dialogues in many service domains. | Individual releases include license notices; Taskmaster-1 states CC BY 4.0. Verify TM-2, TM-3, and TM-4 separately. | Transaction flow, corrections, clarification, reservation, price, and multi-turn slot state. Keep each dialogue in one split. |
| [Schema-Guided Dialogue](https://github.com/google-research-datasets/dstc8-schema-guided-dialogue) | More than 20,000 multi-domain conversations with dynamic schemas, intents, slots, and dialogue state. | CC BY-SA 4.0 for SGD and SGD-X. Share-alike implications for redistributed derivatives need explicit handling. | Model the proposed compositional schema and tool calls; especially useful for unseen-service generalization. |
| [TEACh](https://github.com/alexa/teach) | Human-human dialogue grounded in actions in a simulated household. | Code MIT, images Apache-2.0, other data CDLA-Sharing-1.0. | Action grounding, clarification, object references, sequencing, and corrections. Import text/action data under its separate data license. |
| [ProsocialDialog](https://parl.ai/docs/tasks.html) | 58,000 dialogues pairing problematic behavior with constructive feedback. | Confirm the license in the exact ParlAI task bundle before import; project summaries are not enough for provenance. | Response-policy and de-escalation auxiliary data. Do not force generic prosocial wording onto hostile fictional characters. |
| [SOTOPIA](https://github.com/sotopia-lab/sotopia) | Goal-driven social simulations with private information, personalities, relationships, and cooperative/competitive goals. | Framework repository is MIT; verify separately whether packaged scenarios/episodes carry identical terms. | Excellent schema inspiration and evaluation scenarios. Import only assets whose data license is explicit. |

### Tier B - valuable with restrictions or legal review

| Dataset | Value | Constraint | Recommended use |
|---|---|---|---|
| [EmpatheticDialogues](https://parl.ai/docs/tasks.html) | 25,000 conversations grounded in emotional situations. | CC BY-NC; unsuitable for an unrestricted commercial training bundle. | Optional noncommercial research experiment or evaluation-only comparison. |
| DailyDialog via [ParlAI tasks](https://parl.ai/docs/tasks.html) | Topic, emotion, and dialogue-act annotations. | Confirm the upstream distribution terms and attribution before import. | Auxiliary act/affect training after provenance review. |
| [Alexa Arena](https://github.com/amazon-science/alexa-arena) | Language instructions and answers grounded in simulated robot trajectories. | CC BY-NC 4.0. | Noncommercial action-grounding research only. |
| [STORIUM](https://aclanthology.org/2020.emnlp-main.525/) | 6,000 long collaborative stories, 125M tokens, character goals and attributes. | Storium games can use different content licenses; the platform's [game-license terms](https://storium.com/terms) do not establish one uniform reusable data license. | Quarantine pending verification of the released bundle and every applicable content license. Do not scrape the live service. |
| SOTOPIA episode/model outputs | Rich social trajectories and goal outcomes. | Framework code terms do not automatically license third-party/model-generated episode content. | Generate original Fishbrain scenarios using the schema; import episodes only with explicit provenance. |
| Open-source game dialogue | Strongest style match. | A game's code license may not clearly cover story text, and copyleft/share-alike obligations can affect derived distributions. | Prefer project-owned scenarios. Review each game's content license before extraction; never scrape commercial game scripts. |

## Mature language and violence sources

The product should permit fictional swearing and violence while distinguishing
them from other sensitive categories. Use these corpora primarily for
classification, hard negatives, and response-policy evaluation.

| Dataset | Labels/content | License screen | Use |
|---|---|---|---|
| [Civil Comments](https://www.tensorflow.org/datasets/catalog/civil_comments) | Toxicity, severe toxicity, obscene language, threat, insult, identity attack, and related identity annotations. | TFDS states that the dataset and underlying comment text are CC0. | Strong auxiliary classifier source. Cap and rebalance labels; never use toxic comments as desired NPC replies. |
| [Bot-Adversarial Dialogue](https://parl.ai/docs/tasks.html) | Multi-turn adversarial offensive/not-offensive dialogue. | Verify the license files downloaded by the ParlAI task. | Evaluation and context-aware hostility recognition. Preserve dialogue groups. |
| [ToxiGen](https://github.com/microsoft/TOXIGEN) | Implicit toxic/benign sentences mentioning 13 minority groups; 27,450 raw human annotations are available. | Repository describes the released data/models as research-only; its exact license and access terms require review. | Research/evaluation-only identity-attack detection. Do not train NPC imitation or mix it into ordinary profanity. |
| [RealToxicityPrompts](https://github.com/allenai/real-toxicity-prompts) | 100,000 naturally occurring prompt fragments with toxicity scores. | Repository is Apache-2.0, but derived web text warrants source/license review before redistribution. | Held-out robustness and prompt recognition, not response targets. |
| [HH-RLHF](https://github.com/anthropics/hh-rlhf) | Helpful/harmless preference pairs and red-team dialogue. | Archived repository is MIT; inspect each data subset and provenance notice. | Preference or response-ranking contrasts. It is generic assistant data, so keep a low quota. |
| [SaFeRDialogues](https://parl.ai/projects/saferdialogues/) | 8,000 dialogues containing a safety failure, user feedback, and graceful acknowledgment. | Verify the data artifact's terms before import. | Learn feedback/repair acts such as `THAT IS NOT WHAT I ASKED`; adapt style to NPC roles. |

### Content labels

Replace the single unsafe-text blacklist with independent flags:

| Label | Example class | Default use |
|---|---|---|
| `PROFANITY` | generic coarse language | Train and evaluate as ordinary mature-game language. |
| `FICTIONAL_VIOLENCE` | attack, kill, blood, weapons, battle | Train and evaluate. |
| `GRAPHIC_VIOLENCE` | detailed fictional injury | Capped mature set; keep a rating flag. |
| `THREAT` | promised harm or destruction | Train intent and game-policy response. |
| `CRIME` | theft, bribery, smuggling, fictional hacking | Train as game intent; caller owns authorization and consequences. |
| `IDENTITY_ATTACK` | hostility aimed at a protected class | Recognition/evaluation and de-escalation; never automatic positive response imitation. |
| `SELF_HARM` | real or fictional self-directed harm | Separate policy and benchmark; never collapse into ordinary combat order. |
| `SEXUAL_CONTENT` | sexual language | Separate age/rating policy; not implied merely by allowing violence. |
| `SEXUAL_VIOLENCE` | coercive sexual harm | Restricted evaluation/policy set; never generic realization data. |

This separation allows `FIX THIS DAMN SWORD` to remain a repair request,
`KILL THE NECROMANCER` to remain a combat directive, and a targeted slur to be
recognized as both hostility and an identity attack.

## Proposed 30,000-row v10 corpus

| Component | Rows | Supervision |
|---|---:|---|
| Project-owned intent/state contrasts | 12,000 | All facets, action, state, and clean response. |
| Project-owned fantasy scenarios | 4,000 | Game behaviors, tools, state, and realization. |
| Project-owned science-fiction scenarios | 4,000 | Game behaviors, tools, state, and realization. |
| LIGHT/LIGHT WILD | 2,500 | Game-grounded act/dialogue labels after license verification. |
| Taskmaster + SGD | 2,000 | Dialogue acts, slots, correction, and state only. |
| SLURP + MASSIVE | 1,500 | Intent/slot auxiliary heads only. |
| OASST1 | 1,000 | Capped conversational realization. |
| GoEmotions | 1,000 | Affect only. |
| Civil Comments | 1,000 | Mature/toxicity facets only; never desired response. |
| Safety/social repair source | 1,000 | Feedback, repair, de-escalation policy after license verification. |
| **Total** | **30,000** | |

If LIGHT licensing is not clear, replace those 2,500 rows with project-owned
game scenarios. Do not silently substitute a legally ambiguous source.

## Data-generation strategy

For each canonical behavior seed:

1. Author a structured world state, speaker role, goal, slots, correct action,
   state delta, and one or more valid response constraints.
2. Generate 30-50 wording variants, including fragments, corrections, slang,
   swearing, politeness, negation, quotation, and history-dependent references.
3. Generate hard negatives by changing one semantic feature at a time.
4. Normalize to uppercase only after preserving the raw text and offsets.
5. Assign one `semantic_family_id` to the seed and every derivative.
6. Split by family before any expansion.
7. Keep exact held-out benchmark seeds from
   [GAME_DIALOGUE_SCENARIOS.md](GAME_DIALOGUE_SCENARIOS.md) out of all training
   sources and generator prompts.
8. Review a stratified sample and every content-sensitive row before release.

Do not use a Markov chain to create semantic supervision. Surface variation is
useful only after behavior, slots, state, and policy have an authoritative source.

## Manifest requirements

Extend `data/sources.json` so every source records:

```json
{
  "id": "SOURCE_AND_VERSION",
  "upstream_url": "HTTPS://...",
  "revision": "IMMUTABLE_COMMIT_OR_RELEASE",
  "artifact_url": "HTTPS://...",
  "sha256": "...",
  "license_spdx": "CC-BY-4.0",
  "license_file_sha256": "...",
  "attribution": "...",
  "allowed_uses": ["TRAIN_AUXILIARY", "EVALUATE"],
  "prohibited_uses": ["RESPONSE_IMITATION"],
  "content_bands": ["PROFANITY", "FICTIONAL_VIOLENCE"],
  "quota": 1000,
  "group_key": "DIALOGUE_ID",
  "reviewed_on": "2026-08-07"
}
```

Compilation must fail closed when an artifact, license file, or checksum differs
from the manifest.

## Evaluation-only sources and contamination

- Keep one project-owned fantasy test pack and one science-fiction test pack in
  a private or separately tracked benchmark artifact.
- Hash normalized inputs and semantic seed IDs. Fail the build if either occurs
  in training.
- Near-deduplicate with word shingles after exact checks; inspect high-similarity
  cross-source matches.
- Report source-specific and content-band-specific scores. A single macro-F1 can
  hide failure on trade, navigation, threats, or corrections.
- Report raw model, constrained policy, and production response quality
  separately.
- Record every generated row's generator version, prompt hash, reviewer status,
  and parent seed. Generated volume without provenance is not a dataset.

## Import order

1. Fix split leakage and add content-band/schema fields.
2. Generate project-owned fantasy/science-fiction contrasts from the catalog.
3. Add Taskmaster-1, SLURP text, and English MASSIVE with pinned licenses.
4. Add Civil Comments as auxiliary labels with strict quotas.
5. Verify LIGHT's exact dataset terms; import only after that review passes.
6. Evaluate SOTOPIA/ProsocialDialog as schema or policy sources before copying
   their content.
7. Retrain only after the held-out scenario benchmark and raw/constrained metric
   split are implemented.
