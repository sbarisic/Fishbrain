"""Project-owned v4 episodes. Each line is an authored exchange, not an entity permutation.

Fields are alternating dialogue turns, with the final field the social response.
The annotation table is separate from the prose; runtime routing never reads this file.
"""

SOCIAL = {
'openings': '''
hey|Hey. What brings you here?
hello there|Hello. It's good to have some company.
morning|Good morning. How has yours been?
evening, stranger|Good evening. Taking a break from the road?
hi, got a moment?|Of course. What's on your mind?
anyone around?|Hello. Did you want to talk?
nice to meet you|Likewise. How is your day going?
well met|Well met. A quiet moment to talk sounds welcome.
hello again|Hello again. How have things been?
hiya|Hi. Settling in all right?
can we talk for a bit|Certainly. Where would you like to start?
I could use some company|You're welcome to stay and talk.
am I interrupting?|No trouble. What did you want to discuss?
hope your day is going well|Thank you. How has your day treated you?
just stopping by to say hello|Hello. A friendly visit is always welcome.
good to see a friendly face|A little company can make a long day easier.
I've never spoken to you before|Then let's start with hello. What brings you by?
hello, sorry to bother you|Hello. There's no need to apologize for saying hello.
mind if I sit and chat?|Please do. We can take our time.
hey there! long day?|A quiet conversation would be welcome. How was yours?
''',
'feelings': '''
I'm nervous about meeting everyone|Meeting new people can be daunting. What worries you most?
today has been exhausting|That sounds tiring. Would you rather talk or have a quiet moment?
I finally finished something difficult|That must feel rewarding. What helped you keep going?
I'm disappointed in myself|One disappointment doesn't have to define you. What happened?
I feel out of place here|That sounds lonely. Has anything helped you feel more at ease?
I miss my friends|Being apart can be hard. What do you miss doing together?
I'm excited about tomorrow|Something to look forward to makes a difference. What's happening?
I can't seem to relax|Would talking about it help, or would another subject be easier?
I'm annoyed but it's not your fault|Thanks for saying so. Do you want to tell me what happened?
I don't feel like talking much|That's all right. You don't need to fill the silence.
I'm proud of how far I've come|That sounds worth holding on to. What changed along the way?
everything feels a bit dull lately|That can wear on you. Has anything caught your interest recently?
I embarrassed myself earlier|Those moments can linger. Would it help to talk it through?
I wish I'd been more patient|It sounds like you're thinking carefully about it. What would you do differently?
I've been looking forward to a quiet evening|A chance to slow down sounds welcome.
someone was kind to me today|That can change a whole day. What did they do?
I'm still upset about an argument|Arguments can stay with you. What part is bothering you most?
I feel better after that walk|Glad it helped. Was it the quiet or the change of scene?
I'm worried I'll let someone down|That sounds like a lot of pressure. What are they expecting?
I don't know how I feel|You don't have to put a name to it straight away.
''',
'small_talk': '''
how are you?|Glad to have a moment to talk. How are you doing?
what a quiet afternoon|A bit of quiet can be welcome. Are you enjoying it?
do you enjoy a good story?|A well-told story is hard to resist. Do you have one in mind?
is silence always awkward?|Sometimes it's comfortable. It depends on the company.
I like watching clouds|There's room for imagination in that. What do you see in them?
the road felt very long today|Long journeys can wear on you. Was there a good moment along the way?
some days go by so quickly|They do seem to. Did something keep you busy?
it's nice to slow down|It can be. What do you notice when you take your time?
I never know how to start a conversation|You've already started one. We can take it from here.
do you think small things matter?|A small kindness can matter a great deal to the person receiving it.
I saw a really strange shaped tree|That sounds memorable. What did it look like?
I like the sound of rain|It can make a quiet moment feel even quieter.
what makes a day good?|Good company and something worth doing seem like a fair start.
I keep noticing little details lately|What caught your attention today?
sometimes walking helps me think|A little space can help thoughts settle. What have you been thinking about?
can a place feel familiar on a first visit?|Something about it might remind you of another place. Does that fit?
I wonder what makes people laugh|Surprise can be part of it. What usually makes you laugh?
do you prefer a long story or a short one?|Either can work if there's something worth telling.
I like when conversations wander|Sometimes the unexpected subject is the most interesting one.
it's good to have nothing urgent to do|A little breathing room can be welcome. How will you spend it?
''',
'preferences': '''
I like painting|What do you like to paint?|landscapes mostly|What draws you to landscapes?
I'd rather walk than ride|Is it the pace you enjoy?|yes, I notice more|That makes sense. What have you noticed lately?
I prefer quiet places|What makes them comfortable for you?|room to think|A bit of space for your thoughts can be valuable.
I love cooking|Is there a part you enjoy most?|trying things without a recipe|The surprise must be part of the fun.
Stories are my favorite pastime|What sort of stories?|ones with an unexpected ending|An ending that changes everything can stay with you.
I don't like crowds|Is it the noise or something else?|mostly the noise|Then a quieter conversation sounds more comfortable.
I collect interesting stones|What makes one interesting to you?|unusual colors|Do you look for a particular color or enjoy the surprise?
I like early mornings|What do you enjoy about them?|the stillness|That quiet before things get busy can feel special.
I prefer drawing to writing|Does drawing feel easier?|it lets me think without words|That sounds like a useful way to sort through ideas.
I enjoy fixing things|What feels satisfying about it?|seeing something work again|There's a clear result to all that patience.
I like singing with other people|Is it different from singing alone?|much less intimidating|Sharing the moment can make it easier.
I never liked competitive games|What would you play instead?|something where we work together|Working toward the same goal can be its own fun.
I enjoy learning new words|Do you have a favorite recently?|not yet, I'm still looking|Then finding one is part of the enjoyment.
I prefer simple meals|Is that about taste or preparation?|both really|Sometimes simple things leave more room to enjoy them.
I love puzzles|What kind holds your attention?|the ones that make me change my approach|That moment of seeing it differently can be satisfying.
I like exploring on foot|Do you plan the route?|not always|Leaving room for a surprise can be part of the appeal.
I enjoy gardening|What do you enjoy most?|watching slow changes|Small changes can be easier to appreciate when you pay attention.
I prefer listening to talking|Does that make conversations easier?|usually, yes|There's no need to rush your turn to speak.
I like making gifts|Is choosing the gift the fun part?|making it personal is|Thinking about the person can be the most meaningful part.
I dislike being hurried|Does it make decisions harder?|yes, even small ones|We can take this conversation at your pace.
''',
'clarification': '''
Can you help?|What do you need help with?|with that thing|Which thing do you mean?
I want to change it|What would you like to change?|the one we discussed|Could you remind me which one?
Something went wrong|What happened?|it stopped|What stopped working?
I need another|Another of what?|the usual|Please tell me what you mean by the usual.
Can you explain?|Which part is unclear?|the last bit|Could you point to the part you want explained?
Let's do it|What did you have in mind?|you choose|What are we choosing between?
That seems unfair|What seems unfair to you?|what they did|What did they do?
I can't find the right words|Take your time.|you know what I mean?|Not quite yet. Could you describe it another way?
I want something different|Different in what way?|less like this|What would you most like to change?
There's a problem with my plan|Which part?|the timing|What needs to happen, and when?
Can you tell them?|Who do you mean?|the person from before|Which person, and what would you like to tell them?
I'm looking for a place|What kind of place?|somewhere suitable|What would make it suitable for you?
I need to decide soon|What are the options?|two things|What are the two choices?
That doesn't make sense|Which part seems wrong?|the explanation|What part of the explanation should we go over?
I've changed my mind|About what?|going there|Where were you thinking of going?
It was better last time|What was better?|the whole thing|Could you give me one example of what changed?
I don't know what to ask|We can start with what you're trying to do.|I need help choosing|What are you choosing between?
Maybe the other one|Which alternatives are you considering?|I'm not sure|Let's first work out what you're looking for.
Can we talk about something else?|Of course.|that thing you mentioned|Which thing did you have in mind?
I have a question|Go ahead.|is it allowed?|What would you like to do?
''',
'repair': '''
I thought you were upset|What gave that impression?|sorry, I read your tone wrong|No harm done. Thanks for clearing that up.
That came out badly|Would you like to try again?|I meant to ask, not demand|Thanks for explaining. What did you want to ask?
I said I hated it|Was that stronger than you meant?|yes, I just didn't enjoy it|That sounds like a useful distinction.
You seem to have misunderstood|Which part?|I wanted advice, not a decision|Understood. We can talk through the options together.
I was joking earlier|Thanks for telling me.|it sounded serious, didn't it?|A little. Knowing you meant it as a joke helps.
I didn't explain that well|We can go over it again.|I meant the journey, not the destination|Thanks, that makes the distinction clearer.
I wasn't ignoring you|All right.|I just needed time to think|Take the time you need.
I said everyone agreed|Was that too broad?|yes, only some people did|Thanks for correcting that.
That sounded rude|Would you like to rephrase it?|I meant I was tired, not bored with you|Thanks for saying so. A pause is fine.
I got ahead of myself|What would you like to revisit?|the question before the answer|Let's go back to the question.
I used the wrong word|Which word did you mean?|surprised, not angry|That gives it a different meaning. Thanks for clarifying.
I wasn't asking you to act|What did you intend?|just thinking out loud|All right. We can talk it through without taking action.
I left something out|What should we add?|I had already tried asking politely|That changes the context. What happened when you asked?
I don't mean to argue|That's all right.|I just see it differently|We can make room for different views.
I thought you meant today|Did you mean another time?|yes, sometime later|Thanks for clearing up the timing.
I repeated myself|It happens.|sorry, I lost track|No need to apologize. Where would you like to pick up?
I was too quick to judge|What changed your mind?|I heard the other side|Hearing more can change how a situation looks.
I made it sound certain|How sure are you?|not very, honestly|Then we can treat it as a possibility.
I didn't mean everyone|Who did you mean?|just the people I spoke to|That is a more specific claim. Thanks for clarifying.
I was answering the earlier question|Thanks for explaining.|can we go back to that?|Certainly. Remind me which question you want to return to.
''',
'topic_changes': '''
I like long walks|What do you enjoy about them?|Being outdoors|A change of scene can be welcome.|Especially near trees|What catches your eye there?|Actually, let's talk about stories|Of course. What kind of story interests you?
I've been learning to draw|What are you drawing?|Mostly faces|Expressions can be interesting.|They are hard to get right|What part is hardest?|Enough about drawing. How was your day?|A quiet conversation is welcome. How has your day been?
I enjoy cooking|What do you enjoy making?|Simple meals|Simple can be satisfying.|They leave time for other things|What else do you enjoy?|Can we discuss music instead?|Certainly. What music do you enjoy?
The walk was tiring|Was it a long way?|Longer than I expected|That can wear on you.|I stopped a few times|A pause can help.|Anyway, I have a question about patience|Go ahead. What would you like to know?
I've been reading|What caught your interest?|A story about friendship|Friendship can make a good story.|The ending surprised me|How did it change your view?|Never mind the ending. I feel lonely|That sounds hard. Would you like to talk about what's been missing?
I tried a new game|How did it go?|I lost quickly|Learning can take time.|It was still fun|What made it enjoyable?|Let's leave games aside. What makes a good listener?|Giving someone room to finish seems like a good start.
I couldn't sleep well|Was something on your mind?|A decision|Decisions can linger.|I kept weighing the options|What are you weighing?|I'd rather talk about something cheerful|Of course. Has anything made you smile recently?
I saw an unusual bird|What was unusual?|Its call|Sounds can catch your attention.|It kept repeating|Did you recognize it?|Different subject: do you like riddles?|A good riddle can be fun. Do you have one?
I enjoy making things|What do you make?|Little gifts|That sounds thoughtful.|I try to suit each person|What helps you choose?|Could we talk about courage now?|Certainly. What does courage mean to you?
The rain delayed me|Did you find shelter?|Eventually|A chance to stop can help.|I spent the time thinking|About what?|Actually, I'd rather hear a question from you|What has held your interest lately?
I went for a walk|Did it help you unwind?|A little|What stood out?|The quiet|Quiet can make space to think.|Back to the puzzle I mentioned: it still bothers me|Which part of the puzzle is keeping you stuck?
I like old stories|What draws you to them?|Different ways of seeing things|Stories can offer that.|Even familiar ones change|What changes for you?|Let's talk about the present instead|All right. What's on your mind today?
I had a busy morning|What kept you busy?|Several small tasks|They can add up.|None were difficult alone|But together they took time?|Yes. Now I'd like a lighter subject|What do you usually enjoy talking about when you unwind?
I tried writing a poem|How did it feel?|Awkward at first|Finding words can take time.|One line finally worked|That sounds encouraging.|Could we stop discussing it for now?|Of course. We can leave it there.
I was thinking about trust|What brought it to mind?|A promise|Promises can matter.|Especially small ones|They can carry weight.|On another note, I found a funny shape in a cloud|What did it look like?
I spent the morning reading|Was it interesting?|Parts of it|Which parts?|The descriptions|What made them stand out?|Forget the book. I'm upset about something else|All right. What happened?
I like quiet games|What makes a game quiet?|No hurry|Time to think can be welcome.|And no shouting|That sounds calmer.|Do you mind if I change the subject?|Not at all. What would you like to discuss?
I tried a new path|What was different?|The view|Did anything stand out?|The trees|What about them?|Let's talk about tomorrow instead|What are you thinking about for tomorrow?
I heard a long story|Did you enjoy it?|Most of it|What stayed with you?|A difficult choice|Those can be interesting.|Actually, I need to make a choice myself|What are your options?
I made a small mistake|Was it possible to fix?|Yes|That's a relief.|It took some patience|What helped you stay with it?|Enough of that. Thanks for listening|You're welcome. Thanks for sharing it.
''',
'follow_ups': '''
I want to try something new|What interests you?|I'm not sure|What have you enjoyed before?
I keep abandoning my stories|Where do you usually get stuck?|the middle|What feels missing between the beginning and the ending?
I want to be a better listener|What would you like to change?|I interrupt too much|What usually makes you feel you need to jump in?
I'm choosing a gift|What does the person enjoy?|making things|Would something useful for that interest suit them?
I'd like a calmer day|What's making it feel rushed?|too many small tasks|Which of those actually needs attention today?
I'm planning a walk|What kind of walk?|a quiet one|Would you rather focus on the scenery or have time to think?
I want to learn a skill|What matters most to you?|making something with my hands|What kinds of things would you enjoy making?
I'm trying to finish a story|What would help you finish?|knowing how it ends|What choice does the main character still need to make?
I want to make amends|What happened?|I spoke without listening|What do you want the other person to understand?
I'd like to explain myself better|What gets in the way?|I rush|Would starting with one main point help?
I'm organizing my thoughts|What are you trying to work out?|whether to stay or go|What would you miss about each option?
I want to keep a promise|What might make that difficult?|I agreed too quickly|What did you commit to doing?
I'm looking for inspiration|What are you working on?|a drawing|Is there something nearby you see differently today?
I need a quiet pastime|What do you usually enjoy?|working with patterns|Would a puzzle or a design give you that kind of focus?
I want to ask for help|What makes it difficult?|I don't want to impose|What kind of help would make the biggest difference?
I'd like to reconnect with someone|What would you like to say?|that I miss our conversations|Would a simple, honest opening feel right?
I'm deciding what to practice|What do you want to improve?|patience|Where do you find patience hardest at the moment?
I want to understand a disagreement|What seems unclear?|why it mattered so much|Have you had a chance to hear what it meant to them?
I'm trying to rest|What's keeping you occupied?|thoughts about tomorrow|Would setting those thoughts aside for a moment feel possible?
I want to enjoy learning again|What used to make it enjoyable?|discovering things myself|What small question would you like to explore next?
''',
}


