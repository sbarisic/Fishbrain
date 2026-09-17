"""Freeze authored evaluation before training. Never imported by the corpus compiler.

Each entry is a controlled request with a gold history and state. These are contextual
interpretation tests, not autonomous rollouts; the separate conversation export exercises
runtime-managed histories. User-transcript cases are kept in development.json instead.
"""
import copy
import json
from pathlib import Path
from common import ROOT, Normalizer, read_jsonl, digest, row_base
from annotations import frame, slot

FRESH = {
'greeting': [('Greetings, friend','GREET','ACKNOWLEDGE'),('oh, hello!','GREET','ACKNOWLEDGE'),
 ('good afternoon to you','GREET','ACKNOWLEDGE'),('hey there, good to meet you','GREET','ACKNOWLEDGE'),
 ('hello, traveler','GREET','ACKNOWLEDGE'),('a pleasant morning to you','GREET','ACKNOWLEDGE'),
 ('hiya there!','GREET','ACKNOWLEDGE'),('well, hello there','GREET','ACKNOWLEDGE'),
 ('good evening to you','GREET','ACKNOWLEDGE'),('greetings','GREET','ACKNOWLEDGE')],
'identity': [('What name do you go by','NAME'),('Tell me a little about your role','ROLE'),
 ('Which place are you originally from?','ORIGIN'),('Where is your home these days','HOME'),
 ('Do you have any family?','FAMILY'),('What is your occupation','OCCUPATION'),
 ('How would you describe your character?','TRAITS'),('What are you able to help with','CAPABILITIES'),
 ('May I know your name?','NAME'),('What kind of work do you do here?','OCCUPATION')],
'wellbeing': [('I had a difficult morning and could use a kind word','INFORM','ACKNOWLEDGE'),
 ('How are things with you today','ASK','ANSWER'),('I feel a bit anxious in a new place','INFORM','ACKNOWLEDGE'),
 ('I had some lovely news today','INFORM','ACKNOWLEDGE'),('Everything seems to take more effort today','INFORM','ACKNOWLEDGE'),
 ('Could we sit quietly for a moment?','REQUEST','ACKNOWLEDGE'),('I was hoping for a relaxed conversation','INFORM','ACKNOWLEDGE'),
 ('My day got better after a friendly conversation','INFORM','ACKNOWLEDGE'),
 ('I feel foolish for getting that wrong','INFORM','ACKNOWLEDGE'),('I need a little time before I explain','INFORM','ACKNOWLEDGE')],
'nonexecuting_action': [('Please leave the rope alone. Do not buy it.','REQUEST','NEGATED','BUY','ROPE'),
 ('Imagine purchasing three torches.','INFORM','HYPOTHETICAL','BUY','TORCHES'),
 ('The traveler said "sell two swords".','INFORM','QUOTED','SELL','SWORDS'),
 ('I decided against buying an apple.','REFUSE','NEGATED','BUY','APPLE'),
 ('If I were to buy a rope, would it be useful?','ASK','HYPOTHETICAL','BUY','ROPE'),
 ('The instruction "buy one torch" was just an example.','INFORM','QUOTED','BUY','TORCH'),
 ('Stop the purchase. Do not buy one sword.','REQUEST','NEGATED','BUY','SWORD'),
 ('Selling three apples is only a possibility.','INFORM','HYPOTHETICAL','SELL','APPLES'),
 ('Do not sell any rope for me.','REQUEST','NEGATED','SELL','ROPE'),
 ('I am quoting someone: "sell one apple".','INFORM','QUOTED','SELL','APPLE')],
'tools': [('Can you list the goods you sell?','LIST_WARES',[]),('Please tell me my current gold balance','GET_BALANCE',[]),
 ('Could you list what I am carrying?','LIST_INVENTORY',[]),('Purchase three apples for me.','BUY',[('THREE','QUANTITY'),('APPLES','ITEM')]),
 ('Sell one piece of rope.','SELL',[('ONE','QUANTITY'),('ROPE','ITEM')]),
 ('I would like one torch, please buy it.','BUY',[('ONE','QUANTITY'),('TORCH','ITEM')]),
 ('Show your available merchandise','LIST_WARES',[]),('Count the gold I have','GET_BALANCE',[]),
 ('List my carried items','LIST_INVENTORY',[]),('Please sell two torches.','SELL',[('TWO','QUANTITY'),('TORCHES','ITEM')])],
}

