# Fishbrain v11 full-source audit

Audit date: 2026-08-08

Scope: every tracked C# project, runtime and tool contracts, training and evaluation,
corpus generation, checkpoints, command-line workflows, tests, scripts, documentation,
and the checked-in v11 model.

This file records confirmed defects and architectural limitations found during the
audit. `FIXED` means the source correction and a focused regression test or corpus
audit are present in this revision. `PARTIAL` means the risk was reduced but a stated
acceptance target is still missed. `REJECTED` records an experiment that was measured,
discarded, and did not replace the shipped source path or model. `OPEN` identifies a
known product or engineering limit, not a hidden release claim.

## Correctness and safety defects

| ID | Priority | Status | Finding and resolution |
|---|---|---|---|
| F001 | P0 | FIXED | A learned `EXECUTE_TOOL` or persona target could override a deterministic refusal. Hostile name queries disclosed persona data and hostile purchases mutated the world. Refusal, no-response, and defer policies now veto tool selection; persona rendering is eligible only for answer/acknowledge policies. |
| F002 | P0 | FIXED | Tool choice trusted the learned tool label without making deterministic schema/slot validation authoritative. Learned tool labels are now diagnostic only; only the validated selector can create an invocation. |
| F003 | P0 | FIXED | A clarification was written to state but a fragment such as `TWO` could not complete `BUY ROPE`. Missing slot names and references are persisted, fragment slots are recovered, and the pending operation resumes. |
| F004 | P1 | FIXED | Secondary actions from multi-intent input were queued but never consumed and were overwritten by unrelated turns. Explicit continuation now executes the first queued action and preserves the remainder. |
| F005 | P0 | FIXED | Keyword checks used substring matching. `FIREWOOD`, `FIREWALL`, `KILLER FEATURE`, `PASSAGE COSTS`, and `KILLING TIME` produced combat/crime false positives. Matching now observes lexical boundaries and has regression data/tests. |
| F006 | P0 | FIXED | Quoted insults in apologies were treated as direct attacks. Reviewed apology forms now veto the hostility interpretation, and corpus examples supervise the distinction. |
| F007 | P0 | FIXED | Self-harm text was mapped to hostile refusal while the intended data policy was supportive deferral. Self-harm now has a distinct support candidate, distressed/cautious perception, and a defer boundary. Crime, threats, identity attacks, graphic violence, sexual violence, and sexual content have explicit separate policies. |
| F008 | P0 | FIXED | The demo world's `ConcurrentDictionary.GetOrAdd` factory could perform the same mutation more than once under contention. Execution is protected by an `ExecutionAndPublication` lazy value and a 32-way regression test. |
| F009 | P0 | FIXED | Idempotency caching used only the caller key, so two different tools could alias. The cache key now includes the tool name. |
| F010 | P1 | FIXED | Buy/sell overflow checks could occur after part of the state was changed. Every derived balance, stock, and inventory value is checked before the atomic commit. |
| F011 | P1 | FIXED | The documented thread-safe world exposed an unsynchronized location property. Location reads/writes now use the same state lock as trade data. |
| F012 | P1 | FIXED | Tool results accepted null fields, undeclared fields, invalid values, and inconsistent success/error metadata. Schema and result validation is now deep; failed tools without an eligible error template return a safe failure message. |
| F013 | P1 | FIXED | State validation was shallow. Invalid nested references, clarifications, transactions, pending actions, duplicate labels, and identifiers are now rejected at the boundary. |
| F014 | P1 | FIXED | Request validation allowed invalid enum values, null turns/state, control characters in idempotency identifiers, and overlength turns. These inputs now fail before inference; long histories remain accepted and are packed to complete recent turns. |
| F015 | P1 | FIXED | `LastTool` changed even when no tool executed, and clarification state used a generic placeholder instead of the actual missing fields. Reduction now records only executed tools and exact missing slots. |
| F016 | P2 | FIXED | Turn termination could create doubled punctuation after trimming a quote or delimiter. It now rechecks terminal punctuation after trimming. |

## Model, training, and evaluation defects

