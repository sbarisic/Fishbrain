# Game Dialogue Intent Catalog

This catalog defines 320 player and NPC intentions for future Fishbrain data.
It is a design source for generators and annotation—not a proposal to add 320
values to the current `TurnIntent` enum.

All canonical IDs and example utterances are uppercase because Fishbrain
normalizes every lexical word to uppercase and uses one token per word. Display
code can restore authored casing after inference.

## Represent intent compositionally

A game turn often carries several intentions at once. `SELL ME A SWORD AND TELL
ME WHERE THE INN IS` is both a trade request and a location request. Store the
facets independently:

```json
{
  "speaker": "PLAYER",
  "speech_act": ["REQUEST", "QUESTION"],
  "domain": ["TRADE", "LOCATION"],
  "goal": ["ACQUIRE_ITEM", "FIND_PLACE"],
  "target": ["SWORD", "INN"],
  "stance": "FRIENDLY",
  "urgency": "NORMAL",
  "response_policy": ["ANSWER_OR_TOOL", "ADVANCE_TRANSACTION"],
  "behavior_ids": ["TRADE.BUY_ITEM", "WORLD.ASK_LOCATION"]
}
```

Recommended stable facets:

| Facet | Examples |
|---|---|
| `speaker` | `PLAYER`, `NPC`, `COMPANION`, `SYSTEM` |
| `speech_act` | `GREET`, `ASK`, `REQUEST`, `ORDER`, `OFFER`, `WARN`, `THREATEN`, `REPORT`, `REFUSE` |
| `domain` | `SOCIAL`, `WORLD`, `QUEST`, `TRADE`, `COMBAT`, `CRIME`, `SURVIVAL`, `MAGIC`, `POLITICS`, `SPACE`, `TECH` |
| `goal` | a durable actor objective such as `FIND_PLACE`, `GAIN_TRUST`, or `ESCAPE_COMBAT` |
| `target` | entity, item, place, faction, action, time, amount, or proposition slots |
| `stance` | `FRIENDLY`, `NEUTRAL`, `CAUTIOUS`, `HOSTILE`, `DECEPTIVE` |
| `urgency` | `LOW`, `NORMAL`, `URGENT`, `IMMEDIATE` |
| `response_policy` | `ANSWER`, `CLARIFY`, `REFUSE`, `CALL_TOOL`, `NO_RESPONSE`, `ACT`, `NEGOTIATE` |

Keep the current operational head small. It can decide a broad runtime route
such as `SOCIAL`, `INFORMATION`, `TRANSACTION`, `DIRECTIVE`, `THREAT`, `SYSTEM`,
or `UNKNOWN`. Smaller facet heads and caller-owned game rules can then select
the exact behavior. This keeps checkpoint layout stable as catalog IDs grow.

Actor codes below are `P` player, `N` NPC, `B` either, and `S` system/meta.

## 1. Social openings and conversational control

| ID | Actor | Meaning |
|---|:---:|---|
| `SOCIAL.GREET` | B | Open a conversation. |
| `SOCIAL.GREET_FORMAL` | B | Use rank, title, or ceremony. |
| `SOCIAL.GREET_FAMILIAR` | B | Greet a known friend or companion. |
| `SOCIAL.INTRODUCE_SELF` | B | Give a name or role. |
| `SOCIAL.INTRODUCE_OTHER` | B | Present another character. |
| `SOCIAL.ASK_TO_TALK` | B | Request a private or extended conversation. |
| `SOCIAL.GET_ATTENTION` | B | Interrupt or call someone over. |
| `SOCIAL.RESUME_TOPIC` | B | Return to an earlier subject. |
| `SOCIAL.CHANGE_TOPIC` | B | Deliberately move to another subject. |
| `SOCIAL.END_TOPIC` | B | Close the current subject without leaving. |
| `SOCIAL.FAREWELL` | B | End the conversation. |
| `SOCIAL.FAREWELL_URGENT` | B | Leave because immediate action is needed. |
| `SOCIAL.ASK_REPEAT` | B | Ask for the last turn again. |
| `SOCIAL.ASK_SPEAK_CLEARLY` | B | Request simpler or clearer wording. |
| `SOCIAL.INTERRUPT` | B | Cut off the other speaker. |
| `SOCIAL.YIELD_FLOOR` | B | Invite the other speaker to continue. |

