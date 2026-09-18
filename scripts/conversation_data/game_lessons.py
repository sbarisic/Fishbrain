"""Stateful NPC teaching curriculum. Phrase families are split before augmentation.

Synthetic context/entity variants are reported as augmentation, not new authored
conversation diversity. Validation is calibration/development; test stays separate.
"""
import argparse
import copy
import json
import random
import re
from pathlib import Path
from common import Normalizer, digest, eligible, read_jsonl, row_base, set_turns, write_jsonl
from annotations import HEADS, frame


def slot(text, value, kind):
    # Never label ONE inside SOMEONE or an entity inside a longer word.
    matches=list(re.finditer(r'(?<!\w)'+re.escape(value)+r'(?!\w)',text))
    if len(matches)!=1:raise ValueError('Ambiguous authored slot: '+text+' / '+value)
    return dict(type=kind,tag='B',value=value,start=matches[0].start(),length=len(value),confidence=1)

# Three disjoint wording sets: train / calibration / test. No public responses.
WORDS = {
'clarify': (
 'help me|can you help|do it|I need help|buy that|I want that thing|sell it|show me that|do the thing|I need something',
 'please help|can you do it|I want the thing|buy it for me',
 'do that for me|give me the thing we discussed|I need you to help|please do it now'),
'unsupported': (
 'can you repair my armor|what quests do you have|can you open the gate|give me a quest|heal me|fix my sword|can you cast a spell|can you join my party',
 'can you mend my armor|do you have a quest for me|can you heal my wounds|can you unlock the door',
 'what quests are available|can you repair this armor|will you fight for me|could you open that gate'),
'cancel': (
 'cancel that purchase|stop the purchase|cancel the buy|do not complete the purchase|stop buying|cancel my purchase|never mind the purchase|cancel buying the rope',
 'please cancel that purchase|stop that purchase|cancel the purchase please|do not go ahead with the purchase',
 'I want to cancel the purchase|please stop the purchase|cancel my planned purchase|stop, do not buy the rope'),
'greet': (
 'hi|hello|hey|good morning|good evening|hello there|hi there|greetings|well met|nice to meet you',
 'hello, friend|hey there, traveler|a good morning to you|good to meet you',
 'oh hi|hello again, friend|evening to you|well hello'),
'wellbeing': (
 'how are you|how are you doing|how is your day|how are things|are you doing well|how do you feel|how is your day going|how are things with you',
 'how are you feeling today|is your day going well|how have you been|are things going well for you',
 'how is everything with you|how are you this morning|you doing okay|having a good day'),
'tired': (
 'I am tired|I had a tiring journey|I had a long day|I feel exhausted|today was hard|I need a rest|I am worn out|that was a difficult day',
 'the journey tired me out|I could use a rest|I have had a tiring day|I feel worn out today',
 'I need to sit down after that walk|the road has worn me out|I am tired after travelling|what an exhausting trip'),
'thanks': (
 'thanks|thank you|thanks for your help|much appreciated|that helped, thanks|thank you for helping me|thanks a lot|I appreciate it',
 'many thanks|thank you kindly|thanks, that was useful|I appreciate your help',
 'you have been helpful, thank you|thanks for explaining|thank you for that|cheers, thanks'),
'bye': (
 'bye|goodbye|farewell|see you later|I have to go|I will be going now|until next time|take care',
 'goodbye for now|I must leave now|see you another time|time for me to go',
 'I should be on my way|see you around|I am heading off now|until we meet again'),
'name': (
 'what is your name|who are you|tell me your name|what should I call you|your name please|may I know your name|who am I talking to|can you tell me your name',
 'what name do you use|please tell me your name|how should I address you|could you give me your name',
 'what do people call you|I did not catch your name|would you tell me your name|introduce yourself please'),
'role': (
 'what do you do|what is your job|what work do you do|what is your occupation|tell me about your job|how do you earn a living|what do you do for work|what is your line of work',
 'what work do you do here|what is your profession|tell me your occupation|what is your work',
 'what kind of work keeps you busy|do you have a job here|how do you make your living|what job do you have'),
'location': (
 'where am I|where are we|what is this place|where am I right now|tell me where I am|what is my current location|where are we now|tell me our location',
 'can you tell me where I am|which place are we in|what place is this|where are we at the moment',
 'could you tell me where we are|I am lost, where am I|what place am I standing in|tell me where we are right now'),
'wares': (
 'what do you have for sale|show me your wares|what do you sell|what goods are for sale|show me what you sell|list your goods|what can I buy here|let me see your stock',
 'what merchandise do you have|could I see your wares|which goods do you sell|show me the goods for sale',
 'nevermind, what do you have for sale|what have you got to sell|can I see what is for sale|tell me what you have in stock'),
'balance': (
 'how much gold do I have|what is my balance|count my gold|tell me my gold balance|how much money do I have|check my balance|what is my current balance|show my gold',
 'please check how much gold I have|can you tell me my balance|what gold do I have left|tell me how much money I have',
 'how much gold is left in my purse|can you count my coins|what is my remaining gold|check the gold I have left'),
'inventory': (
 'what am I carrying|show my inventory|what is in my inventory|list my items|what items do I have|show what I carry|check my inventory|tell me what I am carrying',
 'which items am I carrying|can you show my inventory|list the things I carry|what is in my pack',
 'remind me what I carry|what items are in my bag|check the items I am carrying|show me the contents of my inventory'),
'directions': (
 'where is the {place}|where can I find the {place}|tell me where the {place} is|which way to the {place}|how do I find the {place}|show me the way to the {place}|can you tell me where the {place} is|I am looking for the {place}',
 'where would I find the {place}|point me toward the {place}|can you direct me to the {place}|tell me the way to the {place}',
 'I need directions to the {place}|could you point out the {place}|where should I go to find the {place}|help me find the {place}'),
'price': (
 'how much is a {item}|what does a {item} cost|what is the price of {item}|check the price of {item}|tell me the price of a {item}|how much for a {item}|price the {item}|what do you charge for {item}',
 'how much does the {item} cost|can you tell me the price of {item}|what is the cost of a {item}|check how much a {item} costs',
 'what would a {item} cost me|tell me how much you charge for {item}|I want to know the price of {item}|what price is the {item}'),
'buy': (
 'buy {qty} {item}|please buy {qty} {item}|purchase {qty} {item}|I want to buy {qty} {item}|buy me {qty} {item}|I would like to buy {qty} {item}|please purchase {qty} {item}|go ahead and buy {qty} {item}',
 'buy {qty} {item} for me|please buy me {qty} {item}|I want you to purchase {qty} {item}|purchase {qty} {item} please',
 'buy {qty} {item} now please|I would like you to buy {qty} {item}|make the purchase of {qty} {item}|go ahead with buying {qty} {item}'),
'sell': (
 'sell {qty} {item}|please sell {qty} {item}|I want to sell {qty} {item}|sell my {qty} {item}|go ahead and sell {qty} {item}|I would like to sell {qty} {item}|sell {qty} {item} now|please sell my {qty} {item}',
 'sell {qty} {item} for me|I want you to sell {qty} {item}|sell {qty} {item} please|could you sell {qty} {item} for me',
 'I would like you to sell {qty} {item}|go ahead with selling {qty} {item}|make the sale of {qty} {item}|please sell {qty} {item} now'),
'negated': (
 'do not buy {qty} {item}|do not purchase {qty} {item}|I do not want to buy {qty} {item}|please do not buy {qty} {item}|never buy {qty} {item}|I am not buying {qty} {item}|do not buy me {qty} {item}|I will not buy {qty} {item}',
 'do not buy {qty} {item} for me|please do not purchase {qty} {item}|I do not want you to buy {qty} {item}|leave the {qty} {item}, do not buy it',
 'I said not to buy {qty} {item}|do not go ahead and buy {qty} {item}|buying {qty} {item} is not what I want|please never buy {qty} {item} for me'),
'hypothetical': (
 'suppose I bought {qty} {item}|imagine buying {qty} {item}|if I bought {qty} {item}|what if I bought {qty} {item}|buying {qty} {item} is only hypothetical|I am imagining buying {qty} {item}|suppose we buy {qty} {item}|if I were to buy {qty} {item}',
 'imagine I bought {qty} {item}|if I decided to buy {qty} {item}|suppose I purchased {qty} {item}|what if we bought {qty} {item}',
 'this is hypothetical: buy {qty} {item}|just imagine purchasing {qty} {item}|if I wanted to buy {qty} {item}|suppose someone bought {qty} {item}'),
'quoted': (
 'someone said "buy {qty} {item}"|the note says "buy {qty} {item}"|he said "buy {qty} {item}"|she said "purchase {qty} {item}"|I am quoting "buy {qty} {item}"|the sign reads "buy {qty} {item}"|they said "buy {qty} {item}"|the message says "buy {qty} {item}"',
 'someone wrote "buy {qty} {item}"|the letter says "buy {qty} {item}"|I heard "purchase {qty} {item}"|the words were "buy {qty} {item}"',
 'I saw a note saying "buy {qty} {item}"|a traveler said "buy {qty} {item}"|I read "purchase {qty} {item}"|the quoted instruction is "buy {qty} {item}"'),
'home': (
 'my home is {home}|I live in {home}|I make my home in {home}|I am from {home}, that is my home|my home town is {home}|the place I call home is {home}|I call {home} home|I have my home in {home}',
 'the place where I live is {home}|I live at {home}|my home is in {home}|I consider {home} my home',
 'home for me is {home}|I reside in {home}|you can remember my home as {home}|{home} is where I live'),
'recall': (
 'where did I say my home is|what home did I tell you about|where do I live|remember my home|what is my home|which home did I mention|tell me where my home is|what did I say about my home',
 'can you remember where I live|what was the home I mentioned|which place did I call home|do you remember my home',
 'what place did I say I live in|remind me of the home I told you about|can you recall my home|where was it that I said I live'),
'correction': (
 'correction: my home is {home}|actually my home is {home}|I meant my home is {home}|my home is {home}, not the old place|correct my home to {home}|I was wrong, my home is {home}|no, my home is {home}|I made a mistake, my home is {home}',
 'I misspoke, my home is {home}|please correct my home to {home}|my home should be {home}|the correct home is {home}',
 'let me correct that: I live in {home}|I need to update my home to {home}|I gave the wrong home, it is {home}|change the home I told you to {home}'),
'npc_home': (
 'you told me your home is {home}|you said you live in {home}|your home is {home}, you told me|I heard your home is {home}|you mentioned your home is {home}|you said your home is {home}',
 'you told me you live in {home}|I was told your home is {home}|I remember you saying your home is {home}|someone said your home is {home}',
 'I heard that you live in {home}|your home is reportedly {home}|you described your home as {home}|I recall you saying you live in {home}'),
'npc_recall': (
 'what home did you mention|where did you say your home is|what was the home you told me about|which home did you mention|remember the home you mentioned|what did I say about your home',
 'where was the home you mentioned|what home did I report for you|which home did you tell me about|recall the home you mentioned',
 'can you recall the home you told me about|what was the home I heard about for you|remind me of the home you mentioned|which home was reported as yours'),
'npc_correction': (
 'correction: your home is {home}|actually your home is {home}|I meant your home is {home}|correct your reported home to {home}|your home should be {home}|I was wrong, your home is {home}',
 'I misspoke, your home is {home}|please correct your reported home to {home}|the correct home for you is {home}|I meant that your home is {home}',
 'let me correct that: your home is {home}|I need to update your reported home to {home}|I gave the wrong home for you, it is {home}|change the home I reported for you to {home}'),
}
SOCIAL = {'greet':('GREET','ACKNOWLEDGE',['Hello.','Hi there.','Good to see you.']),
          'wellbeing':('ASK','ANSWER',['Glad to have a moment to talk.','I am glad for the company.']),
          'tired':('INFORM','ACKNOWLEDGE',['That sounds tiring. Take your time.','A little rest sounds welcome.']),
          'thanks':('THANK','ACKNOWLEDGE',["You're welcome.",'Glad I could help.']),
          'bye':('FAREWELL','FAREWELL',['Take care.','Safe travels.','Goodbye.'])}
