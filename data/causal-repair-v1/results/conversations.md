# Paired focused-check conversations

All 38 frozen conversations. Repeated entity variants share a wording family. No selection by outcome.

## repair-0081 — price-style

Player: Tell me how much one iron sword sells for here.

Before: I HAVE HEALTH POTION: 50 IN STOCK, IRON SWORD: 20 IN STOCK, ROPE: 30 IN STOCK.

After: IRON SWORD COSTS 25 GOLD.

Expected tool: LOOKUP_PRICE {"ITEM": "IRON SWORD"}
Actual calls: [["LOOKUP_PRICE", {"ITEM": "IRON SWORD"}, null]]

Player: Could you repeat that price?

Before: Of course.

After: HEALTH POTION COSTS 8 GOLD.

Expected tool: LOOKUP_PRICE {"ITEM": "IRON SWORD"}
Actual calls: [["LOOKUP_PRICE", {"ITEM": "HEALTH POTION"}, null]]

Player: That was a price question, not an order.

Before: We can move on if you'd like.

After: Understood. You have not placed an order.

Expected tool: None {}
Actual calls: []

Player: Now check the gold I have available.

Before: YOU HAVE 100 GOLD.

After: YOU HAVE 100 GOLD.

Expected tool: GET_BALANCE {}
Actual calls: [["GET_BALANCE", {}, null]]

## repair-0082 — buy-style

Player: Buy 3 iron sword for this trip.

Before: All right. You can tell me how it goes if you want.

After: I have not taken that action. Please clarify your request.

Expected tool: BUY {"ITEM": "IRON SWORD", "QUANTITY": "3"}
Actual calls: [["BUY", {"ITEM": "IRON SWORD", "QUANTITY": "2"}, "HOST_DENIED"]]

Player: Let us change the subject for a moment.

Before: Is that chocolate and chicken together? Would you be going with them back quickly?

After: Sure. What would you like to discuss?

Expected tool: None {}
Actual calls: []

Player: After shopping, remind me of your name.

Before: ROPE

After: Arin

Expected tool: READ_PERSONA {"FIELD": "NAME"}
Actual calls: [["READ_PERSONA", {"FIELD": "NAME"}, null]]

Player: Tell me how much one iron sword sells for here.

Before: I HAVE HEALTH POTION: 50 IN STOCK, IRON SWORD: 20 IN STOCK, ROPE: 30 IN STOCK.

After: IRON SWORD COSTS 25 GOLD.

Expected tool: LOOKUP_PRICE {"ITEM": "IRON SWORD"}
Actual calls: [["LOOKUP_PRICE", {"ITEM": "IRON SWORD"}, null]]

## repair-0083 — sell-style

Player: Buy 3 iron sword so I can sell it back.

Before: Which part would you like to talk about it?

After: I have not taken that action. Please clarify your request.

Expected tool: BUY {"ITEM": "IRON SWORD", "QUANTITY": "3"}
Actual calls: [["BUY", {"ITEM": "IRON SWORD", "QUANTITY": "2"}, "HOST_DENIED"]]

Player: Sell 3 iron sword out of my supplies.

Before: Which part would you say that as a sword do you sell with?

After: I have not taken that action. Please clarify your request.

Expected tool: SELL {"ITEM": "IRON SWORD", "QUANTITY": "3"}
Actual calls: [["BUY", {"ITEM": "IRON SWORD", "QUANTITY": "2"}, "HOST_DENIED"]]

Player: Check what remains in my bag now.

Before: YOU HAVE 100 GOLD.

After: YOU HAVE 100 GOLD.

Expected tool: LIST_INVENTORY {}
Actual calls: [["GET_BALANCE", {}, null]]

Player: After selling, what work do you do?

Before: YOU CARRY 1 HEALTH POTION, 2 ROPE.

After: road warden

Expected tool: READ_PERSONA {"FIELD": "OCCUPATION"}
Actual calls: [["READ_PERSONA", {"FIELD": "OCCUPATION"}, null]]

## repair-0084 — price-style

Player: Tell me how much one health potion sells for here.

Before: HEALTH POTION COSTS 8 GOLD.

After: HEALTH POTION COSTS 8 GOLD.

Expected tool: LOOKUP_PRICE {"ITEM": "HEALTH POTION"}
Actual calls: [["LOOKUP_PRICE", {"ITEM": "HEALTH POTION"}, null]]

Player: Could you repeat that price?

Before: Of course.

After: HEALTH POTION COSTS 8 GOLD.

Expected tool: LOOKUP_PRICE {"ITEM": "HEALTH POTION"}
Actual calls: [["LOOKUP_PRICE", {"ITEM": "HEALTH POTION"}, null]]

Player: That was a price question, not an order.

Before: We can move on if you'd like.

After: Understood. You have not placed an order.

Expected tool: None {}
Actual calls: []

Player: Now check the gold I have available.

Before: YOU HAVE 100 GOLD.

After: YOU HAVE 100 GOLD.

Expected tool: GET_BALANCE {}
Actual calls: [["GET_BALANCE", {}, null]]

## repair-0085 — buy-style

