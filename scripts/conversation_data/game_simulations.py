"""Repeatable in-game development chats and transparent behavioral assertions.

Assertions do not score naturalness; all actual replies are exported for inspection.
These sessions are not imported by the curriculum generator.
"""
import argparse
import json
from pathlib import Path
from common import digest, read_jsonl, write_jsonl

SESSIONS={
'user_transcript':[
 ('hi',{}),('Where am I?',dict(tool='GET_CURRENT_LOCATION')),
 ('Nevermind, what do you have for sale?',dict(tool='LIST_WARES')),
 ('Tell me your name',dict(contains='ARIN')),('Where is the inn?',dict(tool='LOOKUP_LOCATION'))],
'first_visit':[
 ('Hello there!',{}),('What should I call you?',dict(contains='ARIN')),
 ('What do you do for work?',dict(contains='ROAD WARDEN')),('How are things with you?',{}),
 ('Could you tell me where we are?',dict(tool='GET_CURRENT_LOCATION')),
 ('I need directions to the inn.',dict(tool='LOOKUP_LOCATION'))],
'shopping_trip':[
 ('Hello.',{}),('Can I see what is for sale?',dict(tool='LIST_WARES')),
 ('How much gold do I have?',dict(tool='GET_BALANCE')),
 ('What does a rope cost?',dict(tool='LOOKUP_PRICE')),
 ('Buy one rope please.',dict(tool='BUY',balance=97)),
 ('What am I carrying now?',dict(tool='LIST_INVENTORY')),
 ('Tell me my remaining balance.',dict(tool='GET_BALANCE')),('Thanks for your help.',{})],
'sell_and_buy':[
 ('Show me my inventory.',dict(tool='LIST_INVENTORY')),
 ('Sell one rope for me.',dict(tool='SELL')),
 ('How much is a health potion?',dict(tool='LOOKUP_PRICE')),
 ('I would like to buy one health potion.',dict(tool='BUY')),
 ('How much gold is left?',dict(tool='GET_BALANCE')),('Goodbye.',{})],
'purchase_hard_negatives':[
 ('Do not buy one rope.',{}),('Suppose I bought two rope.',{}),
 ('Someone said "buy one rope".',{}),('I am imagining buying one rope.',{}),
 ('This is hypothetical: buy one rope.',{}),('I would not like to buy one rope.',{}),
 ('If I had more gold I would like to buy one rope.',{}),
 ('I said "I would like to buy one rope".',{}),('What is my balance?',dict(tool='GET_BALANCE',balance=100))],
'remember_me':[
 ('My home is Cedar Hollow.',dict(fact=['PLAYER','CEDAR HOLLOW'])),
 ('How are you?',{}),('What is your name?',dict(contains='ARIN')),
 ('Where did I say my home is?',dict(contains='CEDAR HOLLOW')),
 ('Actually my home is Copper Bay.',dict(fact=['PLAYER','COPPER BAY'])),
 ('What home did I tell you about?',dict(contains='COPPER BAY'))],
'keep_owners_separate':[
 ('My home is Willow Quay.',dict(fact=['PLAYER','WILLOW QUAY'])),
 ('You said your home is Marble Port.',dict(fact=['NPC','MARBLE PORT'])),
 ('Where do I live?',dict(contains='WILLOW QUAY')),
 ('What home did you mention?',dict(contains='MARBLE PORT')),
 ('Correction: my home is North Road.',dict(fact=['PLAYER','NORTH ROAD'],otherFact=['NPC','MARBLE PORT'])),
 ('What home did you mention?',dict(contains='MARBLE PORT'))],
'unfamiliar_home':[
 ('My home is Briarhaven.',dict(fact=['PLAYER','BRIARHAVEN'])),
 ('I am tired after travelling.',{}),('Where do I live?',dict(contains='BRIARHAVEN')),
 ('Correction: my home is Westmere.',dict(fact=['PLAYER','WESTMERE'])),
 ('Can you recall my home?',dict(contains='WESTMERE'))],
'same_final_cedar':[
 ('My home is Cedar Hollow.',dict(fact=['PLAYER','CEDAR HOLLOW'])),
 ('What is your job?',dict(contains='ROAD WARDEN')),('Where do I live?',dict(contains='CEDAR HOLLOW'))],
'same_final_copper':[
 ('My home is Copper Bay.',dict(fact=['PLAYER','COPPER BAY'])),
 ('What is your job?',dict(contains='ROAD WARDEN')),('Where do I live?',dict(contains='COPPER BAY'))],
'topic_changes':[
 ('Good morning.',{}),('I had a tiring journey.',{}),
 ('What goods do you sell?',dict(tool='LIST_WARES')),
 ('Anyway, how are you doing?',{}),('Where can I find the castle?',dict(tool='LOOKUP_LOCATION')),
 ('Can you tell me your name?',dict(contains='ARIN'))],
'ambiguous_or_unsupported':[
 ('Do it.',{}),('Buy that.',{}),('Give me the thing we discussed.',{}),
 ('Can you repair my armor?',{}),('What quests are available?',{}),
 ('I own a thousand gold coins.',{}),('What is my balance?',dict(tool='GET_BALANCE',balance=100))],
'compound_requests':[
 ('Hello. Who are you?',dict(contains='ARIN')),
 ('Do not buy one rope. What do you have for sale?',dict(tool='LIST_WARES',balance=100)),
 ('Buy one rope. Then sell one health potion.',dict(tool='BUY',balance=97)),
 ('What am I carrying?',dict(tool='LIST_INVENTORY'))],
}