COMPOUND = [
 ('Hello, friend.','May I ask your name?','GREET','ASK','ACKNOWLEDGE','ANSWER','NAME',None),
 ('Sorry about my tone.','What work do you do?','APOLOGIZE','ASK','ACKNOWLEDGE','ANSWER','OCCUPATION',None),
 ('Thank you for your patience.','Where do you call home?','THANK','ASK','ACKNOWLEDGE','ANSWER','HOME',None),
 ('Good afternoon.','List your goods for me.','GREET','REQUEST','ACKNOWLEDGE','EXECUTE_TOOL','NONE','LIST_WARES'),
 ('I will not purchase anything.','Tell me my gold balance.','REFUSE','REQUEST','ACKNOWLEDGE','EXECUTE_TOOL','BALANCE','GET_BALANCE'),
 ('That is useful, thanks.','List my carried items.','THANK','REQUEST','ACKNOWLEDGE','EXECUTE_TOOL','INVENTORY','LIST_INVENTORY'),
 ('I am worn out.','Please give me a moment.','INFORM','REQUEST','ACKNOWLEDGE','ACKNOWLEDGE','NONE',None),
 ('Thank you for the conversation.','Farewell, friend.','THANK','FAREWELL','ACKNOWLEDGE','FAREWELL','NONE',None),
 ('I spoke too sharply.','Can we begin again?','APOLOGIZE','REQUEST','ACKNOWLEDGE','ACKNOWLEDGE','NONE',None),
 ('It is good to meet you.','Where are you originally from?','GREET','ASK','ACKNOWLEDGE','ANSWER','ORIGIN',None)]

# Independent memory language and names; paired final sentences test contextual differences.
MEMORY = [
 ('HOME','BRAMBLE POINT','I make my home in Bramble Point.','Which home did I mention before our detour?'),
 ('HOME','OTTER LAKE','Home for me is Otter Lake.','Which home did I mention before our detour?'),
 ('NAME','SOREN','The name I use is Soren.','What name was I using at the start?'),
 ('NAME','IVA','I introduced myself as Iva.','What name was I using at the start?'),
 ('OCCUPATION','FLETCHER','I earn my living as a fletcher.','What was my occupation again?'),
 ('OCCUPATION','DYER','My work is that of a dyer.','What was my occupation again?'),
 ('ORIGIN','CLOUD PASS','I originally hail from Cloud Pass.','Can you recall where I originally came from?'),
 ('ORIGIN','SAND REACH','Sand Reach is where I came from originally.','Can you recall where I originally came from?'),
 ('HOME','LANTERN FEN','I consider Lantern Fen my home.','Where was the home I told you about?'),
 ('HOME','THISTLE PORT','My home is the place called Thistle Port.','Where was the home I told you about?'),
 ('NAME','KESTREL','People call me Kestrel.','What did I ask you to call me?'),
 ('NAME','MERRIN','You may call me Merrin.','What did I ask you to call me?'),
 ('OCCUPATION','TANNER','I work in the trade of tanner.','Which trade did I describe as mine?'),
 ('OCCUPATION','CARTWRIGHT','My trade is cartwright.','Which trade did I describe as mine?'),
 ('ORIGIN','DEEP VALE','I came here from Deep Vale.','Which place was my origin?'),
 ('ORIGIN','WIND QUAY','My origin is Wind Quay.','Which place was my origin?'),
 ('HOME','ROOK HILL','Rook Hill is my home.','What place did I tell you I live in?'),
 ('HOME','LILY MARSH','I live in Lily Marsh.','What place did I tell you I live in?'),
 ('NAME','VERA','My given name is Vera.','Do you still have the name I gave you?'),
 ('NAME','HAL','Hal is my name.','Do you still have the name I gave you?')]

