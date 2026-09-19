"""Read-only audit of frozen conversation isolation and actual packed targets."""
import collections,json,re
from context_data import ROOT,SOURCE,rows
from prepare import write_json,sha

def canonical(s):return ' '.join(re.findall(r'\w+',s.lower()))
def main():
    episodes=rows(ROOT/'prepared/episodes.jsonl')
    signatures={}
    for e in episodes:
        if e['split']!='train':continue
        value=canonical(' '.join(m['text'] for m in e['messages'] if m['role']=='player'))
        signatures.setdefault(value,[]).append(e['id'])
    exact=[];near=[];overlap={}
    for suite in [SOURCE/'challenge.json',__import__('pathlib').Path('data/causal-v1/challenge.json')]:
        cases=json.loads(suite.read_text(encoding='utf8'))['cases']
        training_utterances={canonical(m['text']) for e in episodes for m in e['messages'] if m['role']=='player'}
        overlap[str(suite)]=sum(canonical(c['steps'][-1]['player']) in training_utterances for c in cases)
        for case in cases:
            value=canonical(' '.join(s['player'] for s in case['steps']))
            if value in signatures:exact.append(dict(case=case['id'],episodes=signatures[value]))
            words=value.split();a=set(zip(words,words[1:],words[2:]))
            if len(a)<8:continue
            for other,ids in signatures.items():
                w=other.split();b=set(zip(w,w[1:],w[2:]))
                score=len(a&b)/len(a|b) if a|b else 0
                if score>=.85:near.append(dict(case=case['id'],episodes=ids,wordTrigramJaccard=score))
    groups=collections.defaultdict(list)
    packed=rows(ROOT/'prepared/specialization.jsonl')
    for r in packed:groups[tuple(r['tokens'][:r['promptLength']])].append(r)
    mixed=[]
    for group in groups.values():
        types={json.loads(r['target'])['type'] for r in group}
        if len(types)>1:mixed.append([dict(id=r['id'],target=r['target']) for r in group])
    required=[r for r in packed if r['requiredSequences']]
    report=dict(status='PASS' if not exact and not near and not mixed else 'FAIL',exactConversationLeakage=exact,
        nearConversationLeakage=near,mixedTextToolTargets=mixed,finalUtteranceOverlap=overlap,
        dependencyTargets=len(required),lostDependencies=sum(not set(r['requiredSequences'])<=set(r['retainedSequences']) for r in required),
        corpusSha256=sha(ROOT/'prepared/specialization.jsonl'),
        note='Final-utterance overlap is reported separately. Different complete histories are contextual tests, not automatically leakage.')
    write_json(SOURCE/'isolation-audit.json',report);print(json.dumps(report,indent=2))
    if report['status']!='PASS':raise RuntimeError('Isolation or target audit failed')
if __name__=='__main__':main()
