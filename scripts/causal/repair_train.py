"""A capped data-intervention check on copies of the causal candidate; never a full pilot."""
import hashlib,json,math,random,time
from pathlib import Path
import numpy as np
import torch
from model import load_artifact,export
from prepare import sha,write_json
from repair_data import ROOT,SOURCE,storage

BASE=Path('data/training/causal-v1/pilot/specialization-final.fbc')
WEIGHTS={'calls':.5,'repair':.2,'continuations':.1,'public':.1,'memory':.1}
LIMITS={'overfitUpdates':200,'overfitSeconds':120,'focusedUpdates':400,'focusedSeconds':600,'batch':32,'microbatch':4,'seed':42}

def rows(path):return [json.loads(line) for line in path.read_text(encoding='utf8').splitlines()]
def seed():
    torch.manual_seed(42);torch.cuda.manual_seed_all(42);random.seed(42);np.random.seed(42)
    torch.backends.cuda.matmul.allow_tf32=False
def batch(selected):
    length=max(len(r['tokens'])-1 for r in selected)
    x=torch.zeros((len(selected),length),dtype=torch.long);y=torch.full_like(x,-100)
    for i,row in enumerate(selected):
        tokens=torch.tensor(row['tokens']);x[i,:len(tokens)-1]=tokens[:-1];y[i,row['promptLength']-1:len(tokens)-1]=tokens[row['promptLength']:]
    return x,y
def update(model,opt,selected):
    x,y=batch(selected);count=int((y!=-100).sum());opt.zero_grad(set_to_none=True);loss=0.
    for i in range(0,len(selected),4):
        xx=x[i:i+4].cuda();yy=y[i:i+4].cuda();value=model.loss(xx,yy)*int((yy!=-100).sum())/count
        if not torch.isfinite(value):raise RuntimeError('Nonfinite loss')
        value.backward();loss+=float(value.detach())
    torch.nn.utils.clip_grad_norm_(model.parameters(),1.,error_if_nonfinite=True);opt.step();torch.cuda.synchronize()
    return loss
def accuracy(model,selected):
    exact=correct=count=0;loss=0.;model.eval()
    with torch.no_grad():
        for i in range(0,len(selected),4):
            x,y=batch(selected[i:i+4]);x=x.cuda();y=y.cuda();logits=model(x);mask=y!=-100;pred=logits.argmax(-1)
            correct+=int(((pred==y)&mask).sum());count+=int(mask.sum())
            exact+=int(((pred==y)|~mask).all(-1).sum())
            loss+=float(torch.nn.functional.cross_entropy(logits.flatten(0,1),y.flatten(),ignore_index=-100,reduction='sum'))
    model.train();return dict(exact=exact,rows=len(selected),tokenAccuracy=correct/count,loss=loss/count)

