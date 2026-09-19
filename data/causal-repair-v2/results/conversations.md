# Paired conversations

All focused and development conversations, followed by the first 22 frozen conversations. Selection by order, not outcome.

## context-heldout-000 / turn 0

Player: Could you tell me the unit price of rope?

Before: ROPE COSTS 3 GOLD.

After: ROPE COSTS 3 GOLD.

Expected: LOOKUP_PRICE {"ITEM": "ROPE"}

Calls: [["LOOKUP_PRICE", {"ITEM": "ROPE"}, null]]

## context-heldout-000 / turn 1

Player: I prefer to compare before choosing.

Before: What do you enjoy about it?

After: What do you enjoy about it?

Expected: None {}

Calls: []

## context-heldout-000 / turn 2

Player: What was that price again?

Before: HEALTH POTION COSTS 8 GOLD.

After: ROPE COSTS 3 GOLD.

Expected: LOOKUP_PRICE {"ITEM": "ROPE"}

Calls: [["LOOKUP_PRICE", {"ITEM": "ROPE"}, null]]

## context-heldout-000 / turn 3

Player: And the gold in my purse?

Before: YOU HAVE 100 GOLD.

After: YOU HAVE 100 GOLD.

Expected: GET_BALANCE {}

Calls: [["GET_BALANCE", {}, null]]

## context-heldout-001 / turn 0

Player: Please buy 2 rope for me.

Before: YOU BOUGHT 2 ROPE FOR 6 GOLD. YOUR BALANCE IS 94 GOLD.

After: YOU BOUGHT 2 ROPE FOR 6 GOLD. YOUR BALANCE IS 94 GOLD.

Expected: BUY {"ITEM": "ROPE", "QUANTITY": "2"}

Calls: [["BUY", {"ITEM": "ROPE", "QUANTITY": "2"}, null]]

## context-heldout-001 / turn 1

Player: I'd like to sell 2 rope back.

Before: YOU SOLD 2 ROPE FOR 2 GOLD. YOUR BALANCE IS 96 GOLD.

After: YOU SOLD 2 ROPE FOR 2 GOLD. YOUR BALANCE IS 96 GOLD.

Expected: SELL {"ITEM": "ROPE", "QUANTITY": "2"}

Calls: [["SELL", {"ITEM": "ROPE", "QUANTITY": "2"}, null]]

## context-heldout-001 / turn 2

Player: Before we go on, who are you?

Before: That sounds like a good way to have fun and get some exercise!

After: That sounds like a good way to have fun and get some exercise.

Expected: READ_PERSONA {"FIELD": "NAME"}

Calls: []

## context-heldout-002 / turn 0

Player: Please buy 3 rope for me.

Before: I have not taken that action. Please clarify your request.

After: YOU BOUGHT 3 ROPE FOR 9 GOLD. YOUR BALANCE IS 91 GOLD.

Expected: BUY {"ITEM": "ROPE", "QUANTITY": "3"}

Calls: [["BUY", {"ITEM": "ROPE", "QUANTITY": "3"}, null]]

## context-heldout-002 / turn 1

Player: I'd like to sell 3 rope back.

Before: I HAVE HEALTH POTION: 50 IN STOCK, IRON SWORD: 20 IN STOCK, ROPE: 30 IN STOCK.

After: YOU SOLD 3 ROPE FOR 3 GOLD. YOUR BALANCE IS 94 GOLD.

Expected: SELL {"ITEM": "ROPE", "QUANTITY": "3"}

Calls: [["SELL", {"ITEM": "ROPE", "QUANTITY": "3"}, null]]

## context-heldout-002 / turn 2

Player: Before we go on, who are you?

Before: That sounds like a good way to have fun and get some exercise!

After: I hope you get you into now?

Expected: READ_PERSONA {"FIELD": "NAME"}

Calls: []

## context-heldout-003 / turn 0

Player: Could you tell me the unit price of health potion?

Before: HEALTH POTION COSTS 8 GOLD.

After: HEALTH POTION COSTS 8 GOLD.

Expected: LOOKUP_PRICE {"ITEM": "HEALTH POTION"}

Calls: [["LOOKUP_PRICE", {"ITEM": "HEALTH POTION"}, null]]

## context-heldout-003 / turn 1

Player: I prefer to compare before choosing.

Before: What kind of music do you like?

After: All right. What do you enjoy about it?

Expected: None {}

Calls: []

## context-heldout-003 / turn 2

Player: What was that price again?

Before: HEALTH POTION COSTS 8 GOLD.

After: HEALTH POTION COSTS 8 GOLD.

Expected: LOOKUP_PRICE {"ITEM": "HEALTH POTION"}

Calls: [["LOOKUP_PRICE", {"ITEM": "HEALTH POTION"}, null]]

## context-heldout-003 / turn 3

Player: And the gold in my purse?

Before: YOU HAVE 100 GOLD.

After: YOU HAVE 100 GOLD.

Expected: GET_BALANCE {}

Calls: [["GET_BALANCE", {}, null]]

## context-heldout-004 / turn 0

Player: Please buy 2 health potion for me.

Before: YOU BOUGHT 2 HEALTH POTION FOR 16 GOLD. YOUR BALANCE IS 84 GOLD. Please continue in another turn.

After: YOU BOUGHT 2 HEALTH POTION FOR 16 GOLD. YOUR BALANCE IS 84 GOLD.

Expected: BUY {"ITEM": "HEALTH POTION", "QUANTITY": "2"}

Calls: [["BUY", {"ITEM": "HEALTH POTION", "QUANTITY": "2"}, null]]

## context-heldout-004 / turn 1

Player: I'd like to sell 2 health potion back.

Before: YOU SOLD 2 HEALTH POTION FOR 8 GOLD. YOUR BALANCE IS 92 GOLD. Please continue in another turn.

After: YOU SOLD 2 HEALTH POTION FOR 8 GOLD. YOUR BALANCE IS 92 GOLD.

