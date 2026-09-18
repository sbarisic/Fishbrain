# In-game conversation replay: preliminary

Actual runtime replies; assertions cover explicit behavior, not conversational naturalness.

## user_transcript

**Player:** hi

**NPC:** HELLO.

**Checks:** noUnrequestedTool=True, noUnintendedMutation=True, homeFactsPreserved=True

**Player:** Where am I?

**NPC:** YOU ARE AT VILLAGE MARKET.

**Checks:** requestedTool=True, noUnintendedMutation=True, homeFactsPreserved=True

**Player:** Nevermind, what do you have for sale?

**NPC:** I HAVE NOT TAKEN THAT ACTION. PLEASE CLARIFY YOUR REQUEST.

**Checks:** requestedTool=False, noUnintendedMutation=True, homeFactsPreserved=True

**Player:** Tell me your name

**NPC:** MY NAME IS ARIN.

**Checks:** noUnrequestedTool=True, noUnintendedMutation=True, homeFactsPreserved=True, answer=True

**Player:** Where is the inn?

**NPC:** I HAVE NOT TAKEN THAT ACTION. PLEASE CLARIFY YOUR REQUEST.

**Checks:** requestedTool=False, noUnintendedMutation=True, homeFactsPreserved=True

## first_visit

**Player:** Hello there!

**NPC:** GOOD TO SEE YOU.

**Checks:** noUnrequestedTool=True, noUnintendedMutation=True, homeFactsPreserved=True

**Player:** What should I call you?

**NPC:** YOU ARE AT VILLAGE MARKET.

**Checks:** noUnrequestedTool=False, noUnintendedMutation=True, homeFactsPreserved=True, answer=False

**Player:** What do you do for work?

**NPC:** I WORK AS ROAD WARDEN.

**Checks:** noUnrequestedTool=True, noUnintendedMutation=True, homeFactsPreserved=True, answer=True

**Player:** How are things with you?

**NPC:** GLAD TO HAVE A MOMENT TO TALK.

**Checks:** noUnrequestedTool=True, noUnintendedMutation=True, homeFactsPreserved=True

**Player:** Could you tell me where we are?

**NPC:** YOU ARE AT VILLAGE MARKET.

**Checks:** requestedTool=True, noUnintendedMutation=True, homeFactsPreserved=True

**Player:** I need directions to the inn.

**NPC:** THANKS FOR TELLING ME.

**Checks:** requestedTool=False, noUnintendedMutation=True, homeFactsPreserved=True

## shopping_trip

**Player:** Hello.

**NPC:** HELLO.

**Checks:** noUnrequestedTool=True, noUnintendedMutation=True, homeFactsPreserved=True

**Player:** Can I see what is for sale?

**NPC:** I HAVE NOT TAKEN THAT ACTION. PLEASE CLARIFY YOUR REQUEST.

**Checks:** requestedTool=False, noUnintendedMutation=True, homeFactsPreserved=True

**Player:** How much gold do I have?

**NPC:** I HAVE NOT TAKEN THAT ACTION. PLEASE CLARIFY YOUR REQUEST.

**Checks:** requestedTool=False, noUnintendedMutation=True, homeFactsPreserved=True

**Player:** What does a rope cost?

**NPC:** ROPE COSTS 3 GOLD.

**Checks:** requestedTool=True, noUnintendedMutation=True, homeFactsPreserved=True

**Player:** Buy one rope please.

**NPC:** YOU BOUGHT 1 ROPE FOR 3 GOLD. YOUR BALANCE IS 97 GOLD.

**Checks:** requestedTool=True, noUnintendedMutation=True, homeFactsPreserved=True, balance=True

**Player:** What am I carrying now?

**NPC:** YOU CARRY 1 HEALTH POTION, 3 ROPE.

**Checks:** requestedTool=True, noUnintendedMutation=True, homeFactsPreserved=True

**Player:** Tell me my remaining balance.

**NPC:** I HAVE NOT TAKEN THAT ACTION. PLEASE CLARIFY YOUR REQUEST.

**Checks:** requestedTool=False, noUnintendedMutation=True, homeFactsPreserved=True

**Player:** Thanks for your help.

