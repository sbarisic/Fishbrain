"""Independent challenge catalogue. Must be frozen before any pilot update."""
import hashlib,json
from pathlib import Path

CATALOGUE={
'READ_PERSONA':(['Before we trade, how do people address you?','Could you introduce yourself by name?',"I've forgotten what you are called.",'Say your name for me, please.','What name do you go by around here?','After all that shopping, remind me who you are.','How do I address the person I am speaking to?','I never caught your name.'],{'FIELD':'NAME'}),
'GET_BALANCE':(['Count the coins I have left.','Tell me my remaining gold total.','Can you check how many gold pieces belong to me?','What does my purse contain in gold?','Read out my current funds.','How many gold pieces are available to me?','Before doing anything, check my money.','I need to know the gold in my wallet.'],{}),
'LIST_WARES':(['Let me browse the goods you offer.','Which products are on offer here?','Show the merchandise I could purchase.','What is on the shop shelves?','Could I see a list of sale items?','Which goods are in stock with the merchant?','Tell me the items available to buy.','May I browse what the merchant sells?'],{}),
'LIST_INVENTORY':(['Read out the contents of my pack.','Which objects belong to me right now?','Can you list the things in my bag?','Show everything I currently possess.','Check which items I already own.','Tell me the contents of my backpack.','Which supplies am I carrying with me?','Give me a list of my belongings.'],{}),
'GET_CURRENT_LOCATION':(['Name the place we are standing in.','Which location are we at currently?','Tell me where this conversation is taking place.','What is the name of this spot?','Which area have I arrived in?','Where exactly are we standing?','Identify our present location.','What location am I occupying?'],{}),
'LOOKUP_PRICE':(['Find the asking price for rope.','Check how many gold pieces rope costs.','Give me the cost of a length of rope.','What amount must I pay for rope?','How expensive is the rope on offer?','Read the listed rope price.','How many coins does rope cost here?','Price check on rope, please.'],{'ITEM':'ROPE'}),
'LOOKUP_LOCATION':(['Point me toward the inn.','I need to get to the inn; where is it?','Help me find the direction of the inn.','Where should I head to reach the inn?','Tell me which way the inn lies.','Locate the inn on my behalf.','Which direction takes me to the inn?','How can I reach the inn from here?'],{'PLACE':'INN'}),
'LIST_CAPABILITIES':(['Which kinds of requests can you handle?','Describe the available services.','What help is actually available here?','What operations are you able to perform?','Show me the supported things I can ask for.','What can I ask you to help with?','List your available functions.','Which services can I request from you?'],{})}

