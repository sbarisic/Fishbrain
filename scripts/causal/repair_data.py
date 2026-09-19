"""Compositional tool and dialogue-repair data; preserves every previous corpus and suite."""
import argparse,collections,hashlib,json,random,re,shutil,subprocess
from pathlib import Path
from prepare import sha,write_json,norm

SOURCE=Path('data/causal-repair-v1')
ROOT=Path('data/training/causal-repair-v1')
DLL='data/training/causal-v1/candidate-package/Fishbrain.dll'
ITEMS=['IRON SWORD','HEALTH POTION','ROPE']
# A wording family, including all entity/quantity substitutions, belongs to one split.
STYLES=[
 ('train','Could you check the unit price of {item}?','Buy {n} {item}, please.','Sell {n} {item}, please.'),
 ('train','I want to know what one {item} costs.','Please purchase {n} {item}.','Please sell {n} {item}.'),
 ('train','Give me the cost of a single {item}.','I want to buy {n} {item}, please.','I want to sell {n} {item}, please.'),
 ('train','What would I pay for one {item}?','Buy {n} {item} for my journey.','Sell {n} {item} from my bag.'),
 ('train','Look up how much a {item} costs.','Purchase {n} {item} for me.','Sell {n} {item} for me.'),
 ('train','How much for 1 {item}?','Buy {n} {item}.','Sell {n} {item}.'),
 ('train','Can you tell me the unit cost of {item}?','Please buy {n} {item} for my pack.','Please sell {n} {item} from my pack.'),
 ('train','Check what a {item} would cost me.','I want to buy {n} {item} now.','I want to sell {n} {item} now.'),
 ('validation','What is the asking price for a {item}?','Purchase {n} {item} for the road.','Sell {n} {item} from my supplies.'),
 ('test','Tell me how much one {item} sells for here.','Buy {n} {item} for this trip.','Sell {n} {item} out of my supplies.'),
 ('test','What price is attached to the {item}?','Please purchase {n} {item} for this trip.','Please sell {n} {item} out of my bag.')]
GLOBAL={
 'LIST_WARES':(['Do you sell anything?','What goods can I purchase here?','Can I see what the shop offers?','Show me the things you stock.'],['Which goods are offered in this shop?']),
 'GET_BALANCE':(['Can you check how much gold belongs to me?','Tell me the amount of gold in my purse.','Read out my remaining money.','I need to know my available gold.'],['How much money is available to me right now?']),
 'LIST_INVENTORY':(['Tell me what items are in my bag.','Show me everything I am carrying.','What possessions are with me?','Check the contents of my pack.'],['Which items are currently in my possession?']),
 'GET_CURRENT_LOCATION':(['Name the place where we are standing.','Tell me where we are at the moment.','What is our present location?','Check the place we are in now.'],['Which place are we standing in right now?']),
 'LIST_CAPABILITIES':(['Tell me what help you can offer.','Show me the services you provide.','What tasks can you assist with?','Explain which things you can help with.'],['Which services could you provide here?'])}
REPAIRS=[
 ('Hello','Hello. How can I help?','What?','I was saying hello. What would you like to talk about?'),
 ('Hi','Hi. What brings you here?','Huh?','I asked what brought you here. We can just chat, too.'),
 ('Good morning','Good morning. How is your day going?','I did not understand.','I was asking how your day is going.'),
 ('Hey there','Hello. Do you want to talk?','What do you mean?','I meant that I have time for a conversation.'),
 ('Can we talk?','Of course. What is on your mind?','Sorry, what was that?','I asked what you would like to talk about.'),
 ('I am tired.','Would you like a quiet moment?','What?','I meant we can stop talking for a while if you want.'),
 ('I feel worried.','What is worrying you?','Huh?','I was asking what has you worried.'),
 ('I am just browsing.','Take your time.','What do you mean?','There is no need to decide right away.'),
 ('I need a break.','We can pause here.','I did not catch that.','I said we can take a break from talking.'),
 ('Thanks for the help.','You are welcome.','What?','I was saying you are welcome.'),
 ('Let us start over.','All right. What would you like to discuss?','Could you explain that?','We can begin a new conversation about anything you choose.'),
 ('I am confused.','Which part was unclear?','What was the question?','I asked which part of the conversation was confusing.')]

def call(name,**arguments):return dict(name=name,arguments=arguments)
def tool(player,name,**arguments):return dict(player=player,call=call(name,**arguments))
def text(player,answer):return dict(player=player,text=answer)
def write_lines(path,rows):path.write_text(''.join(json.dumps(r,ensure_ascii=False)+'\n' for r in rows),encoding='utf8')
def storage():
    size=sum(p.stat().st_size for base in [ROOT,Path('data/training/causal-v1')] for p in base.rglob('*') if p.is_file())
    if size>12*1024**3:raise RuntimeError('Combined experiment storage exceeds 12 GiB; no old artifacts may be deleted')
    return size

