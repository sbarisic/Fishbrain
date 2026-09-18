"""Run native gates and actual stateful conversations for a retained teaching candidate."""
import argparse
import json
import subprocess
import sys
import time
from pathlib import Path


def main():
    p=argparse.ArgumentParser()
    p.add_argument('--cli',required=True);p.add_argument('--corpus',required=True)
    p.add_argument('--run',required=True);p.add_argument('--scenarios',default='data/game-teaching/scenarios.jsonl')
    p.add_argument('--challenge',default='data/conversation-v4/challenge.json')
    a=p.parse_args();root=Path(a.run);started=time.perf_counter();results={}
    def native(name,*args,json_stdout=False):
        print('NATIVE',name,flush=True)
        tick=time.perf_counter()
        result=subprocess.run(['dotnet',a.cli,*map(str,args)],capture_output=True,text=True,encoding='utf-8')
        (root/(name+'.log')).write_text(result.stdout+result.stderr,encoding='utf-8')
        results[name]=dict(exitCode=result.returncode,seconds=time.perf_counter()-tick)
        if result.returncode not in (0,1):raise RuntimeError(result.stderr or result.stdout)
        if json_stdout:
            (root/(name+'.json')).write_text(json.dumps(json.loads(result.stdout),indent=2))
        return result.returncode
    model=root/'calibrated.fbm'
    if native('calibration','calibrate-game-teaching',a.corpus,root/'candidate.fbm',model):
        raise RuntimeError('Calibration failed; inspect calibration.log')
    native('test','evaluate',Path(a.corpus)/'test.jsonl',model,json_stdout=True)
    native('acceptance','acceptance-contextual',model,root/'acceptance.json')
    native('challenge','challenge-conversation',model,a.challenge,root/'challenge.json')
    if native('simulation','simulate-game',model,a.scenarios,root/'review.jsonl',root/'trace.jsonl'):
        raise RuntimeError('Simulation failed')
    if native('candidate-smoke','artifact-smoke',model):raise RuntimeError('Candidate smoke failed')
    (root/'evaluation-run.json').write_text(json.dumps(dict(steps=results,elapsedSeconds=time.perf_counter()-started,
        promoted=False,note='Quality gate failures are retained. Read actual conversations before judging usefulness.'),indent=2))
    print('EVALUATION COMPLETE; CANDIDATE RETAINED, NOT PROMOTED',flush=True)


if __name__=='__main__':main()
