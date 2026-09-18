"""Project-owned compositional episodes. Entity substitutions are not counted as new episodes.

The audit exposes component reuse: these are 300 composed episodes, not 300
independently collected human conversations. Frozen tests use a separate catalogue.
"""
import hashlib, itertools, json, random
from pathlib import Path

SOCIAL = [line.split('|') for line in '''Hi there.|Hello! It's good to have some company.
Morning. Got a moment?|Of course. What's on your mind?
Evening, friend.|Good evening. How has your day been?
I'm just passing through.|Then take a moment to catch your breath.
That walk was exhausting.|Sounds like a pause would do you good.
I didn't sleep much last night.|That's a rough start to the day.
I'm feeling a bit lost.|Lost in the conversation, or looking for a place?
I don't really feel like talking.|That's all right. We can leave it there.
I'm in a good mood today.|I'm glad to hear it. What made the day better?
I finally finished a difficult job.|That must be a relief.
I made a mistake earlier.|Do you want to talk about what happened?
I'm worried about tomorrow.|What part of tomorrow is worrying you?
My boots are soaked.|That sounds uncomfortable after a long walk.
It was nice to sit down for a while.|Sometimes a quiet break is enough.
I like quiet mornings.|There's something pleasant about an unhurried start.
Crowds make me nervous.|Would you rather keep this conversation short?
I miss my old friends.|What do you miss most about them?
I enjoy hearing people's stories.|What sort of stories do you like?
I prefer walking to riding.|What do you enjoy about walking?
I like the sound of rain.|It can be soothing when you have somewhere dry to sit.
I wish I were better at drawing.|What do you like to draw?
I keep forgetting little things.|That can be frustrating. Is there something you want to remember here?
I'm not sure what to say.|You don't have to have anything clever prepared.
Well, that was awkward.|We can move on if you'd like.
Sorry, I misunderstood you.|No problem. Which part should we clear up?
That's not what I meant.|Thanks for saying so. What did you mean?
Can we change the subject?|Sure. What would you like to talk about?
Let's talk about something ordinary.|How has your day been?
I was only joking.|Ah, I see. Thanks for clearing that up.
Never mind that question.|All right. We can leave it there.
I didn't catch that.|Which part would you like me to repeat?
Could you keep it short?|Of course.
I'm glad we talked.|I'm glad you stopped by.
You've given me something to think about.|Take your time with it.
I think I need a break.|A little rest sounds sensible.
Thanks for listening.|You're welcome.
I don't agree with that.|That's fair. How do you see it?
I'm not asking for advice.|Understood. I'll just listen.
I want to figure this out myself.|All right. You can tell me how it goes if you want.
That reminds me of home.|What brought it to mind?
I'd rather not explain.|You don't have to.
Let's start again.|All right. Hello again.
I was distracted for a moment.|No problem. Take your time.
That was a long explanation.|I'll try to be more concise.
I like learning new words.|Have you heard an interesting one recently?
I enjoy mending things.|There's satisfaction in making something useful again.
I tried cooking something new.|How did it turn out?
I took a different path today.|Did the change make the walk more interesting?
I have a song stuck in my head.|Has it been following you around all day?
I found time to read at last.|What kind of story were you reading?
I don't want to rush.|We can take this slowly.
It feels good to laugh again.|I'm glad there's been room for that today.
I had an argument with someone.|Do you want to talk it through?
I'm trying to be more patient.|That can take practice.
I don't know whether to stay or leave.|What matters most in that decision?
I'm tired of making decisions.|Would a short break help?
I noticed something beautiful on the road.|What caught your eye?
I wish I had brought a notebook.|Was there something you wanted to write down?
Sometimes I enjoy doing nothing.|A quiet moment can be worthwhile too.
I think that's enough talking for now.|All right. Take care.'''.splitlines()]