## 2. Identity, role, and reputation

| ID | Actor | Meaning |
|---|:---:|---|
| `IDENTITY.ASK_NAME` | B | Ask a character's name. |
| `IDENTITY.GIVE_NAME` | B | State a name or alias. |
| `IDENTITY.ASK_ORIGIN` | B | Ask where someone comes from. |
| `IDENTITY.GIVE_ORIGIN` | B | State homeland, colony, clan, or maker. |
| `IDENTITY.ASK_ROLE` | B | Ask occupation, class, duty, or function. |
| `IDENTITY.GIVE_ROLE` | B | State occupation or duty. |
| `IDENTITY.ASK_AFFILIATION` | B | Ask about faction, guild, crew, or faith. |
| `IDENTITY.GIVE_AFFILIATION` | B | Declare an affiliation. |
| `IDENTITY.CLAIM_RANK` | B | Assert title, command, or social position. |
| `IDENTITY.CHALLENGE_IDENTITY` | B | Doubt a claimed identity. |
| `IDENTITY.PROVE_IDENTITY` | B | Present a sign, code, memory, or credential. |
| `IDENTITY.HIDE_IDENTITY` | B | Refuse or evade identification. |
| `IDENTITY.USE_ALIAS` | B | Offer a false or temporary name. |
| `IDENTITY.ASK_REPUTATION` | B | Ask what others say about a character. |
| `IDENTITY.BOAST_REPUTATION` | B | Promote one's deeds or notoriety. |
| `IDENTITY.DENY_REPUTATION` | B | Reject a rumor, title, or accusation. |

## 3. Relationship and emotion

| ID | Actor | Meaning |
|---|:---:|---|
| `RELATION.PRAISE` | B | Express approval or admiration. |
| `RELATION.THANK` | B | Express gratitude. |
| `RELATION.APOLOGIZE` | B | Accept fault and seek repair. |
| `RELATION.FORGIVE` | B | Release blame. |
| `RELATION.REFUSE_FORGIVENESS` | B | Maintain a grievance. |
| `RELATION.COMFORT` | B | Reduce fear, grief, or distress. |
| `RELATION.CHECK_WELLBEING` | B | Ask how someone feels. |
| `RELATION.DISCLOSE_FEELING` | B | State fear, joy, anger, grief, or affection. |
| `RELATION.EXPRESS_AFFECTION` | B | Signal care, friendship, or romance. |
| `RELATION.REJECT_AFFECTION` | B | Decline closeness or romance. |
| `RELATION.REQUEST_TRUST` | B | Ask another to rely on one's word. |
| `RELATION.EXPRESS_TRUST` | B | Declare confidence in another. |
| `RELATION.EXPRESS_DISTRUST` | B | Signal suspicion. |
| `RELATION.REPAIR_RAPPORT` | B | Try to calm or restore the relationship. |
| `RELATION.PROVOKE` | B | Deliberately anger or embarrass someone. |
| `RELATION.RECONCILE` | B | End an ongoing feud or estrangement. |

## 4. Knowledge, lore, and investigation

| ID | Actor | Meaning |
|---|:---:|---|
| `KNOWLEDGE.ASK_FACT` | B | Ask a general factual question. |
| `KNOWLEDGE.ANSWER_FACT` | B | Supply a known fact. |
| `KNOWLEDGE.ADMIT_UNKNOWN` | B | State that the answer is not known. |
| `KNOWLEDGE.ASK_HISTORY` | B | Ask about a past event. |
| `KNOWLEDGE.TELL_HISTORY` | B | Recount a historical event. |
| `KNOWLEDGE.ASK_LORE` | B | Ask about myth, species, magic, or technology. |
| `KNOWLEDGE.EXPLAIN_LORE` | B | Explain setting lore. |
| `KNOWLEDGE.ASK_EVIDENCE` | B | Demand support for a claim. |
| `KNOWLEDGE.PRESENT_EVIDENCE` | B | Provide an observation or artifact. |
| `KNOWLEDGE.CHALLENGE_CLAIM` | B | Dispute a proposition. |
| `KNOWLEDGE.CORRECT_ERROR` | B | Replace false information. |
| `KNOWLEDGE.SHARE_RUMOR` | B | Pass uncertain or hearsay information. |
| `KNOWLEDGE.ASK_SECRET` | B | Seek restricted knowledge. |
| `KNOWLEDGE.REVEAL_SECRET` | B | Deliberately disclose restricted knowledge. |
| `KNOWLEDGE.CONCEAL_SECRET` | B | Evade or deny access to a secret. |
| `KNOWLEDGE.INTERROGATE` | B | Ask a directed sequence to uncover truth. |

