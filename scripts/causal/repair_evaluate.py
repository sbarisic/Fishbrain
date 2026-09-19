"""Paired native-runtime evaluation of the capped data intervention. Never trains or promotes."""
import collections,json,shutil,subprocess,time
from pathlib import Path
import numpy as np
import torch
from model import load_artifact
from prepare import sha,write_json
from evaluation import summarize
from repair_data import ROOT,SOURCE,DLL,storage

def rows(path):return [json.loads(line) for line in path.read_text(encoding='utf8').splitlines()]
def run(label,command):
    with (ROOT/(label+'.log')).open('w',encoding='utf8') as log:subprocess.run([str(s) for s in command],stdout=log,stderr=subprocess.STDOUT,check=True)
    print('DONE',label,flush=True)
def paired(a,b,focus,families):
    indexed={(r['id'],r['turn']):r for r in b};groups=collections.defaultdict(list)
    for r in a:
        if (r['id'],r['turn']) not in focus:continue
        other=indexed[(r['id'],r['turn'])]
        if r['input']!=other['input'] or r['expectedTool']!=other['expectedTool'] or r['expectedArguments']!=other['expectedArguments']:raise RuntimeError('Paired input mismatch')
        if r['expectedTool'] is not None:groups[families[r['id']]].append((r,other))
    result={};rng=np.random.default_rng(42);names=list(groups)
    for key in ['exactTool','completed']:
        values=[pair for group in groups.values() for pair in group];estimates=[]
        for _ in range(2000):
            selected=[pair for name in rng.choice(names,len(names)) for pair in groups[name]]
            estimates.append(sum(int(x[key] is True)-int(y[key] is True) for x,y in selected)/len(selected))
        result[key]=dict(applicable=len(values),families=len(groups),candidate=sum(x[key] is True for x,y in values)/len(values),
            baseline=sum(y[key] is True for x,y in values)/len(values),difference95Interval=np.quantile(estimates,[.025,.975]).tolist())
    return result