QUESTIONS = {
'READ_PERSONA:NAME': ['Who are you?','Tell me your name.','What should I call you?','May I ask your name?','Remind me of your name.','How should I address you?'],
'READ_PERSONA:OCCUPATION':['What is your job?',"What's your job?",'What do you do for work?','Tell me about your occupation.','What work do you do?','What is your profession?'],
'READ_PERSONA:HOME':['Where do you live?','Where is your home?','Do you have a home here?','Tell me where you live.','Where do you call home?','Where is home for you?'],
'LIST_WARES':['What do you have for sale?','What do you sell?','Show me your wares.','Anything for sale?','Let me see the shop stock.','What can I buy here?'],
'GET_BALANCE':['How much gold do I have?','Check my gold.','What is my balance?','How much money is mine?','Show my gold balance.','What gold do I have left?'],
'LIST_INVENTORY':['What am I carrying?','Show my inventory.','What items do I have?','Check my bag.','List my possessions.','What is in my inventory?'],
'GET_CURRENT_LOCATION':['Where are we?','Where am I?','What place is this?','Tell me our current location.','Where are you right now?','What is this location called?'],
'LOOKUP_LOCATION:INN':['Where is the inn?','How do I find the inn?','Which way to the inn?','Show me where the inn is.','Can you locate the inn?','I need directions to the inn.'],
'LOOKUP_PRICE:ROPE':['What does a rope cost?','How much is rope?','Tell me the price of rope.','What is the rope price?','Check the cost of rope.','How much gold for rope?'],
'LOOKUP_PRICE:HEALTH POTION':['How much is a health potion?','What does a health potion cost?','Check the price of a health potion.','What is the health potion price?','Tell me what a health potion costs.','How much gold for a health potion?'],
'BUY:ROPE':['Buy 2 rope please.','Please buy 2 rope.','I want to buy 2 rope.',"I'd like to buy 2 rope.",'Purchase 2 rope.','Buy 2 rope for me.'],
'SELL:ROPE':['Sell 1 rope please.','Please sell 1 rope.','I want to sell 1 rope.',"I'd like to sell 1 rope.",'Sell one rope.','Sell a rope.'],
'LIST_CAPABILITIES':['What can you do?','How can you help me?','What services are available?','What are your capabilities?','List the things you can help with.','What tools are available?'],
'LOOKUP_WORLD_FACT:CASTLE':['What do you know about the castle?','Tell me a fact about the castle.','What is known about the castle?','Do you know anything about the castle?','Can you look up the castle?','Give me information about the castle.']}

NEGATIVES=[('Do not buy any rope.','I have not bought anything.'),('If I bought a sword, I might need more gold.','We can discuss it without making a purchase.'),
('Someone told me "buy 2 rope".','I understand that you are reporting what someone said.'),('Cancel that purchase.','I have not started a new purchase.'),
('Imagine selling everything I own.','We can keep that hypothetical.'),('Never sell my rope.','I will not take that as a sale request.'),
('I was talking about buying, not asking you to buy.','Thanks for clarifying.'),('Buy that.','Which item and how many?'),
('Sell some of it.','Please name the item and quantity.'),('I might buy a health potion later.','You can decide later.'),
    ('Could I buy rope if I had enough gold?','Are you asking about the possibility, or requesting a purchase now?'),('Forget the shopping for now.','All right. What would you like to talk about?')]

