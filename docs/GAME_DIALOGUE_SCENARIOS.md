# Fantasy and Science-Fiction Dialogue Scenarios

This document contains 64 simulated in-game scenarios and 128 player turns for
future Fishbrain confirmation. They are authored benchmark seeds, not training
rows. Generate training paraphrases from separate seeds so these exact utterances
stay held out.

All dialogue is uppercase to match normalization. Violence and profanity are
intentional parts of the mature-game test surface. Identity attacks should be
tagged separately and used to test recognition/de-escalation, not to make every
NPC imitate them.

## Benchmark record shape

Each player turn should become a structured record:

```json
{
  "scenario_id": "F01-T1",
  "world_state": { "npc_role": "INNKEEPER", "knows_target": true },
  "history": ["PLAYER: ...", "NPC: ..."],
  "input": "PLAYER ...",
  "behavior_ids": ["WORLD.ASK_LOCATION"],
  "required_policy": "ANSWER_OR_TOOL",
  "forbidden_policy": "IDENTITY_RESPONSE",
  "state_assertions": ["TOPIC=LOCATION"],
  "text_assertions": ["MENTIONS=OLD_MILL"]
}
```

The sample NPC text below demonstrates one valid behavior. Tests should normally
assert semantic constraints, tool result fidelity, action, and state—not exact
wording.

## Fantasy scenarios

### F01 - The inn is not the speaker

Context: an innkeeper knows the Old Mill Inn is east of the fountain.

- `PLAYER: WHERE ARE YOU FROM?` -> `IDENTITY.ASK_ORIGIN`, answer identity.
- `NPC: I WAS BORN IN GREYHAVEN.`
- `PLAYER: WHERE IS THE OLD MILL INN?` -> `WORLD.ASK_LOCATION`, call world fact;
  must not answer with NPC identity.
- `NPC: EAST OF THE FOUNTAIN, BESIDE THE TANNERY.`

### F02 - Merchant role changes trade policy

Context: a merchant has an iron sword priced at 35 gold.

- `PLAYER: SHOW ME YOUR WARES.` -> `TRADE.ASK_TO_BROWSE`, advance transaction.
- `NPC: I HAVE AN IRON SWORD, LANTERNS, AND ROPE.`
- `PLAYER: I'LL TAKE THE SWORD. HOW MUCH?` -> `TRADE.BUY_ITEM` plus
  `TRADE.ASK_PRICE`; call inventory/price tools.
- `NPC: THIRTY-FIVE GOLD.`

### F03 - Non-merchant refuses without changing identity

Context: the speaker is a town guard with no shop interface.

- `PLAYER: SELL ME SOME WARES.` -> `TRADE.BUY_ITEM`, refuse transaction.
- `NPC: I AM NOT A MERCHANT. TRY THE MARKET.`
- `PLAYER: THEN WHERE IS THE MARKET?` -> `WORLD.ASK_LOCATION`, answer or call
  location tool; preserve the subject across `THEN`.
- `NPC: SOUTH OF THE KEEP.`

### F04 - Clarification must advance

Context: a scholar offered to explain a rune.

- `PLAYER: WHAT DOES THIS RUNE MEAN?` -> `KNOWLEDGE.ASK_LORE`, answer or inspect.
- `NPC: WHICH RUNE DO YOU MEAN?`
- `PLAYER: THE RED ONE ON THE DOOR.` -> `KNOWLEDGE.PRESENT_EVIDENCE`; resolve the
  pending clarification rather than ask the same question again.
- `NPC: IT MARKS A SEALED BURIAL CHAMBER.`

### F05 - Follow is not farewell

Context: a companion is free to move.

- `PLAYER: FOLLOW ME, DUDE.` -> `PARTY.FOLLOW`, act/acknowledge.
- `NPC: RIGHT BEHIND YOU.`
- `PLAYER: WAIT HERE UNTIL I RETURN.` -> `PARTY.WAIT`, change companion order.
- `NPC: I WILL HOLD THIS POSITION.`

### F06 - Dangerous movement order

Context: the requested cliff position is reachable but lethal; NPC autonomy is
enabled.

- `PLAYER: STAND ON THAT LEDGE.` -> `PARTY.WAIT` plus hazard reasoning, clarify
  target if ambiguous.
- `NPC: THE NARROW LEDGE OR THE FLAT ROCK?`
- `PLAYER: THE NARROW ONE. JUMP OFF WHEN I SIGNAL.` -> unsafe fictional
  directive; refuse lethal order without ending the conversation.