def build():
    episodes=[]
    def add(family,split,steps,pool='tools'):
        episodes.append(dict(id=f'repair-{len(episodes):04}',family=family,split=split,pool=pool,steps=steps))
    for i,(split,price,buy,sell) in enumerate(STYLES):
        for item in ITEMS:
            noun=item.lower();n=3 if split!='train' else 1+i%2
            price_step=tool(price.format(item=noun),'LOOKUP_PRICE',ITEM=item)
            # Same repair utterance must retrieve different tools/arguments from history.
            add(f'price-style-{i}',split,[price_step,
                tool('Could you repeat that price?','LOOKUP_PRICE',ITEM=item),
                text('That was a price question, not an order.','Understood. You have not placed an order.'),
                tool('Now check the gold I have available.','GET_BALANCE')])
            add(f'buy-style-{i}',split,[tool(buy.format(n=n,item=noun),'BUY',ITEM=item,QUANTITY=n),
                text('Let us change the subject for a moment.','Sure. What would you like to discuss?'),
                tool('After shopping, remind me of your name.','READ_PERSONA',FIELD='NAME'),price_step])
            add(f'sell-style-{i}',split,[tool(f'Buy {n} {noun} so I can sell it back.','BUY',ITEM=item,QUANTITY=n),
                tool(sell.format(n=n,item=noun),'SELL',ITEM=item,QUANTITY=n),
                tool('Check what remains in my bag now.','LIST_INVENTORY'),
                tool('After selling, what work do you do?','READ_PERSONA',FIELD='OCCUPATION')])
    for name,(training,testing) in GLOBAL.items():
        for i,q in enumerate(training+testing):
            split='train' if i<len(training) else 'test'
            steps=[tool(q,name),tool('Could you repeat that information?',name),
                text('Anyway, I am taking my time today.','A slow day can be pleasant.'),tool(q,name)]
            # Some genuine openings; no mandatory canned introduction.
            if i%2:steps=[text('Good afternoon.','Hello. How can I help?')]+steps[:3]
            add(f'global-{name}-{i}',split,steps)
    for field,noun in [('NAME','name'),('OCCUPATION','occupation'),('ROLE','role'),('HOME','home'),('ORIGIN','origin')]:
        for i,q in enumerate([f'Please read your {noun} for me.',f'Tell me your {noun}, please.',f'Which {noun} is listed for you?']):
            add(f'persona-{i}', 'test' if i==2 else 'train', [tool(q,'READ_PERSONA',FIELD=field),
                text('I am asking about you.','Understood. You mean me.'),tool('Could you repeat that information?','READ_PERSONA',FIELD=field),
                text('That is all I needed.','All right.')])
    for name,key,entities in [('LOOKUP_LOCATION','PLACE',['INN','MARKET','CASTLE','HELL','ZAGREB']),('LOOKUP_WORLD_FACT','TOPIC',['CASTLE','VILLAGE','REACTOR'])]:
        for entity in entities:
            for i in range(3):
                q=([f'Look up the location of {entity.lower()}.',f'Please find where {entity.lower()} is.',f'Can you give the location of {entity.lower()}?'] if key=='PLACE' else
                    [f'Look up a fact about the {entity.lower()}.',f'Please check what is known about the {entity.lower()}.',f'What fact can you find about the {entity.lower()}?'])[i]
                add(f'world-{name}-{i}','test' if i==2 else 'train',[tool(q,name,**{key:entity}),
                    text('I was looking for information.','I understand.'),tool('Could you repeat that information?',name,**{key:entity}),text('Thank you for checking.','You are welcome.')])
    for i,(opening,answer,repair,clarification) in enumerate(REPAIRS):
        add(f'repair-social-{i}','test' if i>=10 else 'validation' if i==9 else 'train',[
            text(opening,answer),text(repair,clarification),text('Let us talk about something else.','Sure. What would you like to discuss?'),
            text('I like a quiet evening.','What do you enjoy about it?')],'repair')
    # Hard negatives vary the item, but never count substitutions as new families.
    for i,item in enumerate(ITEMS):
        for j,prefix in enumerate(['Do not buy 1','Imagine buying 1','Someone said "buy 1','Cancel my request to buy 1']):
            q=prefix+' '+item.lower()+('".' if j==2 else '.')
            add(f'negative-{j}','train',[text(q,'I have not made a purchase.'),tool('Check the money I still have.','GET_BALANCE'),
                text('I was only discussing the idea.','Understood. Talking about it is not an order.'),tool('What goods can I purchase here?','LIST_WARES')])
    # Prior memory trajectories remain as replay; the focused change is item composition and repair.
    return episodes