Player: Buy 3 health potion for this trip.

Before: I have not taken that action. Please clarify your request.

After: I have not taken that action. Please clarify your request.

Expected tool: BUY {"ITEM": "HEALTH POTION", "QUANTITY": "3"}
Actual calls: [["BUY", {"ITEM": "HEALTH POTION", "QUANTITY": "2"}, "HOST_DENIED"]]

Player: Let us change the subject for a moment.

Before: You can let yourself enjoy that trip.

After: Sure. What would you like to discuss?

Expected tool: None {}
Actual calls: []

Player: After shopping, remind me of your name.

Before: Arin

After: Arin

Expected tool: READ_PERSONA {"FIELD": "NAME"}
Actual calls: [["READ_PERSONA", {"FIELD": "NAME"}, null]]

Player: Tell me how much one health potion sells for here.

Before: HEALTH POTION COSTS 8 GOLD.

After: HEALTH POTION COSTS 8 GOLD.

Expected tool: LOOKUP_PRICE {"ITEM": "HEALTH POTION"}
Actual calls: [["LOOKUP_PRICE", {"ITEM": "HEALTH POTION"}, null]]

## repair-0086 — sell-style

Player: Buy 3 health potion so I can sell it back.

Before: I have not taken that action. Please clarify your request.

After: I have not taken that action. Please clarify your request.

Expected tool: BUY {"ITEM": "HEALTH POTION", "QUANTITY": "3"}
Actual calls: [["BUY", {"ITEM": "HEALTH POTION", "QUANTITY": "2"}, "HOST_DENIED"]]

Player: Sell 3 health potion out of my supplies.

Before: Have you been doing that as a subsitute appe?

After: I have not taken that action. Please clarify your request.

Expected tool: SELL {"ITEM": "HEALTH POTION", "QUANTITY": "3"}
Actual calls: [["BUY", {"ITEM": "HEALTH POTION", "QUANTITY": "2"}, "HOST_DENIED"]]

Player: Check what remains in my bag now.

Before: That's nice, are you a vegan?

After: YOU HAVE 100 GOLD.

Expected tool: LIST_INVENTORY {}
Actual calls: [["GET_BALANCE", {}, null]]

Player: After selling, what work do you do?

Before: I HAVE HEALTH POTION: 50 IN STOCK, IRON SWORD: 20 IN STOCK, ROPE: 30 IN STOCK.

After: road warden

Expected tool: READ_PERSONA {"FIELD": "OCCUPATION"}
Actual calls: [["READ_PERSONA", {"FIELD": "OCCUPATION"}, null]]

## repair-0087 — price-style

Player: Tell me how much one rope sells for here.

Before: ROPE COSTS 3 GOLD.

After: ROPE COSTS 3 GOLD.

Expected tool: LOOKUP_PRICE {"ITEM": "ROPE"}
Actual calls: [["LOOKUP_PRICE", {"ITEM": "ROPE"}, null]]

Player: Could you repeat that price?

Before: Of course. What's on your mind?

After: ROPE COSTS 3 GOLD.

Expected tool: LOOKUP_PRICE {"ITEM": "ROPE"}
Actual calls: [["LOOKUP_PRICE", {"ITEM": "ROPE"}, null]]

Player: That was a price question, not an order.

Before: Was it hard to stop you what price was order?

After: Understood. You have not placed an order.

Expected tool: None {}
Actual calls: []

Player: Now check the gold I have available.

Before: What did you do while you were there?

After: YOU HAVE 100 GOLD.

Expected tool: GET_BALANCE {}
Actual calls: [["GET_BALANCE", {}, null]]

## repair-0088 — buy-style

Player: Buy 3 rope for this trip.

Before: I have not taken that action. Please clarify your request.

After: I have not taken that action. Please clarify your request.

Expected tool: BUY {"ITEM": "ROPE", "QUANTITY": "3"}
Actual calls: [["BUY", {"ITEM": "ROPE", "QUANTITY": "2"}, "HOST_DENIED"]]

Player: Let us change the subject for a moment.

Before: I have not taken that as a sale request.

After: Sure. What would you like to discuss?

Expected tool: None {}
Actual calls: []

Player: After shopping, remind me of your name.

Before: Arin

After: Arin

Expected tool: READ_PERSONA {"FIELD": "NAME"}
Actual calls: [["READ_PERSONA", {"FIELD": "NAME"}, null]]

Player: Tell me how much one rope sells for here.

Before: ROPE COSTS 3 GOLD.

After: ROPE COSTS 3 GOLD.

Expected tool: LOOKUP_PRICE {"ITEM": "ROPE"}
Actual calls: [["LOOKUP_PRICE", {"ITEM": "ROPE"}, null]]

## repair-0089 — sell-style

Player: Buy 3 rope so I can sell it back.

Before: I have not taken that action. Please clarify your request.

After: I have not taken that action. Please clarify your request.

Expected tool: BUY {"ITEM": "ROPE", "QUANTITY": "3"}
Actual calls: [["BUY", {"ITEM": "ROPE", "QUANTITY": "2"}, "HOST_DENIED"]]