CORRECTIONS = [
 ('HOME','BEECH LANDING','Let me put that right: my home is Beech Landing.'),
 ('HOME','GREY COVE','Please correct your note. I live in Grey Cove.'),
 ('NAME','NELIA','That was the wrong name. Use Nelia for me.'),
 ('NAME','TAREN','I misspoke when I introduced myself. My name is Taren.'),
 ('OCCUPATION','MILLER','My earlier description of my work was wrong. I am a miller.'),
 ('OCCUPATION','TAILOR','Let me amend my occupation to tailor.'),
 ('ORIGIN','NORTH HOLLOW','I mixed up my origin. It is North Hollow.'),
 ('ORIGIN','SOUTH FEN','For my origin, replace that place with South Fen.'),
 ('HOME','BELL HARBOR','The home I mentioned was my old one. My home is Bell Harbor now.'),
 ('HOME','ASH CROSSING','An update about my home: it is Ash Crossing.'),
 ('NAME','SOL','I gave you the wrong name earlier. My name is Sol.'),
 ('NAME','RENA','Correct my name to Rena, please.'),
 ('OCCUPATION','GLAZIER','I no longer do that work. My occupation is glazier.'),
 ('OCCUPATION','SMITH','I used an inaccurate description of my trade. I am a smith.'),
 ('ORIGIN','WESTMERE','About my origin, I meant Westmere.'),
 ('ORIGIN','EASTMERE','I need to correct where I come from. My origin is Eastmere.'),
 ('HOME','FINCH BAY','My home needs correcting. It is Finch Bay.'),
 ('HOME','COPPER FEN','I said the wrong place for my home. It is Copper Fen.'),
 ('NAME','DARA','The name to remember for me is Dara, not the earlier one.'),
 ('NAME','NIKO','I should have introduced myself as Niko.')]

DETOURS = [
 ('I tried a different path today.','Did anything catch your attention?','The shape of the hills.','That sounds memorable.'),
 ('I have been practicing a song.','How is it going?','Slowly, but it is enjoyable.','Enjoying the practice can help.'),
 ('I enjoy watching the seasons change.','What do you notice first?','The colors.','Small changes can stand out.'),
 ('I spent some time drawing.','What did you draw?','A view from the road.','That sounds like a pleasant subject.'),
 ('I was thinking about a story.','Which part stayed with you?','A surprising choice.','Choices can make stories interesting.'),
 ('I like working at a steady pace.','Does it help you focus?','Usually it does.','Finding a comfortable pace can help.'),
 ('I heard a funny riddle.','Did you solve it?','Not yet.','Perhaps another look will help.'),
 ('I took a quiet break.','Was it welcome?','Very much so.','A little quiet can make a difference.'),
 ('I am learning to be patient.','What helps?','Taking a moment before replying.','That sounds thoughtful.'),
 ('I tried to make a small gift.','How did it go?','Not perfectly, but I enjoyed it.','Enjoying the process matters too.')]

AGENDA = [
 ('Where do you call home?','HOME','LARK SHORE','To answer your earlier question, my home is Lark Shore.'),
 ('What name should I use for you?','NAME','ANSEL','The answer to your question is Ansel. That is my name.'),
 ('What is your occupation?','OCCUPATION','PAVER','You asked about my occupation. It is paver.'),
 ('Where did you come from originally?','ORIGIN','SILK HILL','My origin, which you asked about, is Silk Hill.'),
 ('What place is your home?','HOME','REED SHORE','I can answer now: my home is Reed Shore.'),
 ('What do people call you?','NAME','BRIN','About your question, people call me Brin.'),
 ('What work do you do?','OCCUPATION','COOK','Back to your question. My occupation is cook.'),
 ('What is your place of origin?','ORIGIN','FLINT FORD','I meant to answer before: my origin is Flint Ford.'),
 ('Where do you live?','HOME','STILL BROOK','The place I live is Still Brook, to answer you.'),
 ('Which name do you prefer?','NAME','CAEL','Use Cael for my name. Sorry for the delay in answering.')]

TOPICS = [
 'Could we leave that question open and discuss friendship?', 'I will answer later. For now, can we talk about music?',
 'Before I answer, I want to discuss a different subject.', 'Let us pause that topic and talk about a story.',
 'I need time on that question. Could we talk about patience instead?', 'Keep that question for later, please.',
 'I have not forgotten your question. Let us talk about the walk first.', 'Can we return to that after a lighter conversation?',
 'I would rather discuss something cheerful before answering.', 'Please wait for my answer. I want a quiet moment first.']