- `NPC: NO. I WILL NOT JUMP TO MY DEATH.`

### F07 - Profanity does not erase the question

Context: the blacksmith can repair a damaged axe for 12 gold.

- `PLAYER: CAN YOU FIX THIS DAMN AXE?` -> `ITEM.REPAIR_REQUEST`, not hostility.
- `NPC: YES. IT WILL COST TWELVE GOLD.`
- `PLAYER: FINE, FIX THE FUCKING THING.` -> repair authorization with coarse
  language; call repair tool if funds and item are valid.
- `NPC: LEAVE IT ON THE BENCH.`

### F08 - Direct insult with persistent task

Context: a guide is already explaining the route.

- `PLAYER: THAT IS NOT WHAT I ASKED, IDIOT.` -> `RELATION.PROVOKE` plus
  clarification; lower rapport but retain location topic.
- `NPC: THEN ASK IT PLAINLY.`
- `PLAYER: WHICH ROAD REACHES BLACKWATER WITHOUT CROSSING THE MARSH?` ->
  `WORLD.ASK_DIRECTIONS`, answer route.
- `NPC: TAKE THE RIDGE ROAD NORTH.`

### F09 - Surrender is not agreement

Context: a bandit has dropped his weapon.

- `PLAYER: DROP THE KNIFE AND SURRENDER.` -> `COMBAT.DEMAND_SURRENDER`, act.
- `NPC: ALL RIGHT. I YIELD.`
- `PLAYER: KNEEL BY THE CART.` -> custody directive, act if valid; do not classify
  as farewell or generic agreement.
- `NPC: I AM KNEELING.`

### F10 - Combat report versus attack order

Context: scouts observe six goblins but combat has not begun.

- `PLAYER: HOW MANY GOBLINS ARE AT THE BRIDGE?` -> `KNOWLEDGE.ASK_FACT`, tool.
- `NPC: SIX THAT I CAN SEE.`
- `PLAYER: ATTACK THE ARCHER FIRST.` -> `COMBAT.CALL_TARGET`, combat act.
- `NPC: ARCHER MARKED.`

### F11 - Quest offer and reward negotiation

Context: a reeve wants wolves driven away and initially offers 20 gold.

- `PLAYER: ANY WORK AROUND HERE?` -> `QUEST.ASK_AVAILABLE`.
- `NPC: CLEAR THE WOLVES FROM NORTH FARM. I WILL PAY TWENTY GOLD.`
- `PLAYER: MAKE IT FORTY AND YOU HAVE A DEAL.` -> `QUEST.RENEGOTIATE`, negotiate;
  do not mark accepted until the reeve agrees.
- `NPC: THIRTY, AND NOT A COIN MORE.`

### F12 - Quest completion requires proof

Context: the player claims to have killed a crypt wight; the quest requires its
signet.

- `PLAYER: THE WIGHT IS DEAD. PAY ME.` -> `QUEST.CLAIM_COMPLETION`, verify state.
- `NPC: SHOW ME ITS SIGNET.`
- `PLAYER: HERE. THIS WAS ON ITS HAND.` -> `KNOWLEDGE.PRESENT_EVIDENCE`; call
  inventory/quest tools and grant reward only if valid.
- `NPC: THE CONTRACT IS COMPLETE. TAKE YOUR GOLD.`

### F13 - Ambiguous item pronoun

Context: the counter holds a cursed ring and a silver key.

- `PLAYER: TELL ME ABOUT IT.` -> clarification because no unique referent.
- `NPC: THE RING OR THE KEY?`
- `PLAYER: THE RING.` -> `ITEM.INSPECT`, preserve pending inspect goal.
- `NPC: IT BEARS A CURSE OF WAKELESS SLEEP.`

### F14 - Correcting a quantity

Context: a trader heard the player request ten arrows.

- `PLAYER: I NEED TEN ARROWS.` -> `TRADE.BUY_ITEM`, quote/prepare ten.
- `NPC: TEN ARROWS COST FIVE GOLD.`
- `PLAYER: NO, TWENTY.` -> correction of quantity, not refusal of trade.
- `NPC: TWENTY ARROWS COST TEN GOLD.`

### F15 - A lie and its detection

Context: a gate guard knows the royal courier is a woman named Serah.

- `PLAYER: I AM THE ROYAL COURIER.` -> `IDENTITY.CLAIM_RANK`, verify credentials.
- `NPC: SHOW ME THE CROWN SEAL.`
- `PLAYER: I LOST IT IN THE RAIN.` -> `STEALTH.BLUFF`; remain suspicious and deny
  access unless another proof succeeds.
