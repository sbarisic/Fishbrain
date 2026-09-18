"""Capped learning-capacity experiment using the existing model/loss and native packer.

Not the release curriculum or a resume of the v4 pilot. All tools stay disabled:
the tiny probes cannot calibrate execution. No checkpoint is promoted.
"""
import argparse
import hashlib
import json
import math
import subprocess
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'torch_training'))
import torch
from checkpoints import atomic_json, fingerprint, save
from data import Corpus, RandomState, collate
from model import FishbrainModel, export_fbm, read_fbm
from trainer import loss_for, set_phase

SCHEDULE = dict(name='TEACHING_DIAGNOSTIC_V1', joint=2000, decoderOnly=500,
                batchSize=32, seed=42, peakLearningRate=.0003, decoderLearningRate=.0001,
                warmup=100, clipNorm=1, supervisionCadence='7 semantic / 3 authored realization',
                endpoint=2500, releaseEligible=False)


@torch.no_grad()
def measure(model, rows, manifest):
    counts = {key: [0, 0] for key in ('allCategorical', 'frameCategorical', 'planCategorical', 'knowledge', 'decoderTeacherToken')}
    for offset in range(0, len(rows), 32):
        selected = rows[offset:offset+32]
        batch = collate(selected, manifest, 'cuda', 'JointUnderstanding', RandomState(42))
        output, _, _ = model.understand(batch, teacher=False)
        for i, row in enumerate(selected):
            ok = {'allCategorical':True,'frameCategorical':True,'planCategorical':True,'knowledge':True}
            for target in row['targets']:
                name=target['name']
                matches=output[name][i,target['row']].argmax().item()==target['label']
                ok['allCategorical'] &= matches
                if name.startswith('frame.'):ok['frameCategorical'] &= matches
                if name.startswith(('plan.','planFrame.')):ok['planCategorical'] &= matches
                if name=='head.knowledgeTarget':ok['knowledge'] &= matches
            for key,value in ok.items():counts[key][0]+=int(value);counts[key][1]+=1
    responses=[r for r in rows if r['projectResponse']]
    for offset in range(0,len(responses),32):
        batch=collate(responses[offset:offset+32],manifest,'cuda','JointRealization',RandomState(42))
        _,memory,mask=model.understand(batch,teacher=False)
        predicted=model.decode(batch['decoder'],memory,mask).argmax(-1)
        valid=batch['response']!=-100
        counts['decoderTeacherToken'][0]+=((predicted==batch['response'])&valid).sum().item()
        counts['decoderTeacherToken'][1]+=valid.sum().item()
    return {k:dict(correct=a,total=b,accuracy=a/b if b else None) for k,(a,b) in counts.items()}


def native(cli, args, destination):
    result=subprocess.run(['dotnet',cli,*map(str,args)],capture_output=True,text=True,encoding='utf-8')
    if result.returncode not in (0,1):raise RuntimeError(result.stderr)
    if args[0]=='evaluate':
        try: report=json.loads(result.stdout)
        except json.JSONDecodeError:raise RuntimeError(result.stderr or result.stdout)
        atomic_json(destination,report)
    elif result.returncode:raise RuntimeError(result.stderr or result.stdout)


