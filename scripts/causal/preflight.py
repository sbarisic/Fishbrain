"""Mandatory numerical and small-fixture learning gate; never reuses these weights for the pilot."""
import argparse, hashlib, json, subprocess, time
from pathlib import Path
import numpy as np
import torch
from model import Model, export, layout
from tokenizer import Bpe
from prepare import sha, write_json

def main():
    p=argparse.ArgumentParser();p.add_argument('root');a=p.parse_args();root=Path(a.root);start=time.monotonic()
    torch.set_num_threads(4);torch.manual_seed(42);np.random.seed(42)
    if not torch.cuda.is_available() or not torch.version.hip:raise RuntimeError('ROCm GPU required for this pilot')
    definition=json.loads((root/'prepared/tokenizer.json').read_text());tok=Bpe(definition);tools=json.loads((root/'tools.json').read_text())
    config=dict(layers=2,width=24,heads=6,feedForward=96,context=128,vocabulary=len(definition['pieces']),seed=42)
    cpu=Model(config);gpu=Model(config).cuda();gpu.load_state_dict(cpu.state_dict())
    texts=["What's your job?",'Živjo 🌧️ — 世界!','a\n"quoted"\\b','<reserved_4> is ordinary text','Café and cafe\u0301']
    tokens=tok.encode("What's your job?")[:7];targets=tokens[1:]+[2]
    ids=torch.tensor([tokens]);target=torch.tensor([targets]);loss=cpu.loss(ids,target);loss.backward()
    gl=gpu.loss(ids.cuda(),target.cuda());gl.backward()
    export(root/'reference.fbc',cpu,definition,tools,'0'*64,0,'numerical',{})
    write_json(root/'reference-input.json',dict(tokens=tokens,targets=targets,texts=texts))
    subprocess.run(['dotnet','Fishbrain/bin/Release/net10.0/Fishbrain.dll','reference',str(root/'reference.fbc'),str(root/'reference-input.json'),str(root/'reference-output.json')],check=True)
    reference=json.loads((root/'reference-output.json').read_text(encoding='utf8'))
    logits=cpu(ids).detach().numpy().reshape(-1)
    forward=float(np.max(np.abs(logits-np.array(reference['logits']))))
    cached=float(np.max(np.abs(logits[-config['vocabulary']:]-np.array(reference['cached']))))
    gradient=max(float(np.max(np.abs(cpu.p(n).grad.numpy().reshape(-1)-np.array(reference['gradients'][n])))) for n,_,_ in layout(config))
    gpu_forward=float((cpu(ids)-gpu(ids.cuda()).cpu()).abs().max())
    gpu_gradient=max(float((cpu.p(n).grad-gpu.p(n).grad.cpu()).abs().max()) for n,_,_ in layout(config))
    assert reference['encodings']==[tok.encode(t) for t in texts]
    assert reference['roundTrips']==texts
    assert all(i>=16 for t in texts for i in tok.encode(t))
    finite=[]
    for name,_,_ in list(layout(config))[::3]:
        param=cpu.p(name);ix=param.numel()//2;value=param.detach().view(-1)[ix].item();epsilon=.001
        with torch.no_grad():
            param.view(-1)[ix]=value+epsilon;plus=cpu.loss(ids,target).item()
            param.view(-1)[ix]=value-epsilon;minus=cpu.loss(ids,target).item();param.view(-1)[ix]=value
        finite.append(abs((plus-minus)/(2*epsilon)-param.grad.view(-1)[ix].item()))
    assert max(forward,cached,gradient,gpu_forward,gpu_gradient)<.0002,(forward,cached,gradient,gpu_forward,gpu_gradient)
    assert max(finite)<.002,finite
    # Identical short contexts and both output kinds; masked prompt loss and next-token shift.
    fixtures=[('hello',{'type':'text','text':'Hello!'}),('gold please',{'type':'tool_call','name':'GET_BALANCE','arguments':{}}),
        ('what is for sale',{'type':'tool_call','name':'LIST_WARES','arguments':{}}),('thanks',{'type':'text','text':"You're welcome."})]
    sequences=[]
    for prompt,response in fixtures:
        prefix=[1,4]+tok.encode(prompt)+[8,5];answer=tok.encode(json.dumps(response,separators=(',',':')))+[2]
        sequences.append((prefix+answer,[-100]*len(prefix)+answer))
    length=max(len(x) for x,y in sequences);x=torch.zeros(4,length-1,dtype=torch.long,device='cuda');y=torch.full_like(x,-100)
    for i,(seq,labels) in enumerate(sequences):x[i,:len(seq)-1]=torch.tensor(seq[:-1],device='cuda');y[i,:len(seq)-1]=torch.tensor(labels[1:],device='cuda')
    torch.manual_seed(42);small=Model(dict(config,width=48,heads=6,feedForward=192)).cuda();opt=torch.optim.AdamW(small.parameters(),lr=.003)
    initial=float(small.loss(x,y));accuracy=0
    for step in range(600):
        opt.zero_grad(set_to_none=True);l=small.loss(x,y);l.backward();torch.nn.utils.clip_grad_norm_(small.parameters(),1);opt.step()
        if step%20==19:
            with torch.no_grad():accuracy=float((small(x).argmax(-1)[y!=-100]==y[y!=-100]).float().mean())
            if accuracy==1 and float(l)<.05:break
    assert accuracy==1 and float(l)<.05,(accuracy,float(l))
    report=dict(status='PASS',forwardMaxError=forward,cachedMaxError=cached,gradientMaxError=gradient,gpuForwardMaxError=gpu_forward,
        gpuGradientMaxError=gpu_gradient,finiteDifferenceMaxError=max(finite),fixtureInitialLoss=initial,fixtureFinalLoss=float(l),fixtureAccuracy=accuracy,
        fixtureUpdates=step+1,elapsedSeconds=time.monotonic()-start,tokenizerHash=sha(root/'prepared/tokenizer.json'),toolsHash=sha(root/'tools.json'),
        codeHashes={str(f):sha(f) for f in sorted(Path('scripts/causal').glob('*.py'))},device=torch.cuda.get_device_name(),torch=torch.__version__)
    write_json(root/'preflight.json',report);print(json.dumps(report,indent=2),flush=True)
if __name__=='__main__':main()
