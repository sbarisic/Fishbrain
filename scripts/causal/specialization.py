"""Conversation isolation audit and native packing. No old semantic targets are copied."""
import collections, difflib, hashlib, json, subprocess
from pathlib import Path
from prepare import sha,write_json,norm,budget

def main():
    import sys
    root=Path(sys.argv[1]);out=root/'prepared';suite=Path('data/causal-v1/challenge.json')
    assert sha(suite)==Path('data/causal-v1/challenge.sha256').read_text().strip()
    frozen=json.loads(suite.read_text(encoding='utf8'))
    excluded_inputs={norm(s['player']) for c in frozen['cases'] for s in c['steps'] if not s.get('developmentOverlap')}
    preserved=Path('data/causal-v1/preserved-acceptance.json')
    if not preserved.exists():raise RuntimeError('Preserved acceptance inputs must be frozen before packing')
    excluded_inputs.update(norm(s['player']) for c in json.loads(preserved.read_text(encoding='utf8'))['cases'] for s in c['steps'])
    episodes=json.loads((out/'public-restored.json').read_text(encoding='utf8'))+[json.loads(l) for l in (out/'authored.jsonl').read_text(encoding='utf8').splitlines()]
    # Reject whole families when a member conflicts; preserve published public splits.
    rejected={};families=collections.defaultdict(set);exact={};cross=[]
    for e in episodes:
        families[e['family']].add(e['split'])
        if any(norm(m['text']) in excluded_inputs for m in e['messages'] if m['role']=='player'):rejected[e['family']]='FROZEN_INPUT'
        key=norm(' '.join(m['role']+':'+m['text'] for m in e['messages']))
        if key in exact:
            prior=exact[key]
            if prior['split']!=e['split']:rejected[e['family']]='EXACT_CROSS_SPLIT';cross.append([prior['id'],e['id']])
        else:exact[key]=e
    if any(len(s)>1 for s in families.values()):raise RuntimeError('Conversation family crosses splits')
    # Word-shingle Jaccard >=.9, candidate generation by shared shingles; short conversations use exact checks.
    index=collections.defaultdict(set);grams=[]
    for i,e in enumerate(episodes):
        words=norm(' '.join(m['text'] for m in e['messages'])).split();g={' '.join(words[j:j+5]) for j in range(len(words)-4)};grams.append(g)
        candidates=set()
        for token in g:candidates.update(index[token])
        for j in candidates:
            if episodes[j]['split']==e['split'] or not g:continue
            other=grams[j]
            if len(g&other)/len(g|other)>=.9:rejected[e['family']]='NEAR_CROSS_SPLIT';cross.append([episodes[j]['id'],e['id']])
        for token in g:index[token].add(i)
    accepted=[e for e in episodes if e['family'] not in rejected]
    authored=[e for e in episodes if e['pool']!='public']
    if len({norm(' '.join(m['text'] for m in e['messages'])) for e in authored})<300:raise RuntimeError('Fewer than 300 distinct authored episodes')
    with (out/'episodes.jsonl').open('w',encoding='utf8') as f:
        for e in accepted:f.write(json.dumps(e,ensure_ascii=False,separators=(',',':'))+'\n')
    subprocess.run(['dotnet','Fishbrain/bin/Release/net10.0/Fishbrain.dll','pack-corpus',str(root),str(out/'specialization.jsonl')],check=True)
    rows=[json.loads(l) for l in (out/'specialization.jsonl').read_text(encoding='utf8').splitlines()]
    counts=collections.Counter((r['split'],r['pool']) for r in rows)
    if any(counts[('train',p)]==0 or counts[('validation',p)]==0 for p in ('public','tools','social')):raise RuntimeError('Missing training or validation pool')
    responses=collections.Counter(r['target'] for r in rows if r['split']=='train');history=collections.Counter('fresh' if r['historyTurns']==1 else 'short' if r['historyTurns']<=4 else 'long' for r in rows if r['split']=='train')
    audit=dict(acceptedEpisodes=len(accepted),sourceEpisodes=len(episodes),rejectedFamilies=rejected,crossSplitDuplicatesRemoved=cross,
        targetRows={':'.join(k):v for k,v in counts.items()},historyCoverage=history,topResponses=responses.most_common(20),
        sampling=dict(public=.4,tools=.4,social=.2),nativePacker=True,missingClassificationTargets='not present',
        suiteHash=sha(suite),specializationHash=sha(out/'specialization.jsonl'),authoredPlanHash=sha('data/causal-v1/authored-plans.jsonl'))
    audit['preservedAcceptanceHash']=sha(preserved)
    audit['effectiveExactTargetProbability']={}
    for pool,mass in [('public',.4),('tools',.4),('social',.2)]:
        selected=[r for r in rows if r['split']=='train' and r['pool']==pool]
        for target,count in collections.Counter(r['target'] for r in selected).items():
            audit['effectiveExactTargetProbability'][target]=audit['effectiveExactTargetProbability'].get(target,0)+mass*count/len(selected)
    audit['effectiveExactTargetProbability']=sorted(audit['effectiveExactTargetProbability'].items(),key=lambda pair:-pair[1])[:30]
    # Final-utterance overlap is a separate measure: histories may legitimately differ.
    finals=collections.defaultdict(set)
    for e in accepted:
        players=[m['text'] for m in e['messages'] if m['role']=='player']
        if players:finals[norm(players[-1])].add(e['split'])
    audit['finalUtterancesAcrossSplits']=sum(len(v)>1 for v in finals.values())
    write_json(out/'specialization-audit.json',audit);budget(root)
    print(json.dumps({k:v for k,v in audit.items() if k not in ('topResponses','crossSplitDuplicatesRemoved','rejectedFamilies')},indent=2))
if __name__=='__main__':main()
