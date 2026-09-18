"""Bounded, fresh-weight stateful teaching experiment. Never promotes an artifact."""
import argparse
import collections
import hashlib
import json
import math
import signal
import sys
import time
from pathlib import Path

sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'torch_training'))
import torch
from checkpoints import atomic_json, fingerprint, lease, restore, save
from data import Corpus, RandomState, collate
from model import FishbrainModel, export_fbm, read_fbm
from trainer import loss_for, set_phase
from learn_diagnostic import measure

SCHEDULE=dict(name='STATEFUL_GAME_TEACHING_V1',seed=42,batchSize=32,joint=5000,decoderOnly=1000,
              endpoint=6000,semanticCadence=7,peakLearningRate=.0003,decoderLearningRate=.0001,
              warmup=100,clipNorm=1,weightDecay=.01,sampler='BALANCED_EXPLICIT_SEMANTIC_LABEL_SIGNATURE')


def signature(row):
    names={'head.knowledgeTarget','frame.0.act','frame.0.tool','frame.0.status','frame.0.factAct','frame.0.factKind',
           'frame.1.active','frame.1.act','frame.1.tool','plan.1.act'}
    return tuple((t['name'],t['label']) for t in row['targets'] if t['name'] in names)


def groups(corpus, response=False):
    result=collections.defaultdict(list)
    for i in range(len(corpus.offsets)):
        row=corpus.row(i)
        if response and not row['projectResponse']:continue
        result[signature(row)].append(i)
    return list(result.values())


def draw(corpus,pools,rng):
    return [corpus.row((pool:=pools[rng.next()%len(pools)])[rng.next()%len(pool)]) for _ in range(32)]


def digest_parameters(model):
    digest=hashlib.sha256()
    for shape in model.header['Parameters']:
        if not shape['Name'].startswith('decoder.'):
            digest.update(model.p(shape['Name']).detach().cpu().numpy().tobytes())
    return digest.hexdigest()