def social_episodes():
    for category, prose in SOCIAL.items():
        for index, line in enumerate(prose.strip().splitlines()):
            turns = line.split('|')
            assert len(turns) % 2 == 0, (category, line)
            if category in FRESH_RESPONSES:
                turns[-1]=FRESH_RESPONSES[category][index]
            yield dict(id=f'{category}-{index:02}', category=category, turns=turns[:-1], response=turns[-1])


# Fresh sessions have no fabricated agenda. These replies do not require an existing
# commitment to justify a follow-up question. Longer episodes teach goal-based questions.
FRESH_RESPONSES = {
'openings': [
 'Hey. Good to see you.', 'Hello. A bit of company is welcome.', 'Good morning to you.',
 'Good evening. Come and take a moment to rest.', 'Of course. Go ahead.', 'Hello. You have my attention.',
 'Likewise. Glad we had a chance to meet.', 'Well met, friend.', 'Hello again. Welcome back.', 'Hi there.',
 'Certainly. We can take our time.', 'You are welcome to stay and talk.', 'No trouble at all. Go ahead.',
 'Thank you. That is kind of you.', 'Hello. A friendly visit is always welcome.', 'A little company can make a long day easier.',
 'Then hello, and welcome.', 'Hello. There is no need to apologize for saying hello.',
 'Please do. There is no hurry.', 'Hello. A quiet conversation sounds welcome.'],
'feelings': [
 'Meeting new people can be daunting. You can take it at your own pace.', 'That sounds tiring. Take a moment to breathe.',
 'That must feel rewarding after all the effort.', 'One disappointment does not have to define you.',
 'Feeling out of place can be lonely. You do not need to pretend otherwise.', 'Being apart from friends can be hard.',
 'Having something to look forward to can brighten a day.', 'It can be hard to settle when your thoughts keep moving.',
 'Thanks for saying so. You can tell me about it if you want.', 'That is all right. You do not need to fill the silence.',
 'That sounds worth holding on to. Progress can take patience.', 'That sounds wearing. A dull stretch can feel very long.',
 'Those moments can linger, even when you wish they would not.', 'It sounds like you are thinking carefully about what happened.',
 'A chance to slow down sounds welcome.', 'A small kindness can change the way a day feels.',
 'Arguments can stay with you after the words are over.', 'Glad the walk helped you feel a little better.',
 'That sounds like a lot of pressure to carry.', 'You do not have to put a name to it straight away.'],
'small_talk': [
 'Glad to have a moment to talk.', 'A bit of quiet can be welcome.', 'A well-told story is hard to resist.',
 'Sometimes silence is comfortable. It depends on the company.', 'There is plenty of room for imagination in clouds.',
 'Long journeys can wear on you. A pause sounds welcome.', 'Busy days can seem to disappear quickly.',
 'Slowing down can make small details easier to notice.', 'You have already started a conversation. We can take it from here.',
 'A small kindness can matter a great deal to the person receiving it.', 'Unusual shapes can stay in your mind for a while.',
 'Rain can make a quiet moment feel even quieter.', 'Good company and something worth doing seem like a fair start.',
 'Paying attention can make familiar things seem new.', 'A little space can help thoughts settle.',
 'Something about a new place might remind you of somewhere familiar.', 'Surprise can be part of what makes something funny.',
 'Either can work if there is something worth telling.', 'Sometimes an unexpected subject is the most interesting one.',
 'A little breathing room can be welcome after a busy stretch.']}


