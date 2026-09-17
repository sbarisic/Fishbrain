"""A fresh, bounded 50,000-update experiment. Never promotes or starts full training."""
import argparse
import datetime
import hashlib
import json
import subprocess
import shutil
import sys
import time
from pathlib import Path

from common import ROOT,digest


def main():
    parser=argparse.ArgumentParser()
    parser.add_argument('--corpus',required=True);parser.add_argument('--packed',required=True)
    parser.add_argument('--output',required=True);parser.add_argument('--cli',required=True)
    parser.add_argument('--baseline',required=True);parser.add_argument('--data-review',required=True)
    parser.add_argument('--suite',default='data/conversation-v4/challenge.json')
    parser.add_argument('--scenarios',default='data/conversation-v4/review-scenarios.jsonl')
    args=parser.parse_args()
    corpus,packed,output=map(lambda p:Path(p).resolve(),(args.corpus,args.packed,args.output))
    if output.exists() and any(output.iterdir()): raise ValueError('Fresh pilot requires an empty run directory')
    metadata=json.loads((corpus/'conversation-v4.json').read_text())
    review=json.loads(Path(args.data_review).read_text())
    if metadata['preparationStatus']!='READY_FOR_DATA_REVIEW' or review.get('status')!='ACCEPTED_FOR_PILOT':
        raise ValueError('Preparation and recorded data review must pass before training')
    if review['corpusMetadataHash']!=digest((corpus/'conversation-v4.json').read_bytes()):
        raise ValueError('Data review belongs to a different corpus')
    if metadata['challengeHash']!=digest(Path(args.suite).read_bytes()):raise ValueError('Frozen challenge changed')
    manifest=json.loads((packed/'manifest.json').read_text())
    if manifest['format']!=4 or manifest['corpusMetadataHash']!=review['corpusMetadataHash']:
        raise ValueError('Packed dataset does not match reviewed conversation corpus')
    output.mkdir(parents=True,exist_ok=True)
    start=time.perf_counter()
    record=dict(status='RUNNING',seed=42,batchSize=32,endpoint=50000,maskedLanguageUpdates=40000,jointUpdates=10000,
                decoderPolishingUpdates=0,promoted=False,humanReviewComplete=False,baseline=str(Path(args.baseline).resolve()),
                baselineHash=digest(Path(args.baseline).read_bytes()),corpusMetadataHash=review['corpusMetadataHash'],
                challengeHash=metadata['challengeHash'],startedUtc=datetime.datetime.now(datetime.timezone.utc).isoformat(),commands=[])
    def save():
        record['elapsedSeconds']=time.perf_counter()-start
        temporary=output/'pilot.json.tmp';temporary.write_text(json.dumps(record,indent=2));temporary.replace(output/'pilot.json')
    def run(name,command,allow_quality_failure=False):
        record['activeStage']=name;save()
        print(name,flush=True)
        with (output/(name+'.log')).open('w',encoding='utf-8') as log:
            result=subprocess.run(list(map(str,command)),stdout=log,stderr=subprocess.STDOUT)
        record['commands'].append(dict(stage=name,command=list(map(str,command)),exitCode=result.returncode));save()
        if result.returncode and not (allow_quality_failure and result.returncode==1):
            raise RuntimeError(f'{name} failed: inspect {output/(name+".log")}')
        return result.returncode
    cli=Path(args.cli).resolve()
    try:
        for endpoint in (45000,50000):
            run(f'train-{endpoint}',[sys.executable,ROOT/'scripts/torch_training/train.py',packed,output,'--cli',cli,'--until',endpoint])
            progress=json.loads((output/'progress.json').read_text())
            if progress['completedSteps']!=endpoint or progress['status']!='ENDPOINT_REACHED':
                raise RuntimeError('Trainer stopped before the requested bounded endpoint; checkpoint retained')
            (output/'checkpoints').mkdir(exist_ok=True)
            shutil.copyfile(output/'training.pt',output/'checkpoints'/f'step-{endpoint}.pt')
            candidate=output/f'step-{endpoint}-calibrated.fbm'
            run(f'calibrate-{endpoint}',['dotnet',cli,'calibrate-pilot',corpus,output/'latest.fbm',candidate])
            run(f'challenge-{endpoint}',['dotnet',cli,'challenge-conversation',candidate,args.suite,output/f'challenge-{endpoint}.json'],True)
        candidate=output/'step-50000-calibrated.fbm'
        run('baseline-challenge',['dotnet',cli,'challenge-conversation',args.baseline,args.suite,output/'baseline-challenge.json'],True)
        run('legacy-acceptance',['dotnet',cli,'acceptance-contextual',candidate,output/'acceptance.json'],True)
        run('baseline-legacy-acceptance',['dotnet',cli,'acceptance-contextual',args.baseline,output/'baseline-acceptance.json'],True)
        artifact_exit=run('artifact-smoke',['dotnet',cli,'artifact-smoke',candidate],True)
        resource_exit=run('resources',['dotnet',cli,'profile-contextual',candidate,'32'],True)
        run('baseline-resources',['dotnet',cli,'profile-contextual',args.baseline,'32'],True)
        run('review-export',['dotnet',cli,'conversation-sample',candidate,args.scenarios,output/'review-50-conversations.jsonl'])
        run('baseline-review-export',['dotnet',cli,'conversation-sample',args.baseline,args.scenarios,output/'baseline-review-50-conversations.jsonl'])
        from compare import compare
        comparison=compare(output/'baseline-challenge.json',output/'challenge-50000.json')
        (output/'comparison.json').write_text(json.dumps(comparison,indent=2))
        passed=comparison['pilot']['Passed'] and json.loads((output/'acceptance.json').read_text())['Passed'] and artifact_exit==0 and resource_exit==0
        record.update(status='AUTOMATED_GATES_PASSED_AWAITING_REVIEW' if passed else 'PILOT_QUALITY_GATES_FAILED',
                      completedSteps=50000,candidate=str(candidate),candidateHash=digest(candidate.read_bytes()),
                      naturalness='Unrated: review the exported conversations; neither loss nor overlap proves naturalness.')
        save()
        print(json.dumps(record,indent=2))
        return 0 if passed else 1
    except BaseException:
        record['status']='PILOT_INTERRUPTED_OR_FAILED';save();raise


if __name__=='__main__':raise SystemExit(main())