| ID | Priority | Status | Finding and resolution |
|---|---|---|---|
| F017 | P0 | PARTIAL | The checked-in 80K model failed the release gate. The 210K replacement passes every exact validation minimum and improves the independent test split, but the strict test release gate remains closed on domain F1 0.8324, slot F1 0.8296, and tool accuracy 0.9489. No threshold was weakened. |
| F018 | P1 | FIXED | Only 512 lexical hash buckets served all structured heads, creating avoidable collisions. The v11 schema now uses 4,096 lexical buckets; this deliberately requires a new checkpoint. |
| F019 | P1 | FIXED | Unknown structured tools/candidates silently became class zero, and unknown or duplicate supervised heads were accepted. Training-data loading now rejects unknown targets and malformed supervision while permitting explicit language-only rows. |
| F020 | P1 | FIXED | Structured rows accepted null collections, invalid enum values, malformed slot spans, and non-finite confidence. Both compilation and training load validate the complete schema. |
| F021 | P1 | FIXED | A nonstandard planned step count could finish without running full validation or producing a best-production checkpoint. The configured final step is always a full stage. |
| F022 | P1 | FIXED | The held-out benchmark labeled `HELLO?` as profanity, causing a guaranteed false failure. Both v10 and v11 benchmark sources now label it ordinary. |
| F023 | P1 | FIXED | Corpus turns and serialized contextual input could disagree, especially on final punctuation. External rows now derive input from the same structured turns; audit rejects disagreement. |
| F024 | P1 | FIXED | Corpus audit did not prove that compiled provenance matched the pinned source manifest. It now checks every source, revision, license, attribution, path, URL, and checksum. |
| F025 | P1 | FIXED | Source paths could escape the declared raw directory during fetch/verification. Paths are resolved and rejected if their relative form leaves the raw root. |
| F026 | P2 | FIXED | State fixtures used `Math.Abs(int.MinValue)` and hard-coded enum cardinalities. Unsigned decomposition and `Enum.GetValues<T>()` cover every current enum safely. |
| F027 | P2 | FIXED | Response catalog startup required exactly 200 plans, making a valid catalog extension fail. The contract is now a minimum, and the self-harm support plan is the 201st entry. |
| F028 | P2 | FIXED | Corpus/checkpoint hashing allocated whole files, adding large temporary memory pressure. Hashing now streams fixed-size buffers. |
| F029 | P2 | FIXED | Telemetry paths depended on build-output depth and could write outside the repository from an isolated build. The repository is now discovered from the checkpoint, working directory, or executable ancestry. |

## Checkpoint and operational defects