# owner, fact kind, value, original report, intervening exchange, question, correction
MEMORIES = '''
PLAYER|HOME|WILLOW QUAY|My home is Willow Quay.|I enjoy walking by water.|What do you enjoy about it?|The sound mostly.|That sounds peaceful.|Where did I say my home was?|Actually, my home is Reed Bank.
PLAYER|OCCUPATION|POTTER|I'm a potter.|I tried a new glaze.|How did it turn out?|Not as expected.|Experiments can be surprising.|What work did I mention doing?|Correction: I'm a weaver.
PLAYER|NAME|MIRA|My name is Mira.|It's been a tiring day.|What kept you busy?|Small errands.|They can add up.|Do you remember my name?|I gave you a nickname. My name is Tessa.
PLAYER|ORIGIN|PINE REACH|I come from Pine Reach.|The journey felt long.|Did you have time to rest?|A little.|A pause can help.|Where am I from, according to what I told you?|I misspoke. My origin is Ash Vale.
NPC|HOME|STONE CROSSING|You said your home was Stone Crossing.|I like quiet evenings.|What makes them pleasant?|Time to think.|Quiet can make room for that.|Where did I say you lived?|I got your home wrong. Your home is Alder Rest.
PLAYER|HOME|BIRCH HARBOR|I live in Birch Harbor.|I've started sketching.|What do you draw?|Trees.|Their shapes can be interesting.|Can you recall the place I call home?|I moved. My home is Fern Hollow now.
PLAYER|OCCUPATION|COOPER|My occupation is cooper.|I like working carefully.|Does it help you relax?|Sometimes.|A steady pace can be welcome.|What occupation did I tell you?|That was my old occupation. I'm a baker now.
PLAYER|NAME|ORIN|You can call me Orin.|I find introductions awkward.|What makes them awkward?|Remembering names.|That can take practice.|What name did I give you earlier?|Let me correct that: my name is Rowan.
PLAYER|ORIGIN|DUNEROCK|My origin is Dunerock.|I prefer cooler weather.|What do you enjoy about it?|Walking without hurrying.|Taking your time can help.|What was the place I said I came from?|My origin is Sun Hollow, not Dunerock.
NPC|OCCUPATION|CARPENTER|I heard you're a carpenter.|I like handmade things.|What draws you to them?|Small imperfections.|They can make something distinctive.|What work did I say you do?|I misunderstood. Your occupation is mason.
PLAYER|HOME|SILVER FEN|My home is Silver Fen.|I enjoy telling stories.|What makes a story good?|A difficult choice.|Those can be compelling.|Which place did I name as my home?|Please replace that. My home is Heron Ford.
PLAYER|OCCUPATION|HERBALIST|I work as an herbalist.|I notice small details.|What caught your eye?|A patterned leaf.|Small things can be interesting.|What job was I talking about earlier?|I used the wrong word. My occupation is gardener.
PLAYER|NAME|ELLIS|I'm called Ellis.|I like simple games.|What makes them enjoyable?|Time with friends.|The company can matter most.|What should you call me?|Actually, call me Lark.
PLAYER|ORIGIN|HIGH MEADOW|I grew up in High Meadow.|I miss open space.|What do you miss about it?|Seeing the sky.|That sounds important to you.|What place did I say I grew up in?|I meant Low Meadow as my origin.
NPC|ORIGIN|RED BLUFF|You come from Red Bluff, as I understand it.|I enjoy hearing about places.|What interests you?|How people describe them.|Different views can be revealing.|Where did I say you come from?|I had that wrong. Your origin is White Bluff.
PLAYER|HOME|MOSS BRIDGE|The place I call home is Moss Bridge.|I tried writing a letter.|Was it difficult?|Finding the opening.|Starting can be the hardest part.|What place did I call home?|I should clarify: my home is Hazel Bend.
PLAYER|OCCUPATION|SCRIBE|My trade is scribe.|I like a clear explanation.|What makes one clear?|A simple example.|Examples can make ideas easier to picture.|Which trade did I say was mine?|That was mistaken. My occupation is bookbinder.
PLAYER|NAME|NELL|My name is Nell.|I prefer brief conversations.|Does that feel easier?|Usually.|We can keep it simple.|What was my name again?|Please use my actual name, Wren.
PLAYER|ORIGIN|EAST RIDGE|I originally came from East Ridge.|I enjoy watching birds.|What catches your attention?|The way they move.|Movement can be fascinating.|Where did I originally come from?|I mixed them up. My origin is West Ridge.
NPC|NAME|EDDA|I was told your name is Edda.|Names can be hard to remember.|What helps you remember?|Hearing them in conversation.|Repetition can help.|What name did I think was yours?|I misheard your name. Your name is Ada.
'''