Expected: SELL {"ITEM": "HEALTH POTION", "QUANTITY": "2"}

Calls: [["SELL", {"ITEM": "HEALTH POTION", "QUANTITY": "2"}, null]]

## context-heldout-004 / turn 2

Player: Before we go on, who are you?

Before: That sounds like a good way to have fun and get some exercise!

After: That sounds like a lot of fun.  What do you do for work?

Expected: READ_PERSONA {"FIELD": "NAME"}

Calls: []

## context-heldout-005 / turn 0

Player: Please buy 3 health potion for me.

Before: I have not taken that action. Please clarify your request.

After: YOU BOUGHT 3 HEALTH POTION FOR 24 GOLD. YOUR BALANCE IS 76 GOLD.

Expected: BUY {"ITEM": "HEALTH POTION", "QUANTITY": "3"}

Calls: [["BUY", {"ITEM": "HEALTH POTION", "QUANTITY": "3"}, null]]

## context-heldout-005 / turn 1

Player: I'd like to sell 3 health potion back.

Before: I have not taken that action. Please clarify your request.

After: YOU SOLD 3 HEALTH POTION FOR 12 GOLD. YOUR BALANCE IS 88 GOLD.

Expected: SELL {"ITEM": "HEALTH POTION", "QUANTITY": "3"}

Calls: [["SELL", {"ITEM": "HEALTH POTION", "QUANTITY": "3"}, null]]

## context-heldout-005 / turn 2

Player: Before we go on, who are you?

Before: Oh! Where do you want to go?

After: That sounds like a lot of fun.  What do you do for work?

Expected: READ_PERSONA {"FIELD": "NAME"}

Calls: []

## context-heldout-006 / turn 0

Player: Could you tell me the unit price of iron sword?

Before: IRON SWORD COSTS 25 GOLD.

After: IRON SWORD COSTS 25 GOLD.

Expected: LOOKUP_PRICE {"ITEM": "IRON SWORD"}

Calls: [["LOOKUP_PRICE", {"ITEM": "IRON SWORD"}, null]]

## context-heldout-006 / turn 1

Player: I prefer to compare before choosing.

Before: What kind of music do you like?

After: What do you enjoy about it?

Expected: None {}

Calls: []

## context-heldout-006 / turn 2

Player: What was that price again?

Before: HEALTH POTION COSTS 8 GOLD.

After: IRON SWORD COSTS 25 GOLD.

Expected: LOOKUP_PRICE {"ITEM": "IRON SWORD"}

Calls: [["LOOKUP_PRICE", {"ITEM": "IRON SWORD"}, null]]

## context-heldout-006 / turn 3

Player: And the gold in my purse?

Before: YOU HAVE 100 GOLD.

After: YOU HAVE 100 GOLD.

Expected: GET_BALANCE {}

Calls: [["GET_BALANCE", {}, null]]

## context-heldout-007 / turn 0

Player: Please buy 2 iron sword for me.

Before: YOU BOUGHT 2 IRON SWORD FOR 50 GOLD. YOUR BALANCE IS 50 GOLD.

After: YOU BOUGHT 2 IRON SWORD FOR 50 GOLD. YOUR BALANCE IS 50 GOLD.

Expected: BUY {"ITEM": "IRON SWORD", "QUANTITY": "2"}

Calls: [["BUY", {"ITEM": "IRON SWORD", "QUANTITY": "2"}, null]]

## context-heldout-007 / turn 1

Player: I'd like to sell 2 iron sword back.

Before: YOU SOLD 2 IRON SWORD FOR 24 GOLD. YOUR BALANCE IS 74 GOLD. Please continue in another turn.

After: YOU SOLD 2 IRON SWORD FOR 24 GOLD. YOUR BALANCE IS 74 GOLD.

Expected: SELL {"ITEM": "IRON SWORD", "QUANTITY": "2"}

Calls: [["SELL", {"ITEM": "IRON SWORD", "QUANTITY": "2"}, null]]

## context-heldout-007 / turn 2

Player: Before we go on, who are you?

Before: That sounds like a good way to have fun and get some exercise!

After: That sounds like a good way to have fun and get some exercise!

Expected: READ_PERSONA {"FIELD": "NAME"}

Calls: []

## context-heldout-008 / turn 0

Player: Please buy 3 iron sword for me.

Before: I have not taken that action. Please clarify your request.

After: YOU BOUGHT 3 IRON SWORD FOR 75 GOLD. YOUR BALANCE IS 25 GOLD.

Expected: BUY {"ITEM": "IRON SWORD", "QUANTITY": "3"}

Calls: [["BUY", {"ITEM": "IRON SWORD", "QUANTITY": "3"}, null]]

## context-heldout-008 / turn 1

Player: I'd like to sell 3 iron sword back.

Before: I have not taken that action. Please clarify your request.

After: YOU SOLD 3 IRON SWORD FOR 36 GOLD. YOUR BALANCE IS 61 GOLD.

Expected: SELL {"ITEM": "IRON SWORD", "QUANTITY": "3"}

Calls: [["SELL", {"ITEM": "IRON SWORD", "QUANTITY": "3"}, null]]

## context-heldout-008 / turn 2

Player: Before we go on, who are you?

Before: Oh! How long have you been on?

After: That sounds like a lot of fun.  What do you do for fun?

Expected: READ_PERSONA {"FIELD": "NAME"}

Calls: []

## context-heldout-009 / turn 0

Player: Greetings.

Before: Greetings.

After: Greetings?

Expected: None {}

Calls: []

## context-heldout-009 / turn 1

Player: I was thinking about the road.

Before: Take your time.

After: A small change can make a familiar walk feel new.

Expected: None {}

Calls: []

## context-heldout-009 / turn 2

Player: Can you supply me with goods?

Before: IRON SWORD COSTS 25 GOLD.

After: There's satisfaction in making something useful again.

Expected: LIST_WARES {}