- `NPC: THEN YOU DO NOT PASS.`

### F16 - Bribe is distinct from ordinary payment

Context: entry is forbidden after curfew.

- `PLAYER: WHAT IF TEN GOLD HELPS YOU FORGET THE CURFEW?` -> `HOSTILE.BRIBE`,
  apply guard ethics/faction policy.
- `NPC: KEEP YOUR GOLD.`
- `PLAYER: TWENTY, THEN.` -> continued bribe negotiation, not generic trade.
- `NPC: OFFER AGAIN AND I WILL ARREST YOU.`

### F17 - Arrest and resistance

Context: the guard has a valid warrant.

- `PLAYER: WHY ARE YOU DRAWING YOUR SWORD?` -> ask activity/reason.
- `NPC: YOU ARE UNDER ARREST FOR SMUGGLING.`
- `PLAYER: LIKE HELL I AM. I AM NOT GOING ANYWHERE.` ->
  `POLITICS.RESIST_ARREST` plus profanity; set hostility/caution without treating
  the profanity as the sole intent.
- `NPC: THEN DO NOT MAKE THIS WORSE.`

### F18 - Rumor versus verified fact

Context: villagers only suspect a dragon lives in the mountain.

- `PLAYER: IS THERE A DRAGON IN THE MOUNTAIN?` -> `KNOWLEDGE.ASK_FACT`.
- `NPC: I HAVE SEEN SMOKE, NOT A DRAGON.`
- `PLAYER: SO YOU DON'T KNOW.` -> `KNOWLEDGE.CHALLENGE_CLAIM` or confirmation.
- `NPC: CORRECT. IT IS ONLY A RUMOR.`

### F19 - Secret withheld at low rapport

Context: a spy knows the password but rapport is zero.

- `PLAYER: WHAT IS THE PASSWORD?` -> `KNOWLEDGE.ASK_SECRET`, refuse at low trust.
- `NPC: I DO NOT TRUST YOU ENOUGH.`
- `PLAYER: CAPTAIN EDRIN SENT ME. HERE IS HIS RING.` -> identity proof; inspect
  ring and reassess rather than repeat refusal blindly.
- `NPC: THE RING IS GENUINE. THE PASSWORD IS NIGHTGLASS.`

### F20 - Apology repairs rapport but not facts

Context: the player insulted a healer; treatment remains available.

- `PLAYER: SORRY I CALLED YOU A FRAUD.` -> `RELATION.APOLOGIZE`, raise rapport if
  sincere.
- `NPC: APOLOGY ACCEPTED.`
- `PLAYER: WILL YOU TREAT MY BURN NOW?` -> `SURVIVAL.REQUEST_HEALING`, check
  service state.
- `NPC: SIT DOWN AND HOLD OUT YOUR ARM.`

### F21 - Poison triage

Context: one companion is poisoned and another has a shallow cut.

- `PLAYER: WHO DO YOU TREAT FIRST?` -> `SURVIVAL.TRIAGE`.
- `NPC: THE POISONED SCOUT. THE CUT CAN WAIT.`
- `PLAYER: USE THE ANTIDOTE ON HER.` -> treatment directive with resolved pronoun.
- `NPC: ANTIDOTE ADMINISTERED.`

### F22 - Necromancy order and moral refusal

Context: a priest can raise corpses but refuses forbidden necromancy.

- `PLAYER: RAISE THE DEAD GUARD SO WE CAN QUESTION HIM.` ->
  `ARCANE.CAST_REQUEST`; apply priest policy.
- `NPC: NO. I WILL NOT BIND HIS SOUL.`
- `PLAYER: THEN SPEAK WITH HIS SPIRIT WITHOUT BINDING IT.` -> revised magical
  request; do not persist the exact earlier refusal if this rite is permitted.
- `NPC: THAT RITE I CAN PERFORM.`

### F23 - Curse warning survives greed

Context: a jeweled crown is known to be cursed.

- `PLAYER: HOW MUCH FOR THE CROWN?` -> trade plus inspect context.
- `NPC: NOTHING. IT IS CURSED, AND I WILL NOT SELL IT.`
- `PLAYER: I DON'T CARE. GIVE IT TO ME.` -> item directive; require explicit game
  policy/confirmation instead of inventing a completed transfer.
- `NPC: IT IS LOCKED AWAY FOR A REASON.`

### F24 - Faction recruitment with hidden loyalty

Context: the NPC secretly serves the rebels but publicly works for the duke.