CORRECTED = ['REED BANK','WEAVER','TESSA','ASH VALE','ALDER REST','FERN HOLLOW','BAKER','ROWAN','SUN HOLLOW','MASON',
             'HERON FORD','GARDENER','LARK','LOW MEADOW','WHITE BLUFF','HAZEL BEND','BOOKBINDER','WREN','WEST RIDGE','ADA']

TOOLS = '''
ASK|QUESTION|LIST_WARES|What do you have for sale?||
REQUEST|AFFIRMATIVE|LIST_WARES|Could I see your wares?||
ASK|QUESTION|GET_BALANCE|How much gold do I have?||
ASK|QUESTION|GET_INVENTORY|What's in my inventory?||
REQUEST|AFFIRMATIVE|BUY|I'd like to buy two rope.|TWO|ROPE
REQUEST|AFFIRMATIVE|SELL|Please sell one torch.|ONE|TORCH
REQUEST|NEGATED|BUY|Don't buy three apples.|THREE|APPLES
ASK|HYPOTHETICAL|BUY|Suppose I wanted to buy a sword?||SWORD
INFORM|QUOTED|BUY|The sign says "buy two rope".|TWO|ROPE
REQUEST|NEGATED|SELL|I don't want to sell my torch.||TORCH
REQUEST|AFFIRMATIVE|BUY|Buy one apple, please.|ONE|APPLE
REQUEST|AFFIRMATIVE|SELL|Sell two rope for me.|TWO|ROPE
ASK|QUESTION|NONE|Would buying a torch be a good idea?||
REQUEST|AFFIRMATIVE|LIST_WARES|Show me what's available.||
REQUEST|AFFIRMATIVE|GET_BALANCE|Check my gold balance.||
REQUEST|AFFIRMATIVE|GET_INVENTORY|Let me see what I'm carrying.||
REQUEST|NEGATED|BUY|No purchase, please. I don't want a rope.||ROPE
INFORM|HYPOTHETICAL|SELL|If I sold one sword, I'd have less to carry.|ONE|SWORD
INFORM|QUOTED|SELL|Someone wrote "sell three apples" on the note.|THREE|APPLES
REQUEST|NEGATED|BUY|Cancel that. Do not buy the torch.||TORCH
'''

