# Context and transaction repair

This is a separate, bounded fine-tuning experiment. Old models, datasets, and
runtime packages remain preserved. No model is automatically promoted.

## What changed

- Tool declarations use lowercase identifiers in the input. Generated calls
  still use the registered uppercase schemas. The identifiers are equivalent.
- Tool history uses JSON arrays without redundant protocol envelopes. Tool
  values, exact quotations, sequence IDs, roles, and complete exchanges remain.
- The packer still reserves 256 output tokens and rejects oversized required
  input. It drops whole older exchanges.
- Authored targets declare requiredSteps, referring to zero-based player turns
  in that episode. Compilation resolves these into original sequence IDs. A
  target is excluded if packing loses a required player turn. Legacy targets
  without annotations conservatively require all preceding player turns.
- The artifact requires COMPACT_TOOL_HISTORY_V2. Old weights fail explicitly
  in the new runtime; their old packages still work.
- “I would like to buy…” is affirmative unless another veto term applies.
  Negation, quotation, cancellation, and conditions still veto execution.
- Demo authorization accepts a proposed purchase phrased as “sell me 10 rope”.
  It rejects treating that phrase as a sale from the player. These checks
  validate the model's proposal; they never select a tool.
- Invalid or nonpositive quantities fail authorization without throwing.

## Data

plans.jsonl contains 211 newly composed episodes in 73 scenario families,
including 30 separately written four-turn social conversations. 150 episodes
are explicitly counted as augmentation, not new conversational diversity.
Coverage includes clarification, malformed prior replies, identity after
shopping, player names, money versus inventory, and quantities 1–5, 7, and 10.
Tool execution compiles complete typed trajectories; values are not invented
by a text generator.

The corpus reuses only training splits from the preserved causal datasets.
It has 4,840 packed targets. Native packing excludes 593 targets whose required
history does not fit and 21 oversized responses. No labels are silently
reinterpreted. The actual packed inputs contain no conflicting tool labels or
mixed tool/text targets.

challenge.json freezes 28 multi-turn conversations before training.
development.json records the user's latest transcript as development evidence.
The full old 120-case suite and the 17 preserved cases remain separate checks.
See audit.json, isolation-audit.json, and freeze.json for counts and hashes.

## Reproduce and inspect

From the repository root in PowerShell:

~~~powershell
dotnet build Fishbrain -c Release -o data/training/causal-repair-v2/tools
dotnet run -c Release --project Fishbrain.CausalTests
data/training/torch-env/Scripts/python.exe -m unittest discover -s scripts/causal -p test_context.py
data/training/torch-env/Scripts/python.exe scripts/causal/context_restore.py data/training/causal-repair-v2-restored
~~~

Restore requires the preserved source episode files in both older run directories.
It writes to a new directory and checks the exact frozen corpus hash.
Models and datasets remain outside Git.

For a newly prepared experiment, the order is data preparation, isolation audit,
numerical/overfit preflight, bounded training, and paired evaluation. These scripts
reject overwriting completed evidence:

~~~powershell
data/training/torch-env/Scripts/python.exe scripts/causal/context_data.py
data/training/torch-env/Scripts/python.exe scripts/causal/context_audit.py
data/training/torch-env/Scripts/python.exe scripts/causal/context_check.py
data/training/torch-env/Scripts/python.exe scripts/causal/context_train.py
data/training/torch-env/Scripts/python.exe scripts/causal/context_evaluate.py --baseline
data/training/torch-env/Scripts/python.exe scripts/causal/context_evaluate.py
~~~

Training initializes from a copy of the first repair candidate, uses seed 42,
FP32 AdamW, effective batch 32 and microbatch four, and stops at 1,200 updates
or 900 seconds. The overfit preflight is separate and its weights are discarded.
Checkpoints retain weights, optimizer, sampler, RNG, elapsed budget, and input
fingerprints. The --resume option rejects a different fingerprint or completed run.
There is no full retraining or automatic extension.

The sampler assigns 50% to tool calls, 15% to memory, 20% to authored
social/safety/repair text, 5% to other continuations, and 10% to public dialogue.
Training loss and teacher-forced validation do not establish conversational quality.
Actual paired replies and remaining quality failures belong in RESULTS.md.

The separate followup-plans.jsonl file contains 16 six-turn episodes authored
after inspecting package failures. They teach name writes at later source turns,
player/NPC name contrasts, corrections, and switching from wares to prices.
Its 166 accepted targets are prepared only. They were not sampled in this run;
seven targets with lost required history were excluded. The frozen training
corpus and challenge suite are unchanged.

After training, compilation was also hardened so an omitted requiredSteps field
conservatively requires all earlier player turns. An explicit empty array still
means the author marked the response as context-independent. This experiment's
new plans already supplied explicit arrays, so its tokens and model are unchanged.
bound-code/Program.cs preserves the exact compiler source hashed by the completed
run. The final compiler change affects only unannotated future source plans.
The bound authorization source is also preserved there; the final public helper
rejects signed or whitespace-padded quantities before constructing a regex.
Generated protocol integers were already canonical, so this hardening does not
change the evaluated calls or the model input.

Old packages remain usable for comparison. Do not pair their weights with the
newly built runtime.