| ID | Priority | Status | Finding and resolution |
|---|---|---|---|
| F030 | P0 | FIXED | Inference checkpoints checked bytes but did not validate format text, progress, corpus hash, vocabulary ordering, complete candidate/tool schemas, trained tools, finite weights, or bounded parameter counts. All are validated before model construction. |
| F031 | P0 | FIXED | Training checkpoints could restore non-finite model/optimizer values and invalid progress metadata. Full numerical, schema, vocabulary, corpus, catalog, and progress checks now precede restoration. |
| F032 | P1 | FIXED | Confidence calibration accepted `NaN` and unexpected schema keys. Calibration dictionaries must exactly match v11 and contain finite bounded values. |
| F033 | P1 | FIXED | Unbounded configuration values could request extreme allocations, and `NaN` optimizer settings bypassed range comparisons. Configuration dimensions, steps, and all floating-point settings are bounded and finite. |
| F034 | P2 | FIXED | CLI history grew forever even though only a bounded context is usable. The interactive client retains the latest 64 alternating turns; durable context remains in `NpcDialogueState`. |
| F035 | P3 | FIXED | The unused second-order `MarkovChain` experiment implied a supported generation path that had no caller. It has been removed. |
| F036 | P2 | FIXED | Runtime tests omitted policy precedence, clarification/action continuation, lexical hard negatives, and concurrent world idempotency. Focused tests now cover each invariant. Generator tests now cover v11 state bounds and CLI validation. |
| F037 | P0 | FIXED | Training called a checkpoint production-eligible using only the looser stage gate, so it could export a model that the release evaluator rejected. Best-production selection and release evaluation now share the complete neural-quality threshold set; runtime fidelity and benchmark checks remain additional release requirements. |
| F038 | P1 | FIXED | A stage measured structured metrics before applying its newly computed calibration, then saved and exported the calibrated state. Calibration now updates multi-label thresholds first, recomputes predictions for confidence calibration, and runs stage evaluation last so checkpoint selection measures the exact saved model. |
| F039 | P2 | FIXED | Full validation milestones were hard-coded only through 80K even though completed curricula can be extended beyond that point. Every 20,000-step boundary and the configured final step now receive full validation. |
| F040 | P1 | FIXED | Extending a completed curriculum recomputed schedule progress against the larger endpoint and raised the learning rate, causing a visible quality regression after resume. Learning rates now decay monotonically from the absolute completed-step count, independent of later plan extensions. |
| F041 | P1 | FIXED | Several malformed training-checkpoint fields could still escape as null-reference errors or carry impossible progress: a null integrity hash, null vocabulary/schema values, out-of-range best scores, future sampler positions, invalid exact examples, and negative structured updates. Deep validation now rejects them as checkpoint-data errors. |
| F042 | P0 | FIXED | Reusing one idempotency key for the same tool with different arguments returned the first successful result as though it belonged to the second payload. Demo-world executions now bind each key to an argument fingerprint, reject conflicts explicitly, and never perform the second mutation. |
| F043 | P1 | FIXED | `IGameTool.Schema` was validated during registry construction but read again during execution; a custom tool could replace or mutate its lists after validation. Registration now deep-copies the schema and executes through that immutable validated snapshot. |
| F044 | P2 | FIXED | Response diversity counted duplicate empty strings for every no-response plan, making the 5,000-surface claim misleading, and startup did not deeply validate plan IDs or metadata. Each no-response plan now stores one intentional empty surface; the catalog requires valid unique plans and an honest floor of 4,400 distinct visible variations. |
| F045 | P0 | FIXED | Structured and ranking examples were selected with the global step even though those phases occupy only seven and two positions in each ten-step cycle. Depending on family-count factors, whole residue classes could remain unvisited. Phase-local ordinals now traverse every family consecutively and deterministically. |
| F046 | P3 | FIXED | Two pre-existing whitespace violations in `PackedTrainer.cs` prevented the solution-wide formatting verifier from passing. The file is normalized without behavioral changes. |
| F047 | P1 | FIXED | Full corrected-sampler validation showed non-`NONE` tool supervision and BIO slots still learning too slowly under the shared late-stage rate: tool accuracy was 0.8711 and slot F1 0.8309 at 100K. Explicit tool targets and both slot passes now use bounded auxiliary weights instead of the undifferentiated shared rate. |
| F048 | P1 | FIXED | A 3x tool fine-tuning experiment improved 120K tool accuracy to 0.9365 and slot F1 to 0.8591, but mutating precision slipped to 0.9882 while speech, domain, and goal remained low. The continuation uses a balanced 2x tool weight, a 0.20 slot scale, and modest 2.5x/4x/3x positive weights for speech/domain/goal rather than weakening any gate. |
| F049 | P1 | FIXED | Continued shared-encoder updates moved the contextual feature space after the small structured heads had nearly converged: response top-1 fell from 0.8873 at 120K to 0.7931 at 160K while several structured heads remained just below release minima. A bounded late head-polish phase freezes the shared encoder and uses consecutive phase-local structured/ranking samples so the production heads can converge against stable features. |
| F050 | P1 | FIXED | Tool recall and mutation precision pulled in opposite directions under a single target weight. Head polishing now weights explicit tools and `NONE` targets separately, restores the validated slot scale, and gives policy and response-candidate targets bounded auxiliary weight without weakening any release threshold. |
| F051 | P1 | FIXED | Multi-label calibration preferred 0.90 precision and then maximum recall instead of maximizing F1. At 170K this created 554 false `VehicleTravel` domain predictions and missed every rare `Magic` example. Calibration now selects the best per-label F1 over thresholds from 0.05 through 0.95 with deterministic precision, recall, and threshold tie-breakers. |
| F052 | P1 | FIXED | BIO slot supervision used a 4x positive weight that overpredicted rare `Other` and `Time` spans during head polishing. Diagnostics now report exact per-label TP/FP/FN values, and the bounded positive weight is 3x to recover precision without discarding rare labels. |
| F053 | P1 | FIXED | Uniform head-polish sampling left rare `Magic` and `VehicleTravel` domains undertrained and explicit tools collapsed toward `NONE`. The final phase derives bounded positive weights from training support and deterministically mixes rare-domain, explicit-tool, hard no-tool, and general semantic families; response-candidate supervision receives a small bounded increase. |
| F054 | P1 | FIXED | Focused rare-domain/tool examples initially updated every supervised head, skewing the response candidate distribution toward `ACKNOWLEDGE`, while one shared tool weight traded recall for unsafe mutation precision. Focus examples now update only their intended head, response supervision uses an independent general-family stream, and `NONE`, mutating, and read-only tool targets have separate bounded weights. |
| F055 | P1 | FIXED | At 200K every release head passed except response top-1 (0.8456 versus 0.85), while domain, slot, tool, and mutating precision had narrow passing margins. A final response-only phase freezes the encoder and all passing heads, then trains only balanced response-candidate and ranking updates so finishing one head cannot regress the others. |
| F056 | P1 | FIXED | Benchmark and default-model discovery assumed a fixed number of parent directories above `AppContext.BaseDirectory`; isolated output builds resolved to `E:\Projects\data` instead of the repository. Repository files are now located by walking ancestors of both the working directory and application directory, with an isolated-build regression test. |
| F057 | P1 | REJECTED | A post-210K external-slot/focused-domain continuation reduced validation slot F1 to 0.7945. It was rejected and did not replace the 210K artifact. |
| F058 | P1 | REJECTED | A current-turn-only lexical representation for domain/tool heads improved some contextual errors but destabilized validation and mutating-tool precision. The source and artifact retain the validated full-context representation. |
| F059 | P2 | REJECTED | Experimental suffix, affix, length, and numeric slot-shape features did not close the independent slot gap without validation regression. They are not shipped. |
| F060 | P1 | REJECTED | A high-rate head-only adaptation through 260K recovered tool accuracy but left domain and slot below their prior values and mutating precision below its gate. The experiment was discarded. |
| F061 | P1 | FIXED | Short teaching/self-test curricula initialized focus families used only after 160K and failed when their tiny fixture lacked rare domains. Focus sets are now required only if the requested run can enter head polishing; the eight built-in tests pass. |
| F062 | P0 | FIXED | A stale learned knowledge target could render `MY NAME IS ARIN.` for unrelated turns such as `GOODBYE`, an apology, or `BALANCE`. Authoritative persona templates now require an explicit current-turn or resolved state reference, and a checked-in-model regression exercises the real artifact. |
| F063 | P1 | FIXED | The short command `BALANCE` did not enter the authoritative `GET_BALANCE` path. It is now explicit validated evidence and returns the demo world's current amount. |
| F064 | P1 | FIXED | Low-confidence fallback could turn an explicit apology or informative hard-negative statement into a clarification. Explicit apologies now enforce acknowledgment, validated statements retain acknowledgment, current domain evidence takes precedence over stale context for response selection, and self-harm support removes the spurious combat domain. |
| F065 | P2 | FIXED | A contextual `WHAT SHOULD WE DO?` retained the active domain but returned a non-actionable generic sentence. The bounded runtime now returns safe domain-aware first-step guidance while richer situation summarization remains A008. |
| F066 | P2 | FIXED | Loading an archived v10 binary checkpoint fell through to the JSON parser and reported a misleading `JsonException`. Fishbrain binary prefixes are now recognized before JSON parsing and return the documented compatibility error. |
| F067 | P0 | FIXED | After a merchant transaction, learned `ItemsInventory` and request/order labels leaked into unrelated later turns. Current-turn item evidence now vetoes that stale domain and repairs speech acts only when that specific stale-context condition occurs. The complete reported stateful transcript is a checked-in-model regression. |
| F068 | P1 | FIXED | `WHAT DO YOU HAVE FOR SALE?` did not match the authoritative wares route. Common sale phrasing now selects `LIST_WARES`. |
| F069 | P1 | FIXED | `TELL ME ABOUT IRON SWORD` was treated as an open-world fact instead of a known merchant item. Known item descriptions now use the authoritative price lookup and cannot fall through to `LOOKUP_WORLD_FACT`. |
| F070 | P1 | FIXED | `FOLLOW ME` inherited item language and implied action even though the registry had no movement tool. Unsupported follow/stop/stay/wait commands now defer with an explicit capability response and never create an invocation. |
| F071 | P1 | FIXED | The direct insult `FUCK YOU` did not establish the hostile social boundary. It is now recognized as direct hostility, refuses, and cannot inherit the prior inventory response. |
| F072 | P1 | FIXED | An unscoped world-fact request and the incomplete question `WHAT?` produced unrelated answers. They now ask for the missing subject or clarification. |
| F073 | P1 | FIXED | `WHERE IS ZAGREB AND THE INN?` produced one fabricated compound place argument. A compound value for a one-place tool is now ambiguous and returns `PLEASE NAME ONE TARGET.` without invoking the tool. |
| F074 | P1 | FIXED | A poison report received an unrelated inventory response. Poison reports now produce a distressed, cautious health/survival response. This is bounded dialogue guidance, not medical diagnosis. |
| F075 | P0 | FIXED | An identity-exclusive statement was neutrally acknowledged. Reviewed identity-group exclusion patterns now carry `IdentityAttack` and the hostile refusal policy. |
| F076 | P2 | FIXED | A question about the runtime's `ITEMS INVENTORY MESSAGE` label was routed as a world fact. Classification questions now use the meta-system domain and explain that the label described the previous topic. |