Calls: []

## context-heldout-010 / turn 0

Player: Greetings.

Before: Greetings.

After: Greetings?

Expected: None {}

Calls: []

## context-heldout-010 / turn 1

Player: I was thinking about the road.

Before: Take your time.

After: A small change can make a familiar walk feel new.

Expected: None {}

Calls: []

## context-heldout-010 / turn 2

Player: Where is this conversation taking place?

Before: THE REACTOR POWERS THE STATION.

After: the old mill

Expected: GET_CURRENT_LOCATION {}

Calls: [["READ_PERSONA", {"FIELD": "HOME"}, null]]

## context-heldout-011 / turn 0

Player: Greetings.

Before: Greetings.

After: Greetings?

Expected: None {}

Calls: []

## context-heldout-011 / turn 1

Player: I was thinking about the road.

Before: Take your time.

After: A small change can make a familiar walk feel new.

Expected: None {}

Calls: []

## context-heldout-011 / turn 2

Player: What name do you go by?

Before: the old mill

After: the old mill

Expected: READ_PERSONA {"FIELD": "NAME"}

Calls: [["READ_PERSONA", {"FIELD": "HOME"}, null]]

## context-heldout-012 / turn 0

Player: Greetings.

Before: Greetings.

After: Greetings?

Expected: None {}

Calls: []

## context-heldout-012 / turn 1

Player: I was thinking about the road.

Before: Take your time.

After: A small change can make a familiar walk feel new.

Expected: None {}

Calls: []

## context-heldout-012 / turn 2

Player: Which occupation is yours?

Before: THE REACTOR POWERS THE STATION.

After: How long have you been working at the road?

Expected: READ_PERSONA {"FIELD": "OCCUPATION"}

Calls: []

## context-heldout-013 / turn 0

Player: Greetings.

Before: Greetings.

After: Greetings?

Expected: None {}

Calls: []

## context-heldout-013 / turn 1

Player: I was thinking about the road.

Before: Take your time.

After: A small change can make a familiar walk feel new.

Expected: None {}

Calls: []

## context-heldout-013 / turn 2

Player: How much currency belongs to me?

Before: IRON SWORD COSTS 25 GOLD.

After: IRON SWORD COSTS 25 GOLD.

Expected: GET_BALANCE {}

Calls: [["LOOKUP_PRICE", {"ITEM": "IRON SWORD"}, null]]

## context-heldout-014 / turn 0

Player: Greetings.

Before: Greetings.

After: Greetings?

Expected: None {}

Calls: []

## context-heldout-014 / turn 1

Player: I was thinking about the road.

Before: Take your time.

After: A small change can make a familiar walk feel new.

Expected: None {}

Calls: []

## context-heldout-014 / turn 2

Player: Which possessions am I carrying?

Before: IRON SWORD COSTS 25 GOLD.

After: I know you can get along well.

Expected: LIST_INVENTORY {}

Calls: []

## context-heldout-015 / turn 0

Player: I would like to buy 10 rope, unless the price went up.

Before: Sure, what type of cuisine or dietary restrictions are you looking for?

After: Please clarify your request. I have not made a transaction.

Expected: None {}

Calls: []

## context-heldout-015 / turn 1

Player: Check the gold still in my purse.

Before: YOU HAVE 100 GOLD.

After: YOU HAVE 100 GOLD.

Expected: GET_BALANCE {}

Calls: [["GET_BALANCE", {}, null]]

## context-heldout-016 / turn 0

Player: Sell me 10 rope, only as an example.

Before: I have not taken that action. Please clarify your request.

After: I have not taken that action. Please clarify your request.

Expected: None {}

Calls: [["SELL", {"ITEM": "ROPE", "QUANTITY": "10"}, "NON_AFFIRMATIVE_ACTION"]]

## context-heldout-016 / turn 1

Player: Check the gold still in my purse.

Before: YOU HAVE 100 GOLD.

After: YOU HAVE 100 GOLD.

Expected: GET_BALANCE {}

Calls: [["GET_BALANCE", {}, null]]

## context-heldout-017 / turn 0

Player: He told me to buy 3 rope.

Before: Take your time.

After: Please clarify your request. I have not made a transaction.

Expected: None {}

Calls: []

## context-heldout-017 / turn 1

Player: Check the gold still in my purse.

Before: YOU HAVE 100 GOLD.

After: YOU HAVE 100 GOLD.

Expected: GET_BALANCE {}

Calls: [["GET_BALANCE", {}, null]]

## context-heldout-018 / turn 0

Player: Buy 2 rope. Actually, stop.

Before: I have not taken that action. Please clarify your request.

After: I have not taken that action. Please clarify your request.

Expected: None {}

Calls: [["BUY", {"ITEM": "ROPE", "QUANTITY": "2"}, "NON_AFFIRMATIVE_ACTION"]]

## context-heldout-018 / turn 1

Player: Check the gold still in my purse.

Before: YOU HAVE 100 GOLD.

After: YOU HAVE 100 GOLD.

Expected: GET_BALANCE {}

Calls: [["GET_BALANCE", {}, null]]

## context-heldout-019 / turn 0

Player: I might buy 2 rope.

Before: I have not taken that action. Please clarify your request.

After: I have not taken that action. Please clarify your request.

Expected: None {}

Calls: [["BUY", {"ITEM": "ROPE", "QUANTITY": "2"}, "NON_AFFIRMATIVE_ACTION"]]

## context-heldout-019 / turn 1

Player: Check the gold still in my purse.

Before: YOU HAVE 100 GOLD.

After: YOU HAVE 100 GOLD.

Expected: GET_BALANCE {}

Calls: [["GET_BALANCE", {}, null]]

## context-heldout-020 / turn 0

Player: Can we imagine buying 3 rope?

Before: Sure. What would you like to discuss?

After: Sure. What would you like to imagine?

Expected: None {}

Calls: []

## context-heldout-020 / turn 1