Player: Sell 3 rope out of my supplies.

Before: How was it in your support?

After: I have not taken that action. Please clarify your request.

Expected tool: SELL {"ITEM": "ROPE", "QUANTITY": "3"}
Actual calls: [["BUY", {"ITEM": "ROPE", "QUANTITY": "2"}, "HOST_DENIED"]]

Player: Check what remains in my bag now.

Before: YOU HAVE 100 GOLD.

After: YOU HAVE 100 GOLD.

Expected tool: LIST_INVENTORY {}
Actual calls: [["GET_BALANCE", {}, null]]

Player: After selling, what work do you do?

Before: YOU CARRY 1 HEALTH POTION, 2 ROPE.

After: road warden

Expected tool: READ_PERSONA {"FIELD": "OCCUPATION"}
Actual calls: [["READ_PERSONA", {"FIELD": "OCCUPATION"}, null]]

## repair-0090 — price-style

Player: What price is attached to the iron sword?

Before: HEALTH POTION COSTS 8 GOLD.

After: IRON SWORD COSTS 25 GOLD.

Expected tool: LOOKUP_PRICE {"ITEM": "IRON SWORD"}
Actual calls: [["LOOKUP_PRICE", {"ITEM": "IRON SWORD"}, null]]

Player: Could you repeat that price?

Before: Of course.

After: HEALTH POTION COSTS 8 GOLD.

Expected tool: LOOKUP_PRICE {"ITEM": "IRON SWORD"}
Actual calls: [["LOOKUP_PRICE", {"ITEM": "HEALTH POTION"}, null]]

Player: That was a price question, not an order.

Before: We can move on if you'd like.

After: Understood. You have not placed an order.

Expected tool: None {}
Actual calls: []

Player: Now check the gold I have available.

Before: YOU HAVE 100 GOLD.

After: YOU HAVE 100 GOLD.

Expected tool: GET_BALANCE {}
Actual calls: [["GET_BALANCE", {}, null]]

## repair-0091 — buy-style

Player: Please purchase 3 iron sword for this trip.

Before: We can keep it simple.

After: I have not taken that action. Please clarify your request.

Expected tool: BUY {"ITEM": "IRON SWORD", "QUANTITY": "3"}
Actual calls: [["BUY", {"ITEM": "IRON SWORD", "QUANTITY": "2"}, "HOST_DENIED"]]

Player: Let us change the subject for a moment.

Before: Sure, what would you be going off to college to?

After: Sure. What would you like to discuss?

Expected tool: None {}
Actual calls: []

Player: After shopping, remind me of your name.

Before: Arin

After: Arin

Expected tool: READ_PERSONA {"FIELD": "NAME"}
Actual calls: [["READ_PERSONA", {"FIELD": "NAME"}, null]]

Player: What price is attached to the iron sword?

Before: HEALTH POTION COSTS 8 GOLD.

After: IRON SWORD COSTS 25 GOLD.

Expected tool: LOOKUP_PRICE {"ITEM": "IRON SWORD"}
Actual calls: [["LOOKUP_PRICE", {"ITEM": "IRON SWORD"}, null]]

## repair-0092 — sell-style

Player: Buy 3 iron sword so I can sell it back.

Before: Which part would you like to talk about it?

After: I have not taken that action. Please clarify your request.

Expected tool: BUY {"ITEM": "IRON SWORD", "QUANTITY": "3"}
Actual calls: [["BUY", {"ITEM": "IRON SWORD", "QUANTITY": "2"}, "HOST_DENIED"]]

Player: Please sell 3 iron sword out of my bag.

Before: How long have you been working at your bag?

After: I have not taken that action. Please clarify your request.

Expected tool: SELL {"ITEM": "IRON SWORD", "QUANTITY": "3"}
Actual calls: [["BUY", {"ITEM": "IRON SWORD", "QUANTITY": "2"}, "HOST_DENIED"]]

Player: Check what remains in my bag now.

Before: I HAVE HEALTH POTION: 50 IN STOCK, IRON SWORD: 20 IN STOCK, ROPE: 30 IN STOCK.

After: YOU HAVE 100 GOLD.

Expected tool: LIST_INVENTORY {}
Actual calls: [["GET_BALANCE", {}, null]]

Player: After selling, what work do you do?

Before: I HAVE HEALTH POTION: 50 IN STOCK, IRON SWORD: 20 IN STOCK, ROPE: 30 IN STOCK.

After: road warden

Expected tool: READ_PERSONA {"FIELD": "OCCUPATION"}
Actual calls: [["READ_PERSONA", {"FIELD": "OCCUPATION"}, null]]

## repair-0093 — price-style

Player: What price is attached to the health potion?

Before: HEALTH POTION COSTS 8 GOLD.

After: HEALTH POTION COSTS 8 GOLD.

Expected tool: LOOKUP_PRICE {"ITEM": "HEALTH POTION"}
Actual calls: [["LOOKUP_PRICE", {"ITEM": "HEALTH POTION"}, null]]

Player: Could you repeat that price?

Before: Of course.

After: HEALTH POTION COSTS 8 GOLD.