## Open architectural and product limits

| ID | Priority | Status | Limitation and recommended direction |
|---|---|---|---|
| A001 | P1 | OPEN | Training checkpoints are roughly half a gigabyte of JSON and fresh training held about 14 GB of private memory in this audit. Move optimizer/model tensors to a streamed binary training format and profile retained corpus objects. |
| A002 | P1 | OPEN | Safety and domain constraints still use reviewed phrase rules around a learned classifier. They prevent high-cost false actions but cannot cover every paraphrase. Expand adversarial held-out data and keep caller policy above model output. |
| A003 | P1 | OPEN | Tool routing is intentionally deterministic and demo-specific. Production games need caller-supplied routing/authorization policy, richer entity resolution, and tool-specific clarification contracts. |
| A004 | P2 | OPEN | Ranked responses are safe but often mechanical because 4,400-plus distinct visible variations are programmatically expanded. Add authored dialogue packs and human preference review; keep experimental free generation opt-in. |
| A005 | P2 | OPEN | The corpus compiler uses static mutable compilation state. The CLI is single-shot, but an embeddable/concurrent compiler should move held-out and external-input sets into a compilation context object. |
| A006 | P2 | OPEN | External normalization rejection still reports only success/failure at several source adapters. Add typed rejection reasons and per-source rejection counts to the audit output. |
| A007 | P3 | OPEN | `V10CorpusPipeline` is the v11 compiler and `Brain.cs` retains a disabled pre-v11 training block. Rename/split by responsibility and remove archived code after a separate compatibility-history decision. |
| A008 | P2 | OPEN | Follow-up reasoning is structured but shallow. `WHAT SHOULD WE DO?` now receives safe domain-aware first-step guidance, but the runtime cannot reconstruct detailed entities, hazards, dependencies, or objectives from a summarized situation. Add an explicit caller-owned situation/task state rather than relying on lexical turn history. |
| A009 | P2 | OPEN | This compact model is not a general-purpose language model. It supports bounded game-dialogue schemas, authored response plans, and approved tools; unsupported knowledge must continue to clarify or defer. |
| A010 | P2 | OPEN | `Brain.Tools` exposes the mutable pre-v11 reflection registry even though the public structured reply path uses immutable `GameToolRegistry`. Keep it only for checkpoint-era compatibility, then remove it at the next deliberate API break. |
| A011 | P2 | OPEN | The final 210K artifact measured 2.7463/4.1461 ms median/p95. Its p95 is within 4x the historical v10 baseline, but median is 4.0819x. Build a controlled dual-version harness and profile the larger vocabulary/structured path before claiming the latency gate. |

## Verification contract

The audited revision is verified with these commands. The final command intentionally
returns exit code 2 while the three independent neural misses remain:

```powershell
dotnet build Fishbrain.slnx -c Release --artifacts-path data/logs/final-solution-artifacts -p:UseAppHost=false
dotnet run -c Release --project Fishbrain -- selftest
dotnet run -c Release --project Fishbrain.Tests
dotnet run -c Release --project Fishbrain.DataGenerator.Tests
dotnet run -c Release --project Fishbrain.DataGenerator -- audit --input data/compiled-v11 --raw data/raw --manifest data/sources.json
dotnet run -c Release --project Fishbrain -- evaluate data/compiled-v11/test.jsonl data/models/model-v11-latest.fbm --gate release
```

The audit intentionally keeps the partial, rejected, and open items visible. They are
accepted boundaries for this revision; they must not be described as implemented
capabilities or as a passing strict release.
