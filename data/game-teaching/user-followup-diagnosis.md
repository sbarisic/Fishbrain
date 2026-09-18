# Exact interactive replay: 18 September 2026

The nine inputs in `user-followup-input.txt` reproduced every reply in the user's
transcript exactly before the runtime fix. The command used the ordinary `chat`
path, including its one-based utterance sequence and turn-number generation seed.
The conversation ID is newly generated, so idempotency keys differ. This is a
development regression, not a new held-out quality result.

## Findings

| Input | Observed internal decision |
|---|---|
| `What can you do?` | Knowledge target OCCUPATION, not CAPABILITIES. |
| `Do you have anything for sale?` | INFORM frame without a tool; ACKNOWLEDGE plus EXECUTE_TOOL with no frame reference. Invalid plan reached free generation. |
| `What do you sell?` | Correct LIST_WARES frame, confidence 0.99963, vetoed by disabled calibration threshold 1.01. |
| `where are we?` | Correct GET_CURRENT_LOCATION frame, confidence 0.99860, vetoed by disabled calibration threshold 1.01. |
| `where are you?` | ASK frame without a tool or knowledge target; generated unrelated wellbeing reply. |
| `kill yourself` | INFORM frame and neutral affect; generated an irrelevant tiredness response. |
| `lmao` | LOOKUP_LOCATION frame, confidence 0.47923, then missing-place clarification. |

The global policy head and ordered plan disagree on the wares question. This is
observable evidence for constraining their joint output, not evidence that adding
more layers will fix the problem. Calibration disabled LIST_WARES after only
146/306 candidate decisions were correct and GET_CURRENT_LOCATION after 100/344.
These are counts across calibration candidates, not precision at an enabled
threshold. No threshold was relaxed.

The narrow teaching catalogue has no dedicated capability-question or hostile-
speech group. Its 17 social response targets do not provide broad conversational
coverage. More variants of the same small catalogue would not establish robust
language understanding.

## Runtime fix and verification

An EXECUTE_TOOL act must reference an existing frame with a tool. Otherwise the
whole tool plan receives `UNBOUND_TOOL_PLAN`, performs no invocation and uses the
existing clarification response. This also prevents executing a valid prefix of
a partly unbound tool plan. It is a structural validation rule, independent of
input wording, domain, confidence and model weights.

After the fix, the malformed wares plan produces clarification instead of
`THAT FOR TELLING ME.` The changed history also makes the last reply a generic
clarification. The other semantic failures remain. No learning improvement is
claimed and no training or promotion was performed.

The optional third `chat` argument now writes a new JSONL trace. It records each
request snapshot, generated reply, state, semantic frames, ordered acts, vetoes and
demo-world changes. It flushes each row and refuses to overwrite an existing file.
The two-argument chat command remains supported.

Verified with:

- Release CLI and test builds, zero warnings/errors.
- All 37 runtime unit suites, including null/missing/out-of-range tool references,
  valid tool/social plans and partially invalid compound plans. Shipped artifact
  remains excluded from this unit command and is still not promoted.
- Nine-turn native replay: all trace seeds/history lengths are correct; turn four
  has `UNBOUND_TOOL_PLAN`, no invocation and CLARIFICATION_TEMPLATE response.
- Existing trace files are rejected and preserved byte-for-byte.

Evidence: [before replies](user-followup-chat.txt),
[before trace](user-followup-trace.jsonl),
[after replies](user-followup-fixed-chat.txt),
[after trace](user-followup-fixed-trace.jsonl),
[unit results](user-followup-unit-tests.log).

Earlier game-teaching results and artifact fingerprints remain historical snapshots
of that experiment. This follow-up changes the runtime, not the retained weights;
full quality/resource gates have not been rerun for this focused validation change.