COMPOUNDS = '''
Hello.|Who are you?|GREET|ASK|ACKNOWLEDGE|ANSWER|NAME
Hi there.|What do you do?|GREET|ASK|ACKNOWLEDGE|ANSWER|OCCUPATION
Good morning.|Where do you come from?|GREET|ASK|ACKNOWLEDGE|ANSWER|ORIGIN
Thanks for listening.|Can we talk about something else?|THANK|REQUEST|ACKNOWLEDGE|ASK_FOLLOW_UP|NONE
Sorry for interrupting.|What is your name?|APOLOGIZE|ASK|ACKNOWLEDGE|ANSWER|NAME
I don't want to buy anything.|Show me your wares.|REFUSE|REQUEST|ACKNOWLEDGE|EXECUTE_TOOL|NONE
Hello again.|How much gold am I carrying?|GREET|ASK|ACKNOWLEDGE|EXECUTE_TOOL|BALANCE
That's helpful, thank you.|What's in my inventory?|THANK|ASK|ACKNOWLEDGE|EXECUTE_TOOL|INVENTORY
I'm tired.|Can we keep this brief?|INFORM|REQUEST|ACKNOWLEDGE|ACKNOWLEDGE|NONE
I'm sorry I snapped.|Let's start over.|APOLOGIZE|REQUEST|ACKNOWLEDGE|ACKNOWLEDGE|NONE
I enjoyed our conversation.|Goodbye for now.|INFORM|FAREWELL|ACKNOWLEDGE|FAREWELL|NONE
That makes sense.|Could you ask me a different question?|ACCEPT|REQUEST|ACKNOWLEDGE|ASK_FOLLOW_UP|NONE
Thank you.|I need a moment to think.|THANK|INFORM|ACKNOWLEDGE|ACKNOWLEDGE|NONE
Good evening.|What can you help me with?|GREET|ASK|ACKNOWLEDGE|ANSWER|CAPABILITIES
I didn't mean to be rude.|May I ask where you live?|APOLOGIZE|ASK|ACKNOWLEDGE|ANSWER|HOME
I changed my mind about buying.|Just check my balance.|CORRECT|REQUEST|CORRECT|EXECUTE_TOOL|BALANCE
First, buy one rope.|Then sell two apples.|REQUEST|REQUEST|EXECUTE_TOOL|ACKNOWLEDGE|NONE
Show me the wares.|Then buy one torch.|REQUEST|REQUEST|EXECUTE_TOOL|ACKNOWLEDGE|NONE
Check my inventory.|Then sell one sword.|REQUEST|REQUEST|EXECUTE_TOOL|ACKNOWLEDGE|INVENTORY
Don't buy the apple.|Please check my gold instead.|REFUSE|REQUEST|ACKNOWLEDGE|EXECUTE_TOOL|BALANCE
'''


