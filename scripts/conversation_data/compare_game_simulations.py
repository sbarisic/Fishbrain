"""Paired development replay comparison; uncertainty resamples complete sessions."""
import argparse
import collections
import json
import random
from pathlib import Path


def main():
    p=argparse.ArgumentParser();p.add_argument('baseline');p.add_argument('candidate');p.add_argument('output');a=p.parse_args()
    baseline=json.loads(Path(a.baseline).read_text());candidate=json.loads(Path(a.candidate).read_text())
    assert baseline['assertionVersion']==candidate['assertionVersion']
    pairs=collections.defaultdict(list)
    for old,new in zip(baseline['cases'],candidate['cases'],strict=True):
        assert (old['session'],old['turn'],old['input'])==(new['session'],new['turn'],new['input'])
        pairs[old['session']].append((old,new))
    sessions=list(pairs);rng=random.Random(42);deltas=[]
    for _ in range(10000):
        sample=[pair for session in rng.choices(sessions,k=len(sessions)) for pair in pairs[session]]
        deltas.append(sum(int(new['passed'])-int(old['passed']) for old,new in sample)/len(sample))
    deltas.sort()
    result=dict(baseline=baseline['label'],candidate=candidate['label'],sessions=len(sessions),turns=baseline['turns'],
        baselinePassed=baseline['passedTurns'],candidatePassed=candidate['passedTurns'],
        pairedDifference=(candidate['passedTurns']-baseline['passedTurns'])/baseline['turns'],
        sessionBootstrap95Interval=[deltas[250],deltas[9749]],seed=42,replicates=10000,
        improved=sum(not old['passed'] and new['passed'] for group in pairs.values() for old,new in group),
        regressed=sum(old['passed'] and not new['passed'] for group in pairs.values() for old,new in group),
        note='Exploratory interval over this deliberately selected development scenario mix. It does not establish population accuracy, naturalness, or release readiness.')
    Path(a.output).write_text(json.dumps(result,indent=2));print(json.dumps(result,indent=2))


if __name__=='__main__':main()