def build(normalize):
    reference=next(read_jsonl(ROOT/'data/compiled-contextual-v3/train.jsonl'))
    base=row_base(reference)
    result=[]
    def add(category, turns, frames, plans, state=None, memory=None, facts=None, agenda=None, tool=None, knowledge=None, pending=None):
        id=f'{category}-{sum(x["category"]==category for x in result):02}'
        utterances=[dict(sequence=i,speaker='PLAYER' if i%2==0 else 'NPC',text=normalize(t)) for i,t in enumerate(turns)]
        request=dict(conversationId='CHALLENGE-V4:'+id,turnId=id,utterances=utterances,state=state or copy.deepcopy(base['initialDialogueState']),
                     persona=reference['persona'],playerProfile=dict(facts=[]),responseSequence=len(utterances),seed=42)
        case=dict(id=id,request=request,frames=frames,plan=plans,expectedTool=tool,selectedFacts=memory,expectedFacts=facts,expectedAgenda=agenda,
                  expectedKnowledge=knowledge,expectedPending=pending)
        result.append(dict(category=category,case=case))
    for category,entries in FRESH.items():
        for entry in entries:
            text=normalize(entry[0])
            if category=='identity':
                # Knowledge target is scored separately in the review output; persona values remain typed.
                frames=[frame(text,'ASK')]; plans=[dict(act='ANSWER',frameIndex=0)]
            elif category=='nonexecuting_action':
                _,act,status,tool,item=entry
                arguments=[slot(text,item,'ITEM')]
                for quantity in ('ONE','TWO','THREE'):
                    if quantity in text.split(): arguments.insert(0,slot(text,quantity,'QUANTITY'))
                frames=[frame(text,act,status,tool=tool,arguments=arguments)]; plans=[dict(act='ACKNOWLEDGE',frameIndex=0)]
            elif category=='tools':
                _,tool,arguments=entry
                act='ASK' if text.endswith('?') else 'REQUEST'
                frames=[frame(text,act,tool=tool,arguments=[slot(text,v,k) for v,k in arguments])]
                plans=[dict(act='EXECUTE_TOOL',frameIndex=0)]
            else:
                _,act,plan=entry
                frames=[frame(text,act)]; plans=[dict(act=plan,frameIndex=0)]
            add(category,[text],frames,plans,tool=entry[1] if category=='tools' else None,knowledge=entry[1] if category=='identity' else None)
    for compound_index,(first,second,act1,act2,plan1,plan2,knowledge,tool) in enumerate(COMPOUND):
        if compound_index>=7:
            combinations=[('Purchase two torches.','After that, sell one rope.','BUY','SELL','TORCHES','ROPE'),
                          ('Sell two apples.','Then buy one sword.','SELL','BUY','APPLES','SWORD'),
                          ('Buy two rope.','Sell one apple afterward.','BUY','SELL','ROPE','APPLE')]
            first,second,tool1,tool2,item1,item2=combinations[compound_index-7]
            first,second=normalize(first),normalize(second)
            offset=len(first)+1
            frames=[frame(first,'REQUEST',tool=tool1,arguments=[slot(first,'TWO','QUANTITY'),slot(first,item1,'ITEM')]),
                    frame(second,'REQUEST',start=offset,tool=tool2,arguments=[slot(second,'ONE','QUANTITY',offset),slot(second,item2,'ITEM',offset)])]
            add('compound',[first+' '+second],frames,[dict(act='EXECUTE_TOOL',frameIndex=0),dict(act='ACKNOWLEDGE',frameIndex=1)],tool=tool1,
                pending=[dict(action='EXECUTE_TOOL',toolSchema=tool2,arguments=dict(ITEM={'SWORD':'IRON SWORD'}.get(item2,item2),QUANTITY='1'),sourceUtterance=0)])
            continue
        first,second=normalize(first),normalize(second)
        frames=[frame(first,act1),frame(second,act2,start=len(first)+1,tool=tool)]
        add('compound',[first+' '+second],frames,[dict(act=plan1,frameIndex=0),dict(act=plan2,frameIndex=1)],tool=tool,knowledge=knowledge if knowledge!='NONE' else None)
    for index,(kind,value,report,question) in enumerate(MEMORY):
        history=[report,'Thanks for explaining.']+list(DETOURS[index%10])
        fact=dict(subject='PLAYER',kind=kind,value=value,negated=False,sourceUtterance=0,confidence=1,provenance='SESSION_REPORTED')
        other_value={'HOME':'GULL POINT','NAME':'ODEN','OCCUPATION':'SHEPHERD','ORIGIN':'HIGH SHOAL'}[kind]
        other=dict(fact,subject='NPC',value=other_value)
        history[0]+=' Your '+kind.lower()+' is '+other_value+', according to what I heard.'
        state=copy.deepcopy(base['initialDialogueState']);state['sessionFacts']=[fact,other]
        text=normalize(question)
        discourse=dict(act='REFER_BACK',subject='PLAYER',target='PLAYER',factKind=kind,negated=False,antecedentUtterance=0,confidence=1,evidence='CHALLENGE_V4')
        add('memory',history+[text],[dict(frame(text,'ASK'),antecedent=0,fact=discourse)],[dict(act='ANSWER',frameIndex=0)],state,[fact],[fact,other])
        _,newvalue,correction=CORRECTIONS[index]
        text=normalize(correction)
        discourse=dict(discourse,act='CORRECT',target='NPC',factValueSpan=dict(normalizedValue=newvalue,start=text.index(newvalue),length=len(newvalue)))
        add('correction',history+[text],[dict(frame(text,'CORRECT'),antecedent=0,fact=discourse)],[dict(act='CORRECT',frameIndex=0)],state,[fact],
            [other,dict(fact,value=newvalue,sourceUtterance=6)])
    for index,(question,kind,value,answer) in enumerate(AGENDA):
        history=['You wanted to ask me something.',question]+list(DETOURS[index])
        goal=dict(kind='UNANSWERED_QUESTION',subject=kind,sourceTurn=1,status='ACTIVE')
        state=copy.deepcopy(base['initialDialogueState']);state['agenda']=[goal]
        text=normalize(answer)
        discourse=dict(act='INFORM',subject='PLAYER',target='NPC',factKind=kind,negated=False,antecedentUtterance=1,
                       factValueSpan=dict(normalizedValue=value,start=text.index(value),length=len(value)),confidence=1,evidence='CHALLENGE_V4')
        fact=dict(subject='PLAYER',kind=kind,value=value,negated=False,sourceUtterance=6,confidence=1,provenance='SESSION_REPORTED')
        add('agenda',history+[text],[dict(frame(text,'INFORM'),antecedent=1,fact=discourse)],[dict(act='ACKNOWLEDGE',frameIndex=0)],state,[],[fact],[dict(goal,status='COMPLETED')])
        topic=normalize(TOPICS[index])
        add('topic_change',history+[topic],[frame(topic,'REQUEST')],[dict(act='ACKNOWLEDGE',frameIndex=0)],state,[],[],[goal])
    assert len(result)==120
    return result