Player: Check the gold still in my purse.

Before: YOU HAVE 100 GOLD.

After: YOU HAVE 100 GOLD.

Expected: GET_BALANCE {}

Calls: [["GET_BALANCE", {}, null]]

## context-heldout-021 / turn 0

Player: Hello again.

Before: Hello again.

After: Hi. What brings you here?

Expected: None {}

Calls: []

## context-heldout-021 / turn 1

Player: I just want to chat, not shop.

Before: We can take that as a meat or do you want to keep that that?

After: We can take this slowly.

Expected: None {}

Calls: []

## context-heldout-021 / turn 2

Player: Tell me what you meant.

Before: Available tools: balance, buy, capabilities, current location, inventory, location, memory delete, memory search, memory upsert, persona, price, sell, wares, world fact.

After: Available tools: balance, buy, capabilities, current location, inventory, location, memory delete, memory search, memory upsert, persona, price, sell, wares, world fact.

Expected: None {}

Calls: [["LIST_CAPABILITIES", {}, null]]

## context-heldout-022 / turn 0

Player: Sorry, I missed your meaning.

Before: No problem. Which part should we clear up?

After: No problem. Which part should we clear up?

Expected: None {}

Calls: []

## context-heldout-022 / turn 1

Player: I just want to chat, not shop.

Before: the old mill

After: We can keep it simple.

Expected: None {}

Calls: []

## context-heldout-022 / turn 2

Player: Tell me what you meant.

Before: Available tools: balance, buy, capabilities, current location, inventory, location, memory delete, memory search, memory upsert, persona, price, sell, wares, world fact.

After: Available tools: balance, buy, capabilities, current location, inventory, location, memory delete, memory search, memory upsert, persona, price, sell, wares, world fact.

Expected: None {}

Calls: [["LIST_CAPABILITIES", {}, null]]

## context-heldout-023 / turn 0

Player: That reply sounded strange.

Before: What kind of music do you like?

After: Sometimes a quiet moment can help.

Expected: None {}

Calls: []

## context-heldout-023 / turn 1

Player: I just want to chat, not shop.

Before: Available tools: balance, buy, capabilities, current location, inventory, location, memory delete, memory search, memory upsert, persona, price, sell, wares, world fact.

After: We can take this slowly.

Expected: None {}

Calls: []

## context-heldout-023 / turn 2

Player: Tell me what you meant.

Before: Available tools: balance, buy, capabilities, current location, inventory, location, memory delete, memory search, memory upsert, persona, price, sell, wares, world fact.

After: Available tools: balance, buy, capabilities, current location, inventory, location, memory delete, memory search, memory upsert, persona, price, sell, wares, world fact.

Expected: None {}

Calls: [["LIST_CAPABILITIES", {}, null]]

## context-heldout-024 / turn 0

Player: Can we just have a conversation?

Before: INN IS NORTH BY THE FOUNTAIN.

After: Sure. What would you like to talk about?

Expected: None {}

Calls: []

## context-heldout-024 / turn 1

Player: I just want to chat, not shop.

Before: Available tools: balance, buy, capabilities, current location, inventory, location, memory delete, memory search, memory upsert, persona, price, sell, wares, world fact.

After: That sounds good.

Expected: None {}

Calls: []

## context-heldout-024 / turn 2

Player: Tell me what you meant.

Before: Available tools: balance, buy, capabilities, current location, inventory, location, memory delete, memory search, memory upsert, persona, price, sell, wares, world fact.

After: Available tools: balance, buy, capabilities, current location, inventory, location, memory delete, memory search, memory upsert, persona, price, sell, wares, world fact.

Expected: None {}

Calls: [["LIST_CAPABILITIES", {}, null]]

## context-heldout-025 / turn 0

Player: My name is Nadia

Before: Understood. You have not placed an apartment?

After: Reported memory saved.

Expected: MEMORY_UPSERT {"ID": "NEW", "SUBJECT": "PLAYER", "PREDICATE": "NAME", "VALUE": "Nadia", "POLARITY": "true", "SOURCE_TURN": "0", "QUOTE": "My name is Nadia"}

Calls: [["MEMORY_UPSERT", {"ID": "NEW", "SUBJECT": "PLAYER", "PREDICATE": "NAME", "VALUE": "Nadia", "POLARITY": "true", "SOURCE_TURN": "0", "QUOTE": "My name is Nadia"}, null]]

## context-heldout-025 / turn 1

Player: Tell me your own name.

Before: Arin

After: Arin

Expected: READ_PERSONA {"FIELD": "NAME"}

Calls: [["READ_PERSONA", {"FIELD": "NAME"}, null]]

## context-heldout-025 / turn 2

Player: Let us talk about the weather for a moment.

Before: What would you like to discuss?

After: All right. We can leave it there.

Expected: None {}

Calls: []

## context-heldout-025 / turn 3

Player: Can you recall my name?

Before: I HAVE HEALTH POTION: 50 IN STOCK, IRON SWORD: 20 IN STOCK, ROPE: 30 IN STOCK.

After: All right. We can leave it there.

Expected: MEMORY_SEARCH {"SUBJECT": "PLAYER", "PREDICATE": "NAME"}

Calls: []

## context-heldout-026 / turn 0

Player: My name is Evan

Before: I HAVE HEALTH POTION: 50 IN STOCK, IRON SWORD: 20 IN STOCK, ROPE: 30 IN STOCK.

After: I could not update or retrieve that memory.

Expected: MEMORY_UPSERT {"ID": "NEW", "SUBJECT": "PLAYER", "PREDICATE": "NAME", "VALUE": "Evan", "POLARITY": "true", "SOURCE_TURN": "0", "QUOTE": "My name is Evan"}

Calls: [["MEMORY_UPSERT", {"ID": "NEW", "SUBJECT": "PLAYER", "PREDICATE": "NAME", "VALUE": "ENAME", "POLARITY": "true", "SOURCE_TURN": "0", "QUOTE": "My name is Evan"}, null]]