def main():
    if (ROOT/'progress.json').exists():raise RuntimeError('This bounded check already exists. It cannot silently restart or extend.')
    freeze=json.loads((SOURCE/'freeze.json').read_text())
    if sha(SOURCE/'challenge.json')!=freeze['challengeSha256'] or sha(ROOT/'prepared/specialization.jsonl')!=freeze['corpusSha256']:
        raise RuntimeError('Frozen input changed')
    torch.set_num_threads(4)
    if not torch.cuda.is_available() or not torch.version.hip:raise RuntimeError('ROCm GPU required')
    source=rows(ROOT/'prepared/specialization.jsonl');prior=rows(Path('data/training/causal-v1/prepared/specialization.jsonl'))
    train=[r for r in source if r['split']=='train'];validation=[r for r in source if r['split']=='validation']
    pools={k:[] for k in WEIGHTS}
    for r in train:
        target=json.loads(r['target']);key='repair' if r['pool']=='repair' else 'calls' if target['type']=='tool_call' else 'continuations'
        pools[key].append(r)
    for r in prior:
        if r['split']!='train':continue
        if r['pool']=='public':pools['public'].append(r)
        elif 'MEMORY_' in r['target']:pools['memory'].append(r)
    if any(not p for p in pools.values()):raise RuntimeError('Empty sampling pool')
    binding=dict(baseSha256=sha(BASE),freeze=freeze,limits=LIMITS,sampler=WEIGHTS,
        priorCorpusSha256=sha(Path('data/training/causal-v1/prepared/specialization.jsonl')),
        code={str(p):sha(p) for p in [Path(__file__),Path('scripts/causal/repair_data.py'),Path('scripts/causal/model.py'),Path('Fishbrain.CausalRuntime/PromptPacker.cs')]})
    fingerprint=hashlib.sha256(json.dumps(binding,sort_keys=True).encode()).hexdigest();write_json(ROOT/'binding.json',binding)
    seed();model,header=load_artifact(BASE,'cuda');rng=np.random.default_rng(42);start=time.monotonic()
    def progress(phase,step,loss=None):write_json(ROOT/'progress.json',dict(status='RUNNING',phase=phase,step=step,loss=loss,elapsedSeconds=time.monotonic()-start,fingerprint=fingerprint))
    # Unique action/item combinations plus literal dialogue-repair examples, including their real packed histories.
    fixture=[];seen=set()
    for r in pools['calls']:
        t=json.loads(r['target']);key=(t['name'],t['arguments'].get('ITEM'))
        if key not in seen and t['name'] in ('LOOKUP_PRICE','BUY','SELL'):
            seen.add(key);fixture.append(r)
    fixture+=pools['repair'][:8]
    opt=torch.optim.AdamW(model.parameters(),lr=.0003,weight_decay=.01);progress('overfit',0);begin=time.monotonic();overfit=[]
    step=0
    for next_step in range(1,LIMITS['overfitUpdates']+1):
        if time.monotonic()-begin>LIMITS['overfitSeconds']-5:break
        step=next_step
        selected=[fixture[int(rng.integers(len(fixture)))] for _ in range(32)];loss=update(model,opt,selected);progress('overfit',step,loss)
        if step%20==0:
            measured=accuracy(model,fixture);overfit.append(dict(step=step,**measured));print('OVERFIT',step,measured,flush=True)
            if measured['exact']==len(fixture):break
    measured=accuracy(model,fixture)
    write_json(ROOT/'overfit.json',dict(status='PASS' if measured['exact']==len(fixture) else 'FAIL',final=measured,updates=step,elapsedSeconds=time.monotonic()-begin,history=overfit,
        note='Exact teacher-forced target sequences on a small real-prompt fixture. These weights are discarded; this does not establish generalization.'))
    del opt,model;torch.cuda.empty_cache()
    if measured['exact']!=len(fixture):
        write_json(ROOT/'progress.json',dict(status='STOPPED_OVERFIT_FAILED',fingerprint=fingerprint));return
    # Reset both weights and optimizer: the fixture is not extra training for the evaluated candidate.
    seed();rng=np.random.default_rng(42);model,header=load_artifact(BASE,'cuda');opt=torch.optim.AdamW(model.parameters(),lr=.0001,weight_decay=.01)
    begin=time.monotonic();history=[];pool_draws={k:0 for k in pools};names=list(WEIGHTS);prob=list(WEIGHTS.values())
    def save(step):
        path=ROOT/f'focused-{step:04}.pt';temporary=path.with_suffix('.partial')
        torch.save(dict(format='FISHBRAIN_CAUSAL_REPAIR_CHECK_V1',fingerprint=fingerprint,step=step,model=model.state_dict(),optimizer=opt.state_dict(),
            sampler=rng.bit_generator.state,torchRng=torch.get_rng_state(),cudaRng=torch.cuda.get_rng_state_all(),numpyRng=np.random.get_state(),randomRng=random.getstate(),
            elapsedSeconds=time.monotonic()-begin,poolDraws=pool_draws,validation=history),temporary)
        temporary.replace(path);write_json(str(path)+'.json',dict(sha256=sha(path),elapsedSeconds=time.monotonic()-begin,fingerprint=fingerprint));storage()
    initial=accuracy(model,validation);progress('focused',0)
    step=0
    for next_step in range(1,LIMITS['focusedUpdates']+1):
        if time.monotonic()-begin>LIMITS['focusedSeconds']-15:break
        step=next_step
        selected=[]
        for _ in range(32):
            key=rng.choice(names,p=prob);pool_draws[key]+=1;selected.append(pools[key][int(rng.integers(len(pools[key])))])
        lr=.0001*min(1,step/30) if step<30 else .0001*(.1+.9*.5*(1+math.cos(math.pi*(step-30)/370)))
        for group in opt.param_groups:group['lr']=lr
        loss=update(model,opt,selected);progress('focused',step,loss)
        if step%100==0:
            measured=accuracy(model,validation);history.append(dict(step=step,trainingLoss=loss,**measured));print('FOCUSED',step,measured,flush=True)
        if step%200==0:save(step)
    save(step)
    export(ROOT/'candidate.fbc',model,header['tokenizer'],header['tools'],fingerprint,step,'focused-data-check',dict(binding=binding,elapsedSeconds=time.monotonic()-begin))
    storage();result=dict(status='COMPLETE_NOT_PROMOTED',updates=step,elapsedSeconds=time.monotonic()-begin,initialValidation=initial,validation=history,poolDraws=pool_draws,
        modelSha256=sha(ROOT/'candidate.fbc'),fingerprint=fingerprint,limits=LIMITS,quality='NOT_YET_EVALUATED',note='Focused fine-tuning check from the preserved causal candidate, not a fresh full pilot. No shipped artifact changed.')
    write_json(ROOT/'result.json',result);write_json(ROOT/'progress.json',result);print(json.dumps(result,indent=2),flush=True)

if __name__=='__main__':main()
