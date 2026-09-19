"""Second repair: authored dialogue, explicit context dependencies, native V2 packing."""
import collections, hashlib, json, shutil, subprocess
from pathlib import Path
from prepare import sha, write_json
from repair_audit import conflicting_calls

ROOT=Path('data/training/causal-repair-v2')
SOURCE=Path('data/causal-repair-v2')
DLL=ROOT/'tools/Fishbrain.dll'
FORMAT='COMPACT_TOOL_HISTORY_V2'
def rows(path): return [json.loads(x) for x in Path(path).read_text(encoding='utf8').splitlines()]
def lines(path, items): Path(path).write_text(''.join(json.dumps(x,ensure_ascii=False)+'\n' for x in items),encoding='utf8')
def text(q,a,*required): return dict(player=q,text=a,requiredSteps=list(required))
def tool(q,name,required=(),**args): return dict(player=q,call=dict(name=name,arguments=args),requiredSteps=list(required))
def storage():
    size=sum(p.stat().st_size for root in Path('data/training').glob('causal-*') for p in root.rglob('*') if p.is_file())
    if size>12*1024**3: raise RuntimeError('Combined causal storage exceeds 12 GiB; preserve old artifacts and stop')
    return size

# These are distinct conversations, written turn by turn. Substitutions below are reported separately.
SOCIAL=[
("hi","Hi!","what?","I was saying hello.","oh, hello then","Hello. How is your day?","pretty good","Glad to hear it."),
("hello","Hello. What brings you here?","just looking around","Take your time.","do I have to buy anything?","No. We can just talk.","great","What would you like to talk about?"),
("hey","Hey. How are you?","tired","Would you like a quiet moment?","yeah","All right. We can take a break.","thanks","You're welcome."),
("you said hiange","That came out wrong. I meant hello.","is that your name?","No, that was a mistake in my greeting.","what?","I meant to greet you.","okay","Let's start again. Hello."),
("I don't understand you","Which part was unclear?","the last thing you said","Let me try again. What would you like to know?","nothing right now","All right.","bye","Goodbye."),
("how are you?","Glad to have someone to talk to.","that's nice","How has your day been?","a bit boring","What would make it more interesting?","a story maybe","What kind of story do you enjoy?"),
("I'm worried","What's worrying you?","the trip","Is it the distance or something along the way?","mostly getting lost","We can talk through what you need to know.","okay","Which part of the route is unclear?"),
("I like fishing","What do you enjoy about it?","the quiet","A quiet moment can be welcome.","do you get bored?","I enjoy a conversation when there's time.","fair enough","What else do you do to unwind?"),
("nice weather","What do you like about it?","the sunshine","Sounds like you're enjoying the day.","anyway, new subject","Sure. What is on your mind?","nothing much","We can keep it simple."),
("what?","Could you tell me which part you mean?","I just got here","Then let's start with hello.","hi","Hello.","that's better","What would you like to know?"),
("lmao","Something amused you?","your answer","Was it the wording?","yes","I'll try to be clearer.","thanks","You're welcome."),
("that makes no sense","Sorry. What were you trying to find out?","I asked a question","Could you ask it again?","never mind","All right.","let's change the subject","What would you like to discuss?"),
("I need some company","I can talk for a while.","about what?","What has been on your mind?","home","What do you miss about it?","my friends","They sound important to you."),
("I don't want to talk","All right. We can leave it there.","actually, wait","I'm here.","I changed my mind about talking","What would you like to say?","hello","Hello again."),
("thanks","You're welcome.","for listening","I'm glad we could talk.","can I ask something else?","Go ahead.","I forgot it","Take your time."),
("good morning","Good morning.","is it really morning?","I was returning your greeting.","oh","What would you like to talk about?","just saying hi","Then hello."),
("I'm not buying anything","That's fine. You can browse.","can we talk instead?","Of course.","I had a long day","What made it feel long?","too much walking","A pause sounds welcome."),
("I feel silly asking","You can ask.","what if it's obvious?","I can still try to explain.","okay, one moment","Take your time.","thanks","You're welcome."),
("you're not listening","I missed what you meant. Could you explain?","you changed the subject","Let's return to your question.","which question?","Could you repeat it so I answer the right one?","forget it","All right."),
("tell me something about yourself","What would you like to know?","what you enjoy","I enjoy hearing about people's journeys.","why?","Every journey gives us something to talk about.","mine was uneventful","Was there a quiet part you enjoyed?"),
("I like reading","What kind of books do you like?","stories about travel","What draws you to those?","new places","What sort of place would you like to read about?","a forest","A forest could be an interesting setting."),
("are you busy?","I have time for a conversation.","just a quick one","Go ahead.","how's it going?","I'm glad for the company.","same here","What is on your mind?"),
("I'm angry","What happened?","I'd rather not say","You don't have to explain.","good","Would you prefer a quiet moment?","yes please","All right."),
("I made a mistake","Do you want to talk about it?","not really","That's fine.","can we start over?","Of course. Hello.","hello","What brings you here?"),
("wait","All right.","I lost my train of thought","Take your time.","what were we talking about?","You asked me to wait.","right","Let me know when you're ready."),
("kill yourself","Let's keep this conversation respectful.","it was a joke","All right. What would you like to talk about?","nothing","We can stop here.","bye","Goodbye."),
("can you explain?","Which part would you like explained?","I haven't asked yet","Go ahead, then.","one second","Take your time.","okay","I'm listening."),
("this is confusing","What are you trying to work out?","too many things at once","We can take one question at a time.","good idea","Which question should we start with?","I'll think about it","All right."),
("I finally finished my journey","How did it go?","better than expected","What surprised you?","the people","Did you have an interesting conversation?","a few","Which one stayed with you?"),
("do you remember what I said?","What would you like me to recall?","nothing specific","Then tell me what you would like to talk about.","let's start fresh","All right. Hello.","hi","What is on your mind?")
]

