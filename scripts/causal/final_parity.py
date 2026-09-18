"""Check the complete trained export against its checkpoint and native CPU decoding."""
import argparse,json,subprocess,time
from pathlib import Path
import numpy as np
import torch
from model import load_artifact
from tokenizer import Bpe
from prepare import sha,write_json

def main():
    p=argparse.ArgumentParser();p.add_argument('root');a=p.parse_args();root=Path(a.root);start=time.monotonic()
    progress=json.loads((root/'pilot/progress.json').read_text())
    if progress['status']!='COMPLETE':raise RuntimeError('Final parity runs after the bounded training process completes')
    torch.set_num_threads(4);path=root/'pilot/specialization-final.fbc';cpu,header=load_artifact(path)
    checkpoint=max((root/'pilot').glob('specialization-final-*.pt'),key=lambda p:p.stat().st_mtime)
    saved=torch.load(checkpoint,map_location='cpu',weights_only=False)
    exact=all(torch.equal(value,saved['model'][key]) for key,value in cpu.state_dict().items())
    if not exact:raise RuntimeError('Exported weights differ from the complete checkpoint')
    tok=Bpe(header['tokenizer']);texts=["What's your job?",'Café, 世界 🌧️','<reserved_4> buy rope']
    gpu=load_artifact(path,'cuda')[0]
    with (root/'prepared/specialization.jsonl').open(encoding='utf8') as rows:
        long_row=next(row for line in rows if (row:=json.loads(line))['split']=='validation' and row['promptLength']>=600)
    cases={'short':[1,4]+tok.encode(texts[0])+[8,5], 'long':long_row['tokens'][:long_row['promptLength']]}
    errors={};lengths={}
    for label,tokens in cases.items():
        ids=torch.tensor([tokens]);lengths[label]=len(tokens)
        with torch.no_grad():native=cpu(ids).numpy().reshape(-1);device=gpu(ids.cuda()).cpu().numpy().reshape(-1)
        write_json(root/'final-parity-input.json',dict(tokens=tokens,targets=[],texts=texts))
        subprocess.run(['dotnet','Fishbrain/bin/Release/net10.0/Fishbrain.dll','forward',str(path),str(root/'final-parity-input.json'),str(root/'final-parity-output.json')],check=True)
        reference=json.loads((root/'final-parity-output.json').read_text(encoding='utf8'))
        final=native[-header['config']['vocabulary']:]
        errors.update({label+'.'+key:value for key,value in dict(csharpForward=float(np.max(np.abs(native-np.array(reference['logits'])))),
            cachedDecoding=float(np.max(np.abs(final-np.array(reference['cached'])))),
            blockPrefill=float(np.max(np.abs(final-np.array(reference['prefilled'])))),
            gpuForward=float(np.max(np.abs(native-device)))).items()})
        assert max(errors.values())<.0005,errors
        assert reference['roundTrips']==texts and reference['encodings']==[tok.encode(t) for t in texts]
    write_json(root/'final-parity.json',dict(status='PASS',maxAbsoluteErrors=errors,threshold=.0005,
        promptLengths=lengths,
        exportedWeightsBitwiseEqual=True,modelSha256=sha(path),checkpointSha256=sha(checkpoint),
        elapsedSeconds=time.monotonic()-start,parameterCount=sum(p.numel() for p in cpu.parameters())))
    print('FULL TRAINED EXPORT PARITY PASS',errors)
if __name__=='__main__':main()