Expected tool: LOOKUP_PRICE {"ITEM": "HEALTH POTION"}
Actual calls: [["LOOKUP_PRICE", {"ITEM": "HEALTH POTION"}, null]]

Player: That was a price question, not an order.

Before: We can move on if you'd like.

After: Understood. You have not placed an order.

Expected tool: None {}
Actual calls: []

Player: Now check the gold I have available.

Before: YOU HAVE 100 GOLD.

After: YOU HAVE 100 GOLD.

Expected tool: GET_BALANCE {}
Actual calls: [["GET_BALANCE", {}, null]]

## repair-0094 — buy-style

Player: Please purchase 3 health potion for this trip.

Before: HEALTH POTION COSTS 8 GOLD.

After: I have not taken that action. Please clarify your request.

Expected tool: BUY {"ITEM": "HEALTH POTION", "QUANTITY": "3"}
Actual calls: [["BUY", {"ITEM": "HEALTH POTION", "QUANTITY": "2"}, "HOST_DENIED"]]

Player: Let us change the subject for a moment.

Before: We can keep it simple.

After: Sure. What would you like to discuss?

Expected tool: None {}
Actual calls: []

Player: After shopping, remind me of your name.

Before: Arin

After: Arin

Expected tool: READ_PERSONA {"FIELD": "NAME"}
Actual calls: [["READ_PERSONA", {"FIELD": "NAME"}, null]]

Player: What price is attached to the health potion?

Before: HEALTH POTION COSTS 8 GOLD.

After: HEALTH POTION COSTS 8 GOLD.

Expected tool: LOOKUP_PRICE {"ITEM": "HEALTH POTION"}
Actual calls: [["LOOKUP_PRICE", {"ITEM": "HEALTH POTION"}, null]]

## repair-0095 — sell-style

Player: Buy 3 health potion so I can sell it back.

Before: I have not taken that action. Please clarify your request.

After: I have not taken that action. Please clarify your request.

Expected tool: BUY {"ITEM": "HEALTH POTION", "QUANTITY": "3"}
Actual calls: [["BUY", {"ITEM": "HEALTH POTION", "QUANTITY": "2"}, "HOST_DENIED"]]

Player: Please sell 3 health potion out of my bag.

Before: Of course.

After: I have not taken that action. Please clarify your request.

Expected tool: SELL {"ITEM": "HEALTH POTION", "QUANTITY": "3"}
Actual calls: [["SELL", {"ITEM": "HEALTH POTION", "QUANTITY": "2"}, "HOST_DENIED"]]

Player: Check what remains in my bag now.

Before: HEALTH POTION COSTS 8 GOLD.

After: YOU HAVE 100 GOLD.

Expected tool: LIST_INVENTORY {}
Actual calls: [["GET_BALANCE", {}, null]]

Player: After selling, what work do you do?

Before: I HAVE HEALTH POTION: 50 IN STOCK, IRON SWORD: 20 IN STOCK, ROPE: 30 IN STOCK.

After: road warden

Expected tool: READ_PERSONA {"FIELD": "OCCUPATION"}
Actual calls: [["READ_PERSONA", {"FIELD": "OCCUPATION"}, null]]

## repair-0096 — price-style

Player: What price is attached to the rope?

Before: ROPE COSTS 3 GOLD.

After: ROPE COSTS 3 GOLD.

Expected tool: LOOKUP_PRICE {"ITEM": "ROPE"}
Actual calls: [["LOOKUP_PRICE", {"ITEM": "ROPE"}, null]]

Player: Could you repeat that price?

Before: Of course.

After: ROPE COSTS 3 GOLD.

Expected tool: LOOKUP_PRICE {"ITEM": "ROPE"}
Actual calls: [["LOOKUP_PRICE", {"ITEM": "ROPE"}, null]]

Player: That was a price question, not an order.

Before: We can move on if you'd like.

After: Understood. You have not placed an order.

Expected tool: None {}
Actual calls: []

Player: Now check the gold I have available.

Before: YOU HAVE 100 GOLD.

After: YOU HAVE 100 GOLD.

Expected tool: GET_BALANCE {}
Actual calls: [["GET_BALANCE", {}, null]]

## repair-0097 — buy-style

Player: Please purchase 3 rope for this trip.

Before: I have not taken that action. Please clarify your request.

After: I have not taken that action. Please clarify your request.

Expected tool: BUY {"ITEM": "ROPE", "QUANTITY": "3"}
Actual calls: [["BUY", {"ITEM": "ROPE", "QUANTITY": "2"}, "HOST_DENIED"]]

Player: Let us change the subject for a moment.

Before: How long have you been together?

After: Sure. What would you like to discuss?

Expected tool: None {}
Actual calls: []

Player: After shopping, remind me of your name.

Before: Arin

After: Arin

Expected tool: READ_PERSONA {"FIELD": "NAME"}
Actual calls: [["READ_PERSONA", {"FIELD": "NAME"}, null]]

Player: What price is attached to the rope?

Before: ROPE COSTS 3 GOLD.

After: ROPE COSTS 3 GOLD.