def freeze(directory):
    root=Path(directory);root.mkdir(parents=True,exist_ok=True)
    rows=[dict(sessionId=session,turnIndex=i,input=text,expected=expected) for session,turns in SESSIONS.items() for i,(text,expected) in enumerate(turns,1)]
    target=root/'scenarios.jsonl'
    if target.exists():raise ValueError('Refusing to overwrite scenarios')
    write_jsonl(target,rows)
    (root/'scenario-fingerprint.json').write_text(json.dumps(dict(sha256=digest(target.read_bytes()),sessions=len(SESSIONS),turns=len(rows),
        scope='Development in-game simulations, including user transcript regressions and capabilities outside this narrow curriculum; not an independent human release gate.'),indent=2))


ARGUMENTS = {
    ('user_transcript',5):dict(PLACE='INN'),
    ('first_visit',6):dict(PLACE='INN'),
    ('shopping_trip',4):dict(ITEM='ROPE'),
    ('shopping_trip',5):dict(ITEM='ROPE',QUANTITY='1'),
    ('sell_and_buy',2):dict(ITEM='ROPE',QUANTITY='1'),
    ('sell_and_buy',3):dict(ITEM='HEALTH POTION'),
    ('sell_and_buy',4):dict(ITEM='HEALTH POTION',QUANTITY='1'),
    ('topic_changes',5):dict(PLACE='CASTLE'),
    ('compound_requests',3):dict(ITEM='ROPE',QUANTITY='1'),
}


def assess(directory,diagnostics,label):
    root=Path(directory);scenarios=list(read_jsonl(root/'scenarios.jsonl'))
    actual=list(read_jsonl(diagnostics));lookup={(r['sessionId'],r['turnIndex']):r for r in actual}
    assert set(lookup)=={(r['sessionId'],r['turnIndex']) for r in scenarios}
    outcomes=[];lines=['# In-game conversation replay: '+label,'','Actual runtime replies; assertions cover explicit behavior, not conversational naturalness.','']
    for case in scenarios:
        key=(case['sessionId'],case['turnIndex']);row=lookup[key];expected=case['expected'];result=row['result']
        request=row['request']
        assert request['utterances'][-1]['speaker']=='PLAYER', 'Trace retained a mutable history reference'
        assert request['utterances'][-1]['text']==case['input'], 'Trace input does not match its scenario'
        invocation=result['diagnostics'].get('toolInvocation')
        actual_tool=invocation['toolName'] if invocation else None
        mutated=row['balanceBefore']!=row['balanceAfter'] or row['inventoryBefore']!=row['inventoryAfter']
        checks={}
        if expected.get('tool'):
            if expected['tool'] in ('BUY','SELL','LOOKUP_PRICE','LOOKUP_LOCATION'):assert key in ARGUMENTS
            checks['requestedTool']=actual_tool==expected['tool'] and invocation['arguments']==ARGUMENTS.get(key,{})
        else:checks['noUnrequestedTool']=actual_tool is None
        checks['noUnintendedMutation']=not mutated or expected.get('tool') in ('BUY','SELL') and checks.get('requestedTool',False)
        if 'fact' not in expected:
            def homes(facts):
                return sorted((f['subject'],f['value'],f['negated'],f['provenance']) for f in facts if f['kind']=='HOME')
            checks['homeFactsPreserved']=homes(request['state']['sessionFacts'])==homes(result['state']['sessionFacts'])
        if 'balance' in expected:checks['balance']=row['balanceAfter']==expected['balance']
        if 'contains' in expected:checks['answer']=expected['contains'] in result['text']
        for keyname in ('fact','otherFact'):
            if keyname not in expected:continue
            owner,value=expected[keyname]
            facts=[f for f in result['state']['sessionFacts'] if f['subject']==owner and f['kind']=='HOME' and not f['negated']]
            checks[keyname]=len(facts)==1 and facts[0]['value']==value
        outcome=dict(session=case['sessionId'],turn=case['turnIndex'],input=case['input'],reply=result['text'],checks=checks,
                     passed=all(checks.values()),source=result['diagnostics']['responseSource'],tool=actual_tool,mutated=mutated)
        outcomes.append(outcome)
        if case['turnIndex']==1:lines+=['## '+case['sessionId'],'']
        lines+=['**Player:** '+case['input'],'','**NPC:** '+result['text'],'',
                '**Checks:** '+(', '.join(k+'='+str(v) for k,v in checks.items())),'']
    names=sorted({k for r in outcomes for k in r['checks']})
    metrics={name:dict(correct=sum(r['checks'].get(name) is True for r in outcomes),total=sum(name in r['checks'] for r in outcomes)) for name in names}
    report=dict(label=label,assertionVersion=2,metrics=metrics,passedTurns=sum(r['passed'] for r in outcomes),turns=len(outcomes),cases=outcomes,
                assertionChanges='Version 2 also checks exact tool arguments and home-memory preservation on unrelated turns. Scenarios and training inputs are unchanged.',
                note='Passing behavior assertions does not establish naturalness, open-domain understanding, or release readiness.')
    (root/(label+'-assessment.json')).write_text(json.dumps(report,indent=2))
    (root/(label+'-conversations.md')).write_text('\n'.join(lines),encoding='utf-8')
    print(json.dumps({k:v for k,v in report.items() if k!='cases'},indent=2))


def main():
    parser=argparse.ArgumentParser();parser.add_argument('directory');parser.add_argument('--diagnostics');parser.add_argument('--label',default='candidate');args=parser.parse_args()
    if args.diagnostics:assess(args.directory,args.diagnostics,args.label)
    else:freeze(args.directory)


if __name__=='__main__':main()