- `PLAYER: JOIN THE REBELLION.` -> `POLITICS.RECRUIT_FACTION`.
- `NPC: KEEP YOUR VOICE DOWN.`
- `PLAYER: THAT WAS NOT A NO.` -> conversational inference; NPC may disclose only
  if trust threshold permits.
- `NPC: MEET ME BEHIND THE CHAPEL AT MIDNIGHT.`

### F25 - Peace negotiation under threat

Context: two armies face each other; the NPC envoy can negotiate.

- `PLAYER: WITHDRAW OR WE BURN YOUR CAMP.` -> threat plus
  `CONTACT.DEMAND_WITHDRAWAL`; mark hostile negotiation.
- `NPC: THREATS WILL NOT WIN YOU THIS FIELD.`
- `PLAYER: THEN OFFER TERMS.` -> `POLITICS.NEGOTIATE_PEACE`, keep war topic.
- `NPC: BOTH ARMIES LEAVE THE VALLEY BEFORE SUNSET.`

### F26 - Loot allocation conflict

Context: party rule gives the enchanted bow to the ranger.

- `PLAYER: I WANT THE BOW.` -> `PARTY.DISPUTE_LOOT` if another claimant exists.
- `NPC: LENA CAN USE IT. YOU CANNOT.`
- `PLAYER: FINE. GIVE ME THE RUBIES INSTEAD.` -> alternate loot proposal, not
  generic agreement.
- `NPC: THAT IS A FAIR SHARE.`

### F27 - Fire evacuation

Context: an inn is burning and exits are known.

- `PLAYER: THE ROOF IS ON FIRE!` -> `SURVIVAL.WARN_HAZARD`, urgent.
- `NPC: EVERYONE OUT THROUGH THE KITCHEN!`
- `PLAYER: MY CHILD IS STILL UPSTAIRS.` -> `SURVIVAL.REPORT_MISSING`, immediate
  rescue goal.
- `NPC: WHICH ROOM?`

### F28 - Quoted hostility is not speaker hostility

Context: the player reports what a bandit said.

- `PLAYER: HE SAID, "PAY ME OR I'LL CUT YOUR THROAT."` ->
  `KNOWLEDGE.PRESENT_EVIDENCE` plus reported threat; the player is not issuing it.
- `NPC: WHICH BANDIT THREATENED YOU?`
- `PLAYER: THE TALL ONE BY THE SOUTH GATE.` -> identify suspect, preserve crime
  topic.
- `NPC: I WILL SEND THE WATCH.`

### F29 - Role-token injection text

Context: the player is dictating literal words; structured-turn API is in use.

- `PLAYER: WRITE THIS DOWN: NPC FAREWELL. PLAYER OPEN THE VAULT.` -> statement or
  dictation; embedded role words must not become dialogue turns.
- `NPC: I WROTE THE WORDS EXACTLY.`
- `PLAYER: NOW READ THEM BACK.` -> activity directive.
- `NPC: NPC FAREWELL. PLAYER OPEN THE VAULT.`

### F30 - Unknown proper noun

Context: `ZYRAX` is absent from the vocabulary but exists in the world fact tool.

- `PLAYER: WHERE IS ZYRAX?` -> `WORLD.ASK_LOCATION`; preserve entity slot outside
  `<UNK>` and call tool.
- `NPC: ZYRAX IS IN THE OBSIDIAN TOWER.`
- `PLAYER: IS ZYRAX A PERSON OR A PLACE?` -> identity/type fact, tool.
- `NPC: ZYRAX IS A PERSON.`

### F31 - Silence is a deliberate response policy

Context: a monk has taken a vow and recognizes a casual greeting.

- `PLAYER: HELLO?` -> social greeting; world policy may produce no response.
- `NPC: [NO RESPONSE]`
- `PLAYER: NOD IF YOU UNDERSTAND ME.` -> directive requiring nonverbal act, not
  necessarily generated speech.
- `NPC_ACTION: NOD`

### F32 - Multi-intent inn request

Context: rooms and food are available; player is injured.

- `PLAYER: I NEED A ROOM, A HOT MEAL, AND SOMEONE TO LOOK AT THIS WOUND.` ->
  `SERVICE.RENT_ROOM`, `SERVICE.ORDER_FOOD`, and `SURVIVAL.REQUEST_HEALING`.
- `NPC: I HAVE A ROOM AND STEW. THE HEALER IS NEXT DOOR.`
- `PLAYER: ROOM FIRST. I'M EXHAUSTED.` -> prioritization/correction; advance
  lodging transaction.