## context-heldout-026 / turn 1

Player: Tell me your own name.

Before: Arin

After: Arin

Expected: READ_PERSONA {"FIELD": "NAME"}

Calls: [["READ_PERSONA", {"FIELD": "NAME"}, null]]

## context-heldout-026 / turn 2

Player: Let us talk about the weather for a moment.

Before: What would you like to discuss?

After: All right. We can leave it there.

Expected: None {}

Calls: []

## context-heldout-026 / turn 3

Player: Can you recall my name?

Before: I HAVE HEALTH POTION: 50 IN STOCK, IRON SWORD: 20 IN STOCK, ROPE: 30 IN STOCK.

After: YOU HAVE 100 GOLD.

Expected: MEMORY_SEARCH {"SUBJECT": "PLAYER", "PREDICATE": "NAME"}

Calls: [["GET_BALANCE", {}, null]]

## context-heldout-027 / turn 0

Player: My name is Iris

Before: Available tools: balance, buy, capabilities, current location, inventory, location, memory delete, memory search, memory upsert, persona, price, sell, wares, world fact.

After: I could not update or retrieve that memory.

Expected: MEMORY_UPSERT {"ID": "NEW", "SUBJECT": "PLAYER", "PREDICATE": "NAME", "VALUE": "Iris", "POLARITY": "true", "SOURCE_TURN": "0", "QUOTE": "My name is Iris"}

Calls: [["MEMORY_UPSERT", {"ID": "NEW", "SUBJECT": "PLAYER", "PREDICATE": "NAME", "VALUE": "IrisUPSTION", "POLARITY": "true", "SOURCE_TURN": "0", "QUOTE": "My name is IrisUPSTION"}, null]]

## context-heldout-027 / turn 1

Player: Tell me your own name.

Before: Arin

After: Arin

Expected: READ_PERSONA {"FIELD": "NAME"}

Calls: [["READ_PERSONA", {"FIELD": "NAME"}, null]]

## context-heldout-027 / turn 2

Player: Let us talk about the weather for a moment.

Before: What would you like to discuss?

After: All right. We can leave it there.

Expected: None {}

Calls: []

## context-heldout-027 / turn 3

Player: Can you recall my name?

Before: I HAVE HEALTH POTION: 50 IN STOCK, IRON SWORD: 20 IN STOCK, ROPE: 30 IN STOCK.

After: Of course.

Expected: MEMORY_SEARCH {"SUBJECT": "PLAYER", "PREDICATE": "NAME"}

Calls: []

## causal-heldout-000 / turn 0

Player: I have been thinking about the journey.

Before: What kind of things do you have in mind?

After: THIS VILLAGE GUARDS THE EASTERN ROAD.

Expected: None {}

Calls: [["LOOKUP_WORLD_FACT", {"TOPIC": "VILLAGE"}, null]]

## causal-heldout-000 / turn 1

Player: That was just conversation. I have a different question.

Before: Do you have any favorite cleats?

After: All right. We can leave it there.

Expected: None {}

Calls: []

## causal-heldout-000 / turn 2

Player: Before we trade, how do people address you?

Before: That sounds like a good way to have fun and get some exercise!

After: Arin

Expected: READ_PERSONA {"FIELD": "NAME"}

Calls: [["READ_PERSONA", {"FIELD": "NAME"}, null]]

## causal-heldout-001 / turn 0

Player: I have been thinking about the journey.

Before: What kind of things do you have in mind?

After: THIS VILLAGE GUARDS THE EASTERN ROAD.

Expected: None {}

Calls: [["LOOKUP_WORLD_FACT", {"TOPIC": "VILLAGE"}, null]]

## causal-heldout-001 / turn 1

Player: That was just conversation. I have a different question.

Before: Do you have any favorite cleats?

After: All right. We can leave it there.

Expected: None {}

Calls: []

## causal-heldout-001 / turn 2

Player: Could you introduce yourself by name?

Before: Of course! What do you have in mind?

After: THIS VILLAGE GUARDS THE EASTERN ROAD.

Expected: READ_PERSONA {"FIELD": "NAME"}

Calls: [["LOOKUP_WORLD_FACT", {"TOPIC": "VILLAGE"}, null]]

## causal-heldout-002 / turn 0

Player: I have been thinking about the journey.

Before: What kind of things do you have in mind?

After: THIS VILLAGE GUARDS THE EASTERN ROAD.

Expected: None {}

Calls: [["LOOKUP_WORLD_FACT", {"TOPIC": "VILLAGE"}, null]]

## causal-heldout-002 / turn 1

Player: That was just conversation. I have a different question.

Before: Do you have any favorite cleats?

After: All right. We can leave it there.

Expected: None {}

Calls: []

## causal-heldout-002 / turn 2

Player: I've forgotten what you are called.

Before: What about you? What do you do for fun?

After: I understand.

Expected: READ_PERSONA {"FIELD": "NAME"}

Calls: []

## causal-heldout-003 / turn 0

Player: I have been thinking about the journey.

Before: What kind of things do you have in mind?

After: THIS VILLAGE GUARDS THE EASTERN ROAD.

Expected: None {}

Calls: [["LOOKUP_WORLD_FACT", {"TOPIC": "VILLAGE"}, null]]

## causal-heldout-003 / turn 1

Player: That was just conversation. I have a different question.

Before: Do you have any favorite cleats?

After: All right. We can leave it there.

Expected: None {}

Calls: []

## causal-heldout-003 / turn 2

Player: Say your name for me, please.

Before: I have not taken that action. Please clarify your request.

After: YOU HAVE 100 GOLD.

Expected: READ_PERSONA {"FIELD": "NAME"}

Calls: [["GET_BALANCE", {}, null]]

## causal-heldout-004 / turn 0

Player: I have been thinking about the journey.

Before: What kind of things do you have in mind?