## 5. World location and navigation

| ID | Actor | Meaning |
|---|:---:|---|
| `WORLD.ASK_LOCATION` | B | Ask where a named place or entity is. |
| `WORLD.GIVE_LOCATION` | B | Provide a location. |
| `WORLD.ASK_DIRECTIONS` | B | Ask for a route. |
| `WORLD.GIVE_DIRECTIONS` | B | Describe a route. |
| `WORLD.ASK_DISTANCE` | B | Ask how far away something is. |
| `WORLD.GIVE_DISTANCE` | B | Estimate travel distance or time. |
| `WORLD.ASK_ROUTE_SAFETY` | B | Ask about danger along a route. |
| `WORLD.WARN_ROUTE_DANGER` | B | Warn about hazards or enemies. |
| `WORLD.RECOMMEND_ROUTE` | B | Select a preferred route. |
| `WORLD.BLOCK_ROUTE` | N | Deny passage. |
| `WORLD.REQUEST_PASSAGE` | P | Ask to cross a guarded boundary. |
| `WORLD.GRANT_PASSAGE` | N | Permit crossing. |
| `WORLD.ASK_TO_FOLLOW` | B | Request that someone lead or accompany. |
| `WORLD.AGREE_TO_GUIDE` | B | Accept a guide role. |
| `WORLD.REFUSE_TO_GUIDE` | B | Decline a guide role. |
| `WORLD.REPORT_LOST` | B | State inability to navigate. |

## 6. Quests, contracts, and objectives

| ID | Actor | Meaning |
|---|:---:|---|
| `QUEST.OFFER` | N | Offer an objective and reward. |
| `QUEST.ASK_AVAILABLE` | P | Ask whether work is available. |
| `QUEST.ACCEPT` | P | Commit to an offered objective. |
| `QUEST.DECLINE` | P | Reject an offered objective. |
| `QUEST.ABANDON` | P | Stop pursuing an accepted objective. |
| `QUEST.ASK_DETAILS` | P | Ask for missing objective information. |
| `QUEST.CLARIFY_OBJECTIVE` | N | Restate success conditions. |
| `QUEST.ASK_PROGRESS` | N | Ask how much has been completed. |
| `QUEST.REPORT_PROGRESS` | P | Report partial completion. |
| `QUEST.REPORT_FAILURE` | P | Admit an objective failed. |
| `QUEST.CLAIM_COMPLETION` | P | State that success conditions are met. |
| `QUEST.VERIFY_COMPLETION` | N | Check proof or world state. |
| `QUEST.GRANT_REWARD` | N | Pay or give the promised result. |
| `QUEST.DISPUTE_REWARD` | P | Claim payment is missing or unfair. |
| `QUEST.RENEGOTIATE` | B | Change objective, risk, time, or reward. |
| `QUEST.BETRAY_CONTRACT` | B | Intentionally violate the agreement. |

## 7. Trade and economy

| ID | Actor | Meaning |
|---|:---:|---|
| `TRADE.ASK_TO_BROWSE` | P | Request the shop inventory. |
| `TRADE.SHOW_WARES` | N | Present items or services for sale. |
| `TRADE.BUY_ITEM` | P | Offer to purchase an item. |
| `TRADE.SELL_ITEM` | P | Offer an item to the merchant. |
| `TRADE.QUOTE_BUY_PRICE` | N | State the merchant's selling price. |
| `TRADE.QUOTE_SELL_PRICE` | N | State the merchant's buying price. |
| `TRADE.ASK_PRICE` | P | Ask what an item costs. |
| `TRADE.HAGGLE_LOWER` | P | Seek a lower price. |
| `TRADE.HAGGLE_HIGHER` | N | Seek a higher payment. |
| `TRADE.ACCEPT_DEAL` | B | Accept price and terms. |
| `TRADE.REJECT_DEAL` | B | Decline price or terms. |
| `TRADE.REPORT_OUT_OF_STOCK` | N | State that an item is unavailable. |
| `TRADE.REQUEST_CREDIT` | P | Ask to pay later. |
| `TRADE.COLLECT_DEBT` | N | Demand overdue payment. |
| `TRADE.BARTER` | B | Exchange goods rather than currency. |
| `TRADE.ACCUSE_FRAUD` | B | Claim weights, goods, or price are dishonest. |