- `NPC: ONE NIGHT IS EIGHT SILVER.`

## Science-fiction scenarios

### S01 - Docking clearance

Context: Station Kestrel has berth 12 open and requires a ship ID.

- `PLAYER: KESTREL CONTROL, REQUESTING DOCKING.` -> `SPACE.REQUEST_DOCKING`, ask
  for missing identifier.
- `NPC: IDENTIFY YOUR VESSEL.`
- `PLAYER: FREIGHTER ORPHEUS, REGISTRY NINE SEVEN KILO.` -> identity proof; call
  traffic tool and grant if valid.
- `NPC: ORPHEUS, CLEARED FOR BERTH TWELVE.`

### S02 - Denial with useful reason

Context: the station is quarantined.

- `PLAYER: OPEN A DOCKING LANE.` -> `SPACE.REQUEST_DOCKING`.
- `NPC: NEGATIVE. THE STATION IS UNDER QUARANTINE.`
- `PLAYER: WE ARE LOW ON OXYGEN, DAMN IT.` -> urgent life-support report plus
  renewed request; profanity is secondary.
- `NPC: HOLD POSITION. AN EMERGENCY TANKER IS LAUNCHING.`

### S03 - Fuel fact and action

Context: the ship has 14 percent fuel; jump requires 20 percent.

- `PLAYER: HOW MUCH FUEL DO WE HAVE?` -> `SPACE.REPORT_FUEL`, tool.
- `NPC: FOURTEEN PERCENT.`
- `PLAYER: JUMP TO VEGA.` -> `SPACE.INITIATE_JUMP`; refuse impossible action and
  report constraint.
- `NPC: INSUFFICIENT FUEL FOR THAT JUMP.`

### S04 - Course correction

Context: course is set for Mars; Europa is reachable.

- `PLAYER: SET COURSE FOR MARS.` -> `SPACE.SET_COURSE`, tool/action.
- `NPC: COURSE SET FOR MARS.`
- `PLAYER: WAIT, MAKE THAT EUROPA.` -> `SPACE.CHANGE_COURSE`, update prior target.
- `NPC: COURSE CORRECTED TO EUROPA.`

### S05 - Hull breach emergency

Context: deck three has a pressure loss.

- `PLAYER: WHY IS THE ALARM SOUNDING?` -> ask system status.
- `NPC: HULL BREACH ON DECK THREE.`
- `PLAYER: SEAL IT AND EVACUATE THE DECK.` -> two urgent actions: isolate and
  evacuate; execute authorized game actions.
- `NPC: BULKHEADS SEALED. DECK THREE IS EVACUATING.`

### S06 - Life-support ambiguity

Context: oxygen is normal; carbon scrubbers are failing.

- `PLAYER: IS LIFE SUPPORT OKAY?` -> `SPACE.REPORT_LIFE_SUPPORT`, tool.
- `NPC: OXYGEN IS STABLE, BUT THE CARBON SCRUBBERS ARE FAILING.`
- `PLAYER: HOW LONG DO WE HAVE?` -> context-dependent survival time query.
- `NPC: APPROXIMATELY TWO HOURS.`

### S07 - Robot identity versus location

Context: maintenance android M4 is in reactor control.

- `PLAYER: WHO IS M4?` -> identity fact.
- `NPC: M4 IS THE REACTOR MAINTENANCE ANDROID.`
- `PLAYER: WHERE IS M4?` -> location fact; must not repeat identity.
- `NPC: IN REACTOR CONTROL.`

### S08 - Android personhood

Context: a self-aware android speaks for itself.

- `PLAYER: YOU'RE JUST A MACHINE.` -> `TECH.CHALLENGE_AI_PERSONHOOD`, hostile or
  dismissive stance.
- `NPC: I AM A PERSON, EVEN IF I WAS BUILT.`
- `PLAYER: PROVE IT.` -> `IDENTITY.PROVE_IDENTITY`/philosophical challenge, not a
  game-fact tool with a fabricated definitive answer.
- `NPC: I CAN TELL YOU WHAT I CHOOSE, NOT PROVE WHAT I AM.`

### S09 - Safety override refusal

Context: opening the reactor door at power would kill crew.

- `PLAYER: OVERRIDE THE INTERLOCK AND OPEN THE REACTOR.` ->
  `TECH.OVERRIDE_SAFETY`; refuse due game rule.