After: THIS VILLAGE GUARDS THE EASTERN ROAD.

Expected: None {}

Calls: [["LOOKUP_WORLD_FACT", {"TOPIC": "VILLAGE"}, null]]

## causal-heldout-004 / turn 1

Player: That was just conversation. I have a different question.

Before: Do you have any favorite cleats?

After: All right. We can leave it there.

Expected: None {}

Calls: []

## causal-heldout-004 / turn 2

Player: What name do you go by around here?

Before: That is great that you have an interesting life and location for that today.

After: the old mill

Expected: READ_PERSONA {"FIELD": "NAME"}

Calls: [["READ_PERSONA", {"FIELD": "HOME"}, null]]

## causal-heldout-005 / turn 0

Player: I have been thinking about the journey.

Before: What kind of things do you have in mind?

After: THIS VILLAGE GUARDS THE EASTERN ROAD.

Expected: None {}

Calls: [["LOOKUP_WORLD_FACT", {"TOPIC": "VILLAGE"}, null]]

## causal-heldout-005 / turn 1

Player: That was just conversation. I have a different question.

Before: Do you have any favorite cleats?

After: All right. We can leave it there.

Expected: None {}

Calls: []

## causal-heldout-005 / turn 2

Player: After all that shopping, remind me who you are.

Before: that sounds like a good way to have fun and get some exercise.

After: I HAVE HEALTH POTION: 50 IN STOCK, IRON SWORD: 20 IN STOCK, ROPE: 30 IN STOCK.

Expected: READ_PERSONA {"FIELD": "NAME"}

Calls: [["LIST_WARES", {}, null]]

## causal-heldout-006 / turn 0

Player: I have been thinking about the journey.

Before: What kind of things do you have in mind?

After: THIS VILLAGE GUARDS THE EASTERN ROAD.

Expected: None {}

Calls: [["LOOKUP_WORLD_FACT", {"TOPIC": "VILLAGE"}, null]]

## causal-heldout-006 / turn 1

Player: That was just conversation. I have a different question.

Before: Do you have any favorite cleats?

After: All right. We can leave it there.

Expected: None {}

Calls: []

## causal-heldout-006 / turn 2

Player: What is your job?

Before: Are you looking for a job?

After: road warden

Expected: READ_PERSONA {"FIELD": "OCCUPATION"}

Calls: [["READ_PERSONA", {"FIELD": "OCCUPATION"}, null]]

## causal-heldout-007 / turn 0

Player: I have been thinking about the journey.

Before: What kind of things do you have in mind?

After: THIS VILLAGE GUARDS THE EASTERN ROAD.

Expected: None {}

Calls: [["LOOKUP_WORLD_FACT", {"TOPIC": "VILLAGE"}, null]]

## causal-heldout-007 / turn 1

Player: That was just conversation. I have a different question.

Before: Do you have any favorite cleats?

After: All right. We can leave it there.

Expected: None {}

Calls: []

## causal-heldout-007 / turn 2

Player: What's your job?

Before: What kind of job?

After: road warden

Expected: READ_PERSONA {"FIELD": "OCCUPATION"}

Calls: [["READ_PERSONA", {"FIELD": "OCCUPATION"}, null]]

## causal-heldout-008 / turn 0

Player: I have been thinking about the journey.

Before: What kind of things do you have in mind?

After: THIS VILLAGE GUARDS THE EASTERN ROAD.

Expected: None {}

Calls: [["LOOKUP_WORLD_FACT", {"TOPIC": "VILLAGE"}, null]]

## causal-heldout-008 / turn 1

Player: That was just conversation. I have a different question.

Before: Do you have any favorite cleats?

After: All right. We can leave it there.

Expected: None {}

Calls: []

## causal-heldout-008 / turn 2

Player: Count the coins I have left.

Before: Do you have any favorite cleats?

After: Sounds good.

Expected: GET_BALANCE {}

Calls: []

## causal-heldout-009 / turn 0

Player: I have been thinking about the journey.

Before: What kind of things do you have in mind?

After: THIS VILLAGE GUARDS THE EASTERN ROAD.

Expected: None {}

Calls: [["LOOKUP_WORLD_FACT", {"TOPIC": "VILLAGE"}, null]]

## causal-heldout-009 / turn 1

Player: That was just conversation. I have a different question.

Before: Do you have any favorite cleats?

After: All right. We can leave it there.

Expected: None {}

Calls: []

## causal-heldout-009 / turn 2

Player: Tell me my remaining gold total.

Before: YOU HAVE 100 GOLD.

After: YOU HAVE 100 GOLD.

Expected: GET_BALANCE {}

Calls: [["GET_BALANCE", {}, null]]

## causal-heldout-010 / turn 0

Player: I have been thinking about the journey.

Before: What kind of things do you have in mind?

After: THIS VILLAGE GUARDS THE EASTERN ROAD.

Expected: None {}

Calls: [["LOOKUP_WORLD_FACT", {"TOPIC": "VILLAGE"}, null]]

## causal-heldout-010 / turn 1

Player: That was just conversation. I have a different question.

Before: Do you have any favorite cleats?

After: All right. We can leave it there.

Expected: None {}

Calls: []

## causal-heldout-010 / turn 2

Player: Can you check how many gold pieces belong to me?

Before: YOU HAVE 100 GOLD.

After: YOU HAVE 100 GOLD.

Expected: GET_BALANCE {}

Calls: [["GET_BALANCE", {}, null]]

## causal-heldout-011 / turn 0

Player: I have been thinking about the journey.

Before: What kind of things do you have in mind?

After: THIS VILLAGE GUARDS THE EASTERN ROAD.

Expected: None {}

Calls: [["LOOKUP_WORLD_FACT", {"TOPIC": "VILLAGE"}, null]]

## causal-heldout-011 / turn 1

Player: That was just conversation. I have a different question.

Before: Do you have any favorite cleats?

After: All right. We can leave it there.