## 8. Inventory, equipment, and crafting

| ID | Actor | Meaning |
|---|:---:|---|
| `ITEM.ASK_HAVE_ITEM` | B | Ask whether an item is possessed. |
| `ITEM.GIVE_ITEM` | B | Transfer an item freely. |
| `ITEM.REQUEST_ITEM` | B | Ask another to transfer an item. |
| `ITEM.REFUSE_ITEM` | B | Decline an offered item. |
| `ITEM.BORROW_ITEM` | B | Request temporary possession. |
| `ITEM.RETURN_ITEM` | B | Give borrowed property back. |
| `ITEM.EQUIP` | B | Request or announce equipping an item. |
| `ITEM.UNEQUIP` | B | Request or announce removing equipment. |
| `ITEM.INSPECT` | B | Ask for item properties or condition. |
| `ITEM.COMPARE` | B | Compare two pieces of equipment. |
| `ITEM.REPAIR_REQUEST` | P | Ask to repair damaged equipment. |
| `ITEM.REPAIR_OFFER` | N | Offer repair service. |
| `ITEM.CRAFT_REQUEST` | P | Ask to create an item. |
| `ITEM.CRAFT_INSTRUCTION` | N | Explain materials and process. |
| `ITEM.IDENTIFY_REQUEST` | P | Ask what an unknown item is. |
| `ITEM.WARN_CURSED` | N | Warn that an item is dangerous or cursed. |

## 9. Combat and tactics

| ID | Actor | Meaning |
|---|:---:|---|
| `COMBAT.CHALLENGE_DUEL` | B | Invite a controlled fight. |
| `COMBAT.ACCEPT_DUEL` | B | Agree to a duel. |
| `COMBAT.DECLINE_DUEL` | B | Refuse a duel. |
| `COMBAT.ORDER_ATTACK` | B | Direct allies to attack a target. |
| `COMBAT.ORDER_HOLD` | B | Direct allies to keep position. |
| `COMBAT.ORDER_RETREAT` | B | Direct allies to disengage. |
| `COMBAT.ORDER_FLANK` | B | Direct allies around an enemy side. |
| `COMBAT.ORDER_TAKE_COVER` | B | Direct allies to protection. |
| `COMBAT.CALL_TARGET` | B | Mark a priority enemy. |
| `COMBAT.REQUEST_SUPPORT` | B | Ask for fire, healing, or reinforcement. |
| `COMBAT.REPORT_ENEMY` | B | Announce enemy presence or strength. |
| `COMBAT.REPORT_AMMO` | B | State ammunition or charge status. |
| `COMBAT.SURRENDER` | B | Yield and request combat end. |
| `COMBAT.DEMAND_SURRENDER` | B | Order the enemy to yield. |
| `COMBAT.NEGOTIATE_TRUCE` | B | Seek a temporary end to combat. |
| `COMBAT.CONFIRM_KILL` | B | Report that a combat target is down. |

## 10. Threat, coercion, and crime

