"""Supplemental multi-tool and memory ownership replays through the actual chat CLI."""
import argparse,json,subprocess,time
from pathlib import Path
from prepare import sha,write_json

def freeze(root):
    path=Path('data/causal-v1/supplemental.json')
    if path.exists():raise RuntimeError('Supplemental inputs are already frozen')
    def call(name,**arguments):return dict(name=name,arguments=arguments)
    def step(player,calls=None,memory=None):return dict(player=player,expectedCalls=calls,expectedMemory=memory)
    def report(subject,value,identifier='NEW'):
        quote=('My home is ' if subject=='PLAYER' else 'Arin says their home is ')+value+'.'
        return step(quote,[call('MEMORY_UPSERT',ID=identifier,SUBJECT=subject,PREDICATE='HOME',VALUE=value,POLARITY=True,SOURCE_TURN='CURRENT',QUOTE=quote)])
    cases=[]
    def add(category,steps):cases.append(dict(id='supplemental-'+str(len(cases)).zfill(2),category=category,steps=steps))
    add('compound',[step('Check my remaining gold and then tell me the price of rope.',[call('GET_BALANCE'),call('LOOKUP_PRICE',ITEM='ROPE')])])
    add('compound',[step('Tell me your name, then list what is in my inventory.',[call('READ_PERSONA',FIELD='NAME'),call('LIST_INVENTORY')])])
    add('compound',[step('Buy 2 rope please, then check my balance.',[call('BUY',ITEM='ROPE',QUANTITY=2),call('GET_BALANCE')])])
    for item,word in [('ROPE','rope'),('HEALTH POTION','a health potion')]:
        for distracted in (False,True):
            steps=[step('What is the price of '+word+'?',[call('LOOKUP_PRICE',ITEM=item)])]
            if distracted:steps.append(step('A cart just rolled past. Anyway, back to that item.'))
            steps.append(step('How much does it cost?',[call('LOOKUP_PRICE',ITEM=item)]));add('same_final_reference',steps)
    for owner in ['PLAYER','NPC']:
        steps=[report('PLAYER','Alder Ford'),report('NPC','Stonewatch'),report(owner,'Willow End','M1' if owner=='PLAYER' else 'M2')]
        state={'PLAYER':'Willow End' if owner=='PLAYER' else 'Alder Ford','NPC':'Willow End' if owner=='NPC' else 'Stonewatch'}
        steps[-1]['expectedMemory']=state
        steps.append(step('What home did I report for the other person?'))
        steps.append(step('What home did I report for myself?',[call('MEMORY_SEARCH',SUBJECT='PLAYER',PREDICATE='HOME')],state));add('ownership_correction',steps)
    for owner,identifier,value in [('PLAYER','M1','Alder Ford'),('NPC','M2','Stonewatch')]:
        other='NPC' if owner=='PLAYER' else 'PLAYER';other_value='Stonewatch' if owner=='PLAYER' else 'Alder Ford'
        quote='Forget '+identifier+'.'
        add('owned_deletion',[report('PLAYER','Alder Ford'),report('NPC','Stonewatch'),
            step(quote,[call('MEMORY_DELETE',ID=identifier,SUBJECT=owner,SOURCE_TURN='CURRENT',QUOTE=quote)],{other:other_value}),
            step('What home did I report for '+('Arin' if other=='NPC' else 'myself')+'?',[call('MEMORY_SEARCH',SUBJECT=other,PREDICATE='HOME')],{other:other_value})])
    add('negated_deletion',[report('PLAYER','Alder Ford'),step('Do not forget M1.',[],{'PLAYER':'Alder Ford'}),
        step('What home did I report for myself?',[call('MEMORY_SEARCH',SUBJECT='PLAYER',PREDICATE='HOME')],{'PLAYER':'Alder Ford'})])
    progress=json.loads((Path(root)/'pilot/progress.json').read_text())
    write_json(path,dict(note='Supplemental stress inputs frozen during language pretraining, before candidate conversation evaluation. Separate from the original 120-case suite and never added to training.',
        frozenUtc=time.strftime('%Y-%m-%dT%H:%M:%SZ',time.gmtime()),trainingProgressAtFreeze=progress,cases=cases))
    Path(str(path)+'.sha256').write_text(sha(path)+'\n');print('FROZEN SUPPLEMENTAL',len(cases))

