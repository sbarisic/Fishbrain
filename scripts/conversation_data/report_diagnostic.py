"""Archive the small diagnostic evidence and pair desired replies with actual rollouts."""
import argparse
import collections
import hashlib
import json
import shutil
from pathlib import Path

from common import read_jsonl


def main():
    parser=argparse.ArgumentParser()
    parser.add_argument('--lessons',required=True)
    parser.add_argument('--run',required=True)
    args=parser.parse_args()
    lessons,run=Path(args.lessons),Path(args.run)
    evidence=lessons/'results'
    evidence.mkdir(exist_ok=True)
    for name in ('experiment.json','progress.json','learning.json','metrics.json','native-train.json',
                 'native-validation.json','native-test.json','actual-conversations.jsonl','baseline-conversations.jsonl'):
        shutil.copyfile(run/name,evidence/name)
    expected=list(read_jsonl(lessons/'expected.jsonl'))
    lookup={(r['sessionId'],r['turnIndex']+1):r for r in expected}
    actual=list(read_jsonl(run/'actual-conversations.jsonl'))
    baseline={(r['sessionId'],r['turnIndex']):r for r in read_jsonl(run/'baseline-conversations.jsonl')}
    if set(lookup)!=set(baseline) or set(lookup)!={(r['sessionId'],r['turnIndex']) for r in actual}:
        raise ValueError('Paired sessions differ')
    training={r['training']['episodeId'].removeprefix('TEACHING:') for r in read_jsonl(lessons/'train.jsonl')}
    lines=['# Teaching diagnostic: actual conversations','',
           'Development lessons and related paraphrase probes. These are not independent release evaluations.',
           'All tools are disabled in the diagnostic model because these few examples cannot calibrate execution.',
           'Desired authority replies illustrate the demo values; exact tool-template wording can differ.','']
    counts={}
    for label,rows in [('baseline',list(baseline.values())),('diagnostic',actual)]:
        counts[label]=dict(turns=len(rows),sources=dict(collections.Counter(r['responseSource'] for r in rows)),
                           responses=dict(collections.Counter(r['modelResponse'] for r in rows)))
    for row in actual:
        key=(row['sessionId'],row['turnIndex'])
        desired=lookup[key]
        if row['turnIndex']==1:
            lines+=['## '+row['sessionId']+' ('+('trained lesson' if row['sessionId'] in training else 'development probe')+')','']
        lines+=['**Player:** '+row['input'],'','**Desired:** '+desired['expected'],'',
                '**Previous pilot:** '+baseline[key]['modelResponse'],'',
                '**Diagnostic:** '+row['modelResponse']+' ('+row['responseSource']+')','']
    (evidence/'conversations.md').write_text('\n'.join(lines),encoding='utf-8')
    hashes={}
    for name in ('diagnostic.fbm','training.pt'):
        sha=hashlib.sha256()
        with (run/name).open('rb') as stream:
            while block:=stream.read(1024*1024):sha.update(block)
        hashes[name]=dict(sha256=sha.hexdigest(),bytes=(run/name).stat().st_size)
    (evidence/'summary.json').write_text(json.dumps(dict(rollouts=counts,artifacts=hashes,promoted=False,
        independentHumanReview=False,interpretation='Counts describe these development sessions only; response matching is not a naturalness score.'),indent=2))
    print(json.dumps(counts,indent=2))


if __name__=='__main__':main()
