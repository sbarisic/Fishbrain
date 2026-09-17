"""Report deterministic pilot draws as well as prescribed sampling probabilities."""
import argparse
import collections
import json
import sys
from pathlib import Path
sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'torch_training'))
from data import Corpus
from model import phase_at


def main():
    parser=argparse.ArgumentParser();parser.add_argument('packed');parser.add_argument('output');args=parser.parse_args()
    corpus=Corpus(args.packed)
    pools=collections.Counter();histories=collections.Counter();responses=collections.Counter();semantic=collections.Counter()
    for step in range(40000,50000):
        phase=phase_at(step)
        rows=corpus.batch(step,phase)
        if phase=='JointRealization':
            assert len({r['training']['pool'] for r in rows})==1
            for row in rows:
                pools[row['training']['pool']]+=1
                count=len(row['inputData']['utterances'])
                histories['fresh' if count==1 else 'short' if count<=5 else 'long']+=1
                responses[tuple(row['responseTargets'])]+=1
        else:
            for row in rows:
                assert row['training']['pool']!='public'
                assert row['targets'] or row['multi'] or row['memoryTargets'] is not None
                semantic[row['training']['pool']]+=1
    total=sum(pools.values())
    report=dict(seed=42,batchSize=32,updates=10000,realizationDraws=total,realizationDrawsByPool=pools,
                historyDraws=histories,historyFractions={k:v/total for k,v in histories.items()},
                semanticDrawsByPool=semantic,maximumObservedExactResponseFraction=max(responses.values())/total,
                note='Observed frequencies of the fixed seed-42 draws. The 0.5% constraint applies to sampling probability, not random realized counts.')
    Path(args.output).write_text(json.dumps(report,indent=2));print(json.dumps(report,indent=2))


if __name__=='__main__':main()