def main():
    parser=argparse.ArgumentParser();parser.add_argument('packed');parser.add_argument('output')
    parser.add_argument('--until',type=int,default=6000);parser.add_argument('--resume',action='store_true')
    args=parser.parse_args()
    if not 1<=args.until<=6000:raise ValueError('This teaching experiment is capped at 6000 updates')
    output=Path(args.output)
    if output.exists() and not args.resume:raise ValueError('Use a new output directory or explicitly resume this experiment')
    output.mkdir(parents=True,exist_ok=True)
    torch.set_num_threads(2);torch.manual_seed(42);torch.use_deterministic_algorithms(True)
    with lease(output/'training.lock'):
        corpus=Corpus(args.packed);validation=Corpus(args.packed,'validation')
        header,weights=read_fbm(Path(args.packed)/'initial.fbm')
        if header['CompletedSteps']!=0 or not header['Config'].get('CurrentUtteranceFirst') or not header['Config'].get('IndependentMemorySelection'):
            raise ValueError('Expected a fresh stateful teaching model')
        implementation=hashlib.sha256()
        for path in [Path(__file__),*(Path(__file__).resolve().parents[1]/'torch_training'/name for name in ('data.py','model.py','trainer.py','checkpoints.py'))]:
            implementation.update(path.read_bytes())
        binding=hashlib.sha256((fingerprint(args.packed)+json.dumps(SCHEDULE,sort_keys=True)+implementation.hexdigest()).encode()).hexdigest()
        model=FishbrainModel(header,weights,corpus.manifest['headSizes']).cuda()
        optimizer=torch.optim.AdamW(model.parameters(),lr=.0003,weight_decay=.01,fused=True)
        rng=RandomState(corpus.manifest['initialRandomState']);step=0;best={}
        checkpoint=output/'training.pt'
        if args.resume:
            state=restore(checkpoint,model,optimizer,rng,binding,'fp32',SCHEDULE)
            step,best=state['completedSteps'],state['bestValidation'];del state
        if step>=args.until:raise ValueError('Endpoint already reached')
        semantic=groups(corpus);realization=groups(corpus,True)
        validation_groups=groups(validation);vrng=RandomState(42)
        validation_rows=draw(validation,validation_groups,vrng)+draw(validation,validation_groups,vrng)+draw(validation,validation_groups,vrng)
        atomic_json(output/'experiment.json',dict(schedule=SCHEDULE,binding=binding,implementationHash=implementation.hexdigest(),
            counts=corpus.manifest['counts'],semanticGroups=len(semantic),realizationGroups=len(realization),gpu=torch.cuda.get_device_name(0),
            config=header['Config'],promoted=False,startingWeights='FRESH_SEED_42',
            note='Targeted synthetic curriculum. Validation for development and calibration only; no release-gate substitution.'))
        stopped=False
        def stop(*_):
            nonlocal stopped
            stopped=True
        signal.signal(signal.SIGINT,stop);signal.signal(signal.SIGTERM,stop)
        start=time.perf_counter();start_step=step
        freeze=digest_parameters(model) if step>=5000 else None
        thresholds={t['Schema']['Name']:1.01 for t in header['Domain']['Tools']}
        status='RUNNING'
        try:
            while step<args.until and not stopped:
                if (output/'STOP').exists():break
                if step==5000:freeze=digest_parameters(model)
                phase='DecoderPolish' if step>=5000 else 'JointUnderstanding' if step%10<7 else 'JointRealization'
                rows=draw(corpus,semantic if phase=='JointUnderstanding' else realization,rng)
                batch=collate(rows,corpus.manifest,'cuda',phase,rng)
                active=set_phase(model,phase);optimizer.zero_grad(set_to_none=True)
                local,length,peak=(step,5000,.0003) if step<5000 else (step-5000,1000,.0001)
                lr=peak*min(1,(local+1)/100)*(.1+.9*.5*(1+math.cos(math.pi*local/length)))
                for group in optimizer.param_groups:group['lr']=lr
                loss=loss_for(model,batch,phase);loss.backward()
                for parameter in active:
                    if parameter.grad is None:parameter.grad=torch.zeros_like(parameter)
                torch.nn.utils.clip_grad_norm_(active,1,error_if_nonfinite=True,foreach=True);optimizer.step();step+=1
                if step%100==0:
                    elapsed=time.perf_counter()-start
                    atomic_json(output/'progress.json',dict(status=status,completedSteps=step,endpoint=args.until,phase=phase,
                        elapsedSeconds=elapsed,secondsPerUpdate=elapsed/(step-start_step),loss=loss.item(),promoted=False))
                    print(f'GAME TEACHING {step}/{args.until} {phase} loss={loss.item():.5f} seconds/update={elapsed/(step-start_step):.3f}',flush=True)
                if step%1000==0:
                    validation_metrics=measure(model,validation_rows,corpus.manifest)
                    atomic_json(output/f'validation-{step}.json',dict(metrics=validation_metrics,developmentOnly=True))
                    save(checkpoint,model,optimizer,rng,step,binding,'fp32',best,sampler=SCHEDULE)
                    export_fbm(model,output/f'step-{step}.fbm',step,thresholds)
            status='ENDPOINT_REACHED' if step==args.until else 'STOPPED'
            if freeze is not None and freeze!=digest_parameters(model):raise AssertionError('Decoder phase changed understanding weights')
            save(checkpoint,model,optimizer,rng,step,binding,'fp32',best,sampler=SCHEDULE)
            export_fbm(model,output/'candidate.fbm',step,thresholds)
        except BaseException:
            status='FAILED';raise
        finally:
            atomic_json(output/'progress.json',dict(status=status,completedSteps=step,endpoint=args.until,
                elapsedSeconds=time.perf_counter()-start,promoted=False,decoderFreezeVerified=freeze is not None and status=='ENDPOINT_REACHED'))


if __name__=='__main__':main()
