# Paired conversation rollouts

Each model receives the same player turns, but uses its own replies and reduced state as history. These are actual runtime rollouts, not gold-history interpretation tests.

Naturalness, relevance and unsupported claims need review. No human ratings have been filled in. The original JSONL exports retain the fields used by `conversation-gate`.

Sessions: 10. Player turns: 18. Multi-turn sessions: 1.

Baseline export SHA-256: `5b886bb1a7d582fc1f439a2aa20fc364a112eb8a9d300694db8a8202220537e1`.
Pilot export SHA-256: `b937412a2d0d138504541358c8bd83d4b89a698d8aa23fac67190707b6783f05`.

## user-transcript-development

| Player | Failed model | Conversation-v4 pilot |
| --- | --- | --- |
| Hello | I WILL HANDLE ONE ACTION AT A TIME. | HI THERE. |
| How are you? | I MEANT THAT OLD MAPS NEAR CRYSTAL CAVE DURING CEREMONY CAN CHANGE HOW A JOURNEY FEELS. | COULD YOU EXPLAIN WHAT YOU MEAN? |
| How did we get here? | I DO. OLD MAPS NEAR CRYSTAL CAVE DURING CEREMONY CAN MAKE A LONG DAY BETTER. | COULD YOU EXPLAIN WHAT YOU MEAN? |
| Who are you? | I MEANT THAT EARLY TRAINS NEAR CRYSTAL CAVE DURING TRIAL CAN CHANGE HOW A JOURNEY FEELS. | COULD YOU EXPLAIN WHAT YOU MEAN? |
| Hello. Who are you? | I MEANT THAT EARLY TRAINS NEAR CRYSTAL CAVE DURING CEREMONY CAN CHANGE HOW A JOURNEY FEELS. | COULD YOU EXPLAIN WHAT YOU MEAN? |
| What do you have for sale? | I AM NOT CERTAIN ABOUT THAT. | COULD YOU EXPLAIN WHAT YOU MEAN? |
| My home is Cedar Hollow. | I AM NOT CERTAIN ABOUT THAT. | COULD YOU EXPLAIN WHAT YOU MEAN? |
| How much gold do I have? | I CANNOT IDENTIFY ONE CLEAR MEMORY ABOUT THAT. | COULD YOU EXPLAIN WHAT YOU MEAN? |
| I have 10 gold | I WILL REMEMBER THAT FACT. | COULD YOU EXPLAIN WHAT YOU MEAN? |

## fresh-development-1

| Player | Failed model | Conversation-v4 pilot |
| --- | --- | --- |
| Hello | I WILL HANDLE ONE ACTION AT A TIME. | HI THERE. |

## fresh-development-2

| Player | Failed model | Conversation-v4 pilot |
| --- | --- | --- |
| How are you? | I DO. RAINY MORNINGS NEAR CRYSTAL CAVE DURING CEREMONY CAN MAKE A LONG DAY BETTER. | I AM FROM THIS VILLAGE. |

## fresh-development-3

| Player | Failed model | Conversation-v4 pilot |
| --- | --- | --- |
| How did we get here? | I DO. OLD MAPS NEAR CRYSTAL CAVE DURING CEREMONY CAN MAKE A LONG DAY BETTER. | I AM A TRAVELER. |

## fresh-development-4

| Player | Failed model | Conversation-v4 pilot |
| --- | --- | --- |
| Who are you? | MY NAME IS ARIN. | I AM A TRAVELER. |

## fresh-development-5

| Player | Failed model | Conversation-v4 pilot |
| --- | --- | --- |
| Hello. Who are you? | I AM NOT CERTAIN ABOUT THAT. | MY NAME IS ARIN. |

## fresh-development-6

| Player | Failed model | Conversation-v4 pilot |
| --- | --- | --- |
| What do you have for sale? | I DO. RAINY MORNINGS NEAR CRYSTAL CAVE DURING CEREMONY CAN MAKE A LONG DAY BETTER. | I HAVE NOT TAKEN THAT ACTION. PLEASE CLARIFY YOUR REQUEST. |

## fresh-development-7

| Player | Failed model | Conversation-v4 pilot |
| --- | --- | --- |
| My home is Cedar Hollow. | I WILL HANDLE ONE ACTION AT A TIME. | SURE! HAVE YOU ARE THE MISTAKE. |

## fresh-development-8

| Player | Failed model | Conversation-v4 pilot |
| --- | --- | --- |
| How much gold do I have? | I DO. RAINY MORNINGS NEAR CRYSTAL CAVE DURING LANDING CAN MAKE A LONG DAY BETTER. | I WORK AS ROAD WARDEN. |

## fresh-development-9

| Player | Failed model | Conversation-v4 pilot |
| --- | --- | --- |
| I have 10 gold | LATE SUPPERS SMITH CROSSING SMITH SUMMIT CRYSTAL CAVE NEAR CRYSTAL CAVE NEAR CRYSTAL CAVE. | COULD YOU EXPLAIN WHAT YOU MEAN? |
