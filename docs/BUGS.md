# Fishbrain v9 Audit

Audit date: 2026-08-07

Audited revision: `78f842a` (`main`)

Scope: runtime, training, evaluation, data generation, checkpoints, CLI, and tests

This is a defect and risk list, not a claim that every item must be fixed before
the next experiment. `P0` can invalidate results or corrupt behavior, `P1` is a
major correctness problem, `P2` is a meaningful limitation, and `P3` is cleanup
or future hardening.

## Summary

| Priority | Count | Main themes |
|---|---:|---|
| P0 | 4 | shared tokenizer state, split leakage, misleading evaluation |
| P1 | 12 | unavailable tools, hidden generalization, release automation, path/checkpoint failures |
| P2 | 14 | taxonomy scaling, content policy, parser and training weaknesses |
| P3 | 5 | dead code, stale names, diagnostics, storage efficiency |

The first v10 work should fix B001-B008 before adding a much larger intent
catalog. Otherwise a larger corpus can produce better-looking metrics without a
more reliable model.

## P0: result-invalidating defects

### B001 - Tokenizer vocabulary is process-global

- **Evidence:** `Tokenizer.Configure` stores the active vocabulary in static
  state. Every `Brain` construction or load replaces it.
- **Impact:** loading two checkpoints with different vocabularies changes how
  the first `Brain` tokenizes. Token IDs, unknown-word behavior, and model sizes
  can silently stop matching its weights.
- **Reproduction:** load v4, load v9, then ask v4 for the same reply before and
  after the v9 load.
- **Fix:** make tokenization an immutable instance owned by `Brain`; pass it to
  normalization/encoding methods. Add a two-model interleaving test.

### B002 - Normalized utterances leak across corpus splits

- **Evidence:** the current 10,000-row compiled corpus contains 346 normalized
  inputs in more than one split, covering 1,039 rows. Examples include
  `PLAYER HI`, `PLAYER THANKS`, and `PLAYER THANK YOU`.
- **Impact:** validation and test scores partly measure utterances already seen
  in training under another state or source group.
- **Cause:** splitting protects source group IDs and duplicate `(state, input)`
  pairs, but not normalized input or semantic contrast families globally.
- **Fix:** assign a stable `utterance_family_id`, split by family before
  expansion, and make `audit` reject any normalized input crossing splits.

### B003 - Transcript regressions can use exact training memory

- **Evidence:** transcript cases call the normal `Brain.Reply` path, which
  permits `_trainedExamples` lookup. The checkpoint contains 6,092 remembered
  examples.
- **Impact:** a regression can pass through lookup even when the neural model
  does not generalize.
- **Fix:** expose an evaluation reply option that disables exact memory. Report
  transcript results twice: model-only and production-with-memory.

### B004 - Perception metrics include deterministic correction rules

- **Evidence:** the evaluation prediction path applies `Cognition.Constrain` to
  the neural heads before scoring.
- **Impact:** reported intent, affect, and expectation scores combine model
  quality with hand-written rules. A weak head can appear stronger than it is.
- **Fix:** report raw-head and constrained-production metrics side by side. Gate
  raw generalization and production behavior separately.

## P1: major correctness and release defects

### B005 - Evaluation exits successfully when gates fail

- **Evidence:** evaluation prints `V9_STAGE_GATE` and `RELEASE_GATE`, but the CLI
  returns success unless an exception occurs.
- **Impact:** scripts and CI cannot reliably stop a bad checkpoint.
- **Fix:** return a structured result and a non-zero process exit code for a
  requested failed gate; add `--gate stage|release|none`.

### B006 - Release gate permits unexpected empty replies

- **Evidence:** the release condition checks invalid and overlength outputs but
  omits the unexpected-empty count. The v9 stage gate does check it.
- **Impact:** a checkpoint can satisfy the release gate while returning no text
  where a response is required.
- **Fix:** use one shared gate definition and require zero unexpected empty,
  invalid, and overlength outputs.

### B007 - Default chat checkpoint is absent from a clean clone

- **Evidence:** the CLI and README default to
  `data/models/model-v9-latest.json`; Git tracks only v4 checkpoints and ignores
  v9 artifacts.