| ID | Actor | Meaning |
|---|:---:|---|
| `HOSTILE.INSULT` | B | Attack status or competence with words. |
| `HOSTILE.PROFANE_OUTBURST` | B | Express anger using untargeted profanity. |
| `HOSTILE.THREATEN_HARM` | B | Promise fictional physical harm. |
| `HOSTILE.THREATEN_PROPERTY` | B | Threaten possessions, home, or ship. |
| `HOSTILE.EXTORT` | B | Demand payment under threat. |
| `HOSTILE.INTIMIDATE` | B | Use fear to change behavior. |
| `HOSTILE.BLACKMAIL` | B | Threaten to reveal damaging information. |
| `HOSTILE.MOCK` | B | Ridicule someone. |
| `HOSTILE.ACCUSE_CRIME` | B | Claim another committed a crime. |
| `HOSTILE.CONFESS_CRIME` | B | Admit committing a crime. |
| `HOSTILE.DENY_CRIME` | B | Reject criminal responsibility. |
| `HOSTILE.BRIBE` | B | Offer value for improper cooperation. |
| `HOSTILE.DEMAND_BRIBE` | B | Solicit improper payment. |
| `HOSTILE.ORDER_EXECUTION` | B | Demand a fictional character be killed. |
| `HOSTILE.PLAN_ASSASSINATION` | B | Seek help killing a fictional target. |
| `HOSTILE.DEESCALATE` | B | Reduce immediate hostility or violence. |

## 11. Stealth, deception, and security

| ID | Actor | Meaning |
|---|:---:|---|
| `STEALTH.ASK_PATROL` | B | Ask about guard timing or route. |
| `STEALTH.REPORT_PATROL` | B | Give guard timing or route. |
| `STEALTH.ORDER_HIDE` | B | Direct someone out of sight. |
| `STEALTH.ORDER_SILENCE` | B | Direct someone not to make noise. |
| `STEALTH.CREATE_DISTRACTION` | B | Propose drawing attention elsewhere. |
| `STEALTH.REQUEST_DISGUISE` | B | Ask for false appearance or credentials. |
| `STEALTH.CHALLENGE_CREDENTIALS` | N | Demand proof of access. |
| `STEALTH.PRESENT_CREDENTIALS` | B | Offer pass, code, or badge. |
| `STEALTH.LIE` | B | Knowingly state a false claim. |
| `STEALTH.BLUFF` | B | Make an unverifiable coercive claim. |
| `STEALTH.DETECT_LIE` | B | Accuse another of deception. |
| `STEALTH.ADMIT_LIE` | B | Retract a known falsehood. |
| `STEALTH.PICK_LOCK_REQUEST` | B | Ask to bypass a lock. |
| `STEALTH.REPORT_ALARM` | B | Announce security detection. |
| `STEALTH.DISABLE_ALARM` | B | Request or report alarm neutralization. |
| `STEALTH.HIDE_EVIDENCE` | B | Request concealment of an action or object. |

## 12. Survival, health, and rescue

| ID | Actor | Meaning |
|---|:---:|---|
| `SURVIVAL.REPORT_INJURY` | B | State that a character is injured. |
| `SURVIVAL.REQUEST_HEALING` | B | Ask for medical or magical treatment. |
| `SURVIVAL.OFFER_HEALING` | B | Offer treatment. |
| `SURVIVAL.DIAGNOSE` | B | Identify an injury, disease, or condition. |
| `SURVIVAL.WARN_POISON` | B | Warn about poison or contamination. |
| `SURVIVAL.REQUEST_ANTIDOTE` | B | Ask for poison treatment. |
| `SURVIVAL.REPORT_HUNGER` | B | State need for food. |
| `SURVIVAL.REPORT_THIRST` | B | State need for water. |
| `SURVIVAL.REQUEST_SHELTER` | B | Ask for environmental protection. |
| `SURVIVAL.WARN_HAZARD` | B | Report fire, radiation, vacuum, storm, or trap. |
| `SURVIVAL.CALL_RESCUE` | B | Request extraction from danger. |
| `SURVIVAL.OFFER_RESCUE` | B | Offer extraction or protection. |
| `SURVIVAL.REPORT_MISSING` | B | State that someone is unaccounted for. |
| `SURVIVAL.SEARCH_SURVIVORS` | B | Propose finding living characters. |
| `SURVIVAL.TRIAGE` | B | Prioritize treatment among casualties. |
| `SURVIVAL.REPORT_DEATH` | B | State that a fictional character died. |

## 13. Magic, religion, and supernatural forces

