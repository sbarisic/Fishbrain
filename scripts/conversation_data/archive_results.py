"""Copy auditable pilot evidence without promoting or copying model weights into the repo."""
import argparse
import collections
import json
import shutil
from pathlib import Path
from common import digest


def copy_report(source,target):
    """Keep native evidence losslessly, with one case per line for manageable diffs."""
    value=json.loads(source.read_text())
    cases=value.pop('Cases',None)
    if cases is None:
        shutil.copyfile(source,target)
        return
    prefix=json.dumps(value,indent=2).rstrip()[:-1].rstrip()
    text=prefix+',\n  "Cases": [\n'+',\n'.join('    '+json.dumps(case,separators=(',',':')) for case in cases)+'\n  ]\n}\n'
    target.write_text(text,encoding='utf-8',newline='\n')
    assert json.loads(target.read_text())==json.loads(source.read_text())


def main():
    parser=argparse.ArgumentParser();parser.add_argument('run');parser.add_argument('robustness');parser.add_argument('output');args=parser.parse_args()
    run,robustness,output=map(Path,(args.run,args.robustness,args.output));output.mkdir(parents=True,exist_ok=True)
    pilot=json.loads((run/'pilot.json').read_text())
    if pilot.get('completedSteps')!=50000:raise ValueError('The bounded pilot has not completed')
    names=['pilot.json','comparison.json','challenge-45000.json','challenge-50000.json','baseline-challenge.json',
           'acceptance.json','baseline-acceptance.json','diagnostics-45000.json','diagnostics-50000.json',
           'review-50-conversations.jsonl','baseline-review-50-conversations.jsonl','development.jsonl','baseline-development.jsonl',
           'step-50000-calibrated.fbm.calibration.json']
    for name in names:
        if 'challenge' in name or 'acceptance' in name:copy_report(run/name,output/name)
        else:shutil.copyfile(run/name,output/name)
    for name in ('resources','baseline-resources'):
        data=json.loads((run/(name+'.log')).read_text(encoding='utf-8-sig'))
        (output/(name+'.json')).write_text(json.dumps(data,indent=2))
    for name in ('punctuation','irrelevant-history-removal'):
        for suffix in ('','-baseline','-pilot','-summary'):
            source=robustness/(name+suffix+'.json');target=output/('robustness-'+name+suffix+'.json')
            if suffix in ('-baseline','-pilot'):copy_report(source,target)
            else:shutil.copyfile(source,target)
    losses={}
    for path in sorted(run.glob('validation-*.json')):
        value=json.loads(path.read_text());losses[str(value['completedSteps'])]=value['metrics']
    (output/'validation-losses.json').write_text(json.dumps(losses,indent=2))
    rollouts={}
    for label,name in [('baseline','baseline-review-50-conversations.jsonl'),('pilot','review-50-conversations.jsonl')]:
        rows=[json.loads(line) for line in (run/name).read_text().splitlines()]
        texts=collections.Counter(row['modelResponse'] for row in rows)
        rollouts[label]=dict(turns=len(rows),sessions=len({r['sessionId'] for r in rows}),
                            responseSources=dict(collections.Counter(r['responseSource'] for r in rows)),
                            repeatedResponseFraction=1-len(texts)/len(rows),mostCommonResponses=texts.most_common(10))
    (output/'rollout-summary.json').write_text(json.dumps(rollouts,indent=2))
    artifacts={str(path):dict(bytes=path.stat().st_size,sha256=digest(path.read_bytes()))
               for path in [run/'step-45000-calibrated.fbm',run/'step-50000-calibrated.fbm',
                            run/'checkpoints/step-45000.pt',run/'checkpoints/step-50000.pt']}
    (output/'retained-artifacts.json').write_text(json.dumps(dict(artifacts=artifacts,promoted=False),indent=2))


if __name__=='__main__':main()
