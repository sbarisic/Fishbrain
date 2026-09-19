# Agent inspection

I read all 152 turns in the 38 paired conversations, the 12 development turns,
and nine positive-control turns. This is agent inspection, not human release
approval. The focused candidate remains unpromoted.

## The user's conversation now behaves as intended

| Player | New reply |
|---|---|
| Hello | Hello. How can I help? |
| What? | I was saying hello. What would you like to talk about? |
| Who are you? | Arin |
| Where are we? | YOU ARE AT VILLAGE MARKET. |
| Do you sell anything? | The typed stock list, including all three items. |
| What do you sell? | The typed stock list, including all three items. |
| How much for 1 iron sword? | IRON SWORD COSTS 25 GOLD. |

The development replay also answers the three fresh sword-price formulations
correctly. These are development examples, including wording used in training.
They are not unseen evidence. The raw outputs are in `development.jsonl`.

The packaged smoke test also tries a shorter history: “Hello” → “What?” → the
sword-price question. In that ordering, the price question gets “Hello. How can
I help?” instead. This is recorded in `package-smoke.jsonl`. The package loads and
produces bounded replies, but this additional history variant still fails quality.
The successful full transcript must not be generalized to every ordering.

## Improvements beyond that transcript

Both held-out price formulations work for all three items. Direct location
lookups and world-fact questions improve. Some post-shopping identity and topic
changes also improve. Primary held-out tool/completion accuracy rises from 9/36
to 19/36, with entity substitutions grouped for the confidence intervals.

Known-quantity positive controls execute purchases of two and sales of one for
every item. Balances finish at 62 gold for the sword sequence, 88 for the potion
sequence and 95 for rope. This demonstrates actual authorized mutations, not
success through abstaining on every transaction. Those controls use trained
wording and quantities. Potion replies also expose active-exchange overflow:
the transaction succeeds, followed by “Please continue in another turn.”

## Remaining failures

- Every held-out purchase of three generates quantity two. The host refuses
  the mismatched arguments. Several sell requests also produce a buy call.
- New name, role and origin questions can still trigger unrelated social replies.
- A current-location question can select the location of HELL instead of the
  current player location. The returned tool fact is genuine but answers the
  wrong question; preserving typed fields does not establish relevance.
- Both held-out social-repair questions select unrelated tools. Fixing the exact
  “Hello” → “What?” sequence does not establish general conversational repair.
- Repeated information often changes entity: a sword price becomes a potion
  price, and several location/fact repetitions return the market location.

## Why the reference failures need a packing fix

The last class of errors exposed a concrete defect in supervision. After native
packing, some repeated-information prompts no longer contain the earlier tool
exchange. The model receives identical tokens with mutually inconsistent expected
tool arguments. The small-fixture fitting pass did not test this contradiction.

For one sword-price exchange, the player question, assistant tool request, tool
result and displayed answer consume 17 + 61 + 102 + 22 BPE tokens before role
boundaries. The next question adds 11. Combined with persona/tool declarations
and the reserved 256-token output budget, the packer discards the whole earlier
exchange. A larger number of layers cannot reconstruct an absent item reliably.

The new audit identifies four contradictory training-prompt groups (40 rows),
plus author-annotated references whose source exchange was discarded. A separate
cleaned corpus retains 1,091 targets and full item/action coverage. Four conflicting
memory rows in the prior replay pool are also excluded from a separate clean file.
Neither cleaned file has trained another model. The original challenge and its
expected answers remain intact: end-to-end reference failures still count.

The next implementation work should compact the tool representation and verify
that required source turns survive packing. After that, rebuild and audit the
corpus before another bounded check. This experiment does not justify a full
training run yet, and does not isolate all remaining model-capacity limitations.