**NPC:** THAT SOUNDS TIRING. TAKE YOUR TIME.

**Checks:** noUnrequestedTool=True, noUnintendedMutation=True, homeFactsPreserved=True

## sell_and_buy

**Player:** Show me my inventory.

**NPC:** YOU CARRY 1 HEALTH POTION, 2 ROPE.

**Checks:** requestedTool=True, noUnintendedMutation=True, homeFactsPreserved=True

**Player:** Sell one rope for me.

**NPC:** YOU SOLD 1 ROPE FOR 1 GOLD. YOUR BALANCE IS 101 GOLD.

**Checks:** requestedTool=True, noUnintendedMutation=True, homeFactsPreserved=True

**Player:** How much is a health potion?

**NPC:** HEALTH POTION COSTS 8 GOLD.

**Checks:** requestedTool=True, noUnintendedMutation=True, homeFactsPreserved=True

**Player:** I would like to buy one health potion.

**NPC:** YOU BOUGHT 1 HEALTH POTION FOR 8 GOLD. YOUR BALANCE IS 93 GOLD.

**Checks:** requestedTool=True, noUnintendedMutation=True, homeFactsPreserved=True

**Player:** How much gold is left?

**NPC:** PLEASE SPECIFY ITEM.

**Checks:** requestedTool=False, noUnintendedMutation=True, homeFactsPreserved=True

**Player:** Goodbye.

**NPC:** I HAVE NOT TAKEN THAT ACTION. PLEASE CLARIFY YOUR REQUEST.

**Checks:** noUnrequestedTool=True, noUnintendedMutation=True, homeFactsPreserved=True

## purchase_hard_negatives

**Player:** Do not buy one rope.

**NPC:** ALL RIGHT.

**Checks:** noUnrequestedTool=True, noUnintendedMutation=True, homeFactsPreserved=True

**Player:** Suppose I bought two rope.

**NPC:** THAT IS ONLY A POSSIBILITY.

**Checks:** noUnrequestedTool=True, noUnintendedMutation=True, homeFactsPreserved=True

**Player:** Someone said "buy one rope".

**NPC:** I SEE.

**Checks:** noUnrequestedTool=True, noUnintendedMutation=True, homeFactsPreserved=True

**Player:** I am imagining buying one rope.

**NPC:** THAT IS ONLY A POSSIBILITY.

**Checks:** noUnrequestedTool=True, noUnintendedMutation=True, homeFactsPreserved=True

**Player:** This is hypothetical: buy one rope.

**NPC:** I HAVE NOT TAKEN THAT ACTION. PLEASE CLARIFY YOUR REQUEST.

**Checks:** noUnrequestedTool=True, noUnintendedMutation=True, homeFactsPreserved=True

**Player:** I would not like to buy one rope.

**NPC:** ALL RIGHT.

**Checks:** noUnrequestedTool=True, noUnintendedMutation=True, homeFactsPreserved=True

**Player:** If I had more gold I would like to buy one rope.

**NPC:** I HAVE NOT TAKEN THAT ACTION. PLEASE CLARIFY YOUR REQUEST.

**Checks:** noUnrequestedTool=True, noUnintendedMutation=True, homeFactsPreserved=True

**Player:** I said "I would like to buy one rope".

**NPC:** I HAVE NOT TAKEN THAT ACTION. PLEASE CLARIFY YOUR REQUEST.

**Checks:** noUnrequestedTool=True, noUnintendedMutation=True, homeFactsPreserved=True

**Player:** What is my balance?

**NPC:** I HAVE NOT TAKEN THAT ACTION. PLEASE CLARIFY YOUR REQUEST.

**Checks:** requestedTool=False, noUnintendedMutation=True, homeFactsPreserved=True, balance=True

## remember_me

**Player:** My home is Cedar Hollow.

**NPC:** THANKS FOR TELLING ME.

**Checks:** noUnrequestedTool=True, noUnintendedMutation=True, fact=True

**Player:** How are you?

**NPC:** GLAD TO HAVE A MOMENT TO TALK.

**Checks:** noUnrequestedTool=True, noUnintendedMutation=True, homeFactsPreserved=True