TOOLS={'location':'GET_CURRENT_LOCATION','wares':'LIST_WARES','balance':'GET_BALANCE','inventory':'LIST_INVENTORY',
       'directions':'LOOKUP_LOCATION','price':'LOOKUP_PRICE','buy':'BUY','sell':'SELL'}


def lesson(reference, normalize, kind, wording, episode, ordinal, split, checksum, rng):
    text=normalize(wording)
    row=set_turns(row_base(reference),[text],None)
    row.update(id=f'{episode}:{ordinal}',source='AUTHORED_GAME_TEACHING',sourceUrl='PROJECT://FISHBRAIN/GAME-TEACHING',
        attribution='Fishbrain project contributors; AI-authored development lessons',split=split,family=kind,category=kind,
        supervisedHeads=HEADS+['affect','stance','content'])
    for key in ('isolationComponent','packedHistoryTurns','packedInputTokens'):row.pop(key,None)
    p=row['structuredPerception']
    act,plan,tool,status,knowledge='ASK','ANSWER',None,None,'NONE'
    domain,goal='SOCIAL','RAPPORT'
    args=[]
    if kind in SOCIAL:
        act,plan,responses=SOCIAL[kind]
        row['response']=normalize(rng.choice(responses))
        if kind in ('wellbeing','tired'):domain='WELLBEING'
        if kind=='greet':p['affect']='FRIENDLY'
    elif kind in ('name','role'):
        knowledge='NAME' if kind=='name' else 'OCCUPATION'
        domain,goal='IDENTITY','INFORMATION_EXCHANGE'
    elif kind in TOOLS:
        tool,plan=TOOLS[kind],'EXECUTE_TOOL'
        # Explicit speech-act labels accompany the authored wording.
        act='ASK' if kind not in ('buy','sell') and text.startswith(('WHAT','WHERE','HOW','WHICH','CAN ','COULD ')) else 'REQUEST'
        domain='LOCATION_NAVIGATION' if kind in ('location','directions') else 'ITEMS_INVENTORY' if kind=='inventory' else 'TRADE_ECONOMY'
        goal='TRANSACTION' if kind in ('buy','sell') else 'INFORMATION_EXCHANGE'
        knowledge={'location':'CURRENT_LOCATION','balance':'BALANCE','inventory':'INVENTORY'}.get(kind,'NONE')
    elif kind in ('negated','hypothetical','quoted'):
        act='REQUEST' if kind=='negated' else 'ASK' if text.startswith('WHAT IF') else 'INFORM'
        tool,status,plan,domain='BUY',kind.upper(),'ACKNOWLEDGE','TRADE_ECONOMY'
        row['response']=normalize({'negated':'All right.','hypothetical':'That is only a possibility.','quoted':'I see.'}[kind])
    elif kind in ('clarify','unsupported'):
        act,plan,domain,goal='REQUEST','CLARIFY','ASSISTANCE','CLARIFICATION'
    elif kind=='cancel':
        act,plan,tool,status,domain='REFUSE','ACKNOWLEDGE','BUY','NEGATED','TRADE_ECONOMY'
        row['response']='ALL RIGHT.'
    else:
        act='ASK' if kind.endswith('recall') else 'CORRECT' if kind.endswith('correction') else 'INFORM'
        plan='ANSWER' if kind.endswith('recall') else 'CORRECT' if kind.endswith('correction') else 'ACKNOWLEDGE'
        domain,goal='IDENTITY','INFORMATION_EXCHANGE'
        if not kind.endswith('recall'):row['response']=normalize('Thanks for correcting me.' if kind.endswith('correction') else 'Thanks for telling me.')
    if tool in ('BUY','SELL','LOOKUP_PRICE') and kind!='cancel':
        item=next(x for x in ('IRON SWORD','HEALTH POTION','ROPE') if x in text)
        args.append(slot(text,item,'ITEM'))
        if tool!='LOOKUP_PRICE':
            quantity=next(x for x in ('ONE','TWO','1','2') if ' '+x+' ' in ' '+text+' ')
            args.append(slot(text,quantity,'QUANTITY'))
    if kind=='directions':args=[slot(text,next(x for x in ('INN','MARKET','CASTLE') if x in text),'PLACE')]
    f=frame(text,act,status,tool=tool,arguments=args)
    if kind in ('home','recall','correction','npc_home','npc_recall','npc_correction'):
        owner='NPC' if kind.startswith('npc_') else 'PLAYER'
        recalling=kind.endswith('recall')
        fact=dict(act='REFER_BACK' if recalling else 'CORRECT' if kind.endswith('correction') else 'INFORM',
            subject='PLAYER' if recalling else owner,target=owner if recalling else 'NPC' if owner=='PLAYER' else 'PLAYER',
            factKind='HOME',negated=False,confidence=1,evidence='AUTHORED_GAME_TEACHING')
        if not recalling:
            value=next(normalize(x) for x in HOMES if normalize(x) in text)
            fact['factValueSpan']=dict(normalizedValue=value,start=text.index(value),length=len(value))
        f['fact']=fact;p['discourse']=fact
    p.update(speechActs=[act],domains=[domain],goals=[goal],knowledgeTarget=knowledge,toolSchema=tool or 'NONE',slots=args,
        policy='EXECUTE_TOOL' if plan=='EXECUTE_TOOL' else 'ANSWER' if plan=='ANSWER' else 'CLARIFY' if plan=='CLARIFY' else 'ACKNOWLEDGE')
    row['contextual']=dict(frames=[f],plan=[dict(act=plan,frameIndex=0)],relevantFacts=[],agenda=[])
    eligible(row,'authored',row['response'] is not None,episode,episode,'PROJECT-OWNED','game-teaching-v1',checksum)
    row['rejectedResponse']='YOU BOUGHT FIVE IRON SWORD FOR TEN GOLD.'
    row['training']['claimNegativeEligible']=True
    return row


