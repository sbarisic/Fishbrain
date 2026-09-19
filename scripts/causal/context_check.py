"""Preflight for the changed prompt format. Discards fixture-trained weights."""
import json, shutil, subprocess, time
from pathlib import Path
import numpy as np
import torch
from context_data import ROOT,SOURCE,DLL,rows,lines,storage
from context_train import BASE,export_bound
from model import load_artifact
from repair_train import seed,update,accuracy
from prepare import sha,write_json

def main():
    if (ROOT/'preflight.json').exists():raise RuntimeError('Preflight already recorded')
    audit=json.loads((SOURCE/'isolation-audit.json').read_text(encoding='utf8'))
    if audit['status']!='PASS' or audit['corpusSha256']!=sha(ROOT/'prepared/specialization.jsonl'):
        raise RuntimeError('Audit this exact corpus before preflight')
    validation=ROOT/'validation';(validation/'prepared').mkdir(parents=True,exist_ok=True)
    shutil.copy2(ROOT/'prepared/tokenizer.json',validation/'prepared/tokenizer.json')
    lines(validation/'prepared/episodes.jsonl',[r for r in rows('data/training/causal-v1/prepared/episodes.jsonl') if r['split']=='validation'])
    subprocess.run(['dotnet',DLL,'pack-corpus',validation,validation/'packed.jsonl'],check=True)
    torch.set_num_threads(4);seed();model,header=load_artifact(BASE,'cuda')
    export_bound(model,header,sha(BASE),0,dict(purpose='Numerical preflight only; not a trained V2 candidate'))
    (ROOT/'candidate.fbc').replace(ROOT/'preflight-initialization.fbc')
    tokens=rows(ROOT/'prepared/specialization.jsonl')[0]['tokens'][:32]
    cpu,_=load_artifact(ROOT/'preflight-initialization.fbc')
    with torch.no_grad():a=cpu(torch.tensor([tokens])).numpy().reshape(-1);b=model(torch.tensor([tokens],device='cuda')).cpu().numpy().reshape(-1)
    write_json(ROOT/'parity-input.json',dict(tokens=tokens,targets=[],texts=["What's that?",'Živjo 世界']))
    subprocess.run(['dotnet',DLL,'forward',ROOT/'preflight-initialization.fbc',ROOT/'parity-input.json',ROOT/'parity-output.json'],check=True)
    reference=json.loads((ROOT/'parity-output.json').read_text(encoding='utf8'));last=a[-header['config']['vocabulary']:]
    errors=dict(full=float(np.max(np.abs(a-np.array(reference['logits'])))),gpu=float(np.max(np.abs(a-b))),
        cached=float(np.max(np.abs(last-np.array(reference['cached'])))),prefilled=float(np.max(np.abs(last-np.array(reference['prefilled'])))))
    if max(errors.values())>=.0005:raise RuntimeError('Numerical parity failed: '+str(errors))
    del cpu
    fixture=[];seen=set()
    for r in rows(ROOT/'prepared/specialization.jsonl'):
        if not r['id'].startswith('context-'):continue
        t=json.loads(r['target'])
        key=(t.get('name'),t.get('arguments',{}).get('ITEM')) if t['type']=='tool_call' else ('social',r['id'])
        if key not in seen and (t['type']=='tool_call' or len(fixture)<4):
            seen.add(key);fixture.append(r)
        if len(fixture)>=16:break
    opt=torch.optim.AdamW(model.parameters(),lr=.0003,weight_decay=.01);rng=np.random.default_rng(42);start=time.monotonic();step=0
    while step<200 and time.monotonic()-start<180:
        step+=1;update(model,opt,[fixture[int(rng.integers(len(fixture)))] for _ in range(32)])
        if step%20==0:
            measured=accuracy(model,fixture);print('FIXTURE',step,measured,flush=True)
            if measured['exact']==len(fixture):break
    measured=accuracy(model,fixture);passed=measured['exact']==len(fixture)
    write_json(ROOT/'preflight.json',dict(status='PASS' if passed else 'FAIL',parityErrors=errors,fixture=measured,
        updates=step,elapsedSeconds=time.monotonic()-start,validationSha256=sha(validation/'packed.jsonl'),
        fixtureWeightsDiscarded=True,note='Teacher-forced overfit verifies learnability only, not conversation quality.'))
    storage()
    if not passed:raise RuntimeError('Fixture did not overfit; substantive run blocked')
if __name__=='__main__':main()