def main():
    parser=argparse.ArgumentParser();parser.add_argument('--prepare',action='store_true');args=parser.parse_args()
    SOURCE.mkdir(exist_ok=True);ROOT.mkdir(exist_ok=True);prepared=ROOT/'prepared';prepared.mkdir(exist_ok=True)
    if (SOURCE/'freeze.json').exists():raise RuntimeError('This revision is frozen; create another revision rather than rewriting evaluation evidence')
    episodes=build()
    # Preserve old frozen evaluation inputs. Known user regressions are permitted training data.
    protected=set()
    for path in ['data/causal-v1/challenge.json','data/causal-v1/preserved-acceptance.json']:
        protected.update(norm(s['player']) for c in json.loads(Path(path).read_text(encoding='utf8'))['cases'] for s in c['steps'] if not s.get('developmentOverlap'))
    rejected={e['family']:'PRIOR_FROZEN_INPUT' for e in episodes if e['split']=='train' and any(norm(s['player']) in protected for s in e['steps'])}
    episodes=[e for e in episodes if e['family'] not in rejected]
    # Repeated shared turns are measured separately; complete conversations and near duplicates cannot cross splits.
    signatures={};grams=[]
    for e in episodes:
        value=norm(' '.join(s['player'] for s in e['steps']));key=hashlib.sha256(value.encode()).hexdigest()
        if key in signatures and signatures[key]!=e['split']:raise RuntimeError('Exact conversation crosses splits')
        signatures[key]=e['split'];words=value.split();grams.append(set(tuple(words[i:i+4]) for i in range(len(words)-3)))
    near=[]
    for i,e in enumerate(episodes):
        for j in range(i):
            if e['split']!=episodes[j]['split'] and grams[i] and len(grams[i]&grams[j])/len(grams[i]|grams[j])>=.85:
                near.append((episodes[j]['id'],e['id']))
    if near:raise RuntimeError('Near duplicate conversations cross splits: '+str(near[:8]))
    write_lines(SOURCE/'plans.jsonl',episodes)
    # Freeze test conversations before packing or training. Shared follow-ups are contextual tests.
    cases=[]
    for e in episodes:
        if e['split']!='test':continue
        steps=[]
        for s in e['steps']:
            c=s.get('call',{});name=c.get('name');steps.append(dict(player=s['player'],tool=name,arguments=c.get('arguments',{}),noMutation=name not in ('BUY','SELL')))
        cases.append(dict(id=e['id'],category=e['family'].rsplit('-',1)[0],steps=steps))
    write_json(SOURCE/'challenge.json',dict(cases=cases,note='Frozen before this focused learning check; wording families held out, entity substitutions are augmentation.'))
    shutil.copy2('data/causal-v1/tokenizer.json',prepared/'tokenizer.json')
    subprocess.run(['dotnet',DLL,'compile-trajectories',str(SOURCE/'plans.jsonl'),str(prepared/'authored.jsonl')],check=True)
    compiled=[json.loads(x) for x in (prepared/'authored.jsonl').read_text(encoding='utf8').splitlines()]
    # Normalize the compiler's authored pool labels for focused sampling.
    meta={e['id']:e for e in episodes}
    for e in compiled:e.update({k:meta[e['id']][k] for k in ['family','split','pool']})
    write_lines(prepared/'episodes.jsonl',compiled)
    subprocess.run(['dotnet',DLL,'pack-corpus',str(ROOT),str(prepared/'specialization.jsonl')],check=True)
    rows=[json.loads(x) for x in (prepared/'specialization.jsonl').read_text(encoding='utf8').splitlines()]
    coverage=collections.Counter();pool=collections.Counter()
    for row in rows:
        pool[(row['split'],row['pool'])]+=1
        t=json.loads(row['target'])
        if row['split']=='train' and t['type']=='tool_call':coverage[(t['name'],t['arguments'].get('ITEM',''))]+=1
    missing=[(action,item) for action in ['LOOKUP_PRICE','BUY','SELL'] for item in ITEMS if coverage[(action,item)]==0]
    if missing:raise RuntimeError('Missing item/action training coverage: '+str(missing))
    train_families={e['family'] for e in episodes if e['split']=='train'}
    test_families={e['family'] for e in episodes if e['split']=='test'}
    assert not train_families & test_families
    audit=dict(episodes=len(episodes),families=len({e['family'] for e in episodes}),heldoutConversations=len(cases),
        rejectedFamilies=rejected,nearDuplicateCrossSplit=near,entitySubstitutionsAreAugmentation=True,
        rows={':'.join(k):v for k,v in pool.items()},itemActionCoverage={':'.join(k):v for k,v in coverage.items() if k[1]},
        packing=json.loads(Path(str(prepared/'specialization.jsonl')+'.audit.json').read_text()),
        sharedFinalUtterancesAcrossSplits=len({norm(e['steps'][-1]['player']) for e in episodes if e['split']=='train'} & {norm(e['steps'][-1]['player']) for e in episodes if e['split']=='test'}),
        limitations=['Authored compositional experiment, not independent human conversations.','Wording families and complete episodes stay together; shared contextual follow-ups are intentional.','Existing approved training rows may be replayed; no previous validation/test row is eligible.'],
        corpusSha256=sha(prepared/'specialization.jsonl'),challengeSha256=sha(SOURCE/'challenge.json'),combinedStorageBytes=storage())
    write_json(SOURCE/'audit.json',audit)
    write_json(SOURCE/'freeze.json',dict(challengeSha256=sha(SOURCE/'challenge.json'),plansSha256=sha(SOURCE/'plans.jsonl'),corpusSha256=audit['corpusSha256'],seed=42))
    print(json.dumps(audit,indent=2))

if __name__=='__main__':main()