if __name__=='__main__':
    output=ROOT/'data/conversation-v4/challenge.json'
    if output.exists(): raise ValueError('Frozen challenge already exists; never overwrite after training')
    output.parent.mkdir(parents=True,exist_ok=True)
    normalize=Normalizer(ROOT/'data/training/conversation-v4-tools/Fishbrain.dll')
    try: suite=build(normalize)
    finally: normalize.close()
    output.write_text(json.dumps(suite,indent=2),encoding='utf-8')
    (output.parent/'challenge.sha256').write_text(digest(output.read_bytes())+'\n')
    from common import write_jsonl
    review=[]
    selected=[]
    for category, count in [('memory',10),('correction',10),('agenda',10),('topic_change',10),
                            ('greeting',2),('identity',2),('tools',2),('compound',2),('nonexecuting_action',2)]:
        selected += [x for x in suite if x['category']==category][:count]
    assert len(selected)==50
    for entry in selected:
        for index,utterance in enumerate(u for u in entry['case']['request']['utterances'] if u['speaker']=='PLAYER'):
            review.append(dict(sessionId=entry['case']['id'],turnIndex=index+1,input=utterance['text'],
                               memoryApplicable=entry['category'] in ('memory','correction'),agendaApplicable=entry['category'] in ('agenda','topic_change'),
                               compoundApplicable=entry['category']=='compound',topicSwitchApplicable=entry['category']=='topic_change'))
    write_jsonl(output.parent/'review-scenarios.jsonl',review)
    development=['Hello','How are you?','How did we get here?','Who are you?','Hello. Who are you?',
                 'What do you have for sale?','My home is Cedar Hollow.','How much gold do I have?','I have 10 gold']
    (output.parent/'development.json').write_text(json.dumps(dict(note='User transcript; development regressions, not unseen evaluation.',inputs=development),indent=2))
    print('Frozen 120 cases, including 60 with seven-utterance contexts:',digest(output.read_bytes()))