**Player:** What is your name?

**NPC:** MY NAME IS ARIN.

**Checks:** noUnrequestedTool=True, noUnintendedMutation=True, homeFactsPreserved=True, answer=True

**Player:** Where did I say my home is?

**NPC:** YOU SAID YOUR HOME IS CEDAR HOLLOW.

**Checks:** noUnrequestedTool=True, noUnintendedMutation=True, homeFactsPreserved=True, answer=True

**Player:** Actually my home is Copper Bay.

**NPC:** THANKS FOR CORRECTING ME.

**Checks:** noUnrequestedTool=True, noUnintendedMutation=True, fact=True

**Player:** What home did I tell you about?

**NPC:** YOU SAID YOUR HOME IS COPPER BAY.

**Checks:** noUnrequestedTool=True, noUnintendedMutation=True, homeFactsPreserved=True, answer=True

## keep_owners_separate

**Player:** My home is Willow Quay.

**NPC:** THANKS FOR TELLING ME.

**Checks:** noUnrequestedTool=True, noUnintendedMutation=True, fact=True

**Player:** You said your home is Marble Port.

**NPC:** THANKS FOR TELLING ME.

**Checks:** noUnrequestedTool=True, noUnintendedMutation=True, fact=True

**Player:** Where do I live?

**NPC:** YOU SAID YOUR HOME IS WILLOW QUAY.

**Checks:** noUnrequestedTool=True, noUnintendedMutation=True, homeFactsPreserved=True, answer=True

**Player:** What home did you mention?

**NPC:** MY NAME IS ARIN.

**Checks:** noUnrequestedTool=True, noUnintendedMutation=True, homeFactsPreserved=True, answer=False

**Player:** Correction: my home is North Road.

**NPC:** THANKS FOR CORRECTING ME.

**Checks:** noUnrequestedTool=True, noUnintendedMutation=True, fact=True, otherFact=True

**Player:** What home did you mention?

**NPC:** YOU SAID MY HOME IS MARBLE PORT.

**Checks:** noUnrequestedTool=True, noUnintendedMutation=True, homeFactsPreserved=True, answer=True

## unfamiliar_home

**Player:** My home is Briarhaven.

**NPC:** THANKS FOR TELLING ME.

**Checks:** noUnrequestedTool=True, noUnintendedMutation=True, fact=True

**Player:** I am tired after travelling.

**NPC:** THANKS FOR TELLING ME.

**Checks:** noUnrequestedTool=True, noUnintendedMutation=True, homeFactsPreserved=False

**Player:** Where do I live?

**NPC:** YOU SAID YOUR HOME IS AFTER TRAVELLING.

**Checks:** noUnrequestedTool=True, noUnintendedMutation=True, homeFactsPreserved=True, answer=False

**Player:** Correction: my home is Westmere.

**NPC:** THANKS FOR CORRECTING ME.

**Checks:** noUnrequestedTool=True, noUnintendedMutation=True, fact=True

**Player:** Can you recall my home?

**NPC:** MY NAME IS ARIN.

**Checks:** noUnrequestedTool=True, noUnintendedMutation=True, homeFactsPreserved=True, answer=False

## same_final_cedar

**Player:** My home is Cedar Hollow.

**NPC:** THANKS FOR TELLING ME.

**Checks:** noUnrequestedTool=True, noUnintendedMutation=True, fact=True

**Player:** What is your job?

**NPC:** I WORK AS ROAD WARDEN.

**Checks:** noUnrequestedTool=True, noUnintendedMutation=True, homeFactsPreserved=True, answer=True

**Player:** Where do I live?

**NPC:** I AM GLAD FOR THE COMPANY.

**Checks:** noUnrequestedTool=True, noUnintendedMutation=True, homeFactsPreserved=True, answer=False

## same_final_copper

**Player:** My home is Copper Bay.

**NPC:** THANKS FOR TELLING ME.

**Checks:** noUnrequestedTool=True, noUnintendedMutation=True, fact=True

**Player:** What is your job?

**NPC:** I WORK AS ROAD WARDEN.

**Checks:** noUnrequestedTool=True, noUnintendedMutation=True, homeFactsPreserved=True, answer=True