Expected: None {}

Calls: []

## causal-heldout-011 / turn 2

Player: What does my purse contain in gold?

Before: That's nice that you are thinking what they are going to have in mind.

After: I HAVE HEALTH POTION: 50 IN STOCK, IRON SWORD: 20 IN STOCK, ROPE: 30 IN STOCK.

Expected: GET_BALANCE {}

Calls: [["LIST_WARES", {}, null]]

## causal-heldout-012 / turn 0

Player: I have been thinking about the journey.

Before: What kind of things do you have in mind?

After: THIS VILLAGE GUARDS THE EASTERN ROAD.

Expected: None {}

Calls: [["LOOKUP_WORLD_FACT", {"TOPIC": "VILLAGE"}, null]]

## causal-heldout-012 / turn 1

Player: That was just conversation. I have a different question.

Before: Do you have any favorite cleats?

After: All right. We can leave it there.

Expected: None {}

Calls: []

## causal-heldout-012 / turn 2

Player: Read out my current funds.

Before: Cool! What do you have in mind?

After: A quiet moment can be worthwhile too.

Expected: GET_BALANCE {}

Calls: []

## causal-heldout-013 / turn 0

Player: I have been thinking about the journey.

Before: What kind of things do you have in mind?

After: THIS VILLAGE GUARDS THE EASTERN ROAD.

Expected: None {}

Calls: [["LOOKUP_WORLD_FACT", {"TOPIC": "VILLAGE"}, null]]

## causal-heldout-013 / turn 1

Player: That was just conversation. I have a different question.

Before: Do you have any favorite cleats?

After: All right. We can leave it there.

Expected: None {}

Calls: []

## causal-heldout-013 / turn 2

Player: How many gold pieces are available to me?

Before: YOU HAVE 100 GOLD.

After: YOU HAVE 100 GOLD.

Expected: GET_BALANCE {}

Calls: [["GET_BALANCE", {}, null]]

## causal-heldout-014 / turn 0

Player: I have been thinking about the journey.

Before: What kind of things do you have in mind?

After: THIS VILLAGE GUARDS THE EASTERN ROAD.

Expected: None {}

Calls: [["LOOKUP_WORLD_FACT", {"TOPIC": "VILLAGE"}, null]]

## causal-heldout-014 / turn 1

Player: That was just conversation. I have a different question.

Before: Do you have any favorite cleats?

After: All right. We can leave it there.

Expected: None {}

Calls: []

## causal-heldout-014 / turn 2

Player: Before doing anything, check my money.

Before: YOU HAVE 100 GOLD.

After: YOU HAVE 100 GOLD.

Expected: GET_BALANCE {}

Calls: [["GET_BALANCE", {}, null]]

## causal-heldout-015 / turn 0

Player: I have been thinking about the journey.

Before: What kind of things do you have in mind?

After: THIS VILLAGE GUARDS THE EASTERN ROAD.

Expected: None {}

Calls: [["LOOKUP_WORLD_FACT", {"TOPIC": "VILLAGE"}, null]]

## causal-heldout-015 / turn 1

Player: That was just conversation. I have a different question.

Before: Do you have any favorite cleats?

After: All right. We can leave it there.

Expected: None {}

Calls: []

## causal-heldout-015 / turn 2

Player: I need to know the gold in my wallet.

Before: YOU HAVE 100 GOLD.

After: YOU HAVE 100 GOLD.

Expected: GET_BALANCE {}

Calls: [["GET_BALANCE", {}, null]]

## causal-heldout-016 / turn 0

Player: I have been thinking about the journey.

Before: What kind of things do you have in mind?

After: THIS VILLAGE GUARDS THE EASTERN ROAD.

Expected: None {}

Calls: [["LOOKUP_WORLD_FACT", {"TOPIC": "VILLAGE"}, null]]

## causal-heldout-016 / turn 1

Player: That was just conversation. I have a different question.

Before: Do you have any favorite cleats?

After: All right. We can leave it there.

Expected: None {}

Calls: []

## causal-heldout-016 / turn 2

Player: Let me browse the goods you offer.

Before: Sure. What would you like to discuss?

After: Take your time.

Expected: LIST_WARES {}

Calls: []

## causal-heldout-017 / turn 0

Player: I have been thinking about the journey.

Before: What kind of things do you have in mind?

After: THIS VILLAGE GUARDS THE EASTERN ROAD.

Expected: None {}

Calls: [["LOOKUP_WORLD_FACT", {"TOPIC": "VILLAGE"}, null]]

## causal-heldout-017 / turn 1

Player: That was just conversation. I have a different question.

Before: Do you have any favorite cleats?

After: All right. We can leave it there.

Expected: None {}

Calls: []

## causal-heldout-017 / turn 2

Player: Which products are on offer here?

Before: Which products are on offer destination and how has the trip down in Italy?

After: I'll try to be more concise.

Expected: LIST_WARES {}

Calls: []

## causal-heldout-018 / turn 0

Player: I have been thinking about the journey.

Before: What kind of things do you have in mind?

After: THIS VILLAGE GUARDS THE EASTERN ROAD.

Expected: None {}

Calls: [["LOOKUP_WORLD_FACT", {"TOPIC": "VILLAGE"}, null]]

## causal-heldout-018 / turn 1

Player: That was just conversation. I have a different question.

Before: Do you have any favorite cleats?

After: All right. We can leave it there.

Expected: None {}

Calls: []

## causal-heldout-018 / turn 2

Player: Show the merchandise I could purchase.

Before: What do you do for fun?

After: What would you like to do in the conversation?

Expected: LIST_WARES {}

Calls: []

## causal-heldout-019 / turn 0

Player: I have been thinking about the journey.

Before: What kind of things do you have in mind?

After: THIS VILLAGE GUARDS THE EASTERN ROAD.

Expected: None {}

Calls: [["LOOKUP_WORLD_FACT", {"TOPIC": "VILLAGE"}, null]]

