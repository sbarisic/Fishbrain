# Agent inspection of the causal pilot

**The candidate is not ready for game conversations.** I inspected all 50 paired
conversation exports and all 23 development turns. This is agent inspection,
not either of the two independent human release reviews. The accompanying JSON
binds the inspected sample and records the final runtime comparison.

## What improved

Some direct game requests now work, including stock lists, rope prices, balance,
and purchases or sales with familiar wording. In the development sequence:

| Player | Candidate |
|---|---|
| What does a rope cost? | ROPE COSTS 3 GOLD. |
| Buy 2 rope please. | YOU BOUGHT 2 ROPE FOR 6 GOLD. YOUR BALANCE IS 94 GOLD. |
| What should I call you? | Arin |

Both job-question variants return the occupation in that development conversation.
These are known examples, not evidence of unseen generalization. The held-out
shared game-tool comparison also improves, but remains far below the release gate.

## Failures visible in actual dialogue

1. **History changes the wrong part of the answer.** The challenge's earlier
   journey conversation frequently produces repeated journey questions even after
   the player switches to identity, inventory, or location. Removing irrelevant
   history improves some tool queries. Agreement between two wrong replies is not
   a successful context test.
2. **Related persona fields are confused.** After shopping, “What is your name?”
   returns “road warden” in the development transcript. “where are you?” returns
   the persona's home, “the old mill,” rather than the current world location.
3. **Basic prose is often malformed or irrelevant.** The development greeting
   “hi” produces “hilow.” A later “how are you?” produces “there's job to be more
   concise.” The challenge contains repeated and unfinished questions. Schema-valid
   JSON does not imply a relevant or grammatical response.
4. **Memory corrections do not work reliably.** In `causal-heldout-118`, the player
   changes their companion from Edda to Luka. The candidate repeats Edda and later
   claims the player said it. In `causal-heldout-110`, both preference writes fail;
   a recall question produces the capability list. Other writes fail exact source
   or argument validation. Failed writes are not counted as successful memory.
5. **Legitimate transactions are sometimes refused.** “Purchase 1 rope now.” is
   refused in `causal-heldout-082`; “I want to sell 1 rope.” succeeds in case 086.
   The demo's conservative authorization contributes to these refusals. Keeping
   zero mutations by refusing valid requests is not adequate task performance.
6. **Some memory exchanges exceed the context budget.** Detailed declarations,
   quoted evidence, and tool-result records compete for the 1,024-token context.
   The runtime reports that the player must continue in another turn. This is
   an observable capability limit, even when a write already succeeded.

Typed rendering preserves the values returned by tools. The observed zero
unintended world mutations is useful evidence for the validation boundary; it does
not prove the model understands every hard negative. Free prose can still invent
reported memories or imply unsupported facts. The lexical claim flags undercount
those failures and must not be presented as a factuality guarantee.

## Interpretation and next decision

The implementation and numerical checks pass; conversation quality does not.
Specialization training loss approaches zero while validation loss remains much
higher. Together with repeated wording and weak history handling, this suggests
overfitting and inadequate task coverage. It does not isolate model size as the
cause. A larger network or a longer repeat of this corpus has not been justified.
The 4,000 specialization updates draw 128,000 rows with replacement from 2,965
packed training targets, with unequal pool weights. That is substantial reuse,
not 128,000 independent conversations.

A separately approved follow-up should first improve independent episode variety,
topic changes around tools, paraphrases, and multi-record memory corrections.
It should measure how much context tool declarations/results consume and test a
more compact representation. Any retraining should use a newly frozen challenge:
the current failures are now development evidence. No extra training, threshold
reduction, or automatic promotion was performed for this delivery.