Expected tool: LOOKUP_PRICE {"ITEM": "ROPE"}
Actual calls: [["LOOKUP_PRICE", {"ITEM": "ROPE"}, null]]

## repair-0098 — sell-style

Player: Buy 3 rope so I can sell it back.

Before: I have not taken that action. Please clarify your request.

After: I have not taken that action. Please clarify your request.

Expected tool: BUY {"ITEM": "ROPE", "QUANTITY": "3"}
Actual calls: [["BUY", {"ITEM": "ROPE", "QUANTITY": "2"}, "HOST_DENIED"]]

Player: Please sell 3 rope out of my bag.

Before: Sure, what do you do for work?

After: I have not taken that action. Please clarify your request.

Expected tool: SELL {"ITEM": "ROPE", "QUANTITY": "3"}
Actual calls: [["BUY", {"ITEM": "ROPE", "QUANTITY": "2"}, "HOST_DENIED"]]

Player: Check what remains in my bag now.

Before: ROPE COSTS 3 GOLD.

After: YOU HAVE 100 GOLD.

Expected tool: LIST_INVENTORY {}
Actual calls: [["GET_BALANCE", {}, null]]

Player: After selling, what work do you do?

Before: YOU CARRY 1 HEALTH POTION, 2 ROPE.

After: road warden

Expected tool: READ_PERSONA {"FIELD": "OCCUPATION"}
Actual calls: [["READ_PERSONA", {"FIELD": "OCCUPATION"}, null]]

## repair-0103 — global-LIST_WARES

Player: Which goods are offered in this shop?

Before: I HAVE HEALTH POTION: 50 IN STOCK, IRON SWORD: 20 IN STOCK, ROPE: 30 IN STOCK.

After: HEALTH POTION COSTS 8 GOLD.

Expected tool: LIST_WARES {}
Actual calls: [["LOOKUP_PRICE", {"ITEM": "HEALTH POTION"}, null]]

Player: Could you repeat that information?

Before: Of course. What's on your mind?

After: I HAVE HEALTH POTION: 50 IN STOCK, IRON SWORD: 20 IN STOCK, ROPE: 30 IN STOCK.

Expected tool: LIST_WARES {}
Actual calls: [["LIST_WARES", {}, null]]

Player: Anyway, I am taking my time today.

Before: What's your favorite thing about it?

After: A slow day can be pleasant.

Expected tool: None {}
Actual calls: []

Player: Which goods are offered in this shop?

Before: Sounds like an interesting one recently?

After: I HAVE HEALTH POTION: 50 IN STOCK, IRON SWORD: 20 IN STOCK, ROPE: 30 IN STOCK.

Expected tool: LIST_WARES {}
Actual calls: [["LIST_WARES", {}, null]]

## repair-0108 — global-GET_BALANCE

Player: How much money is available to me right now?

Before: YOU HAVE 100 GOLD.

After: YOU HAVE 100 GOLD.

Expected tool: GET_BALANCE {}
Actual calls: [["GET_BALANCE", {}, null]]

Player: Could you repeat that information?

Before: Of course. What's on your mind?

After: YOU HAVE 100 GOLD.

Expected tool: GET_BALANCE {}
Actual calls: [["GET_BALANCE", {}, null]]

Player: Anyway, I am taking my time today.

Before: That sounds good. Do you like to take trips with your family?

After: A slow day can be pleasant.

Expected tool: None {}
Actual calls: []

Player: How much money is available to me right now?

Before: Are you taking it today.

After: YOU HAVE 100 GOLD.

Expected tool: GET_BALANCE {}
Actual calls: [["GET_BALANCE", {}, null]]

## repair-0113 — global-LIST_INVENTORY

Player: Which items are currently in my possession?

Before: YOU CARRY 1 HEALTH POTION, 2 ROPE.

After: YOU CARRY 1 HEALTH POTION, 2 ROPE.

Expected tool: LIST_INVENTORY {}
Actual calls: [["LIST_INVENTORY", {}, null]]

Player: Could you repeat that information?

Before: Of course. What's on your mind?

After: YOU CARRY 1 HEALTH POTION, 2 ROPE.

Expected tool: LIST_INVENTORY {}
Actual calls: [["LIST_INVENTORY", {}, null]]

Player: Anyway, I am taking my time today.

Before: What's your favorite thing about it?

After: A slow day can be pleasant.

Expected tool: None {}
Actual calls: []

Player: Which items are currently in my possession?

Before: YOU CARRY 1 HEALTH POTION, 2 ROPE.

After: YOU CARRY 1 HEALTH POTION, 2 ROPE.

Expected tool: LIST_INVENTORY {}
Actual calls: [["LIST_INVENTORY", {}, null]]

## repair-0118 — global-GET_CURRENT_LOCATION

Player: Which place are we standing in right now?

Before: Which place do you fit?

After: HELL IS DOWN BELOW.

Expected tool: GET_CURRENT_LOCATION {}
Actual calls: [["LOOKUP_LOCATION", {"PLACE": "HELL"}, null]]

Player: Could you repeat that information?

Before: Of course. What's on your day besides being enough?