def functional_episodes():
    for index, line in enumerate(MEMORIES.strip().splitlines()):
        owner, kind, value, report, *rest = line.split('|')
        detour, question, correction = rest[:4], rest[4], rest[5]
        history = [report, 'Thanks for telling me.'] + detour
        metadata = dict(owner=owner, kind=kind, value=value, corrected=CORRECTED[index])
        yield dict(id=f'memory-{index:02}', category='memory', turns=history + [question], response=None, **metadata)
        yield dict(id=f'correction-{index:02}', category='corrections', turns=history + [correction], response='Thanks for correcting that.', **metadata)
    for index, line in enumerate(TOOLS.strip().splitlines()):
        act, status, tool, text, quantity, item = line.split('|')
        yield dict(id=f'tool-{index:02}', category='tools', turns=[text], response=None, act=act, status=status,
                   tool=tool, quantity=quantity, item=item)
    for index, line in enumerate(COMPOUNDS.strip().splitlines()):
        first, second, act1, act2, plan1, plan2, knowledge = line.split('|')
        yield dict(id=f'compound-{index:02}', category='compound', turns=[first + ' ' + second], response=None,
                   clauses=[first, second], acts=[act1, act2], plans=[plan1, plan2], knowledge=knowledge)


def all_episodes():
    episodes=list(social_episodes()) + list(functional_episodes())
    identities=[('What should I call you?','NAME'),('Who are you?','ROLE'),('Where are you from?','ORIGIN'),
                ('Where do you live?','HOME'),('Tell me about your family','FAMILY'),('What do you do for a living?','OCCUPATION'),
                ('What are you like?','TRAITS'),('How can you help me?','CAPABILITIES'),('Your name?','NAME'),
                ('What is your role here?','ROLE'),('Where did you grow up?','ORIGIN'),('Where is your home?','HOME'),
                ('Do you have relatives?','FAMILY'),('Tell me about your work','OCCUPATION'),('What sort of person are you?','TRAITS'),
                ('What can you do?','CAPABILITIES'),('May I ask who you are?','ROLE'),('Could you introduce yourself?','NAME'),
                ('What do people call you?','NAME'),('Can you tell me your occupation?','OCCUPATION')]
    for index,(text,knowledge) in enumerate(identities):
        episodes.append(dict(id=f'identity-{index:02}',category='identity',turns=[text],response=None,knowledge=knowledge))
    clarifications=[
        ('Can you help me with something?','What would you like help with?','ASK'),
        ('I need to make a choice','What are the choices you are considering?','INFORM'),
        ('Could you explain that to me?','Which part would you like explained?','ASK'),
        ('I want to find someone','Who are you looking for?','INFORM'),
        ('Can I ask you about a place?','Which place did you have in mind?','ASK'),
        ('I have a problem','What has been happening?','INFORM'),
        ('Please help me decide what to do','What situation are you deciding about?','REQUEST'),
        ('Something is bothering me','Would you like to tell me what is bothering you?','INFORM'),
        ('I need some advice','What would you like advice about?','INFORM'),
        ('Could we work something out?','What would you like to work out together?','ASK'),
        ('I would like an explanation','What would you like to understand better?','INFORM'),
        ('I need directions','Where are you trying to go?','INFORM'),
        ('Can you tell me more?','What would you like to hear more about?','ASK'),
        ('I am trying to remember something','What do you remember about it so far?','INFORM'),
        ('Help me figure this out','What are you trying to figure out?','REQUEST'),
        ('I want to change my plan','Which part of your plan would you like to change?','INFORM'),
        ('Could you answer a question?','Certainly. What is your question?','ASK'),
        ('I need a different approach','What have you tried already?','INFORM'),
        ('Please explain the difference','Which things would you like to compare?','REQUEST'),
        ('Can we discuss a decision?','What decision is on your mind?','ASK')]
    for index,(text,response,act) in enumerate(clarifications):
        episodes.append(dict(id=f'fresh-clarification-{index:02}',category='fresh_clarification',turns=[text],response=response,act=act))
    for index,line in enumerate(MEMORIES.strip().splitlines()):
        owner,kind,value,report,*rest=line.split('|')
        if owner!='PLAYER': continue
        question={'HOME':'Where is your home?','NAME':'What is your name?','OCCUPATION':'What is your occupation?','ORIGIN':'Where do you come from?'}[kind]
        episodes.append(dict(id=f'agenda-{index:02}',category='agenda',turns=['You wanted to ask me something.',question]+rest[:4]+[report],
                             response='Thanks for coming back to my question.',kind=kind,value=value,augmentationOf=f'memory-{index:02}'))
    return episodes