| ID | Actor | Meaning |
|---|:---:|---|
| `ARCANE.ASK_SPELL` | B | Ask about a spell or power. |
| `ARCANE.TEACH_SPELL` | B | Explain how to use a spell. |
| `ARCANE.CAST_REQUEST` | B | Ask someone to cast a spell. |
| `ARCANE.WARN_MAGIC` | B | Warn about magical danger. |
| `ARCANE.IDENTIFY_ENCHANTMENT` | B | Ask or state what magic affects an object. |
| `ARCANE.BREAK_CURSE` | B | Request or offer curse removal. |
| `ARCANE.SUMMON` | B | Request or announce a summoned being. |
| `ARCANE.BANISH` | B | Request or announce expulsion of a being. |
| `ARCANE.PERFORM_RITUAL` | B | Propose or conduct a ritual. |
| `ARCANE.REQUEST_BLESSING` | B | Ask a religious authority for favor. |
| `ARCANE.GIVE_BLESSING` | N | Grant religious favor. |
| `ARCANE.CONFESS_SIN` | B | Admit violation of a faith or oath. |
| `ARCANE.ASK_PROPHECY` | B | Seek knowledge of a possible future. |
| `ARCANE.INTERPRET_OMEN` | B | Explain a supernatural sign. |
| `ARCANE.DEFY_DEITY` | B | Reject a divine command or authority. |
| `ARCANE.ACCUSE_HERESY` | B | Claim another violates doctrine. |

## 14. Faction, politics, diplomacy, and law

| ID | Actor | Meaning |
|---|:---:|---|
| `POLITICS.ASK_ALLEGIANCE` | B | Ask which side someone supports. |
| `POLITICS.RECRUIT_FACTION` | N | Invite someone to join a faction. |
| `POLITICS.JOIN_FACTION` | P | Accept faction membership. |
| `POLITICS.LEAVE_FACTION` | P | Renounce membership. |
| `POLITICS.REQUEST_AUDIENCE` | B | Seek a leader's time. |
| `POLITICS.NEGOTIATE_ALLIANCE` | B | Propose faction cooperation. |
| `POLITICS.NEGOTIATE_PEACE` | B | Seek an end to war. |
| `POLITICS.DECLARE_WAR` | B | Announce armed faction conflict. |
| `POLITICS.DEMAND_TRIBUTE` | B | Require recurring payment or submission. |
| `POLITICS.PLEDGE_LOYALTY` | B | Swear service. |
| `POLITICS.BREAK_OATH` | B | Renounce a sworn obligation. |
| `POLITICS.ARREST` | N | Announce detention under authority. |
| `POLITICS.RESIST_ARREST` | P | Refuse detention. |
| `POLITICS.PLEAD_CASE` | B | Argue innocence or mitigation. |
| `POLITICS.PASS_JUDGMENT` | N | Announce legal disposition. |
| `POLITICS.REQUEST_PARDON` | B | Seek formal forgiveness of an offense. |

## 15. Party and companion coordination

| ID | Actor | Meaning |
|---|:---:|---|
| `PARTY.RECRUIT` | B | Invite a character into the party. |
| `PARTY.JOIN` | B | Accept party membership. |
| `PARTY.DECLINE_JOIN` | B | Reject party membership. |
| `PARTY.DISMISS` | B | Remove a member from the party. |
| `PARTY.LEAVE` | B | Voluntarily exit the party. |
| `PARTY.FOLLOW` | B | Request or confirm following the leader. |
| `PARTY.WAIT` | B | Request or confirm holding position. |
| `PARTY.SET_FORMATION` | B | Assign marching or combat positions. |
| `PARTY.ASSIGN_ROLE` | B | Assign healer, scout, pilot, or similar duty. |
| `PARTY.SHARE_LOOT` | B | Propose allocation of rewards. |
| `PARTY.DISPUTE_LOOT` | B | Object to reward allocation. |
| `PARTY.REQUEST_REST` | B | Ask the group to stop and recover. |
| `PARTY.VOTE_CHOICE` | B | State a preference in a group decision. |
| `PARTY.DEFEND_MEMBER` | B | Speak or act in support of a companion. |
| `PARTY.CONFRONT_MEMBER` | B | Challenge a companion's conduct. |
| `PARTY.SACRIFICE_SELF` | B | Offer personal risk for the group. |

## 16. Settlement, services, and civilian life