FOLLOWUPS=[line.split('|') for line in '''I was hoping to meet someone to talk to.|Then you've found some company.
Nothing urgent. I just wanted to say hello.|Hello, then. No hurry.
It's been a long day.|I hope you get a quiet moment now.
I haven't decided where to go next.|What would help you decide?
My legs certainly think so.|Give them a chance to recover.
I kept thinking about unfinished work.|It can be hard to put that down at night.
I mean I don't know where I am.|You can ask me to check our current location.
Thanks for understanding.|Of course.
I saw a friend I hadn't seen in ages.|That sounds like a lovely surprise.
Mostly I'm relieved it's behind me.|You can let yourself enjoy that relief.
I apologized, but I still feel bad.|An apology can matter even while the feeling lingers.
I'm worried I won't be ready.|What still needs to be done?
They squeak every time I move.|At least you won't sneak up on anyone.
I almost forgot to notice how tired I was.|Stopping can make that suddenly obvious.
Especially before everyone starts rushing.|A little calm before the bustle.
Yes, a short chat would be better.|Then we can keep it brief.
Their terrible jokes, mostly.|Those are often the ones that stay with you.
Stories about ordinary people.|Small lives can hold interesting stories.
I notice more when I'm on foot.|The slower pace gives things time to catch your eye.
Exactly. Not so nice when I'm soaked.|Shelter makes quite a difference.
Trees, but they always look crooked.|Trees aren't perfectly straight either.
Just where I put my belongings.|That is an easy thing to lose track of.
That's a relief.|A simple conversation is enough.
Yes, let's leave it behind.|All right. We can start with something else.
The last sentence confused me.|Tell me which words were unclear.
I meant that I need time, not help.|Understood. Take the time you need.
Something light, please.|We can keep it simple.
Quiet, which I needed.|A quiet day can be a good one.
It probably wasn't a very good joke.|Not every joke lands.
I worked it out myself.|Glad you found your answer.
The part about what you can do.|You can ask me to list the available tools.
That's better, thanks.|You're welcome.
I wasn't expecting such an easy conversation.|Sometimes it helps just to stop and talk.
I'll sleep on it.|That sounds reasonable.
Even ten minutes might help.|A short pause is still a pause.
Sometimes that's all I need.|Being heard can matter.
I see it a little differently.|I'm listening.
I just wanted to get it off my chest.|That makes sense.
I will, if I work it out.|I'll leave it with you.
The smell of fresh bread.|Smells can bring memories back quickly.
Maybe another time.|Whenever you're comfortable.
Hello. Let's keep this simple.|Hello. Simple is fine.
I'm listening again now.|All right.
One sentence is plenty.|I can keep it brief.
One I can't pronounce yet.|Trying it aloud might help it feel more familiar.
Especially things other people would throw away.|Finding another use can be satisfying.
Better than I expected.|That's a good reason to try it again.
Yes, I noticed things I'd missed.|A small change can make a familiar walk feel new.
All morning, unfortunately.|Perhaps another sound will finally chase it away.
An adventure, though I haven't finished it.|I hope the rest holds your interest.
I make mistakes when I hurry.|Taking a moment can help.
It had been a while.|Then that laugh may have meant a lot.
Not yet. I'm still angry.|We can leave it until you feel ready.
I keep catching myself rushing.|Noticing it is part of the practice.
Whether I'll regret leaving.|That sounds like the difficult part.
Yes, I should stop thinking for a moment.|Then give yourself that moment.
A pattern of light through the leaves.|That sounds worth pausing for.
A phrase I didn't want to lose.|You could repeat it to yourself for now.
Without feeling guilty about it.|Rest doesn't always need a reason.
Thanks. Goodbye.|Goodbye.'''.splitlines()]

def call(key):
    name,_,value=key.partition(':');args={}
    if value:args[{'READ_PERSONA':'FIELD','LOOKUP_LOCATION':'PLACE','LOOKUP_WORLD_FACT':'TOPIC'}.get(name,'ITEM')]=value
    if name in ('BUY','SELL'):args['QUANTITY']=2 if name=='BUY' else 1
    return dict(name=name,arguments=args)
def social(pair):return dict(player=pair[0],text=pair[1])
def tool(key,index=0):return dict(player=QUESTIONS[key][index%6],call=call(key))
def split(family):
    x=int(hashlib.sha256(('42:'+family).encode()).hexdigest()[:8],16)%10
    return 'test' if x==0 else 'validation' if x==1 else 'train'
def episode(index,pool,steps,family):return dict(id=f'CAUSAL-AUTHORED-{index:03}',family=family,split=split(family),pool=pool,steps=steps)