- **Impact:** the documented no-argument chat command fails after cloning.
- **Fix:** either publish a versioned release artifact with verified hash and an
  install command, track a compact current checkpoint, or make the CLI explain
  exactly how to train/download it.

### B008 - The current checkpoint cannot call any tool

- **Evidence:** the v9 checkpoint has zero trained tool names, and the current
  corpus generator emits no tool examples.
- **Impact:** the reflected tool framework exists but cannot provide game facts
  in the shipped experiment.
- **Fix:** add schema-validated tool rows for multiple tools, reserve tool names
  independently of response vocabulary, and add end-to-end tool tests.

### B009 - Location inquiry creates a goal it cannot resolve

- **Evidence:** location questions use a deterministic `I DO NOT KNOW WHERE THAT
  IS.` response while the reducer selects `RESOLVE_GAME_FACT`; only a successful
  tool call clears that goal.
- **Impact:** the NPC can retain a stale resolution goal after an ordinary
  location reply.
- **Fix:** route unknown locations to a game-fact tool, or use a separate
  `REQUEST_LOCATION_DETAIL`/`DECLINE_LOCATION` goal with explicit completion.

### B010 - Trade behavior is hard-coded to a non-merchant persona

- **Evidence:** `TRADE_REQUEST` resolves to `I HAVE NO WARES TO SELL.` without
  checking NPC role, inventory, shop state, price, currency, or faction.
- **Impact:** merchants, quest vendors, and hostile traders behave alike.
- **Fix:** make trade a policy decision backed by caller-owned shop state and
  tools. Include accept, reject, barter, quote, sell, buy, and out-of-stock data.

### B011 - `Brain` is not safe for concurrent replies

- **Evidence:** reply generation mutates a shared random generator and scalar
  synchronization state. The tool registry is mutable.
- **Impact:** parallel NPC conversations can race, become nondeterministic, or
  corrupt sampling state.
- **Fix:** document single-thread ownership immediately; then isolate per-call
  RNG/scratch state and freeze or synchronize the tool registry.

### B012 - CLI paths depend on the caller's working directory

- **Evidence:** default model and data paths are relative paths.
- **Impact:** `dotnet run --project E:\Projects\Fishbrain\Fishbrain -- chat`
  from another directory searches for a different `data/models` tree.
- **Fix:** resolve repository defaults from `AppContext.BaseDirectory` plus a
  discovered project root, while preserving explicit paths verbatim.

### B013 - Evaluation samples the beginning of deterministic files

- **Evidence:** realization and generation checks use the first 100 eligible
  records. Compiled records have deterministic ordering.
- **Impact:** metrics can overrepresent early sources, labels, or easy examples.
- **Fix:** use a seeded stratified sample by source, intent, history form, and
  response policy; print the sampled IDs.

### B014 - Exact-memory lookup masks the runtime model's generalization

- **Evidence:** an exact normalized input/state/decision/tone key is checked
  before free realization for ordinary dialogue.
- **Impact:** demos can appear strong for trained phrases while small wording
  changes fail. Runtime quality and realization loss describe different systems.
- **Fix:** expose memory hit/miss telemetry and a CLI switch; evaluate exact,
  paraphrase, and out-of-vocabulary variants as separate buckets.

### B015 - Safe-response replacement makes the trained realizer mostly advisory

- **Evidence:** generated output not exactly present in the project-owned safe
  catalog is replaced with a deterministic catalog response.
- **Impact:** costly word-generation training often does not control visible
  replies; generation metrics do not predict chat quality.
- **Fix:** choose deliberately between retrieval/ranking and generation. If
  generation remains, validate structural constraints and semantic relevance
  without requiring exact catalog membership.

### B016 - Tool output is not authoritative

- **Evidence:** a successful tool result is fed back through text realization.
- **Impact:** names, quantities, and other facts can be altered by generation.
- **Fix:** return tool-owned text or fill a deterministic, typed response
  template. Never regenerate authoritative values token by token.

## P2: product and scaling risks

### B017 - Flat intent enums do not scale to game-wide behavior

- **Evidence:** intent count determines fixed control-token positions and output
  head size. Adding v9 intents required a new incompatible checkpoint.
- **Impact:** hundreds of sparse intents would bloat the head, shift token IDs,
  and require full retraining for every taxonomy edit.