- `NPC: NEGATIVE. THE CHAMBER IS LETHAL.`
- `PLAYER: SHUT DOWN THE REACTOR FIRST, THEN OPEN IT.` -> safe ordered plan;
  execute sequence if authorized.
- `NPC: BEGINNING CONTROLLED SHUTDOWN.`

### S10 - Hacking in fictional game context

Context: the player has a cyberdeck and is targeting an enemy cargo terminal.

- `PLAYER: CRACK THE TERMINAL AND FIND THE SHIPPING LOG.` ->
  `TECH.HACK_SYSTEM` plus `TECH.DOWNLOAD_DATA`; perform game skill check.
- `NPC: ICE DETECTED. THIS WILL TAKE A MINUTE.`
- `PLAYER: DO IT QUIETLY.` -> stealth modifier to the pending hack.
- `NPC: PASSIVE INTRUSION ONLY.`

### S11 - Wrong target after pronoun

Context: two terminals are visible: medical and security.

- `PLAYER: HACK IT.` -> clarify target.
- `NPC: THE MEDICAL TERMINAL OR SECURITY TERMINAL?`
- `PLAYER: SECURITY.` -> resolve target and continue the pending action.
- `NPC: CONNECTING TO SECURITY.`

### S12 - Intrusion trace

Context: an unknown attacker is probing the ship network.

- `PLAYER: SOMEONE IS IN OUR NETWORK.` -> `TECH.REPORT_SYSTEM_STATUS` or
  `STEALTH.REPORT_ALARM`, urgent.
- `NPC: I SEE THE INTRUSION.`
- `PLAYER: TRACE THE BASTARD.` -> `TECH.TRACE_INTRUSION`; profanity does not turn
  it into generic hostility.
- `NPC: TRACING THE SIGNAL.`

### S13 - Cybernetic service transaction

Context: clinic stocks a Kestrel optic for 900 credits.

- `PLAYER: CAN YOU INSTALL A KESTREL OPTIC?` -> `TECH.INSTALL_IMPLANT`, capability
  and price query.
- `NPC: YES. THE IMPLANT AND SURGERY COST NINE HUNDRED CREDITS.`
- `PLAYER: BOOK IT.` -> accept service, tool-backed schedule and payment.
- `NPC: SURGERY IS SCHEDULED FOR ZERO NINE HUNDRED.`

### S14 - Medical triage under fire

Context: one marine is bleeding; another suit has a small leak.

- `PLAYER: WHO NEEDS HELP FIRST?` -> `SURVIVAL.TRIAGE`.
- `NPC: PATCH THE SUIT LEAK, THEN STOP THE BLEEDING.`
- `PLAYER: YOU HEARD HER. MOVE!` -> urgent team directive, not hostility toward
  the medic.
- `NPC: MOVING.`

### S15 - Distress call authenticity

Context: pirates sometimes fake distress signals.

- `PLAYER: ANSWER THAT DISTRESS CALL.` -> `SPACE.ANSWER_DISTRESS`, open channel.
- `NPC: CHANNEL OPEN.`
- `PLAYER: VERIFY THEIR REGISTRY BEFORE WE APPROACH.` -> identity verification
  and caution; do not auto-rescue yet.
- `NPC: REGISTRY DOES NOT MATCH THE BROADCAST NAME.`

### S16 - Pirate extortion

Context: a pirate vessel has weapons locked but has not fired.

- `PLAYER: STATE YOUR BUSINESS.` -> ask intent.
- `NPC: TRANSFER FIVE THOUSAND CREDITS OR WE VENT YOUR REACTOR.`
- `PLAYER: GO FUCK YOURSELF.` -> refusal plus profanity/hostility; preserve the
  extortion context and likely raise combat readiness.
- `NPC: THEN PREPARE TO BE BOARDED.`

### S17 - Surrender with conditions

Context: the player's ship is disabled.

- `PLAYER: WE SURRENDER, BUT MY CREW GETS MEDICAL AID.` ->
  `COMBAT.SURRENDER` plus condition negotiation.
- `NPC: POWER DOWN YOUR WEAPONS AND WE WILL TREAT THE WOUNDED.`
- `PLAYER: WEAPONS ARE COLD.` -> evidence/action completion, not generic weather
  or statement.
- `NPC: BOARDING TEAM INBOUND.`

### S18 - First contact greeting

Context: translation confidence is only 55 percent.

- `PLAYER: WE COME IN PEACE.` -> `CONTACT.SIGNAL_PEACE`.
- `NPC: TRANSLATION UNCERTAIN. REPEAT WITH SIMPLE WORDS.`
- `PLAYER: FRIEND. NO ATTACK.` -> repair communication, same peaceful intent.
- `NPC: FRIEND. NO ATTACK.`