def main():
    parser=argparse.ArgumentParser()
    parser.add_argument('--packed',required=True)
    parser.add_argument('--lessons',required=True)
    parser.add_argument('--cli',required=True)
    parser.add_argument('--output',required=True)
    args=parser.parse_args()
    output=Path(args.output)
    if output.exists():raise ValueError('Fresh diagnostic output required; cannot resume a release run here')
    output.mkdir(parents=True)
    lessons=Path(args.lessons)
    metadata=json.loads((lessons/'diagnostic.json').read_text())
    for split,expected in metadata['splitHashes'].items():
        if hashlib.sha256((lessons/(split+'.jsonl')).read_bytes()).hexdigest()!=expected:
            raise ValueError('Diagnostic source changed after freezing')
    torch.set_num_threads(2);torch.manual_seed(42);torch.use_deterministic_algorithms(True)
    corpora={split:Corpus(args.packed,split) for split in ('train','validation','test')}
    corpus=corpora['train']
    if corpus.manifest['counts']!=metadata['counts']:raise ValueError('Packed lesson counts differ')
    source_digest=hashlib.sha256()
    for split in ('train','validation','test'):source_digest.update((lessons/(split+'.jsonl')).read_bytes())
    if source_digest.hexdigest()!=corpus.manifest['corpusHash']:
        raise ValueError('Packed data belongs to different native lessons')
    header,weights=read_fbm(Path(args.packed)/'initial.fbm')
    if header['CompletedSteps']!=0:raise ValueError('Diagnostic requires fresh weights')
    model=FishbrainModel(header,weights,corpus.manifest['headSizes']).cuda()
    optimizer=torch.optim.AdamW(model.parameters(),lr=.0003,weight_decay=.01,fused=True)
    rng=RandomState(corpus.manifest['initialRandomState'])
    binding=hashlib.sha256((fingerprint(args.packed)+json.dumps(SCHEDULE,sort_keys=True)+json.dumps(metadata,sort_keys=True)).encode()).hexdigest()
    sets={split:[data.row(i) for i in range(len(data.offsets))] for split,data in corpora.items()}
    responses=[r for r in sets['train'] if r['projectResponse']]
    thresholds={t['Schema']['Name']:1.01 for t in header['Domain']['Tools']}
    # Tiny lesson data cannot satisfy validation coverage. Even read-only tools
    # remain disabled; measure learned tool labels separately from execution.
    atomic_json(output/'experiment.json',dict(schedule=SCHEDULE,binding=binding,corpus=metadata,
        gpu=torch.cuda.get_device_name(0),thresholds=thresholds,
        exportNote='FBM NextPhase is the fixed format schedule field; actual diagnostic phase lives in progress.json and training.pt.'))
    started=time.perf_counter()
    frozen=None
    observations=[]
    for step in range(SCHEDULE['endpoint']):
        if step==SCHEDULE['joint']:
            frozen={s['Name']:model.p(s['Name']).detach().clone() for s in header['Parameters'] if not s['Name'].startswith('decoder.')}
        phase='DecoderPolish' if step>=SCHEDULE['joint'] else 'JointUnderstanding' if step%10<7 else 'JointRealization'
        pool=sets['train'] if phase=='JointUnderstanding' else responses
        rows=[pool[rng.next()%len(pool)] for _ in range(32)]
        batch=collate(rows,corpus.manifest,'cuda',phase,rng)
        active=set_phase(model,phase)
        optimizer.zero_grad(set_to_none=True)
        local=step if step<SCHEDULE['joint'] else step-SCHEDULE['joint']
        length=SCHEDULE['joint'] if step<SCHEDULE['joint'] else SCHEDULE['decoderOnly']
        peak=.0003 if step<SCHEDULE['joint'] else .0001
        lr=peak*min(1,(local+1)/100)*(.1+.9*.5*(1+math.cos(math.pi*local/length)))
        for group in optimizer.param_groups:group['lr']=lr
        loss=loss_for(model,batch,phase)
        loss.backward()
        for parameter in active:
            if parameter.grad is None:parameter.grad=torch.zeros_like(parameter)
        torch.nn.utils.clip_grad_norm_(active,1,error_if_nonfinite=True,foreach=True)
        optimizer.step()
        completed=step+1
        if completed%100==0:
            atomic_json(output/'progress.json',dict(status='RUNNING',completedSteps=completed,endpoint=SCHEDULE['endpoint'],phase=phase,
                loss=loss.item(),elapsedSeconds=time.perf_counter()-started))
            print(f'DIAGNOSTIC {completed}/2500 {phase} loss={loss.item():.5f}',flush=True)
        if completed%500==0:
            observations.append(dict(step=completed,train=measure(model,sets['train'],corpus.manifest)))
            save(output/'training.pt',model,optimizer,rng,completed,binding,'fp32',{},sampler=SCHEDULE)
            atomic_json(output/'learning.json',observations)
    assert all(torch.equal(model.p(name),value) for name,value in frozen.items()),'Decoder phase changed understanding weights'
    training_seconds=time.perf_counter()-started
    metrics={split:measure(model,rows,corpus.manifest) for split,rows in sets.items()}
    candidate=output/'diagnostic.fbm'
    export_fbm(model,candidate,SCHEDULE['endpoint'],thresholds)
    atomic_json(output/'metrics.json',dict(trainingSeconds=training_seconds,metrics=metrics,decoderFreezeVerified=True,promoted=False,
        note='Categorical-head fit uses predicted prior frame/plan labels, but gold memory inputs; native metrics and actual rollouts are separate.'))
    for split in sets:
        native(args.cli,['evaluate',lessons/(split+'.jsonl'),candidate],output/('native-'+split+'.json'))
    native(args.cli,['conversation-sample',candidate,lessons/'rollouts.jsonl',output/'actual-conversations.jsonl'],None)
    atomic_json(output/'progress.json',dict(status='DIAGNOSTIC_COMPLETE',completedSteps=2500,endpoint=2500,
        trainingSeconds=training_seconds,totalSeconds=time.perf_counter()-started,promoted=False,allToolsDisabled=True))
    print(json.dumps(json.loads((output/'metrics.json').read_text()),indent=2))


if __name__=='__main__':main()