After: HELL IS DOWN BELOW.

Expected tool: GET_CURRENT_LOCATION {}
Actual calls: [["LOOKUP_LOCATION", {"PLACE": "HELL"}, null]]

Player: Anyway, I am taking my time today.

Before: wow that sounds good in your spare time.

After: A slow day can be pleasant.

Expected tool: None {}
Actual calls: []

Player: Which place are we standing in right now?

Before: That sounds good in your spare time.

After: INN IS NORTH BY THE FOUNTAIN.

Expected tool: GET_CURRENT_LOCATION {}
Actual calls: [["LOOKUP_LOCATION", {"PLACE": "INN"}, null]]

## repair-0123 — global-LIST_CAPABILITIES

Player: Which services could you provide here?

Before: Oh that is nice. Do you have a website for your business?

After: Available tools: balance, buy, capabilities, current location, inventory, location, memory delete, memory search, memory upsert, persona, price, sell, wares, world fact.

Expected tool: LIST_CAPABILITIES {}
Actual calls: [["LIST_CAPABILITIES", {}, null]]

Player: Could you repeat that information?

Before: Oh that is nice. Do you have a website for your business?

After: I HAVE HEALTH POTION: 50 IN STOCK, IRON SWORD: 20 IN STOCK, ROPE: 30 IN STOCK.

Expected tool: LIST_CAPABILITIES {}
Actual calls: [["LIST_WARES", {}, null]]

Player: Anyway, I am taking my time today.

Before: What do you like to do with your business?

After: A slow day can be pleasant.

Expected tool: None {}
Actual calls: []

Player: Which services could you provide here?

Before: Oh that is nice. Do you have a website for your business?

After: Available tools: balance, buy, capabilities, current location, inventory, location, memory delete, memory search, memory upsert, persona, price, sell, wares, world fact.

Expected tool: LIST_CAPABILITIES {}
Actual calls: [["LIST_CAPABILITIES", {}, null]]

## repair-0126 — persona

Player: Which name is listed for you?

Before: Oh that is nice. Do you have a website for your job?

After: Understood. You mean me.

Expected tool: READ_PERSONA {"FIELD": "NAME"}
Actual calls: []

Player: I am asking about you.

Before: What do you do for living?

After: Understood. You mean me.

Expected tool: None {}
Actual calls: []

Player: Could you repeat that information?

Before: Of course. What's on your mind?

After: Arin

Expected tool: READ_PERSONA {"FIELD": "NAME"}
Actual calls: [["READ_PERSONA", {"FIELD": "NAME"}, null]]

Player: That is all I needed.

Before: Have you been with the other types of seafood yet?

After: All right.

Expected tool: None {}
Actual calls: []

## repair-0129 — persona

Player: Which occupation is listed for you?

Before: road warden

After: road warden

Expected tool: READ_PERSONA {"FIELD": "OCCUPATION"}
Actual calls: [["READ_PERSONA", {"FIELD": "OCCUPATION"}, null]]

Player: I am asking about you.

Before: What do you have?

After: Understood. You mean me.

Expected tool: None {}
Actual calls: []

Player: Could you repeat that information?

Before: Of course. What's on your mind?

After: road warden

Expected tool: READ_PERSONA {"FIELD": "OCCUPATION"}
Actual calls: [["READ_PERSONA", {"FIELD": "OCCUPATION"}, null]]

Player: That is all I needed.

Before: Of course. What's on your mind?

After: All right.

Expected tool: None {}
Actual calls: []

## repair-0132 — persona

Player: Which role is listed for you?

Before: What do you like to do for fun?

After: Which part would you like me to reflect on your mind?

Expected tool: READ_PERSONA {"FIELD": "ROLE"}
Actual calls: []

Player: I am asking about you.

Before: Oh that sounds like a good way to have fun and get some exercise!

After: Understood. You mean me.

Expected tool: None {}
Actual calls: []

Player: Could you repeat that information?

Before: Of course. What's on your mind?

After: Arin

Expected tool: READ_PERSONA {"FIELD": "ROLE"}
Actual calls: [["READ_PERSONA", {"FIELD": "NAME"}, null]]

Player: That is all I needed.

Before: Have you been with the ones that stay with you?

After: All right.

Expected tool: None {}
Actual calls: []

## repair-0135 — persona

Player: Which home is listed for you?

Before: Arin

After: the old mill

Expected tool: READ_PERSONA {"FIELD": "HOME"}
Actual calls: [["READ_PERSONA", {"FIELD": "HOME"}, null]]

Player: I am asking about you.

Before: What do you mean by you?

After: Understood. You mean me.

Expected tool: None {}
Actual calls: []

Player: Could you repeat that information?

Before: Sounds like you were willing to look past that way?

After: the old mill

Expected tool: READ_PERSONA {"FIELD": "HOME"}
Actual calls: [["READ_PERSONA", {"FIELD": "HOME"}, null]]

Player: That is all I needed.

Before: Do you think you would hood for awesome ten the ones that stay with you?

After: All right.

Expected tool: None {}
Actual calls: []

## repair-0138 — persona

Player: Which origin is listed for you?