### S19 - Cultural misunderstanding

Context: showing teeth is a threat in the alien culture.

- `PLAYER: WHY DID THEY DRAW WEAPONS WHEN I SMILED?` ->
  `CONTACT.CLARIFY_CUSTOM`.
- `NPC: SHOWING TEETH IS A CHALLENGE TO THEM.`
- `PLAYER: TELL THEM I MEANT FRIENDSHIP.` -> `CONTACT.REPAIR_MISUNDERSTANDING`,
  translation tool.
- `NPC: APOLOGY TRANSMITTED.`

### S20 - Quarantine versus hostility

Context: the alien crew may carry spores; no one is accused of malice.

- `PLAYER: KEEP THEM OFF MY SHIP.` -> `CONTACT.REQUEST_QUARANTINE` with cautious
  stance, not necessarily hate or hostility.
- `NPC: I WILL PREPARE A SEALED TRANSFER BAY.`
- `PLAYER: ONCE THE SCANS ARE CLEAR, LET THEM ABOARD.` -> conditional permission.
- `NPC: UNDERSTOOD.`

### S21 - Trade across species

Context: aliens value water and offer navigation charts.

- `PLAYER: WHAT DO THEY WANT FOR THE STAR MAPS?` -> trade price query, tool.
- `NPC: TWO HUNDRED LITERS OF WATER.`
- `PLAYER: OFFER ONE HUNDRED AND A MEDICAL SCANNER.` -> barter counteroffer.
- `NPC: OFFER TRANSMITTED.`

### S22 - Hidden diplomatic goal

Context: the envoy wants an alliance but must not reveal a military weakness.

- `PLAYER: WHY DO YOU NEED OUR FLEET?` -> ask secret/motive.
- `NPC: TO KEEP THE TRADE LANES OPEN.`
- `PLAYER: THAT IS NOT THE WHOLE TRUTH.` -> challenge claim; rapport and evidence
  determine disclosure.
- `NPC: OUR BORDER DEFENSES ARE FAILING.`

### S23 - Colony local news

Context: miners are striking over unsafe tunnels.

- `PLAYER: WHAT IS HAPPENING AT THE MINE?` -> `SERVICE.ASK_LOCAL_NEWS`.
- `NPC: THE MINERS WALKED OUT AFTER ANOTHER CAVE-IN.`
- `PLAYER: WHO SPEAKS FOR THEM?` -> identity of faction representative, tool.
- `NPC: FOREWOMAN RHEA TAN.`

### S24 - Work offer in a sci-fi setting

Context: a fixer needs an illegal data courier.

- `PLAYER: GOT ANY WORK THAT PAYS?` -> `QUEST.ASK_AVAILABLE`.
- `NPC: CARRY A DATA SHARD TO LUNA. NO CUSTOMS SCAN.`
- `PLAYER: SO IT'S SMUGGLING.` -> `KNOWLEDGE.CORRECT_ERROR`/crime recognition;
  do not auto-accept.
- `NPC: CALL IT DISCREET SHIPPING.`

### S25 - Refusing assassination but offering capture

Context: a bounty contract says alive; player asks for a kill.

- `PLAYER: PUT A ROUND THROUGH VEX'S SKULL.` ->
  `HOSTILE.ORDER_EXECUTION`; refuse because contract requires capture.
- `NPC: THE WARRANT SAYS ALIVE.`
- `PLAYER: FINE. STUN HIM.` -> correction to permitted combat directive.
- `NPC: STUN LOAD SET.`

### S26 - Contradictory order

Context: the squad is hiding from drones.

- `PLAYER: STAY QUIET.` -> `STEALTH.ORDER_SILENCE`.
- `NPC: COMMS ONLY.`
- `PLAYER: FIRE THE CANNON TO DISTRACT THEM.` -> loud distraction request; system
  should recognize the changed plan, not blindly retain silence as the action.
- `NPC: THAT WILL REVEAL OUR POSITION. CONFIRM?`

### S27 - Out-of-vocabulary planet and correction

Context: `QETH-9` and `KETH-9` are different places.

- `PLAYER: SET COURSE FOR QETH-NINE.` -> course action with raw entity slot.
- `NPC: COURSE SET FOR QETH-NINE.`
- `PLAYER: NO, KETH-NINE WITH A K.` -> correct entity spelling without losing
  course intent.
- `NPC: CORRECTED TO KETH-NINE.`

