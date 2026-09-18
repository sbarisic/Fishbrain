"""Read-only checkpoint verification. Probe updates are discarded; never resumes the pilot."""
import argparse,copy,json,time
from pathlib import Path
import numpy as np
import torch
from model import Model
from train import Data
from prepare import sha,write_json

def main():
    p=argparse.ArgumentParser();p.add_argument('root');p.add_argument('checkpoint');p.add_argument('output');p.add_argument('--device',choices=['cpu','cuda'],default='cpu');a=p.parse_args();root=Path(a.root);path=Path(a.checkpoint)
    start=time.monotonic();torch.set_num_threads(2)
    meta=json.loads(Path(str(path)+'.json').read_text());assert sha(path)==meta['sha256']
    binding=json.loads((root/'pilot/binding.json').read_text());results=[]
    for attempt in range(2):
        state=torch.load(path,map_location='cpu',weights_only=False)
        assert state['format']=='FISHBRAIN_CAUSAL_TRAIN_V1' and state['fingerprint']==meta['fingerprint']
        assert all(k in state for k in ['model','optimizer','sampler','torchRng','cudaRng','randomRng','numpyRng','phase','step','total','elapsed'])
        model=Model(binding['config']).to(a.device);model.load_state_dict(state['model']);opt=torch.optim.AdamW(model.parameters());opt.load_state_dict(state['optimizer'])
        data=Data(root);data.restore(state['sampler']);torch.set_rng_state(state['torchRng'])
        if a.device=='cuda':torch.cuda.set_rng_state_all(state['cudaRng']);torch.backends.cuda.matmul.allow_tf32=False
        x,y=data.batch('language' if state['phase']==0 else 'specialization')
        # Short CPU probe checks restoration of moments and sampler without sharing the busy GPU.
        x=x[:4,:32].to(a.device);y=y[:4,:32].to(a.device)
        if not (y!=-100).any():raise RuntimeError('Choose a language-phase checkpoint for the CPU probe')
        opt.zero_grad();loss=model.loss(x,y);loss.backward();torch.nn.utils.clip_grad_norm_(model.parameters(),1);opt.step()
        results.append(dict(loss=float(loss.detach()),weights={k:v.detach().clone() for k,v in model.state_dict().items()},sampler=json.dumps(data.state(),sort_keys=True)))
    exact=results[0]['sampler']==results[1]['sampler'] and results[0]['loss']==results[1]['loss'] and all(torch.equal(v,results[1]['weights'][k]) for k,v in results[0]['weights'].items())
    assert exact
    write_json(a.output,dict(status='PASS',checkpoint=str(path),checkpointSha256=meta['sha256'],step=state['step'],restoredUpdateBitwiseEqual=True,
        completeState=True,probeLoss=results[0]['loss'],device=a.device,elapsedSeconds=time.monotonic()-start,scope='Two discarded short updates from independently loaded complete checkpoints; pilot weights untouched.'))
    print('CHECKPOINT RESTORATION PASS')
if __name__=='__main__':main()