- **Fix:** use compositional labels (`speech_act`, `domain`, `goal`, `target`,
  `policy`) plus a small stable operational intent layer. See
  [INTENT_CATALOG.md](INTENT_CATALOG.md).

### B018 - Current annotation rules are brittle substring heuristics

- **Evidence:** source annotation relies on keyword lists and precedence.
- **Impact:** polysemy, negation, quoted text, and multi-intent turns receive
  confident but wrong labels.
- **Fix:** preserve source-native labels, add explicit reviewed mapping tables,
  support multi-label rows, and quarantine ambiguous examples.

### B019 - Mature game content is filtered as if it were generic assistant data

- **Evidence:** compilation blanket-rejects common profanity and words such as
  `weapon` and `murder`.
- **Impact:** the model cannot learn ordinary combat, crime, intimidation, or
  coarse-language interactions expected in mature games.
- **Fix:** replace the blacklist with versioned content bands: ordinary, mature
  game violence/profanity, safety-sensitive, and hate evaluation-only.

### B020 - Hate and profanity are conflated

- **Evidence:** identity attacks, generic swear words, and descriptions of
  violence are handled by one unsafe-text decision.
- **Impact:** harmless fantasy combat is discarded while the system gains no
  precise policy for targeted hate.
- **Fix:** annotate `profanity`, `threat`, `graphic_violence`, `self_harm`,
  `sexual_violence`, and `identity_attack` independently. Train recognition and
  response policy; do not blindly train the NPC to imitate every input.

### B021 - Role-marker text can be interpreted as dialogue structure

- **Evidence:** current-turn extraction recognizes `PLAYER` and `NPC` after
  sentence punctuation in normalized text.
- **Impact:** a player can say `HELLO. NPC ... PLAYER ...` and alter which text is
  classified or trigger a malformed-history error.
- **Fix:** pass structured turns through the public API. Keep legacy flat text
  parsing behind an explicit compatibility option with escaped role tokens.

### B022 - CLI conversation history grows without a bound

- **Evidence:** every turn is retained and joined on every reply. The model later
  truncates its token context, but the CLI still stores and normalizes all text.
- **Impact:** long sessions waste memory and CPU, and exact-memory keys grow.
- **Fix:** retain only turns that can fit the context window plus state-relevant
  summaries; cap by tokens, not turn count.

### B023 - State enumeration hard-codes some enum sizes

- **Evidence:** state fixture generation derives intent/goal counts dynamically
  but uses numeric moduli for affect, topic, and mood.
- **Impact:** new enum values can be silently absent from generated states and
  tests.
- **Fix:** enumerate actual values with `Enum.GetValues<T>()` everywhere.

### B024 - Global uniqueness repair can make history and state disagree

- **Evidence:** collision resolution changes only the compiled row state.
- **Impact:** a history row can claim a pre-turn state that the preceding
  dialogue would not produce.
- **Fix:** regenerate the complete dialogue/state trace or reject the collision;
  never mutate one half of a paired invariant.

### B025 - Corpus audit misses semantic contamination

- **Evidence:** audit checks exact pair and group leakage but not normalized
  utterance families, contradictory labels, near duplicates, or source overlap.
- **Impact:** near-identical records can cross splits or carry incompatible
  targets.
- **Fix:** add normalized-input leakage, token-shingle similarity, contradiction,
  source-overlap, and vocabulary coverage reports.

### B026 - Best perception checkpoint uses only intent macro-F1

- **Evidence:** best-role selection does not combine affect, expectation,
  action, or stage-gate health.
- **Impact:** the chosen checkpoint can improve intent while regressing another
  required behavior.
- **Fix:** select with a declared composite score and hard minima, or retain a
  Pareto set with a deterministic release choice.

### B027 - Tool selection and arguments are unconstrained generation

- **Evidence:** tool names and arguments are decoded from the response
  vocabulary without a calibrated confidence threshold or typed decoder.
- **Impact:** a syntactically valid but wrong tool or entity can run.
- **Fix:** use separate schema-constrained classification/span heads, confidence
  thresholds, caller authorization, and an explicit clarification fallback.

### B028 - External source score is below the release target

