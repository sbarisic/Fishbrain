"""Fresh FP32/ROCm causal pilot. Two independently capped phases; never promotes a model."""
import argparse, contextlib, hashlib, json, math, os, random, time
from pathlib import Path
import numpy as np
import torch
from model import Model,DEFAULT,export
from prepare import sha,write_json,budget

SCHEDULE=[dict(name='language',updates=8000,seconds=7200,lr=.0003),dict(name='specialization',updates=4000,seconds=7200,lr=.0001)]
SAMPLER=dict(seed=42,batch=32,microbatch=4,languageTokens=dict(wikitext=.5,tinystories=.5),specialization=dict(public=.4,tools=.4,social=.2))

class Data:
    def __init__(self,root):
        self.root=root;self.rng=np.random.default_rng(42);self.validation=np.random.default_rng(4242)
        self.text={}
        for source in ('wikitext','tinystories'):
            for split in ('train','validation'):
                self.text[(source,split)]=np.memmap(root/'prepared'/f'{source}-{split}.bin',dtype='<u2',mode='r')
        self.dialogue={}
        for line in (root/'prepared/specialization.jsonl').open(encoding='utf8'):
            row=json.loads(line)
            if set(row)!={'id','family','split','pool','tokens','promptLength','historyTurns','target'} or not 0<row['promptLength']<len(row['tokens'])<=1024 or any(t<0 or t>=8192 for t in row['tokens']):
                raise RuntimeError('Invalid causal batch schema; no retired targets or empty supervision are accepted')
            self.dialogue.setdefault((row['pool'],row['split']),[]).append(row)
    def batch(self,phase,validation=False):
        rng=self.validation if validation else self.rng;split='validation' if validation else 'train';rows=[]
        if phase=='language':
            for i in range(32):
                source=('wikitext','tinystories')[i%2];data=self.text[(source,split)];start=int(rng.integers(0,len(data)-1025))
                seq=np.asarray(data[start:start+1025],dtype=np.int64);rows.append((seq[:-1],seq[1:]))
        else:
            for i in range(32):
                pool=rng.choice(['public','tools','social'],p=[.4,.4,.2]);data=self.dialogue[(pool,split)];row=data[int(rng.integers(len(data)))];seq=np.asarray(row['tokens'],dtype=np.int64)
                targets=seq[1:].copy();targets[:row['promptLength']-1]=-100;rows.append((seq[:-1],targets))
        length=max(len(x) for x,y in rows);x=np.zeros((32,length),dtype=np.int64);y=np.full((32,length),-100,dtype=np.int64)
        for i,(xx,yy) in enumerate(rows):x[i,:len(xx)]=xx;y[i,:len(yy)]=yy
        return torch.from_numpy(x),torch.from_numpy(y)
    def state(self):return dict(training=self.rng.bit_generator.state,validation=self.validation.bit_generator.state)
    def restore(self,state):self.rng.bit_generator.state=state['training'];self.validation.bit_generator.state=state['validation']

def fingerprint(root):
    files=[root/'sources.json',root/'tools.json',root/'prepared/tokenizer.json',root/'prepared/specialization.jsonl',root/'prepared/language-audit.json',
        root/'prepared/episodes.jsonl',root/'prepared/specialization-audit.json',root/'data-review.json',
        Path('data/causal-v1/challenge.json'),Path('data/causal-v1/preserved-acceptance.json')]
    for source in ('wikitext','tinystories'):
        for split in ('train','validation'):files.append(root/'prepared'/f'{source}-{split}.bin')
    files += [Path(__file__),Path(__file__).with_name('model.py'),Path(__file__).with_name('prepare.py'),Path('Fishbrain.CausalRuntime/PromptPacker.cs'),Path('Fishbrain.CausalRuntime/ByteBpe.cs'),Path('Fishbrain.CausalRuntime/Contracts.cs')]
    binding=dict(config=DEFAULT,schedule=SCHEDULE,sampler=SAMPLER,files={str(f):sha(f) for f in files},torch=torch.__version__,device=torch.cuda.get_device_name())
    return hashlib.sha256(json.dumps(binding,sort_keys=True).encode()).hexdigest(),binding