def main():
    start=time.monotonic();training=json.loads((ROOT/'result.json').read_text())
    if training['status']!='COMPLETE_NOT_PROMOTED':raise RuntimeError('Complete the bounded check first')
    if (ROOT/'evaluation.json').exists():raise RuntimeError('Preserve existing evaluation; no automatic overwrite')
    freeze=json.loads((SOURCE/'freeze.json').read_text());binding=json.loads((ROOT/'binding.json').read_text())
    if sha(SOURCE/'challenge.json')!=freeze['challengeSha256']:raise RuntimeError('Challenge was altered')
    if any(sha(path)!=digest for path,digest in binding['code'].items()):raise RuntimeError('Bound training code changed')
    if sha(ROOT/'candidate.fbc')!=training['modelSha256']:raise RuntimeError('Candidate changed')
    torch.set_num_threads(4);cpu,header=load_artifact(ROOT/'candidate.fbc');gpu,_=load_artifact(ROOT/'candidate.fbc','cuda')
    row=next(r for r in rows(ROOT/'prepared/specialization.jsonl') if r['split']=='test')
    tokens=row['tokens'][:row['promptLength']];ids=torch.tensor([tokens])
    with torch.no_grad():native=cpu(ids).numpy().reshape(-1);device=gpu(ids.cuda()).cpu().numpy().reshape(-1)
    write_json(ROOT/'parity-input.json',dict(tokens=tokens,targets=[],texts=["What's that?",'Živjo 世界']))
    run('parity',['dotnet',DLL,'forward',ROOT/'candidate.fbc',ROOT/'parity-input.json',ROOT/'parity-output.json'])
    reference=json.loads((ROOT/'parity-output.json').read_text(encoding='utf8'));final=native[-header['config']['vocabulary']:]
    errors=dict(full=float(np.max(np.abs(native-np.array(reference['logits'])))),gpu=float(np.max(np.abs(native-device))),
        cached=float(np.max(np.abs(final-np.array(reference['cached'])))),prefilled=float(np.max(np.abs(final-np.array(reference['prefilled'])))))
    if max(errors.values())>=.0005:raise RuntimeError('Export parity failed: '+str(errors))
    write_json(ROOT/'parity.json',dict(status='PASS',errors=errors,tolerance=.0005,promptTokens=len(tokens),modelSha256=training['modelSha256']))
    del cpu,gpu;torch.cuda.empty_cache()
    for label,suite in [('candidate',SOURCE/'challenge.json'),('preserved',Path('data/causal-v1/preserved-acceptance.json')),
        ('development',SOURCE/'development.json')]:
        run(label,['dotnet',DLL,'evaluate',ROOT/'candidate.fbc',suite,ROOT/(label+'.jsonl')])
    candidate=rows(ROOT/'candidate.jsonl');baseline=rows(ROOT/'baseline.jsonl')
    plans=rows(SOURCE/'plans.jsonl');families={r['id']:r['family'] for r in plans}
    suite=json.loads((SOURCE/'challenge.json').read_text());focus={(c['id'],1 if c['category'] in ('sell-style','repair-social') else 0) for c in suite['cases']}
    focus_rows=[r for r in candidate if (r['id'],r['turn']) in focus]
    before=rows(Path('data/causal-v1/results/pilot-preserved.jsonl'));after=rows(ROOT/'preserved.jsonl')
    report=dict(status='EXPERIMENTAL_NOT_PROMOTED',allTurns=summarize(candidate),focusTurns=summarize(focus_rows),
        pairedFocus=paired(candidate,baseline,focus,families),baselineAll=summarize(baseline),
        preservedBefore=summarize(before),preservedAfter=summarize(after),development=summarize(rows(ROOT/'development.jsonl')),
        evaluationSeconds=time.monotonic()-start,combinedStorageBytes=storage(),modelSha256=training['modelSha256'],
        challengeSha256=freeze['challengeSha256'],
        notes=['Focus is the held-out formulation, excluding repeated/control follow-ups; sale focus follows a stocking purchase.',
            'Intervals resample wording families, so entity substitutions do not count as independent evidence.',
            'Baseline replay overlapped GPU training. Timing samples are diagnostic, not a controlled latency comparison.',
            'Old frozen acceptance remains a separate regression check. User transcript inputs are development evidence.',
            'No promotion or full pilot. Agent reply inspection does not replace two human release reviews.'])
    write_json(ROOT/'evaluation.json',report)
    out=SOURCE/'results';out.mkdir(exist_ok=True)
    for name in ['evaluation.json','result.json','overfit.json','parity.json','binding.json','candidate.jsonl','baseline.jsonl','preserved.jsonl','development.jsonl']:
        shutil.copy2(ROOT/name,out/name)
    indexed={(r['id'],r['turn']):r for r in baseline};lines=['# Paired focused-check conversations','',
        'All 38 frozen conversations. Repeated entity variants share a wording family. No selection by outcome.','']
    for c in suite['cases']:
        lines+=['## '+c['id']+' — '+c['category'],'']
        for r in [r for r in candidate if r['id']==c['id']]:
            old=indexed[(r['id'],r['turn'])]
            lines+=['Player: '+r['input'],'','Before: '+(old.get('result') or {}).get('text','ERROR'),'',
                'After: '+(r.get('result') or {}).get('text','ERROR'),'',
                'Expected tool: '+str(r['expectedTool'])+' '+json.dumps(r['expectedArguments']),
                'Actual calls: '+json.dumps([(x['name'],x['arguments'],x['veto']) for x in (r.get('result') or {}).get('toolOutcomes',[])]),'']
    (out/'conversations.md').write_text('\n'.join(lines),encoding='utf8')
    print(json.dumps({k:report[k] for k in ['status','pairedFocus','evaluationSeconds','combinedStorageBytes']},indent=2),flush=True)

if __name__=='__main__':main()
