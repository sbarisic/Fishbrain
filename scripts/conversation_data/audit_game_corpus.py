"""Exclude complete episodes that touch frozen acceptance contexts and verify split isolation."""
import argparse
import collections
import json
from pathlib import Path
from common import Normalizer, digest, read_jsonl, write_jsonl


def main():
    parser=argparse.ArgumentParser();parser.add_argument('corpus');parser.add_argument('output')
    parser.add_argument('--challenge',required=True);parser.add_argument('--cli',required=True)
    args=parser.parse_args();source=Path(args.corpus);output=Path(args.output)
    if output.exists():raise ValueError('Use a new audited corpus directory')
    output.mkdir(parents=True)
    normalizer=Normalizer(args.cli)
    try:
        frozen={normalizer(' '.join(u['speaker']+' '+u['text'] for u in e['case']['request']['utterances']))
                for e in json.loads(Path(args.challenge).read_text())}
    finally:normalizer.close()
    sets={s:list(read_jsonl(source/(s+'.jsonl'))) for s in ('train','validation','test')}
    rejected=[]
    # Exact context overlap across splits is quarantined in all affected splits.
    split_for=collections.defaultdict(set)
    for split,rows in sets.items():
        for row in rows:
            split_for[row['input']].add(split)
            current=row['turns'][-1]['text']
            for frame in row['contextual']['frames']:
                for slot in frame['arguments']:
                    start=slot['start'];end=start+slot['length']
                    if current[start:end]!=slot['value'] or (start and current[start-1].isalnum()) or (end<len(current) and current[end].isalnum()):
                        raise ValueError('Invalid whole-word argument span in '+row['id'])
    conflicts={text for text,splits in split_for.items() if len(splits)>1}
    counts={};families={};finals={};states={}
    for split,rows in sets.items():
        banned={r['training']['episodeId'] for r in rows if r['input'] in frozen or r['input'] in conflicts}
        accepted=[]
        for row in rows:
            if row['training']['episodeId'] in banned:
                rejected.append(dict(id=row['id'],episode=row['training']['episodeId'],reason='EPISODE_TOUCHES_FROZEN_OR_CROSS_SPLIT_CONTEXT'))
            else:accepted.append(row)
        assert accepted
        write_jsonl(output/(split+'.jsonl'),accepted)
        counts[split]=len(accepted)
        families[split]={r['training']['augmentationFamily'] for r in accepted}
        finals[split]={r['turns'][-1]['text'] for r in accepted}
        states[split]=dict(withSummaries=sum(bool(r['initialDialogueState']['topicSummaries']) for r in accepted),
            withMultipleFacts=sum(len(r['initialDialogueState']['sessionFacts'])>1 for r in accepted),
            compound=sum(len(r['contextual']['frames'])>1 for r in accepted))
    for a,b in [('train','validation'),('train','test'),('validation','test')]:assert not families[a]&families[b]
    report=dict(schema='STATEFUL_GAME_TEACHING_V1',counts=counts,states=states,excludedRows=len(rejected),
        splitHashes={s:digest((output/(s+'.jsonl')).read_bytes()) for s in sets},
        frozenChallengeHash=digest(Path(args.challenge).read_bytes()),familyOverlap=0,exactCrossSplitContexts=0,
        finalUtteranceOverlap={a+'_'+b:len(finals[a]&finals[b]) for a,b in [('train','validation'),('train','test'),('validation','test')]},
        nearDuplicatePolicy='Related template/semantic families intentionally span development wording sets. No independent unseen quality claim; frozen release contexts excluded exactly.',
        promotionAllowed=False)
    write_jsonl(output/'exclusions.jsonl',rejected)
    (output/'audit.json').write_text(json.dumps(report,indent=2))
    print(json.dumps(report,indent=2))


if __name__=='__main__':main()