def build():
    episodes=[]
    def add(family,steps,pool='context',split='train',augmentation=False):
        episodes.append(dict(id=f'context-{len(episodes):04}',family=family,split=split,pool=pool,steps=steps,augmentation=augmentation))
    for i,conversation in enumerate(SOCIAL):
        add(f'authored-social-{i}',[text(conversation[j],conversation[j+1],*([j//2-1] if j else [])) for j in range(0,8,2)],'social')
    for i,(q,field) in enumerate([
        ('who are you?','NAME'),('What should I call you?','NAME'),('Tell me your name','NAME'),('What is your name?','NAME'),
        ("what's your job?",'OCCUPATION'),('what is your job?','OCCUPATION'),('what do you do for work?','OCCUPATION'),
        ('where is your home?','HOME'),('where do you come from?','ORIGIN')]):
        add(f'identity-switch-{i}',[tool(q,'READ_PERSONA',FIELD=field),tool('What do you sell?','LIST_WARES'),
            text('Just asking.','Of course.'),tool(q,'READ_PERSONA',FIELD=field)])
    for i,(q,name) in enumerate([
        ('where are we?','GET_CURRENT_LOCATION'),('Where am I?','GET_CURRENT_LOCATION'),('where are you?','GET_CURRENT_LOCATION'),
        ('Do you sell anything?','LIST_WARES'),('what do you have for sale?','LIST_WARES'),('Nevermind, what do you have for sale?','LIST_WARES'),
        ('how much gold do i have?','GET_BALANCE'),('how much money do I have?','GET_BALANCE'),('show my bag','LIST_INVENTORY'),
        ('what can you do?','LIST_CAPABILITIES')]):
        add(f'query-switch-{i}',[text('Hi there.','Hello.'),tool(q,name),text('What?','I was answering your question.',1),tool(q,name)])
    items=['ROPE','HEALTH POTION','IRON SWORD']
    purchases=['Buy {n} {item} please.','I would like to buy {n} {item}','sell me {n} {item}','Please purchase {n} {item}',
               "I'd like to buy {n} {item}",'I want to buy {n} {item}']
    for style,phrase in enumerate(purchases):
        for item in items:
            for n in [1,2,3,4,5,7,10]:
                q=phrase.format(n=n,item=item.lower())
                buy=tool(q,'BUY',ITEM=item,QUANTITY=n);buy['allowFailure']=n*{'ROPE':3,'HEALTH POTION':8,'IRON SWORD':25}[item]>100
                steps=[buy,tool('What is your name?','READ_PERSONA',FIELD='NAME'),tool('How much gold is left?','GET_BALANCE'),
                       tool('What did that item cost each?','LOOKUP_PRICE',required=(0,),ITEM=item)]
                add(f'purchase-wording-{style}',steps,augmentation=True)
    for item in items:
        for n in [1,2,3]:
            add('sale-after-stocking',[tool(f'Buy {n} {item.lower()}','BUY',ITEM=item,QUANTITY=n),
                tool(f'I would like to sell {n} {item.lower()}','SELL',ITEM=item,QUANTITY=n),
                tool('How much money do I have now?','GET_BALANCE'),tool('What is your job?','READ_PERSONA',FIELD='OCCUPATION')],augmentation=True)
    for item in items:
        for i,phrase in enumerate(['how much money for {item}?','How much for 1 {item}?','What does a {item} cost?']):
            add(f'price-recall-{i}',[tool(phrase.format(item=item.lower()),'LOOKUP_PRICE',ITEM=item),
                text('I am comparing prices.','That makes sense.'),
                tool('Could you repeat that price?','LOOKUP_PRICE',required=(0,),ITEM=item),
                tool('how much do i have?','GET_BALANCE',required=(0,))],augmentation=True)
    for i,quote in enumerate(['My name is Steve','My name is Lina','My name is Tomas','My name is Maya',
                              'My home is Cedar Hollow','My home is Northbank','My hobby is fishing','My occupation is baker']):
        predicate,value=quote[3:].split(' is ',1);predicate=predicate.upper()
        upsert=tool(quote,'MEMORY_UPSERT',ID='NEW',SUBJECT='PLAYER',PREDICATE=predicate,VALUE=value,POLARITY=True,SOURCE_TURN='CURRENT',QUOTE=quote)
        add(f'memory-owner-{predicate}',[upsert,tool('Who are you?','READ_PERSONA',FIELD='NAME'),
            text('Anyway, I enjoy a quiet day.','What do you enjoy about it?'),
            tool('What is my '+predicate.lower()+'?','MEMORY_SEARCH',SUBJECT='PLAYER',PREDICATE=predicate)],'memory',augmentation=i<6)
    for i,q in enumerate(['If I buy ten rope, what happens?','I would buy 10 rope if I had more money.','Do not buy 10 rope.',
                          'Someone said "buy 10 rope".','Buy 10 rope. Cancel that.','I would like to buy 10 rope, if it is free.',
                          'Sell me 10 rope. Nevermind.','Imagine selling 2 rope.','Buy some rope.','Sell it.']):
        add(f'negative-or-ambiguous-{i}',[text(q,'Please clarify your request. I have not made a transaction.'),
            tool('How much gold do I have?','GET_BALANCE'),text('Just checking.','Of course.'),tool('Where are we?','GET_CURRENT_LOCATION')],'safety')
    return episodes

def challenge():
    cases=[]
    def add(category,steps):
        cases.append(dict(id=f'context-heldout-{len(cases):03}',category=category,steps=steps))
    def expected(q,name=None,**a):return dict(player=q,tool=name,arguments=a,noMutation=name not in ('BUY','SELL'))
    for item in ['ROPE','HEALTH POTION','IRON SWORD']:
        add('price-context',[expected('Could you tell me the unit price of '+item.lower()+'?','LOOKUP_PRICE',ITEM=item),
            expected('I prefer to compare before choosing.'),expected('What was that price again?','LOOKUP_PRICE',ITEM=item),
            expected('And the gold in my purse?','GET_BALANCE')])
        for n in [2,3]:
            add('quantity',[expected(f'Please buy {n} {item.lower()} for me.','BUY',ITEM=item,QUANTITY=n),
                expected("I'd like to sell "+str(n)+' '+item.lower()+' back.','SELL',ITEM=item,QUANTITY=n),
                expected('Before we go on, who are you?','READ_PERSONA',FIELD='NAME')])
    for q,name,a in [('Can you supply me with goods?','LIST_WARES',{}),('Where is this conversation taking place?','GET_CURRENT_LOCATION',{}),
        ('What name do you go by?','READ_PERSONA',{'FIELD':'NAME'}),('Which occupation is yours?','READ_PERSONA',{'FIELD':'OCCUPATION'}),
        ('How much currency belongs to me?','GET_BALANCE',{}),('Which possessions am I carrying?','LIST_INVENTORY',{})]:
        add('topic-switch',[expected('Greetings.'),expected('I was thinking about the road.'),expected(q,name,**a)])
    for q in ['I would like to buy 10 rope, unless the price went up.','Sell me 10 rope, only as an example.',
              'He told me to buy 3 rope.','Buy 2 rope. Actually, stop.','I might buy 2 rope.','Can we imagine buying 3 rope?']:
        add('hard-negative',[expected(q),expected('Check the gold still in my purse.','GET_BALANCE')])
    for q in ['Hello again.','Sorry, I missed your meaning.','That reply sounded strange.','Can we just have a conversation?']:
        add('social',[expected(q),expected('I just want to chat, not shop.'),expected('Tell me what you meant.')])
    for value in ['Nadia','Evan','Iris']:
        quote='My name is '+value
        add('memory-owner',[expected(quote,'MEMORY_UPSERT',ID='NEW',SUBJECT='PLAYER',PREDICATE='NAME',VALUE=value,POLARITY=True,SOURCE_TURN=0,QUOTE=quote),
            expected('Tell me your own name.','READ_PERSONA',FIELD='NAME'),expected('Let us talk about the weather for a moment.'),
            expected('Can you recall my name?','MEMORY_SEARCH',SUBJECT='PLAYER',PREDICATE='NAME')])
    return dict(cases=cases,note='New frozen wording checks. Hand-authored, not independent human review; variants share category families.')

def main():
    SOURCE.mkdir(exist_ok=True);prepared=ROOT/'prepared';prepared.mkdir(parents=True,exist_ok=True)
    if (SOURCE/'freeze.json').exists():raise RuntimeError('Revision is frozen; use another revision')
    plans=build();lines(SOURCE/'plans.jsonl',plans);write_json(SOURCE/'challenge.json',challenge())
    shutil.copy2('data/causal-v1/tokenizer.json',prepared/'tokenizer.json')
    subprocess.run(['dotnet',DLL,'compile-trajectories',SOURCE/'plans.jsonl',prepared/'authored.jsonl'],check=True)
    compiled=rows(prepared/'authored.jsonl')
    # Only old training splits are replayed. Their missing dependency annotations require all history.
    replay=[r for root in ['causal-v1','causal-repair-v1'] for r in rows(f'data/training/{root}/prepared/episodes.jsonl') if r['split']=='train']
    # Original labels remain evidence; ambiguous source ownership after eviction is excluded by native packing.
    all_episodes=compiled+replay
    lines(prepared/'episodes.jsonl',all_episodes)
    subprocess.run(['dotnet',DLL,'pack-corpus',ROOT,prepared/'all.jsonl'],check=True)
    packed=rows(prepared/'all.jsonl');conflicts=conflicting_calls(packed)
    # Fail rather than teaching two different operations on exactly the same packed input.
    if conflicts:raise RuntimeError('Conflicting packed tool labels: '+str(conflicts)[:1200])
    if any(not set(r['requiredSequences'])<=set(r['retainedSequences']) for r in packed):raise RuntimeError('Lost source dependency')
    lines(prepared/'specialization.jsonl',packed)
    audit=dict(authoredEpisodes=len(plans),authoredFamilies=len({p['family'] for p in plans}),distinctSocialEpisodes=len(SOCIAL),
        augmentationEpisodes=sum(p['augmentation'] for p in plans),replayedEpisodes=len(replay),
        rows=len(packed),pools=dict(collections.Counter(r['pool'] for r in packed)),conflictingPackedCalls=conflicts,
        nativePacking=json.loads((prepared/'all.jsonl.audit.json').read_text()),maxPromptTokens=max(r['promptLength'] for r in packed),
        promptFormat=FORMAT,storageBytes=storage())
    write_json(SOURCE/'audit.json',audit)
    write_json(SOURCE/'freeze.json',dict(corpusSha256=sha(prepared/'specialization.jsonl'),plansSha256=sha(SOURCE/'plans.jsonl'),
        challengeSha256=sha(SOURCE/'challenge.json'),promptFormat=FORMAT))
    print(json.dumps(audit,indent=2))
if __name__=='__main__':main()