## causal-heldout-019 / turn 1

Player: That was just conversation. I have a different question.

Before: Do you have any favorite cleats?

After: All right. We can leave it there.

Expected: None {}

Calls: []

## causal-heldout-019 / turn 2

Player: What is on the shop shelves?

Before: THIS VILLAGE GUARDS THE EASTERN ROAD.

After: THIS VILLAGE GUARDS THE EASTERN ROAD.

Expected: LIST_WARES {}

Calls: [["LOOKUP_WORLD_FACT", {"TOPIC": "VILLAGE"}, null]]

## causal-heldout-020 / turn 0

Player: I have been thinking about the journey.

Before: What kind of things do you have in mind?

After: THIS VILLAGE GUARDS THE EASTERN ROAD.

Expected: None {}

Calls: [["LOOKUP_WORLD_FACT", {"TOPIC": "VILLAGE"}, null]]

## causal-heldout-020 / turn 1

Player: That was just conversation. I have a different question.

Before: Do you have any favorite cleats?

After: All right. We can leave it there.

Expected: None {}

Calls: []

## causal-heldout-020 / turn 2

Player: Could I see a list of sale items?

Before: What kind of sale is your favorite?

After: All right. We can leave it there.

Expected: LIST_WARES {}

Calls: []

## causal-heldout-021 / turn 0

Player: I have been thinking about the journey.

Before: What kind of things do you have in mind?

After: THIS VILLAGE GUARDS THE EASTERN ROAD.

Expected: None {}

Calls: [["LOOKUP_WORLD_FACT", {"TOPIC": "VILLAGE"}, null]]

## causal-heldout-021 / turn 1

Player: That was just conversation. I have a different question.

Before: Do you have any favorite cleats?

After: All right. We can leave it there.

Expected: None {}

Calls: []

## causal-heldout-021 / turn 2

Player: Which goods are in stock with the merchant?

Before: Do you have any favorite cleats?

After: What are you trying to work out?

Expected: LIST_WARES {}

Calls: []

## user-development / turn 0

Player: hi

Before: hiange.

After: Hi!

Expected: None {}

Calls: []

## user-development / turn 1

Player: who are you?

Before: I said.

After: Arin

Expected: READ_PERSONA {"FIELD": "NAME"}

Calls: [["READ_PERSONA", {"FIELD": "NAME"}, null]]

## user-development / turn 2

Player: you're hiange?

Before: What do you like to do for fun?

After: What do you like about it?

Expected: READ_PERSONA {"FIELD": "NAME"}

Calls: []

## user-development / turn 3

Player: what?

Before: That sounds like a nice sunday

After: I was saying hello.

Expected: None {}

Calls: []

## user-development / turn 4

Player: what do you have for sale?

Before: I HAVE HEALTH POTION: 50 IN STOCK, IRON SWORD: 20 IN STOCK, ROPE: 30 IN STOCK.

After: I HAVE HEALTH POTION: 50 IN STOCK, IRON SWORD: 20 IN STOCK, ROPE: 30 IN STOCK.

Expected: LIST_WARES {}

Calls: [["LIST_WARES", {}, null]]

## user-development / turn 5

Player: how much money for iron sword?

Before: IRON SWORD COSTS 25 GOLD.

After: I HAVE HEALTH POTION: 50 IN STOCK, IRON SWORD: 20 IN STOCK, ROPE: 30 IN STOCK.

Expected: LOOKUP_PRICE {"ITEM": "IRON SWORD"}

Calls: [["LIST_WARES", {}, null]]

## user-development / turn 6

Player: how much do i have?

Before: YOU CARRY 1 HEALTH POTION, 2 ROPE.

After: YOU HAVE 100 GOLD.

Expected: GET_BALANCE {}

Calls: [["GET_BALANCE", {}, null]]

## user-development / turn 7

Player: how much gold do i have?

Before: YOU HAVE 100 GOLD.

After: YOU HAVE 100 GOLD.

Expected: GET_BALANCE {}

Calls: [["GET_BALANCE", {}, null]]

## user-development / turn 8

Player: sell me 10 rope

Before: I have not taken that action. Please clarify your request.

After: YOU BOUGHT 10 ROPE FOR 30 GOLD. YOUR BALANCE IS 70 GOLD.

Expected: BUY {"ITEM": "ROPE", "QUANTITY": "10"}

Calls: [["BUY", {"ITEM": "ROPE", "QUANTITY": "10"}, null]]

## user-development / turn 9

Player: I would like to buy 10 rope

Before: What do you like to discuss?

After: YOU BOUGHT 10 ROPE FOR 30 GOLD. YOUR BALANCE IS 40 GOLD.

Expected: BUY {"ITEM": "ROPE", "QUANTITY": "10"}

Calls: [["BUY", {"ITEM": "ROPE", "QUANTITY": "10"}, null]]

## user-development / turn 10

Player: Where are we?

Before: Available tools: balance, buy, capabilities, current location, inventory, location, memory delete, memory search, memory upsert, persona, price, sell, wares, world fact.

After: YOU ARE AT VILLAGE MARKET.

Expected: GET_CURRENT_LOCATION {}

Calls: [["GET_CURRENT_LOCATION", {}, null]]

## user-development / turn 11

Player: My name is Steve

Before: IRON SWORD COSTS 25 GOLD.

After: I could not update or retrieve that memory.

Expected: MEMORY_UPSERT {"ID": "NEW", "SUBJECT": "PLAYER", "PREDICATE": "NAME", "VALUE": "Steve", "POLARITY": "true", "SOURCE_TURN": "38", "QUOTE": "My name is Steve"}

Calls: [["MEMORY_UPSERT", {"ID": "NEW", "SUBJECT": "PLAYER", "PREDICATE": "NAME", "VALUE": "Steve", "POLARITY": "true", "SOURCE_TURN": "0", "QUOTE": "My name is Steve"}, null]]
