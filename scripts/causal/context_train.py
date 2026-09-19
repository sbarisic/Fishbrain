"""One bounded repair run; the historical model is initialization, never overwritten."""
import argparse, hashlib, json, math, random, struct, time
from pathlib import Path
import numpy as np
import torch
from model import load_artifact, export, MAGIC
from repair_train import seed, update, accuracy
from context_data import ROOT,SOURCE,FORMAT,rows,storage
from prepare import sha,write_json

BASE=Path('data/training/causal-repair-v1/candidate.fbc')
LIMITS=dict(updates=1200,seconds=900,batch=32,microbatch=4,seed=42)
SAMPLER=dict(calls=.50,memory=.15,social=.20,continuation=.05,public=.10)

def export_bound(model,header,fingerprint,step,binding):
    path=ROOT/'candidate.fbc'
    export(path,model,header['tokenizer'],header['tools'],fingerprint,step,'context-repair',
           dict(binding=binding))
    # Same numerical layout, a mandatory new input-format binding; old runtime packages remain intact.
    raw=path.read_bytes();size=struct.unpack_from('<i',raw,len(MAGIC))[0];offset=len(MAGIC)+4
    h=json.loads(raw[offset:offset+size]);h['promptFormat']=FORMAT
    encoded=json.dumps(h,separators=(',',':')).encode()
    content=MAGIC+struct.pack('<i',len(encoded))+encoded+raw[offset+size:-32]
    temp=path.with_suffix('.partial');temp.write_bytes(content+hashlib.sha256(content).digest());temp.replace(path)

def main():
    parser=argparse.ArgumentParser();parser.add_argument('--resume',action='store_true');args=parser.parse_args()
    freeze=json.loads((SOURCE/'freeze.json').read_text())
    preflight=json.loads((ROOT/'preflight.json').read_text())
    if preflight['status']!='PASS':raise RuntimeError('Preflight must pass')
    if sha(ROOT/'validation/packed.jsonl')!=preflight['validationSha256']:raise RuntimeError('Validation changed')
    if sha(ROOT/'prepared/specialization.jsonl')!=freeze['corpusSha256'] or sha(SOURCE/'challenge.json')!=freeze['challengeSha256']:raise RuntimeError('Frozen input changed')
    binding=dict(baseSha256=sha(BASE),freeze=freeze,limits=LIMITS,sampler=SAMPLER,preflight=preflight,
        code={str(p):sha(p) for p in [Path(__file__),Path('scripts/causal/context_data.py'),Path('scripts/causal/model.py'),
            Path('scripts/causal/repair_train.py'),Path('Fishbrain.CausalRuntime/PromptPacker.cs'),Path('Fishbrain.CausalCli/Program.cs'),
            Path('Fishbrain.CausalRuntime/Brain.cs'),Path('Fishbrain.CausalDemo/Authorization.cs')]})
    fingerprint=hashlib.sha256(json.dumps(binding,sort_keys=True).encode()).hexdigest()
    if (ROOT/'result.json').exists():raise RuntimeError('Bounded run complete. No automatic extension.')
    if (ROOT/'progress.json').exists() and not args.resume:raise RuntimeError('Explicit --resume required')
    write_json(ROOT/'binding.json',binding);storage()
    torch.set_num_threads(4)
    if not torch.cuda.is_available() or not torch.version.hip:raise RuntimeError('ROCm required')
    seed();model,header=load_artifact(BASE,'cuda');opt=torch.optim.AdamW(model.parameters(),lr=.0001,weight_decay=.01);rng=np.random.default_rng(42)
    packed=rows(ROOT/'prepared/specialization.jsonl');pools={k:[] for k in SAMPLER}
    validation=rows(ROOT/'validation/packed.jsonl')[:96]
    for r in packed:
        if r['split']!='train':continue
        t=json.loads(r['target'])
        key='public' if r['pool']=='public' else 'memory' if 'MEMORY_' in r['target'] else 'calls' if t['type']=='tool_call' else 'social' if r['pool'] in ('social','safety','repair') else 'continuation'
        pools[key].append(r)
    if any(not p for p in pools.values()):raise RuntimeError('Empty sampling pool')
    elapsed=0.;step=0;history=[];draws={k:0 for k in pools}
    if args.resume:
        checkpoint=sorted(ROOT.glob('step-*.pt'))[-1];state=torch.load(checkpoint,weights_only=False,map_location='cpu')
        if state['fingerprint']!=fingerprint:raise RuntimeError('Checkpoint fingerprint differs')
        model.load_state_dict(state['model']);opt.load_state_dict(state['optimizer']);rng.bit_generator.state=state['sampler']
        torch.set_rng_state(state['torchRng']);torch.cuda.set_rng_state_all(state['cudaRng']);random.setstate(state['randomRng']);np.random.set_state(state['numpyRng'])
        step=state['step'];elapsed=state['elapsedSeconds'];history=state['validation'];draws=state['draws']
    start=time.monotonic()
    def used():return elapsed+time.monotonic()-start
    def save():
        path=ROOT/f'step-{step:04}.pt';temp=path.with_suffix('.partial')
        torch.save(dict(fingerprint=fingerprint,binding=binding,model=model.state_dict(),optimizer=opt.state_dict(),sampler=rng.bit_generator.state,
            torchRng=torch.get_rng_state(),cudaRng=torch.cuda.get_rng_state_all(),randomRng=random.getstate(),numpyRng=np.random.get_state(),
            step=step,elapsedSeconds=used(),validation=history,draws=draws),temp);temp.replace(path);storage()
    while step<LIMITS['updates'] and used()<LIMITS['seconds']-20:
        step+=1
        selected=[]
        for _ in range(32):
            key=rng.choice(list(SAMPLER),p=list(SAMPLER.values()));draws[key]+=1
            selected.append(pools[key][int(rng.integers(len(pools[key])))])
        lr=.0001*min(1.,step/40)*(.15+.85*.5*(1+math.cos(math.pi*step/LIMITS['updates'])))
        for g in opt.param_groups:g['lr']=lr
        loss=update(model,opt,selected)
        if step%25==0:
            write_json(ROOT/'progress.json',dict(status='RUNNING',step=step,loss=loss,elapsedSeconds=used(),limits=LIMITS))
            print('UPDATE',step,'LOSS',loss,'SECONDS',round(used(),1),flush=True)
        if step%300==0 and validation:
            measured=accuracy(model,validation);history.append(dict(step=step,**measured));print('VALIDATION',measured,flush=True)
        if step%600==0:save()
    save();export_bound(model,header,fingerprint,step,binding);storage()
    result=dict(status='COMPLETE_NOT_PROMOTED',updates=step,elapsedSeconds=used(),limits=LIMITS,validation=history,
        sampler=SAMPLER,poolRows={k:len(v) for k,v in pools.items()},draws=draws,modelSha256=sha(ROOT/'candidate.fbc'),fingerprint=fingerprint)
    write_json(ROOT/'result.json',result);write_json(ROOT/'progress.json',result);print(json.dumps(result,indent=2),flush=True)
if __name__=='__main__':main()