def train(root,resume=None):
    root=Path(root);out=root/'pilot';out.mkdir(exist_ok=True);torch.set_num_threads(4)
    if not torch.cuda.is_available() or not torch.version.hip:raise RuntimeError('ROCm GPU required')
    checks=json.loads((root/'preflight.json').read_text())
    if checks['status']!='PASS' or checks['tokenizerHash']!=sha(root/'prepared/tokenizer.json') or checks['codeHashes']['scripts\\causal\\model.py']!=sha(Path(__file__).with_name('model.py')):
        raise RuntimeError('Numerical preflight missing or stale')
    if json.loads((root/'data-review.json').read_text())['status']!='REVIEWED_FOR_BOUNDED_PILOT':raise RuntimeError('Data review required before training')
    # Runtime tests must pass immediately before training, not merely in a historical report.
    import subprocess
    tested=subprocess.run(['dotnet','run','--project','Fishbrain.CausalTests','-c','Release'],capture_output=True,text=True)
    (out/'runtime-tests.txt').write_text(tested.stdout+tested.stderr)
    if tested.returncode:raise RuntimeError('Runtime implementation gate failed')
    fp,binding=fingerprint(root);write_json(out/'binding.json',binding)
    torch.manual_seed(42);torch.cuda.manual_seed_all(42);random.seed(42);np.random.seed(42)
    torch.backends.cuda.matmul.allow_tf32=False
    model=Model().cuda();optimizer=torch.optim.AdamW(model.parameters(),lr=.0003,weight_decay=.01);data=Data(root)
    phase_index=0;step=0;total=0;elapsed=[0.,0.];checkpoints=[];losses=[]
    if resume:
        saved=torch.load(resume,map_location='cpu',weights_only=False)
        if saved.get('format')!='FISHBRAIN_CAUSAL_TRAIN_V1' or saved['fingerprint']!=fp:raise RuntimeError('Incompatible causal optimizer/checkpoint binding; old formats cannot resume')
        model.load_state_dict(saved['model']);optimizer.load_state_dict(saved['optimizer']);data.restore(saved['sampler']);torch.set_rng_state(saved['torchRng']);torch.cuda.set_rng_state_all(saved['cudaRng']);random.setstate(saved['randomRng']);np.random.set_state(saved['numpyRng'])
        phase_index=saved['phase'];step=saved['step'];total=saved['total'];elapsed=saved['elapsed'];losses=saved['losses']
        sidecar=Path(str(resume)+'.json')
        if not sidecar.exists():raise RuntimeError('Checkpoint lacks durable elapsed-budget sidecar')
        meta=json.loads(sidecar.read_text());elapsed=[max(a,b) for a,b in zip(elapsed,meta['elapsed'])]
        progress=out/'progress.json'
        if progress.exists():
            old=json.loads(progress.read_text())
            if old['fingerprint']!=fp:raise RuntimeError('Progress binding mismatch')
            if old['status']=='COMPLETE':raise RuntimeError('This bounded pilot is complete; no automatic extension or repeated phase is allowed')
            elapsed=[max(a,b) for a,b in zip(elapsed,old['elapsed'])]
    elif (out/'progress.json').exists():raise RuntimeError('Existing pilot found. Resume explicitly; fresh restart cannot reset elapsed budgets.')
    tokenizer=json.loads((root/'prepared/tokenizer.json').read_text());tools=json.loads((root/'tools.json').read_text())
    phase_started=time.monotonic();base=elapsed.copy();last_seconds=10.
    def used():
        times=base.copy()
        if phase_index<len(SCHEDULE):times[phase_index]+=time.monotonic()-phase_started
        return times
    def progress(status,loss=None):
        write_json(out/'progress.json',dict(status=status,processId=os.getpid(),fingerprint=fp,phase=SCHEDULE[phase_index]['name'] if phase_index<2 else 'complete',
            phaseUpdates=step,totalUpdates=total,elapsed=used(),loss=loss,secondsPerUpdate=last_seconds,updatedUtc=time.time()))
    def save(label):
        budget(root,230*1024**2);path=out/f'{label}.pt';temporary=path.with_suffix('.partial')
        state=dict(format='FISHBRAIN_CAUSAL_TRAIN_V1',fingerprint=fp,model=model.state_dict(),optimizer=optimizer.state_dict(),sampler=data.state(),
            torchRng=torch.get_rng_state(),cudaRng=torch.cuda.get_rng_state_all(),randomRng=random.getstate(),numpyRng=np.random.get_state(),
            phase=phase_index,step=step,total=total,elapsed=used(),losses=losses)
        torch.save(state,temporary)
        with temporary.open('ab') as durable:durable.flush();os.fsync(durable.fileno())
        temporary.replace(path)
        write_json(str(path)+'.json',dict(fingerprint=fp,elapsed=used(),sha256=sha(path)));checkpoints.append(str(path));progress('RUNNING')
        return path
    def validate():
        model.eval();values=[]
        with torch.no_grad():
            x,y=data.batch(SCHEDULE[phase_index]['name'],True)
            for i in range(0,32,4):values.append(float(model.loss(x[i:i+4].cuda(),y[i:i+4].cuda())))
        model.train();losses.append(dict(phase=SCHEDULE[phase_index]['name'],step=step,loss=sum(values)/len(values),elapsed=used()))
        write_json(out/'validation.json',losses)
    while phase_index<len(SCHEDULE):
        phase=SCHEDULE[phase_index];model.train()
        while step<phase['updates'] and used()[phase_index]+max(30.,last_seconds*2)<phase['seconds']:
            started=time.monotonic();x,y=data.batch(phase['name']);optimizer.zero_grad(set_to_none=True);count=int((y!=-100).sum());value=0.
            warmup=min(200,max(1,phase['updates']//20));fraction=(step-warmup)/max(1,phase['updates']-warmup)
            lr=phase['lr']*min(1,(step+1)/warmup) if step<warmup else phase['lr']*(.1+.9*.5*(1+math.cos(math.pi*fraction)))
            for group in optimizer.param_groups:group['lr']=lr
            for i in range(0,32,4):
                xx=x[i:i+4].cuda();yy=y[i:i+4].cuda();weight=int((yy!=-100).sum())/count
                loss=model.loss(xx,yy)*weight
                if not torch.isfinite(loss):raise RuntimeError('Nonfinite training loss')
                loss.backward();value+=float(loss.detach())
            torch.nn.utils.clip_grad_norm_(model.parameters(),1.,error_if_nonfinite=True);optimizer.step();torch.cuda.synchronize()
            step+=1;total+=1;last_seconds=time.monotonic()-started;progress('RUNNING',value)
            if step%25==0:print(phase['name'],step,'loss',round(value,4),'seconds/update',round(last_seconds,3),'elapsed',round(used()[phase_index]),flush=True)
            if step%1000==0:validate()
            if step%500==0:save(f'{phase["name"]}-{step:05}')
        # Phase boundary overhead counts toward the phase budget. Reserve was included above.
        if used()[phase_index]<phase['seconds']-15:validate()
        save(f'{phase["name"]}-final-{step:05}')
        export(out/f'{phase["name"]}-final.fbc',model,tokenizer,tools,fp,total,phase['name'],dict(schedule=SCHEDULE,sampler=SAMPLER,elapsed=used()))
        elapsed=used();base=elapsed.copy();phase_index+=1;step=0;phase_started=time.monotonic()
        if phase_index<len(SCHEDULE):
            # New optimizer gives genuinely phase-local AdamW moments and warmup.
            optimizer=torch.optim.AdamW(model.parameters(),lr=SCHEDULE[phase_index]['lr'],weight_decay=.01)
    progress('COMPLETE');write_json(out/'result.json',dict(status='COMPLETE_NOT_PROMOTED',updates=total,elapsedSeconds=base,checkpoints=checkpoints,
        fingerprint=fp,parameterCount=sum(p.numel() for p in model.parameters()),bounded=True,qualityGates='NOT_YET_EVALUATED'))
    print('BOUNDED PILOT COMPLETE',total,base,flush=True)

if __name__=='__main__':
    p=argparse.ArgumentParser();p.add_argument('root');p.add_argument('--resume');a=p.parse_args();train(a.root,a.resume)