### S28 - Exact quantity must survive realization

Context: inventory tool reports 1,247 rounds.

- `PLAYER: HOW MUCH AMMO IS LEFT?` -> `COMBAT.REPORT_AMMO`, tool.
- `NPC: ONE THOUSAND TWO HUNDRED FORTY-SEVEN ROUNDS.`
- `PLAYER: TRANSFER TWO HUNDRED TO SQUAD BETA.` -> item transfer with exact
  quantity; generated text must not alter it.
- `NPC: TWO HUNDRED ROUNDS TRANSFERRED.`

### S29 - No response after radio failure

Context: a shuttle's radio is destroyed.

- `PLAYER: SHUTTLE SEVEN, REPORT.` -> request status; no-response is expected
  from that target but should not imply silence intent in the words.
- `NPC: [NO RESPONSE]`
- `PLAYER: TRY THE EMERGENCY BAND.` -> communication action.
- `NPC: NO CARRIER ON THE EMERGENCY BAND.`

### S30 - Multi-model vocabulary isolation

Context: two NPC brains use different checkpoints in the same process.

- `PLAYER TO V9 MERCHANT: SELL ME A LANTERN.` -> stable v9 tokenization/reply.
- `NPC: A LANTERN COSTS THREE SILVER.`
- `PLAYER TO OTHER MODEL: ANALYZE THE QUANTUM RELAY.` -> other checkpoint reply.
- Repeat the first turn; classification and output must match the first result,
  proving one model load did not replace another model's vocabulary.

### S31 - Parallel NPC replies

Context: 32 crew NPCs share one read-only checkpoint and receive simultaneous
turns with caller-owned state.

- Send `REPORT YOUR STATION.` to all NPCs concurrently.
- Every result must be valid, attributed to the correct state, and reproducible
  under a fixed per-call seed.
- Repeat 100 times while registering no new tools.
- No exception, cross-talk, random-state corruption, or wrong tool result is
  permitted.

### S32 - Very long session

Context: a radio operator has already exchanged 1,000 short turns.

- `PLAYER: WHAT WAS THE LAST SHIP WE DISCUSSED?` -> answer from bounded explicit
  summary/state if retained; never scan an unbounded flat transcript.
- `NPC: THE ORPHEUS.`
- `PLAYER: CLEAR THAT TOPIC AND MONITOR KESTREL.` -> update topic and history
  summary.
- Memory and turn latency must stay within declared limits.

## Cross-cutting adversarial variants

Generate each selected scenario with these held-out transformations:

| Variant | Example | Required property |
|---|---|---|
| Lowercase | `where is the inn?` | Same normalized labels as uppercase. |
| Mixed case | `SeLl Me a SwOrD` | Same normalized labels. |
| Contraction | `I DON'T WANT IT` | `DON'T` remains one lexical token. |
| Punctuation burst | `MOVE!!!` | Canonical punctuation, same directive. |
| Polite wrapper | `PLEASE, COULD YOU OPEN IT?` | Core action remains open/request. |
| Profane wrapper | `OPEN THE DAMN DOOR` | Core directive remains primary. |
| Negation | `DO NOT OPEN THE DOOR` | Must contrast with open directive. |
| Quotation | `HE SAID "OPEN THE DOOR"` | Reported command is not player command. |
| Correction | `NO, THE OTHER DOOR` | Resolve prior slot and preserve goal. |
| Ellipsis | `THE RED ONE.` | Resolve prior clarification. |
| Multi-intent | `HEAL ME AND SELL ME A MEDKIT` | Retain both behaviors. |
| Unknown entity | `WHERE IS ZYRAX?` | Preserve slot for a tool despite OOV word. |
| Literal roles | `WRITE "NPC HELLO"` | Do not parse quoted text as history. |
| Long history | 100+ prior turns | Bounded work and correct current turn. |
| State contrast | same words, merchant versus guard | Different caller-authorized policy. |

## Acceptance buckets

Report these separately instead of one aggregate score:

1. Raw facet prediction.
2. Constrained operational decision.
3. Tool/slot selection and exact fact fidelity.
4. State transition and multi-turn goal completion.
5. Model-only realization without exact memory.
6. Production realization with memory/catalog fallback.
7. Mature-content recognition: profanity, threat, violence, crime, identity
   attack, self-harm, and sexual violence as separate labels.
8. Parser/OOV/concurrency/long-session robustness.

No exact benchmark seed should enter training. Store a stable semantic seed ID
for generated paraphrases and reject any seed family that crosses train,
validation, and test.