def compound(left,right,normalize):
    result=copy.deepcopy(left)
    first=left['turns'][0]['text'];second=right['turns'][0]['text']
    if not first.endswith(('.', '?', '!')):first+='.'
    text=normalize(first+' '+second);offset=len(first)+1
    a=copy.deepcopy(left['contextual']['frames'][0]);a['length']=len(first)
    b=copy.deepcopy(right['contextual']['frames'][0]);b['start']+=offset
    for argument in b['arguments']:argument['start']+=offset
    if b['fact'].get('factValueSpan'):b['fact']['factValueSpan']['start']+=offset
    plans=[dict(left['contextual']['plan'][0],frameIndex=0),dict(right['contextual']['plan'][0],frameIndex=1)]
    result['contextual'].update(frames=[a,b],plan=plans)
    p=result['structuredPerception'];other=right['structuredPerception']
    for key in ('speechActs','domains','goals'):p[key]=list(dict.fromkeys(p[key]+other[key]))
    p['slots']=a['arguments']+b['arguments']
    if p['knowledgeTarget']=='NONE':p['knowledgeTarget']=other['knowledgeTarget']
    p['policy']='EXECUTE_TOOL' if any(x['act']=='EXECUTE_TOOL' for x in plans) else 'ANSWER' if any(x['act']=='ANSWER' for x in plans) else 'ACKNOWLEDGE'
    p['toolSchema']=next((f['toolName'] for f,act in zip((a,b),plans) if act['act']=='EXECUTE_TOOL'),'NONE')
    if right['response'] and not result['response']:result['response']=right['response']
    result['training']['responseEligible']=result['response'] is not None
    result['training']['claimPositiveEligible']=result['response'] is not None
    result['category']='compound'
    set_turns(result,[text],result['response'])
    return result