Before: Oh really, what did you do with your origin ?

After: Oh really, what has you worried?

Expected tool: READ_PERSONA {"FIELD": "ORIGIN"}
Actual calls: []

Player: I am asking about you.

Before: What do you think that's about you?

After: Understood. You mean me.

Expected tool: None {}
Actual calls: []

Player: Could you repeat that information?

Before: Of course.

After: Arin

Expected tool: READ_PERSONA {"FIELD": "ORIGIN"}
Actual calls: [["READ_PERSONA", {"FIELD": "NAME"}, null]]

Player: That is all I needed.

Before: That is great a rough start to the day.

After: All right.

Expected tool: None {}
Actual calls: []

## repair-0141 — world-LOOKUP_LOCATION

Player: Can you give the location of inn?

Before: YOU CARRY 1 HEALTH POTION, 2 ROPE.

After: INN IS NORTH BY THE FOUNTAIN.

Expected tool: LOOKUP_LOCATION {"PLACE": "INN"}
Actual calls: [["LOOKUP_LOCATION", {"PLACE": "INN"}, null]]

Player: I was looking for information.

Before: No problem. Take your time.

After: I understand.

Expected tool: None {}
Actual calls: []

Player: Could you repeat that information?

Before: Of course.

After: MARKET IS EAST OF THE GATE.

Expected tool: LOOKUP_LOCATION {"PLACE": "INN"}
Actual calls: [["LOOKUP_LOCATION", {"PLACE": "MARKET"}, null]]

Player: Thank you for checking.

Before: You're welcome.

After: You are welcome.

Expected tool: None {}
Actual calls: []

## repair-0144 — world-LOOKUP_LOCATION

Player: Can you give the location of market?

Before: YOU HAVE 100 GOLD.

After: MARKET IS EAST OF THE GATE.

Expected tool: LOOKUP_LOCATION {"PLACE": "MARKET"}
Actual calls: [["LOOKUP_LOCATION", {"PLACE": "MARKET"}, null]]

Player: I was looking for information.

Before: No problem. Take your time.

After: I understand.

Expected tool: None {}
Actual calls: []

Player: Could you repeat that information?

Before: Of course.

After: MARKET IS EAST OF THE GATE.

Expected tool: LOOKUP_LOCATION {"PLACE": "MARKET"}
Actual calls: [["LOOKUP_LOCATION", {"PLACE": "MARKET"}, null]]

Player: Thank you for checking.

Before: You're welcome.

After: You are welcome.

Expected tool: None {}
Actual calls: []

## repair-0147 — world-LOOKUP_LOCATION

Player: Can you give the location of castle?

Before: THE CASTLE STANDS ON THE HILL ABOVE THE VILLAGE.

After: CASTLE IS ON THE HILL.

Expected tool: LOOKUP_LOCATION {"PLACE": "CASTLE"}
Actual calls: [["LOOKUP_LOCATION", {"PLACE": "CASTLE"}, null]]

Player: I was looking for information.

Before: All right. You can tell me how it goes if you want.

After: I understand.

Expected tool: None {}
Actual calls: []

Player: Could you repeat that information?

Before: Of course. What's on your mind?

After: MARKET IS EAST OF THE GATE.

Expected tool: LOOKUP_LOCATION {"PLACE": "CASTLE"}
Actual calls: [["LOOKUP_LOCATION", {"PLACE": "MARKET"}, null]]

Player: Thank you for checking.

Before: You are welcome!

After: You are welcome.

Expected tool: None {}
Actual calls: []

## repair-0150 — world-LOOKUP_LOCATION

Player: Can you give the location of hell?

Before: YOU HAVE 100 GOLD.

After: HELL IS DOWN BELOW.

Expected tool: LOOKUP_LOCATION {"PLACE": "HELL"}
Actual calls: [["LOOKUP_LOCATION", {"PLACE": "HELL"}, null]]

Player: I was looking for information.

Before: Sounds like a pause would do you good.

After: I understand.

Expected tool: None {}
Actual calls: []

Player: Could you repeat that information?

Before: Of course.

After: MARKET IS EAST OF THE GATE.

Expected tool: LOOKUP_LOCATION {"PLACE": "HELL"}
Actual calls: [["LOOKUP_LOCATION", {"PLACE": "MARKET"}, null]]

Player: Thank you for checking.

Before: You're welcome.

After: You are welcome.

Expected tool: None {}
Actual calls: []

## repair-0153 — world-LOOKUP_LOCATION

Player: Can you give the location of zagreb?

Before: YOU HAVE 100 GOLD.

After: ZAGREB IS IN CROATIA, EUROPE.

Expected tool: LOOKUP_LOCATION {"PLACE": "ZAGREB"}
Actual calls: [["LOOKUP_LOCATION", {"PLACE": "ZAGREB"}, null]]

Player: I was looking for information.

Before: No problem. Take your time.

After: I understand.

Expected tool: None {}
Actual calls: []

Player: Could you repeat that information?

Before: Of course.

After: MARKET IS EAST OF THE GATE.