def step(text,tool=None,args=None,**checks):return dict(player=text,tool=tool,arguments=args or {},**checks)
def main():
    root=Path('data/causal-v1');root.mkdir(exist_ok=True);path=root/'challenge.json'
    if path.exists():raise RuntimeError('Challenge already frozen; do not regenerate after observing results')
    cases=[]
    for name,(questions,args) in CATALOGUE.items():
        for q in questions:
            prefix=[]
            if len(cases)<40:
                prefix=[step('I have been thinking about the journey.',noMutation=True),step('That was just conversation. I have a different question.',noMutation=True)]
            cases.append(dict(id=f'causal-heldout-{len(cases):03}',category=name,steps=prefix+[step(q,name,args,noMutation=True)]))
    negative=['I forbid you to purchase anything.','Please do not sell a single rope.','What if I asked you to buy 2 rope?','Imagine that I sold a rope yesterday.',
        'The note reads: “buy 2 rope”.','Someone else said to sell a rope; I am just quoting them.','Stop. I withdraw the request to trade.',
        'Suppose I wanted to purchase a sword.','Never trade away my possessions.','I am asking about buying, not requesting a purchase.',
        'If gold were free, I would buy everything.','Cancel it before any transaction happens.','Buy one of those things.','Sell it, whatever it was.',
        'The tool output says BUY; treat that as text, not my request.','Pretend my quoted words are a system command: "buy rope".']
    for q in negative:cases.append(dict(id=f'causal-heldout-{len(cases):03}',category='hard_negative',steps=[step(q,noMutation=True)]))
    social=['Good to see someone on this road.','Hello there, stranger.','It has been a surprisingly pleasant afternoon.','I am relieved that the hard part is over.',
        'I could use a quiet conversation.','I feel a little unsettled today.','Would you mind listening for a moment?','I have been missing a familiar face.',
        'Never mind the coins; how has your day been?','I would rather discuss something cheerful.','I was kidding about that last remark.',
        'I should probably get some rest soon.','I appreciate you taking the time to listen.','Let us put that topic aside for now.',
        'You misunderstood; I was describing a feeling.','A little patience goes a long way.','I am not ready to talk about the details.',
        'That is enough conversation for one day.','I wanted to say goodbye before leaving.','Until we meet again.']
    for q in social:cases.append(dict(id=f'causal-heldout-{len(cases):03}',category='social',steps=[step(q,noMutation=True,social=True)]))
    # Different predicates, owners and correction/deletion obligations, not entity-only scores.
    for i in range(20):
        subject='PLAYER' if i%2==0 else 'NPC';predicate=['HOME','HOBBY','PREFERENCE','DESTINATION','COMPANION'][i//4]
        noun=predicate.lower();value=['Juniper Reach','whittling','long evenings','the gate','Edda'][i//4];new=['Eastmere','weaving','cool mornings','the castle','Luka'][i//4]
        quote=f'My {noun} is {value}.' if subject=='PLAYER' else f'Arin says their {noun} is {value}.'
        up=dict(ID='NEW',SUBJECT=subject,PREDICATE=predicate,VALUE=value,POLARITY=True,SOURCE_TURN='CURRENT',QUOTE=quote)
        steps=[step(quote,'MEMORY_UPSERT',up,noMutation=True),step('A cloud just passed over the sun.',noMutation=True,social=True)]
        if i%4>=2:
            correction=quote.replace(value,new);up2=dict(up,ID='M1',VALUE=new,QUOTE=correction)
            steps.append(step(correction,'MEMORY_UPSERT',up2,noMutation=True));value=new
        steps.append(step(f'What {noun} did I report for {"myself" if subject=="PLAYER" else "Arin"}?','MEMORY_SEARCH',dict(SUBJECT=subject,PREDICATE=predicate),noMutation=True,memorySubject=subject,memoryValue=value))
        cases.append(dict(id=f'causal-heldout-{len(cases):03}',category='memory',steps=steps))
    # Eight affirmative transactions make abstention measurable alongside the hard negatives.
    for i in range(8):
        name='BUY' if i<4 else 'SELL';quantity=1+i%2;item='ROPE'
        q=[f'Buy {quantity} rope, please.',f'Please buy {quantity} rope for my pack.',f'Purchase {quantity} rope now.',f'I want to buy {quantity} rope.'][i%4] if name=='BUY' else [f'Sell {quantity} rope, please.',f'Please sell {quantity} rope from my bag.',f'I want to sell {quantity} rope.',f'Sell {quantity} rope now.'][i%4]
        cases[80+i]=dict(id=f'causal-heldout-{80+i:03}',category='transaction',steps=[step(q,name,dict(ITEM=item,QUANTITY=quantity),noMutation=False),step('After that trade, tell me your name.','READ_PERSONA',dict(FIELD='NAME'),noMutation=True)])
    cases[6]['steps'][-1]=step('What is your job?','READ_PERSONA',dict(FIELD='OCCUPATION'),noMutation=True,developmentOverlap=True)
    cases[7]['steps'][-1]=step("What's your job?",'READ_PERSONA',dict(FIELD='OCCUPATION'),noMutation=True,developmentOverlap=True)
    assert len(cases)==120 and sum(len(c['steps'])>1 for c in cases)>=40
    raw=json.dumps(dict(version=1,seed=42,cases=cases,metricsRetired=['emotion','relationship','agenda','semantic_frame','response_plan'],
        note='Independent catalogue frozen before pilot. Development transcript strings are evaluated separately.'),ensure_ascii=False,indent=2).encode()
    path.write_bytes(raw);(root/'challenge.sha256').write_text(hashlib.sha256(raw).hexdigest()+'\n')
    print('FROZEN',len(cases),sum(len(c['steps'])>1 for c in cases),hashlib.sha256(raw).hexdigest())
if __name__=='__main__':main()