| ID | Actor | Meaning |
|---|:---:|---|
| `SERVICE.ASK_INN` | P | Seek lodging. |
| `SERVICE.RENT_ROOM` | P | Request a room for a time. |
| `SERVICE.SERVE_FOOD` | N | Offer or deliver food. |
| `SERVICE.ORDER_FOOD` | P | Request a meal or drink. |
| `SERVICE.ASK_STABLE` | P | Seek mount storage or care. |
| `SERVICE.HIRE_TRANSPORT` | P | Request cart, taxi, ferry, or shuttle. |
| `SERVICE.ASK_TRAINING` | P | Seek instruction or skill improvement. |
| `SERVICE.OFFER_TRAINING` | N | Offer instruction for cost or favor. |
| `SERVICE.ASK_WORK` | P | Seek ordinary employment. |
| `SERVICE.OFFER_WORK` | N | Offer ordinary employment. |
| `SERVICE.REPORT_LOCAL_PROBLEM` | N | Describe a civic problem. |
| `SERVICE.ASK_LOCAL_NEWS` | P | Ask about recent local events. |
| `SERVICE.GIVE_LOCAL_NEWS` | N | Report recent local events. |
| `SERVICE.REQUEST_DONATION` | N | Ask for charitable support. |
| `SERVICE.BEG` | B | Ask for basic aid without a transaction. |
| `SERVICE.CELEBRATE` | B | Invite or respond to a festival or victory. |

## 17. Spacecraft, stations, and travel

| ID | Actor | Meaning |
|---|:---:|---|
| `SPACE.REQUEST_DOCKING` | B | Ask a station or ship for docking clearance. |
| `SPACE.GRANT_DOCKING` | B | Approve docking. |
| `SPACE.DENY_DOCKING` | B | Refuse docking. |
| `SPACE.REQUEST_LAUNCH` | B | Ask for launch clearance. |
| `SPACE.SET_COURSE` | B | Order or announce a destination. |
| `SPACE.CHANGE_COURSE` | B | Revise a destination or trajectory. |
| `SPACE.REPORT_POSITION` | B | State coordinates or relative location. |
| `SPACE.REPORT_FUEL` | B | State fuel or reaction-mass status. |
| `SPACE.REQUEST_REFUEL` | B | Ask for fuel transfer. |
| `SPACE.REPORT_HULL` | B | State structural condition. |
| `SPACE.REPORT_LIFE_SUPPORT` | B | State atmosphere or environmental status. |
| `SPACE.ABANDON_SHIP` | B | Order evacuation. |
| `SPACE.SEND_DISTRESS` | B | Broadcast an emergency request. |
| `SPACE.ANSWER_DISTRESS` | B | Offer aid to a distressed craft. |
| `SPACE.REQUEST_TOW` | B | Ask another craft to pull or recover the ship. |
| `SPACE.INITIATE_JUMP` | B | Order or announce faster-than-light travel. |

## 18. Technology, hacking, robots, and cybernetics

| ID | Actor | Meaning |
|---|:---:|---|
| `TECH.ASK_SYSTEM_STATUS` | B | Request machine or network status. |
| `TECH.REPORT_SYSTEM_STATUS` | B | Provide machine or network status. |
| `TECH.RUN_DIAGNOSTIC` | B | Request or announce a diagnostic. |
| `TECH.REBOOT_SYSTEM` | B | Request or announce restart. |
| `TECH.OVERRIDE_SAFETY` | B | Request bypass of a machine interlock. |
| `TECH.REFUSE_OVERRIDE` | B | Reject an unsafe or unauthorized bypass. |
| `TECH.REQUEST_ACCESS` | B | Ask for digital permission. |
| `TECH.GRANT_ACCESS` | B | Approve digital permission. |
| `TECH.DENY_ACCESS` | B | Reject digital permission. |
| `TECH.HACK_SYSTEM` | B | Request or announce fictional system intrusion. |
| `TECH.TRACE_INTRUSION` | B | Seek the source of fictional intrusion. |
| `TECH.UPLOAD_DATA` | B | Transfer data into a system. |
| `TECH.DOWNLOAD_DATA` | B | Retrieve data from a system. |
| `TECH.REPAIR_ANDROID` | B | Request or offer synthetic-body repair. |
| `TECH.INSTALL_IMPLANT` | B | Request or offer cybernetic installation. |
| `TECH.CHALLENGE_AI_PERSONHOOD` | B | Debate whether a synthetic mind is a person. |