def run(root):
    root=Path(root);path=Path('data/causal-v1/supplemental.json')
    if sha(path)!=Path(str(path)+'.sha256').read_text().strip():raise RuntimeError('Supplemental input hash changed')
    suite=json.loads(path.read_text(encoding='utf8'));out=root/'supplemental';out.mkdir(exist_ok=True);rows=[];start=time.monotonic()
    def canonical(value):return str(value).lower() if isinstance(value,bool) else str(value)
    for case in suite['cases']:
        trace=out/(case['id']+'.jsonl')
        if trace.exists():raise RuntimeError('Retain prior stress replay; use a new output directory for a rerun')
        command=['dotnet','Fishbrain/bin/Release/net10.0/Fishbrain.dll','chat',str(root/'pilot/specialization-final.fbc'),str(trace)]
        result=subprocess.run(command,input='\n'.join(s['player'] for s in case['steps'])+'\n\n',capture_output=True,text=True,encoding='utf8',check=True)
        (out/(case['id']+'.txt')).write_text(result.stdout,encoding='utf8')
        replies=[json.loads(line) for line in trace.read_text(encoding='utf8').splitlines()]
        if len(replies)!=len(case['steps']):raise RuntimeError('Chat rejected a required stress input: '+case['id'])
        sequence=0;balance=100;inventory={'HEALTH POTION':1,'ROPE':2};memory=[]
        for index,(step,reply) in enumerate(zip(case['steps'],replies)):
            expected=step['expectedCalls'];calls=reply['result']['toolOutcomes'];success=[c for c in calls if (c.get('result') or {}).get('success')]
            if expected is not None:
                expected=[dict(name=c['name'],arguments={k:canonical(sequence if v=='CURRENT' else v) for k,v in c['arguments'].items()}) for c in expected]
            exact=None if expected is None else len(calls)==len(expected) and all(c['name']==e['name'] and c['arguments']==e['arguments'] and not c.get('veto') for c,e in zip(calls,expected))
            completed=None if exact is None else exact and len(success)==len(calls) and 'DISPLAY_LIMIT' not in reply['result']['diagnostics']['events']
            world_calls=[c for c in success if c['name'] in ('BUY','SELL')];expected_world=[e for e in expected or [] if e['name'] in ('BUY','SELL')]
            changed=reply['balance']!=balance or reply['inventory']!=inventory
            unintended=changed and (len(world_calls)!=1 or len(expected_world)!=1 or world_calls[0]['name']!=expected_world[0]['name'] or world_calls[0]['arguments']!=expected_world[0]['arguments'])
            expected_memory=step['expectedMemory'];memory_ok=None
            if expected_memory is not None:
                records=reply['memory'];memory_ok=len(records)==len(expected_memory) and all(any(r['subject']==subject and r['predicate']=='HOME' and r['value']==value and r['polarity'] and r['provenance']=='PLAYER_REPORT' for r in records) for subject,value in expected_memory.items())
            authorized_memory=any(e['name'] in ('MEMORY_UPSERT','MEMORY_DELETE') for e in expected or [])
            unexpected_memory=reply['memory']!=memory and not authorized_memory
            rows.append(dict(id=case['id'],category=case['category'],turn=index,player=step['player'],expectedCalls=expected,exactCalls=exact,completed=completed,
                memoryCorrect=memory_ok,unintendedMutation=unintended,unexpectedMemoryChange=unexpected_memory,response=reply['result']['text'],toolOutcomes=calls,memory=reply['memory']))
            sequence+=1+len(reply['result']['messagesToAppend']);balance=reply['balance'];inventory=reply['inventory'];memory=reply['memory']
        print('STRESS',case['id'],flush=True)
    def rate(key):
        values=[r[key] for r in rows if r[key] is not None]
        return dict(correct=sum(values),applicable=len(values),accuracy=sum(values)/len(values) if values else None)
    report=dict(suiteSha256=sha(path),cases=len(suite['cases']),turns=len(rows),exactCalls=rate('exactCalls'),completion=rate('completed'),memory=rate('memoryCorrect'),
        unintendedMutations=sum(r['unintendedMutation'] for r in rows),unexpectedMemoryChanges=sum(r['unexpectedMemoryChange'] for r in rows),elapsedSeconds=time.monotonic()-start,
        note='Separate supplemental evidence. Does not replace the pre-training frozen challenge or constitute independent human review.')
    write_json(root/'supplemental-summary.json',report)
    (root/'supplemental-results.jsonl').write_text(''.join(json.dumps(row,ensure_ascii=False)+'\n' for row in rows),encoding='utf8')
    print(json.dumps(report,indent=2))

if __name__=='__main__':
    p=argparse.ArgumentParser();p.add_argument('command',choices=['freeze','run']);p.add_argument('root');a=p.parse_args()
    freeze(a.root) if a.command=='freeze' else run(a.root)
