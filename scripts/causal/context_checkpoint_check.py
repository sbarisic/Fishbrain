"""Verify final export and complete checkpoint state without resuming training."""
import json
import numpy as np
import torch
from context_data import ROOT,SOURCE,storage
from model import load_artifact
from prepare import sha,write_json

def main():
    result=json.loads((ROOT/'result.json').read_text());step=result['updates']
    checkpoint=ROOT/f'step-{step:04}.pt';state=torch.load(checkpoint,map_location='cpu',weights_only=False)
    model,header=load_artifact(ROOT/'candidate.fbc')
    if state['fingerprint']!=result['fingerprint'] or header['trainingFingerprint']!=result['fingerprint']:raise RuntimeError('Checkpoint binding differs')
    if not all(torch.equal(value,state['model'][key]) for key,value in model.state_dict().items()):raise RuntimeError('Exported weights differ')
    required=['optimizer','sampler','torchRng','cudaRng','randomRng','numpyRng','elapsedSeconds','validation','draws','binding']
    if any(k not in state for k in required):raise RuntimeError('Incomplete checkpoint')
    if any(int(v['step'])!=step for v in state['optimizer']['state'].values()):raise RuntimeError('Optimizer step differs')
    rng=np.random.default_rng();rng.bit_generator.state=state['sampler'];first=rng.integers(0,100000,10)
    rng.bit_generator.state=state['sampler']
    if not np.array_equal(first,rng.integers(0,100000,10)):raise RuntimeError('Sampler restoration failed')
    if sum(state['draws'].values())!=step*32:raise RuntimeError('Sampler update count differs')
    report=dict(status='PASS',checkpointSha256=sha(checkpoint),modelSha256=sha(ROOT/'candidate.fbc'),updates=step,
        parameters=sum(p.numel() for p in model.parameters()),allWeightsBitwiseEqual=True,optimizerStepsMatch=True,
        rngAndSamplerStatePresent=True,samplerRestoreMatches=True,trainingElapsedSeconds=result['elapsedSeconds'],
        storageBytes=storage(),note='Verified saved state and export; did not execute an interrupted training resume.')
    write_json(SOURCE/'results/checkpoint-check.json',report);print(json.dumps(report,indent=2))
if __name__=='__main__':main()