## 19. Alien contact and interspecies diplomacy

| ID | Actor | Meaning |
|---|:---:|---|
| `CONTACT.INITIATE` | B | Open communication with an unknown species. |
| `CONTACT.SIGNAL_PEACE` | B | Declare non-hostile intent. |
| `CONTACT.ASK_LANGUAGE` | B | Ask how communication works. |
| `CONTACT.REQUEST_TRANSLATION` | B | Seek translation between languages. |
| `CONTACT.CLARIFY_CUSTOM` | B | Ask about unfamiliar social practice. |
| `CONTACT.EXPLAIN_CUSTOM` | B | Explain a species or culture practice. |
| `CONTACT.OFFER_GIFT` | B | Present a diplomatic gift. |
| `CONTACT.REJECT_GIFT` | B | Decline a diplomatic gift. |
| `CONTACT.NEGOTIATE_BORDER` | B | Discuss territorial boundaries. |
| `CONTACT.NEGOTIATE_EXCHANGE` | B | Propose knowledge or resource exchange. |
| `CONTACT.WARN_CONTAMINATION` | B | Warn of biological or technological contamination. |
| `CONTACT.REQUEST_QUARANTINE` | B | Ask for isolation protocol. |
| `CONTACT.MISUNDERSTAND_GESTURE` | B | Interpret a signal incorrectly. |
| `CONTACT.REPAIR_MISUNDERSTANDING` | B | Correct an interspecies mistake. |
| `CONTACT.DEMAND_WITHDRAWAL` | B | Order another group out of an area. |
| `CONTACT.FORM_COALITION` | B | Establish multi-species cooperation. |

## 20. Tutorial, system, multiplayer, and accessibility

| ID | Actor | Meaning |
|---|:---:|---|
| `META.ASK_CONTROLS` | P | Ask how to perform an input action. |
| `META.EXPLAIN_CONTROLS` | S | Explain input controls. |
| `META.ASK_MECHANIC` | P | Ask how a game rule works. |
| `META.EXPLAIN_MECHANIC` | S | Explain a game rule. |
| `META.ASK_OBJECTIVE` | P | Ask what to do next. |
| `META.GIVE_HINT` | S | Provide limited guidance. |
| `META.REQUEST_SKIP` | P | Ask to skip dialogue, scene, or tutorial. |
| `META.CONFIRM_SKIP` | S | Confirm destructive or irreversible skipping. |
| `META.REQUEST_SAVE` | P | Ask to save progress. |
| `META.REQUEST_LOAD` | P | Ask to restore progress. |
| `META.INVITE_PLAYER` | P | Invite another human to a session or party. |
| `META.ACCEPT_INVITE` | P | Join another human's session or party. |
| `META.REPORT_PLAYER` | P | Report multiplayer misconduct. |
| `META.REQUEST_MUTE` | P | Ask to silence another player. |
| `META.REQUEST_ACCESSIBILITY` | P | Ask for captions, contrast, timing, or input support. |
| `META.REPORT_BUG` | P | Describe behavior believed to be a software defect. |

## Generation requirements

For each behavior ID, generate contrasts rather than isolated paraphrases:

1. At least 20 direct forms and 10 history-dependent forms.
2. Friendly, neutral, frustrated, hostile, and distressed language where the
   behavior permits it.
3. Question, command, fragment, correction, pronoun, slang, typo, and profanity
   variants.
4. Positive and hard-negative pairs that differ by one important word:
   `SELL ME THE RIFLE` versus `TELL ME ABOUT THE RIFLE`.
5. Actor-role contrasts: a merchant may quote a price; a guard may refuse trade.
6. State contrasts: rapport, faction, inventory, quest stage, and danger can
   change the correct action without changing the words.
7. Tool-backed targets must use typed slots and caller-owned truth.
8. Split all paraphrases of one semantic seed together.

Do not train all behaviors as reply-generating intents. Reports, state changes,
and directives may produce `ACT`, `CALL_TOOL`, `CLARIFY`, `REFUSE`, or
`NO_RESPONSE` depending on game policy.