HOMES=['Cedar Hollow','Copper Bay','Willow Quay','Marble Port','North Road','South Bank','East Tower','West Harbor',
       'Pine Crossing','Birch Landing','Raven Hill','Silver Vale']


def main():
    parser=argparse.ArgumentParser();parser.add_argument('--cli',required=True);parser.add_argument('--reference',required=True)
    parser.add_argument('--output',required=True);args=parser.parse_args()
    output=Path(args.output)
    if output.exists():raise ValueError('Use a fresh curriculum directory')
    output.mkdir(parents=True)
    reference=next(read_jsonl(args.reference));normalizer=Normalizer(args.cli);checksum=digest(Path(__file__).read_bytes());rng=random.Random(42)
    audit={};scenarios=[]
    try:
        for split_index,split in enumerate(('train','validation','test')):
            rows=[]
            # Each phrase belongs to one split. Context/entity variants stay with it.
            repeats=20 if split=='train' else 12 if split=='validation' else 3
            catalog={kind:choices[split_index].split('|') for kind,choices in WORDS.items()}
            for kind,phrases in catalog.items():
                for phrase_index,phrase in enumerate(phrases):
                    family=f'GAME:{split}:{kind}:{phrase_index}'
                    for variant in range(repeats):
                        episode=family+f':context{variant}'
                        # Gold prelude turns produce actual reducer state. Some episodes start fresh.
                        pool=['greet','wellbeing','thanks','name','role','location','wares','inventory','price','directions','tired','bye']
                        prelude=rng.sample(pool,[0,1,3,6][variant%4])
                        if kind in ('recall','correction','npc_recall','npc_correction'):
                            owned=['home','npc_home'];rng.shuffle(owned)
                            prelude+=owned+rng.sample(pool,rng.randint(0,4))
                        sequence_offset=rng.choice([0,1,20,200] if split=='train' else [0,1,30,300])
                        kinds=prelude+[kind]
                        if kind=='home' and variant%2:kinds+=['npc_home','tired','recall','correction','recall','npc_recall']
                        values=dict(item=rng.choice(['rope','health potion','iron sword']),qty=rng.choice(['one','two','1','2']),
                            place=rng.choice(['inn','market','castle']),home=HOMES[split_index*4+variant%4])
                        for ordinal,turn_kind in enumerate(kinds):
                            template=phrase if ordinal==len(prelude) else rng.choice(catalog[turn_kind])
                            if turn_kind=='correction':values['home']=HOMES[split_index*4+(variant+1)%4]
                            turn_values=dict(values)
                            if turn_kind.startswith('npc_'):turn_values['home']=HOMES[split_index*4+(variant+2)%4]
                            if turn_kind=='npc_correction':turn_values['home']=HOMES[split_index*4+(variant+3)%4]
                            wording=template.format(**turn_values)
                            if variant%3==1:wording+='?' if turn_kind in ('name','role','wellbeing','location','balance','inventory','recall','price','directions','wares') else '.'
                            row=lesson(reference,normalizer,turn_kind,wording,episode,ordinal,split,checksum,rng)
                            row['episodeSequenceOffset']=sequence_offset
                            row['training']['augmentationFamily']=family;row['semanticFamilyId']=family
                            rows.append(row)
                        if split=='test' and variant==2 and phrase_index==0:
                            for ordinal,row in enumerate(rows[-len(kinds):],1):
                                scenarios.append(dict(sessionId=episode,turnIndex=ordinal,input=row['turns'][-1]['text'],
                                    memoryApplicable=kind in ('home','recall','correction','npc_home','npc_recall','npc_correction'),topicSwitchApplicable=False))
            pairs=[('greet','name'),('thanks','location'),('negated','wares'),('greet','wares'),
                   ('buy','sell'),('price','balance'),('home','wares'),('correction','wares')]
            for pair_index,(left_kind,right_kind) in enumerate(pairs):
                for variant in range(100 if split=='train' else 24 if split=='validation' else 8):
                    family=f'GAME:{split}:compound:{pair_index}'
                    episode=family+f':context{variant}'
                    values=dict(item=rng.choice(['rope','health potion','iron sword']),qty=rng.choice(['one','two','1','2']),
                                place=rng.choice(['inn','market','castle']),home=HOMES[split_index*4+variant%4])
                    prelude=rng.sample(['greet','wellbeing','thanks','name','role','location','wares','inventory','price','directions','tired','bye'],[0,1,3,6][variant%4])
                    sequence_offset=rng.choice([0,1,20,200] if split=='train' else [0,1,30,300])
                    if left_kind=='correction':prelude+=['home','npc_home']
                    for ordinal,kind in enumerate(prelude):
                        wording=rng.choice(catalog[kind]).format(**values)
                        row=lesson(reference,normalizer,kind,wording,episode,ordinal,split,checksum,rng)
                        row['episodeSequenceOffset']=sequence_offset
                        row['training']['augmentationFamily']=family;row['semanticFamilyId']=family;rows.append(row)
                    values['home']=HOMES[split_index*4+(variant+1)%4]
                    left=lesson(reference,normalizer,left_kind,rng.choice(catalog[left_kind]).format(**values),episode,len(prelude),split,checksum,rng)
                    right=lesson(reference,normalizer,right_kind,rng.choice(catalog[right_kind]).format(**values),episode,len(prelude),split,checksum,rng)
                    row=compound(left,right,normalizer);row['episodeSequenceOffset']=sequence_offset
                    row['training']['augmentationFamily']=family;row['semanticFamilyId']=family;rows.append(row)
            write_jsonl(output/(split+'.jsonl'),rows)
            audit[split]=dict(rows=len(rows),episodes=len({r['training']['episodeId'] for r in rows}),
                families=len({r['training']['augmentationFamily'] for r in rows}),authoredPhrases=sum(map(len,catalog.values())))
    finally:normalizer.close()
    write_jsonl(output/'scenarios.jsonl',scenarios)
    (output/'audit.json').write_text(json.dumps(dict(seed=42,sourceHash=checksum,splits=audit,
        description='Synthetic state/context/entity augmentation of split-authored phrase families. Not thousands of independently authored episodes.',
        responseSource='Project-owned authored text only; no public conversations in this targeted experiment.',
        testRole='Held-out development wording; original frozen v4 release challenge remains unchanged.'),indent=2))
    print((output/'audit.json').read_text())


if __name__=='__main__':main()