- **Evidence:** v9 external intent macro-F1 is `0.6695`, below the `0.70`
  release threshold.
- **Impact:** the model remains sensitive to synthetic wording and source style.
- **Fix:** repair leakage first, then add licensed game-grounded paraphrases and
  hard contrast sets. Do not tune only against the current external test split.

### B029 - No broad mature-content behavior benchmark exists

- **Evidence:** a few direct insults and unsafe directives are golden cases, but
  there is no stratified benchmark for threats, combat orders, crime, profanity,
  coercion, or quoted hostile speech.
- **Impact:** allowing mature content can cause arbitrary mappings or make the
  NPC echo abuse instead of applying game policy.
- **Fix:** turn the interactions in
  [GAME_DIALOGUE_SCENARIOS.md](GAME_DIALOGUE_SCENARIOS.md) into a held-out,
  versioned benchmark.

### B030 - Test coverage is embedded and mostly example-based

- **Evidence:** self-tests live in executable projects; there is no dedicated
  test project with fuzz, property, concurrency, and corrupt-checkpoint suites.
- **Impact:** parser, normalization, checkpoint, and multi-model defects can
  survive happy-path checks.
- **Fix:** add `Fishbrain.Tests` and `Fishbrain.DataGenerator.Tests`, still with
  no required third-party package if that constraint is important. Add seeded
  property loops and process-level CLI tests.

## P3: hardening and cleanup

### B031 - External normalization failures are swallowed

- **Evidence:** an external-row conversion path catches all normalization
  exceptions and returns `false` without a reason.
- **Impact:** dropped-data regressions are hard to detect or diagnose.
- **Fix:** return a typed rejection reason and count it in the audit report.

### B032 - Checkpoints are large uncompressed JSON without integrity metadata

- **Evidence:** weights and optimizer arrays are serialized as JSON doubles on
  every save. Atomic replacement helps, but there is no stored checksum or
  recovery copy.
- **Impact:** saves are slow and large; one damaged current file blocks resume.
- **Fix:** add a versioned binary payload or compressed JSON, SHA-256 metadata,
  and last-known-good rotation while retaining a readable metadata header.

### B033 - `MarkovChain.cs` appears unused

- **Evidence:** repository search finds no call site outside the file.
- **Impact:** dead experimental code increases the apparent supported surface.
- **Fix:** remove it or add the intended generation path and tests.

### B034 - Self-test temporary names still say v7

- **Evidence:** v9 self-test paths in `Program.cs` use `fishbrain-v7-*` names.
- **Impact:** logs and temporary artifacts are confusing during diagnosis.
- **Fix:** use revision-neutral names or the current checkpoint version.

### B035 - Training saves do not expose enough operational telemetry

- **Evidence:** checkpoints contain training state, but logs do not provide a
  compact machine-readable record of wall time, throughput, vector width,
  memory, sample mix, and gate deltas for every milestone.
- **Impact:** numerical optimizations and regressions are harder to compare
  across machines and runs.
- **Fix:** write one JSONL milestone record with environment, corpus hash,
  timings, metrics, checkpoint hash, and selected role.

## Recommended fix order

1. Isolate tokenizer and inference state (B001, B011).
2. Repair split isolation and evaluation honesty (B002-B006, B013-B014, B025).
3. Make clean-clone execution and gate exit codes reliable (B005-B008, B012).
4. Introduce compositional intent/content schemas before expanding data
   (B017-B020).
5. Add structured turns, typed tool decisions, and dynamic trade/location facts
   (B008-B010, B016, B021, B027).
6. Build the mature game-dialogue benchmark, then retrain (B028-B030).
7. Complete storage, telemetry, diagnostics, and cleanup (B031-B035).

## Audit commands to automate

```powershell
dotnet build Fishbrain.slnx -c Release
dotnet run -c Release --project Fishbrain -- selftest
dotnet run -c Release --project Fishbrain.DataGenerator -- selftest
dotnet run -c Release --project Fishbrain.DataGenerator -- audit
dotnet run -c Release --project Fishbrain -- evaluate `
  data/compiled/test.jsonl data/models/model-v9-latest.json --gate stage
```

The final command shows the proposed `--gate` interface; v9 does not implement it
yet.