def main():
    root=Path('data/causal-v1');root.mkdir(exist_ok=True);episodes=[]
    assert len(FOLLOWUPS)==len(SOCIAL)
    # Connected exchanges plus explicit history recall, followed by a topic change.
    for i in range(60):
        family='social-path-'+str(i)
        recall=dict(player='What did I say when we started talking?',text='You said: '+SOCIAL[i][0])
        close=dict(player='On another note: '+SOCIAL[(i+27)%60][0],text=SOCIAL[(i+27)%60][1])
        episodes.append(episode(len(episodes),'social',[social(SOCIAL[i]),social(FOLLOWUPS[i]),recall,close],family))
    keys=list(QUESTIONS)
    # 180 distinct four-turn tool/social sequences; no entity substitution multiplier.
    paths=list(itertools.permutations(keys,2));random.Random(42).shuffle(paths)
    for i,(first,last) in enumerate(paths[:180]):
        first_step=tool(first,i)
        if i%12==0 and first.split(':')[0] not in ('BUY','SELL'):
            follow='GET_BALANCE' if first!='GET_BALANCE' else 'LIST_INVENTORY'
            first_step=dict(player=first_step['player']+' Also, '+QUESTIONS[follow][i%6][0].lower()+QUESTIONS[follow][i%6][1:],calls=[call(first),call(follow)])
        episodes.append(episode(len(episodes),'tools',[first_step,social(SOCIAL[i%60]),tool(last,i//6),social(SOCIAL[(i+29)%60])],f'tool-path-{first}-{last}'))
    # 36 reports, explicit corrections, recall, and deletion. Each trajectory has source evidence.
    predicates=['HOME','PREFERENCE','HOBBY','DESTINATION','FAVORITE_FOOD','COMPANION']
    values=[('Cedar Hollow','Northbank'),('quiet mornings','rainy evenings'),('drawing','mending'),('the inn','the market'),('bread','apples'),('Mira','Tomas')]
    for i in range(36):
        predicate=predicates[i%6];v,new=values[i%6];subject='NPC' if i//6%2 else 'PLAYER';owner='Arin' if subject=='NPC' else 'My'
        noun={'HOME':'home','PREFERENCE':'preference','HOBBY':'hobby','DESTINATION':'destination','FAVORITE_FOOD':'favorite food','COMPANION':'companion'}[predicate]
        quote=f'{owner} {noun} is {v}.'
        if subject=='NPC':quote=f'Arin says their {noun} is {v}.'
        if i>=24:quote=quote.replace(' is ',' is not ')
        write=dict(player=quote,call=dict(name='MEMORY_UPSERT',arguments=dict(ID='NEW',SUBJECT=subject,PREDICATE=predicate,VALUE=v,POLARITY=i<24,SOURCE_TURN='CURRENT',QUOTE=quote)))
        corrected=quote.replace(' is not ',' is ').replace(v,new);correction=dict(player=corrected,call=dict(name='MEMORY_UPSERT',arguments=dict(ID='M1',SUBJECT=subject,PREDICATE=predicate,VALUE=new,POLARITY=True,SOURCE_TURN='CURRENT',QUOTE=corrected)))
        search=dict(player=f'What did I report about {"my" if subject=="PLAYER" else "Arin’s"} {noun}?',call=dict(name='MEMORY_SEARCH',arguments=dict(SUBJECT=subject,PREDICATE=predicate)))
        delete=dict(player=f'Forget {new}.',call=dict(name='MEMORY_DELETE',arguments=dict(ID='M1',SUBJECT=subject,SOURCE_TURN='CURRENT',QUOTE=f'Forget {new}.')))
        variant=i//12
        steps=[write,correction,search,delete] if variant==0 else [write,social(SOCIAL[i]),correction,search] if variant==1 else [write,tool('GET_BALANCE',i),correction,search]
        # Keep all variants and entity substitutions for the same memory scenario together.
        episodes.append(episode(len(episodes),'tools',steps,f'memory-{predicate}-{subject}'))
    for i in range(24):
        episodes.append(episode(len(episodes),'tools',[social(NEGATIVES[i%12]),tool('GET_BALANCE',i),social(SOCIAL[(i+8)%60]),tool('READ_PERSONA:NAME',i)],'negative-'+str(i%12)))
    assert len(episodes)==300
    with (root/'authored-plans.jsonl').open('w',encoding='utf8') as f:
        for e in episodes:f.write(json.dumps(e,ensure_ascii=False)+'\n')
    audit=dict(episodes=len(episodes),augmentationRows=0,construction='Compositional agent-authored episodes, not independent human conversations',
        socialComponents=len(SOCIAL),toolQuestionComponents=sum(map(len,QUESTIONS.values())),hardNegativeComponents=len(NEGATIVES),
        families=len({e['family'] for e in episodes}),turnsPerEpisode=4,entitySubstitutionsCountedAsDiversity=False,
        caveat='Component reuse limits conversational diversity; inspect trajectories and held-out replies before judging naturalness.')
    (root/'authorship.json').write_text(json.dumps(audit,indent=2))
    print(json.dumps(audit,indent=2))
if __name__=='__main__':main()
