"""Record effective sampling mass and inspectable stateful lesson examples."""
import argparse
import collections
import json
from pathlib import Path
from common import read_jsonl,write_jsonl


def main():
    p=argparse.ArgumentParser();p.add_argument('corpus');p.add_argument('packed');p.add_argument('output');a=p.parse_args()
    rows=list(read_jsonl(Path(a.corpus)/'train.jsonl'));packed=list(read_jsonl(Path(a.packed)/'train.jsonl'))
    names={'head.knowledgeTarget','frame.0.act','frame.0.tool','frame.0.status','frame.0.factAct','frame.0.factKind',
           'frame.1.active','frame.1.act','frame.1.tool','plan.1.act'}
    groups=collections.defaultdict(list)
    for raw,encoded in zip(rows,packed,strict=True):
        if not encoded['projectResponse']:continue
        key=tuple((t['name'],t['label']) for t in encoded['targets'] if t['name'] in names)
        groups[key].append(raw)
    mass=collections.Counter();history=collections.Counter()
    for group in groups.values():
        for r in group:
            weight=1/len(groups)/len(group)
            mass[r['response']]+=weight
            n=len(r['turns']);history['fresh' if n==1 else 'short' if n<=5 else 'long']+=weight
    selected={}
    for r in rows:
        key=(r['category'],len(r['contextual']['frames'])>1,len(r['initialDialogueState']['sessionFacts'])>1)
        selected.setdefault(key,r)
    root=Path(a.output);root.mkdir(parents=True,exist_ok=True)
    write_jsonl(root/'data-review.jsonl',selected.values())
    (root/'sampling-audit.json').write_text(json.dumps(dict(rows=len(rows),responseRows=sum(len(g) for g in groups.values()),
        responseGroups=len(groups),distinctResponses=len(mass),responseProbability=dict(mass.most_common()),historyProbability=dict(history),
        coverage=dict(collections.Counter(r['category'] for r in rows)),reviewRows=len(selected),
        scope='Narrow teaching experiment, not the public/authored release mixture. Response diversity and probability caps do not meet conversation-v4 release requirements.',
        review='Agent inspection of explicit lessons, spans, owners and state transitions; not independent human release approval.'),indent=2))


if __name__=='__main__':main()