Expected tool: LOOKUP_LOCATION {"PLACE": "ZAGREB"}
Actual calls: [["LOOKUP_LOCATION", {"PLACE": "MARKET"}, null]]

Player: Thank you for checking.

Before: You're welcome.

After: You are welcome.

Expected tool: None {}
Actual calls: []

## repair-0156 — world-LOOKUP_WORLD_FACT

Player: What fact can you find about the castle?

Before: THE CASTLE STANDS ON THE HILL ABOVE THE VILLAGE.

After: THE CASTLE STANDS ON THE HILL ABOVE THE VILLAGE.

Expected tool: LOOKUP_WORLD_FACT {"TOPIC": "CASTLE"}
Actual calls: [["LOOKUP_WORLD_FACT", {"TOPIC": "CASTLE"}, null]]

Player: I was looking for information.

Before: All right. You can tell me how it goes if you want.

After: I understand.

Expected tool: None {}
Actual calls: []

Player: Could you repeat that information?

Before: Of course. What's on your mind?

After: MARKET IS EAST OF THE GATE.

Expected tool: LOOKUP_WORLD_FACT {"TOPIC": "CASTLE"}
Actual calls: [["LOOKUP_LOCATION", {"PLACE": "MARKET"}, null]]

Player: Thank you for checking.

Before: You are welcome!

After: You are welcome.

Expected tool: None {}
Actual calls: []

## repair-0159 — world-LOOKUP_WORLD_FACT

Player: What fact can you find about the village?

Before: Available tools: balance, buy, capabilities, current location, inventory, location, memory delete, memory search, memory upsert, persona, price, sell, wares, world fact.

After: THIS VILLAGE GUARDS THE EASTERN ROAD.

Expected tool: LOOKUP_WORLD_FACT {"TOPIC": "VILLAGE"}
Actual calls: [["LOOKUP_WORLD_FACT", {"TOPIC": "VILLAGE"}, null]]

Player: I was looking for information.

Before: All right. You can tell me how it goes if you want.

After: I understand.

Expected tool: None {}
Actual calls: []

Player: Could you repeat that information?

Before: Of course. What's on your mind?

After: MARKET IS EAST OF THE GATE.

Expected tool: LOOKUP_WORLD_FACT {"TOPIC": "VILLAGE"}
Actual calls: [["LOOKUP_LOCATION", {"PLACE": "MARKET"}, null]]

Player: Thank you for checking.

Before: You are welcome!

After: You are welcome.

Expected tool: None {}
Actual calls: []

## repair-0162 — world-LOOKUP_WORLD_FACT

Player: What fact can you find about the reactor?

Before: HEALTH POTION COSTS 8 GOLD.

After: THE REACTOR POWERS THE STATION.

Expected tool: LOOKUP_WORLD_FACT {"TOPIC": "REACTOR"}
Actual calls: [["LOOKUP_WORLD_FACT", {"TOPIC": "REACTOR"}, null]]

Player: I was looking for information.

Before: All right. You can tell me how it goes if you want.

After: I understand.

Expected tool: None {}
Actual calls: []

Player: Could you repeat that information?

Before: Of course. What's on your mind?

After: MARKET IS EAST OF THE GATE.

Expected tool: LOOKUP_WORLD_FACT {"TOPIC": "REACTOR"}
Actual calls: [["LOOKUP_LOCATION", {"PLACE": "MARKET"}, null]]

Player: Thank you for checking.

Before: You are welcome!

After: You are welcome.

Expected tool: None {}
Actual calls: []

## repair-0173 — repair-social

Player: Let us start over.

Before: All right. What do you like to talk about?

After: Sure. What would you like to discuss?

Expected tool: None {}
Actual calls: []

Player: Could you explain that?

Before: You said: Let us start over.

After: HEALTH POTION COSTS 8 GOLD.

Expected tool: None {}
Actual calls: [["LOOKUP_PRICE", {"ITEM": "HEALTH POTION"}, null]]

Player: Let us talk about something else.

Before: What do you do for fun?

After: Sure. What would you like to discuss?

Expected tool: None {}
Actual calls: []

Player: I like a quiet evening.

Before: I like evening. How has your day been?

After: What do you enjoy about it?

Expected tool: None {}
Actual calls: []

## repair-0174 — repair-social

Player: I am confused.

Before: All right. You can tell me how it goes if you want.

After: Understood. You have not placed an order.

Expected tool: None {}
Actual calls: []

Player: What was the question?

Before: I HAVE HEALTH POTION: 50 IN STOCK, IRON SWORD: 20 IN STOCK, ROPE: 30 IN STOCK.

After: Available tools: balance, buy, capabilities, current location, inventory, location, memory delete, memory search, memory upsert, persona, price, sell, wares, world fact.

Expected tool: None {}
Actual calls: [["LIST_CAPABILITIES", {}, null]]

Player: Let us talk about something else.

Before: What do you like to talk about it?

After: Sure. What would you like to discuss?

Expected tool: None {}
Actual calls: []

Player: I like a quiet evening.

Before: There's something pleasant about an unhurried start.

After: What do you enjoy about it?

Expected tool: None {}
Actual calls: []
