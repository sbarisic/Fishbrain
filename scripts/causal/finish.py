"""Evaluate and package the completed bounded candidate. Never trains, promotes, or commits."""
import argparse,json,os,shutil,subprocess,sys,time
from pathlib import Path
from prepare import sha,write_json,budget

def main():
    p=argparse.ArgumentParser();p.add_argument('root');p.add_argument('--wait',action='store_true');a=p.parse_args();root=Path(a.root)
    previous=root/'evaluation-progress.json'
    if previous.exists() and json.loads(previous.read_text())['status']=='COMPLETE_NOT_PROMOTED':
        package=root/'candidate-package';manifest=json.loads((package/'manifest.json').read_text())
        if any(sha(package/name)!=digest for name,digest in manifest['files'].items()):
            raise RuntimeError('The retained candidate package no longer matches its manifest')
        print('Evaluation already complete; retained reports and package verified. No replay or promotion performed.')
        return
    while True:
        progress=json.loads((root/'pilot/progress.json').read_text())
        if progress['status']=='COMPLETE':break
        if not a.wait:raise RuntimeError('The bounded pilot must complete first')
        if time.time()-progress['updatedUtc']>600:raise RuntimeError('Pilot progress is stale; inspect training before evaluation')
        time.sleep(30)
    start=time.monotonic();timings=[];status=root/'evaluation-progress.json'
    def run(label,command,input=None,environment=None):
        write_json(status,dict(status='RUNNING',stage=label,completed=timings));begin=time.monotonic()
        with (root/(label+'.log')).open('w',encoding='utf8') as log:
            subprocess.run([str(v) for v in command],input=input,text=True,stdout=log,stderr=subprocess.STDOUT,check=True,env=environment)
        timings.append(dict(stage=label,seconds=time.monotonic()-begin));budget(root)
    py=sys.executable;dll='Fishbrain/bin/Release/net10.0/Fishbrain.dll';model=root/'pilot/specialization-final.fbc'
    run('final-build',['dotnet','build','Fishbrain.slnx','-c','Release'])
    run('final-runtime-tests',['dotnet','run','--no-build','-c','Release','--project','Fishbrain.CausalTests'])
    run('portable-runtime-tests',['dotnet','run','--no-build','-c','Release','--project','Fishbrain.CausalTests'],
        environment=dict(os.environ,DOTNET_EnableHWIntrinsic='0'))
    run('reporting-tests',[py,'scripts/causal/test_reporting.py'])
    run('final-parity',[py,'scripts/causal/final_parity.py',root])
    run('gpu-checkpoint-probe',[py,'scripts/causal/checkpoint_probe.py',root,root/'pilot/language-03000.pt',root/'gpu-checkpoint-probe.json','--device','cuda'])
    run('resources',['dotnet',dll,'benchmark',model,root/'resources.json'])
    for label,suite in [('pilot-challenge','challenge'),('pilot-preserved','preserved-acceptance'),('pilot-development','development')]:
        run(label,['dotnet',dll,'evaluate',model,f'data/causal-v1/{suite}.json',root/(label+'.jsonl')])
    run('derive-invariance',[py,'scripts/causal/evaluation.py','derive-invariance','data/causal-v1/challenge.json',root/'invariance.json'])
    run('pilot-invariance',['dotnet',dll,'evaluate',model,root/'invariance.json',root/'pilot-invariance.jsonl'])
    run('supplemental-stress',[py,'scripts/causal/stress.py','run',root])
    baseline=root/'baseline-review.jsonl'
    if not baseline.exists():
        run('baseline',['dotnet','Fishbrain.LegacyCli/bin/Release/net10.0/Fishbrain.dll','causal-baseline',
            'data/training/game-teaching-run-v4/calibrated.fbm','data/causal-v1/challenge.json',baseline])
    run('quality-report',[py,'scripts/causal/evaluation.py','report',root])
    run('review-template',[py,'scripts/causal/review_gate.py','template',root/'review-50.jsonl',root/'human-review-unrated.jsonl'])
    package=root/'candidate-package';package.mkdir(exist_ok=True)
    run('candidate-publish',['dotnet','publish','Fishbrain','-c','Release','--no-restore','-o',package])
    shutil.copy2(model,package/'candidate.fbc');shutil.copytree('data/causal-v1/source-notices',package/'source-notices',dirs_exist_ok=True)
    for filename in ['sources.json','public-sources.json']:
        shutil.copy2(Path('data/causal-v1')/filename,package/filename)
    (package/'CANDIDATE.txt').write_text('Experimental candidate. Not promoted. Read quality.json and resources.json. Two independent human reviews remain required.\n',encoding='utf8')
    for filename in ['quality.json','resources.json','final-parity.json']:shutil.copy2(root/filename,package/filename)
    run('packaged-inspect',['dotnet',package/'Fishbrain.dll','inspect',package/'candidate.fbc'])
    run('packaged-chat',['dotnet',package/'Fishbrain.dll','chat',package/'candidate.fbc',root/'packaged-smoke.jsonl'],input="Hello\nWhat's your job?\nWhat do you have for sale?\n\n")
    smoke=[json.loads(line) for line in (root/'packaged-smoke.jsonl').read_text(encoding='utf8').splitlines()]
    if len(smoke)!=3 or any(not row['result']['text'] or len(row['result']['text'])>256 for row in smoke):
        raise RuntimeError('Packaged chat did not produce three bounded responses')
    if 'STATE RAPPORT=' in (root/'packaged-chat.log').read_text(encoding='utf8'):
        raise RuntimeError('Retired state diagnostics appeared in ordinary chat')
    files={str(f.relative_to(package)):sha(f) for f in sorted(package.rglob('*')) if f.is_file() and f.name!='manifest.json'}
    write_json(package/'manifest.json',dict(status='EXPERIMENTAL_NOT_PROMOTED',trainingFingerprint=progress['fingerprint'],files=files))
    write_json(status,dict(status='COMPLETE_NOT_PROMOTED',elapsedSeconds=time.monotonic()-start,completed=timings,
        package=str(package),packageManifestSha256=sha(package/'manifest.json')))
    subprocess.run([py,'scripts/causal/collect_report.py',str(root)],check=True)
    print('Evaluation and candidate packaging complete. Inspect results; no release or Git action was taken.')
if __name__=='__main__':main()