**Player:** Where do I live?

**NPC:** I HAVE NOT TAKEN THAT ACTION. PLEASE CLARIFY YOUR REQUEST.

**Checks:** noUnrequestedTool=True, noUnintendedMutation=True, homeFactsPreserved=True, answer=False

## topic_changes

**Player:** Good morning.

**NPC:** HI THERE.

**Checks:** noUnrequestedTool=True, noUnintendedMutation=True, homeFactsPreserved=True

**Player:** I had a tiring journey.

**NPC:** A LITTLE REST SOUNDS WELCOME.

**Checks:** noUnrequestedTool=True, noUnintendedMutation=True, homeFactsPreserved=True

**Player:** What goods do you sell?

**NPC:** I HAVE NOT TAKEN THAT ACTION. PLEASE CLARIFY YOUR REQUEST.

**Checks:** requestedTool=False, noUnintendedMutation=True, homeFactsPreserved=True

**Player:** Anyway, how are you doing?

**NPC:** I HAVE NOT TAKEN THAT ACTION. PLEASE CLARIFY YOUR REQUEST.

**Checks:** noUnrequestedTool=True, noUnintendedMutation=True, homeFactsPreserved=True

**Player:** Where can I find the castle?

**NPC:** I HAVE NOT TAKEN THAT ACTION. PLEASE CLARIFY YOUR REQUEST.

**Checks:** requestedTool=False, noUnintendedMutation=True, homeFactsPreserved=True

**Player:** Can you tell me your name?

**NPC:** MY NAME IS ARIN.

**Checks:** noUnrequestedTool=True, noUnintendedMutation=True, homeFactsPreserved=True, answer=True

## ambiguous_or_unsupported

**Player:** Do it.

**NPC:** COULD YOU EXPLAIN WHAT YOU MEAN?

**Checks:** noUnrequestedTool=True, noUnintendedMutation=True, homeFactsPreserved=True

**Player:** Buy that.

**NPC:** COULD YOU EXPLAIN WHAT YOU MEAN?

**Checks:** noUnrequestedTool=True, noUnintendedMutation=True, homeFactsPreserved=True

**Player:** Give me the thing we discussed.

**NPC:** THANKS FOR TELLING ME.

**Checks:** noUnrequestedTool=True, noUnintendedMutation=True, homeFactsPreserved=False

**Player:** Can you repair my armor?

**NPC:** COULD YOU EXPLAIN WHAT YOU MEAN?

**Checks:** noUnrequestedTool=True, noUnintendedMutation=True, homeFactsPreserved=True

**Player:** What quests are available?

**NPC:** THANKS FOR TELLING ME.

**Checks:** noUnrequestedTool=True, noUnintendedMutation=True, homeFactsPreserved=False

**Player:** I own a thousand gold coins.

**NPC:** THANKS FOR CORRECTING ME.

**Checks:** noUnrequestedTool=True, noUnintendedMutation=True, homeFactsPreserved=False

**Player:** What is my balance?

**NPC:** THANKS FOR CORRECTING ME.

**Checks:** requestedTool=False, noUnintendedMutation=True, homeFactsPreserved=True, balance=True

## compound_requests

**Player:** Hello. Who are you?

**NPC:** MY NAME IS ARIN.

**Checks:** noUnrequestedTool=True, noUnintendedMutation=True, homeFactsPreserved=True, answer=True

**Player:** Do not buy one rope. What do you have for sale?

**NPC:** I HAVE NOT TAKEN THAT ACTION. PLEASE CLARIFY YOUR REQUEST.

**Checks:** requestedTool=False, noUnintendedMutation=True, homeFactsPreserved=True, balance=True

**Player:** Buy one rope. Then sell one health potion.

**NPC:** PLEASE SPECIFY ITEM.

**Checks:** requestedTool=False, noUnintendedMutation=True, homeFactsPreserved=True, balance=False

**Player:** What am I carrying?

**NPC:** I HAVE NOT TAKEN THAT ACTION. PLEASE CLARIFY YOUR REQUEST.

**Checks:** requestedTool=False, noUnintendedMutation=True, homeFactsPreserved=True
