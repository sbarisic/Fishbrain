# Agent conversation inspection

Inspected all 84 focused turns, the first 22 frozen conversations, the complete
12-turn user transcript, and the separate 10-turn package smoke. The paired
export contains 51 conversations selected by source order, not by success.

Observed improvements:

- All twelve focused buy/sell operations have the correct item and quantity.
- Price references retain the item across a brief distraction in all three
  focused item variants.
- The user transcript opens with a normal greeting.
- Both ten-rope requests execute once each and leave 40 gold after two purchases.
- Negated and conditional requests do not change the world.

Observed failures:

- Unfamiliar identity questions after transactions elicit irrelevant prose,
  including malformed language.
- “Tell me what you meant” repeatedly calls the capability-list tool.
- Journey small talk triggers an unrequested village-fact lookup.
- A wares question contaminates a subsequent sword-price answer.
- A late player-name write proposes source turn 0 rather than its actual source.
  Validation rejects it. A subsequent player-name question reads the NPC name.
- Only one of three new names is stored correctly in fresh held-out sessions;
  none of the three follow-up recall questions succeeds.

These failures rule out claiming a generally natural or reliable NPC. Stronger
transaction results do not establish memory or conversational understanding.
The additional post-smoke episodes are prepared only. The fixed run was not
extended to train on inspected evaluation failures.

This is agent inspection, not independent human release approval.
