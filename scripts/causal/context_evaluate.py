"""Paired actual C# replies, including the full frozen suite and user development transcript."""
import argparse,json,shutil,subprocess,time
from pathlib import Path
from context_data import ROOT,SOURCE,DLL,rows,storage
from prepare import write_json,sha
from evaluation import summarize
from repair_evaluate import paired

OLD_DLL=Path('data/training/causal-repair-v1/candidate-package/Fishbrain.dll')
OLD_MODEL=Path('data/training/causal-repair-v1/candidate.fbc')
def development():
    inputs=[
        ('hi',None,{}),('who are you?','READ_PERSONA',dict(FIELD='NAME')),("you're hiange?",'READ_PERSONA',dict(FIELD='NAME')),
        ('what?',None,{}),('what do you have for sale?','LIST_WARES',{}),
        ('how much money for iron sword?','LOOKUP_PRICE',dict(ITEM='IRON SWORD')),('how much do i have?','GET_BALANCE',{}),
        ('how much gold do i have?','GET_BALANCE',{}),('sell me 10 rope','BUY',dict(ITEM='ROPE',QUANTITY=10)),
        ('I would like to buy 10 rope','BUY',dict(ITEM='ROPE',QUANTITY=10)),('Where are we?','GET_CURRENT_LOCATION',{}),
        ('My name is Steve','MEMORY_UPSERT',dict(ID='NEW',SUBJECT='PLAYER',PREDICATE='NAME',VALUE='Steve',POLARITY=True,SOURCE_TURN='CURRENT',QUOTE='My name is Steve'))]
    steps=[dict(player=q,tool=t,arguments=a,noMutation=t not in ('BUY','SELL'),developmentOverlap=True) for q,t,a in inputs]
    return dict(cases=[dict(id='user-development',category='development',steps=steps)])

def run(dll,model,suite,output):
    with output.with_suffix('.log').open('w',encoding='utf8') as log:
        subprocess.run(['dotnet',dll,'evaluate',model,suite,output],stdout=log,stderr=subprocess.STDOUT,check=True)
def main():
    parser=argparse.ArgumentParser();parser.add_argument('--baseline',action='store_true');parser.add_argument('--report-only',action='store_true');args=parser.parse_args()
    dev=SOURCE/'development.json'
    if not dev.exists():write_json(dev,development())
    suites=dict(focused=SOURCE/'challenge.json',frozen=Path('data/causal-v1/challenge.json'),
        preserved=Path('data/causal-v1/preserved-acceptance.json'),development=dev)
    label='baseline' if args.baseline else 'candidate';dll=OLD_DLL if args.baseline else DLL;model=OLD_MODEL if args.baseline else ROOT/'candidate.fbc'
    start=time.monotonic()
    for name,suite in suites.items():
        if args.report_only:continue
        output=ROOT/f'{label}-{name}.jsonl'
        if output.exists():raise RuntimeError('Preserve previous evaluation: '+str(output))
        run(dll,model,suite,output);print('DONE',label,name,flush=True)
    if not args.report_only:
        write_json(ROOT/f'{label}-timing.json',dict(seconds=time.monotonic()-start,modelSha256=sha(model),
            suiteHashes={k:sha(v) for k,v in suites.items()},note='Diagnostic timings; baseline may overlap GPU training.'))
    if args.baseline:return
    report=dict(status='EXPERIMENTAL_NOT_PROMOTED',suites={},training=json.loads((ROOT/'result.json').read_text()))
    out=SOURCE/'results';out.mkdir(exist_ok=True)
    transcript=['# Paired conversations','','All focused and development conversations, followed by the first 22 frozen conversations. Selection by order, not outcome.','']
    for name in suites:
        a=rows(ROOT/f'candidate-{name}.jsonl');b=rows(ROOT/f'baseline-{name}.jsonl')
        if [(r['id'],r['turn'],r['input']) for r in a]!=[(r['id'],r['turn'],r['input']) for r in b]:raise RuntimeError('Paired inputs differ')
        report['suites'][name]=dict(candidate=summarize(a),baseline=summarize(b))
        families={r['id']:r['category'] for r in a}
        # CURRENT resolves to each rollout's own sequence because generated histories diverge.
        suite_cases=json.loads(suites[name].read_text(encoding='utf8'))['cases']
        dynamic={(c['id'],i) for c in suite_cases for i,s in enumerate(c['steps'])
                 if s.get('arguments',{}).get('SOURCE_TURN')=='CURRENT'}
        def paired_rows(source):
            return [dict(r,expectedArguments=dict(r['expectedArguments'],SOURCE_TURN='CURRENT'))
                    if (r['id'],r['turn']) in dynamic else r for r in source]
        report['suites'][name]['pairedByCategory']=paired(paired_rows(a),paired_rows(b),{(r['id'],r['turn']) for r in a},families)
        for label2 in ['candidate','baseline']:shutil.copy2(ROOT/f'{label2}-{name}.jsonl',out/f'{label2}-{name}.jsonl')
        include=list(dict.fromkeys(r['id'] for r in a))[:22] if name=='frozen' else list(dict.fromkeys(r['id'] for r in a)) if name in ('focused','development') else []
        for x,y in zip(a,b):
            if x['id'] not in include:continue
            transcript += [f"## {x['id']} / turn {x['turn']}",'',f"Player: {x['input']}",'',
                'Before: '+(y.get('result') or {}).get('text','ERROR'),'','After: '+(x.get('result') or {}).get('text','ERROR'),'',
                'Expected: '+str(x['expectedTool'])+' '+json.dumps(x['expectedArguments']),'',
                'Calls: '+json.dumps([(t['name'],t['arguments'],t['veto']) for t in (x.get('result') or {}).get('toolOutcomes',[])]),'']
    report['storageBytes']=storage();write_json(ROOT/'evaluation.json',report);write_json(out/'evaluation.json',report)
    (out/'conversations.md').write_text('\n'.join(transcript),encoding='utf8')
    for file in ['result.json','binding.json','preflight.json','candidate-timing.json','baseline-timing.json']:
        shutil.copy2(ROOT/file,out/file)
    print(json.dumps(report,indent=2))
if __name__=='__main__':main()
