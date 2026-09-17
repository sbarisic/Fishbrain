"""Prepare a separate v4 corpus; failed gates never start training or change an old artifact."""
import argparse
import collections
import copy
import json
import random
import subprocess
from pathlib import Path

from common import ROOT, Normalizer, digest, read_jsonl, write_jsonl, row_base, eligible, set_turns
from public import import_public, sources
from episodes import all_episodes
from annotations import annotate
from sampling import CONFIG, weights, history_bucket


def split_for(family):
    value = int(digest('42:' + family)[:8], 16) % 100
    return 'train' if value < 80 else 'validation' if value < 90 else 'test'


def isolate(rows, reject):
    """Union complete episodes before splitting; quarantine conflicts with published splits.

    Near duplicates use exact Jaccard >= .85 of normalized word trigrams. An inverted
    index checks every overlapping candidate; short exchanges use their full text.
    No current-utterance-only deduplication destroys valid contextual contrasts.
    """
    parent = {r['training']['augmentationFamily']:r['training']['augmentationFamily'] for r in rows}
    def find(x):
        while parent[x] != x:
            parent[x] = parent[parent[x]]
            x = parent[x]
        return x
    def union(a,b):
        a,b = find(a),find(b)
        if a != b: parent[max(a,b)] = min(a,b)
    exact, contexts, inverted, signatures = {}, {}, collections.defaultdict(list), []
    texts=[]
    for row in rows:
        text = '\n'.join(t['speaker']+':'+t['text'] for t in row['turns']) + '\nRESPONSE:' + (row.get('response') or '')
        texts.append(text)
        words=text.split()
        signatures.append(set(zip(words,words[1:],words[2:])) if len(words)>=12 else {(text,)})
    frequency=collections.Counter(s for signature in signatures for s in signature)
    duplicate_rows, near_pairs = set(), 0
    for i,row in enumerate(rows):
        text = texts[i]
        key = digest(text)
        family = row['training']['augmentationFamily']
        context_key=digest('\n'.join(t['speaker']+':'+t['text'] for t in row['turns']))
        if context_key in contexts: union(family,contexts[context_key])
        else: contexts[context_key]=family
        if key in exact:
            j = exact[key]
            union(family, rows[j]['training']['augmentationFamily'])
            # Keep alternate supervision, but remove identical public copies.
            if row['training']['pool'] == rows[j]['training']['pool'] == 'public': duplicate_rows.add(i)
        else: exact[key] = i
        signature=signatures[i]
        ordered=sorted(signature,key=lambda s:(frequency[s],s))
        import math
        prefix=ordered[:len(ordered)-math.ceil(.85*len(ordered))+1]
        candidates=set(j for shingle in prefix for j in inverted[shingle])
        for j in candidates:
            if min(len(signature),len(signatures[j])) < .85*max(len(signature),len(signatures[j])): continue
            overlap=len(signature & signatures[j])
            if overlap / (len(signature) + len(signatures[j]) - overlap) >= .85:
                union(family, rows[j]['training']['augmentationFamily'])
                near_pairs += 1
        for shingle in prefix: inverted[shingle].append(i)
    fixed = collections.defaultdict(set)
    for row in rows:
        if row.get('publishedSplit'): fixed[find(row['training']['augmentationFamily'])].add(row['publishedSplit'])
    accepted = []
    for i,row in enumerate(rows):
        group = find(row['training']['augmentationFamily'])
        if i in duplicate_rows or len(fixed[group]) > 1:
            reject(row['source'],row['id'],'exact_duplicate' if i in duplicate_rows else 'published_split_component_conflict')
            continue
        row['split'] = next(iter(fixed[group])) if fixed[group] else split_for(group)
        row['isolationComponent'] = group
        row['training']['augmentationFamily']=group
        row['semanticFamilyId']=group
        accepted.append(row)
    return accepted, dict(nearDuplicatePairs=near_pairs, exactPublicDuplicates=len(duplicate_rows),
                          quarantinedSplitComponents=sum(len(v)>1 for v in fixed.values()))


def main():
    parser=argparse.ArgumentParser()
    parser.add_argument('--output', default='data/compiled-conversation-v4')
    parser.add_argument('--cli', default='data/training/conversation-v4-tools/Fishbrain.dll')
    args=parser.parse_args()
    output=Path(args.output)
    if output.exists(): raise ValueError('Use a new output directory; corpora are immutable')
    output.mkdir(parents=True)
    rejected=[]
    def reject(source,id,reason): rejected.append(dict(source=source,id=id,reason=reason))
    reference=next(read_jsonl(ROOT/'data/compiled-contextual-v3/train.jsonl'))
    normalizer=Normalizer(args.cli)
    rows=[]
    try:
        decisions=json.loads((ROOT/'data/conversation-v4/public-review-decisions.json').read_text())
        rejected_episodes={id.rsplit(':',1)[0] for id in decisions['rejectedExamples'] if id.startswith('BST:')}
        for item in import_public(normalizer,reject):
            if item['id'] in decisions['rejectedExamples']:
                reject(item['source']['name'],item['id'],'data_review:'+decisions['rejectedExamples'][item['id']])
                continue
            if item['episode'] in rejected_episodes:
                reject(item['source']['name'],item['id'],'episode_contains_review_rejected_response')
                continue
            row=set_turns(row_base(reference),item['turns'],item['response'])
            source=item['source']
            row.update(id='PUBLIC:'+item['id'],source=source['name'],family='PUBLIC_CONVERSATION',category='public',
                       publishedSplit=item['publishedSplit'],review=item['review'],sourceUrl=source['files'][0]['url'],attribution=source['attribution'])
            eligible(row,'public',True,item['episode'],item['episode'],source['license'].upper(),source['revision'],source['files'][0]['sha256'])
            row['input']=normalizer(row['input'])
            if not row['input'] or len(row['input'])>32768:
                reject(row['source'],row['id'],'input_character_budget_or_normalization')
                continue
            rows.append(row)
        episodes=all_episodes()
        authored_hash=digest(json.dumps(episodes,sort_keys=True,ensure_ascii=False))
        for episode in episodes: rows.append(annotate(episode,reference,normalizer,authored_hash))
        # Retain useful labelled examples, with their prior split and family identity.
        for split in ('train','validation','test'):
            for old in read_jsonl(ROOT/f'data/compiled-contextual-v3/{split}.jsonl'):
                heads=old.get('supervisedHeads',[])
                ctx=old.get('contextual') or {}
                if not heads and not any(v is not None for v in ctx.values()): continue
                project=old['source'].startswith('PROJECT_')
                tool=old.get('structuredPerception',{}).get('toolSchema')
                if project and not (tool and tool!='NONE' or ctx.get('relevantFacts') or old.get('factDelta') or old.get('structuredPerception',{}).get('discourse')): continue
                old=copy.deepcopy(old)
                old.update(id='FOCUSED:'+split+':'+str(len(rows)),publishedSplit=split,category='focused' if project else 'classification')
                # A response on an ineligible legacy row is not a decoder or positive-claim target.
                eligible(old,'focused' if project else 'classification',False,old['groupId'],old['semanticFamilyId'],
                         old['sourceLicense'].upper(),old['sourceRevision'],old['sourceChecksum'])
                # These source groups explicitly label fabricated transactions or wrong-owner/world claims.
                # The other legacy rejected responses merely label irrelevant wording, not unsupported claims.
                old['training']['claimNegativeEligible'] = bool(old.get('rejectedResponse')) and old['source'] in {
                    'PROJECT_DISCOURSE_NEGATIVES','PROJECT_CONTEXTUAL_ACTIONS','PROJECT_CONTEXTUAL_MEMORY',
                    'PROJECT_CONTEXTUAL_COMPOUND','PROJECT_CONTEXTUAL_AGENDA'}
                rows.append(old)
    finally: normalizer.close()
    rows,isolation=isolate(rows,reject)
    suite=json.loads((ROOT/'data/conversation-v4/challenge.json').read_text())
    challenge_contexts={tuple(t['text'] for t in entry['case']['request']['utterances']) for entry in suite}
    contaminated={r['isolationComponent'] for r in rows if tuple(t['text'] for t in r['turns']) in challenge_contexts}
    if contaminated:
        kept=[]
        for row in rows:
            if row['isolationComponent'] in contaminated: reject(row['source'],row['id'],'frozen_challenge_overlap')
            else: kept.append(row)
        rows=kept
    def save_splits():
        for split in ('train','validation','test'):
            selected=sorted((r for r in rows if r['split']==split),key=lambda r:r['id'])
            write_jsonl(output/(split+'.jsonl'),selected)
    save_splits()
    # Build the real tokenizer and use the same native packer as inference. No text truncation.
    for iteration in range(5):
        report=output/f'native-budget-{iteration}.json'
        subprocess.run(['dotnet',args.cli,'audit-conversation',str(output),str(report)],check=True)
        native=json.loads(report.read_text())
        failures=native['Rejected']
        if not failures:
            for row in rows:
                if row['id'] in native['Histories']:
                    row['packedHistoryTurns']=native['Histories'][row['id']]['Turns']
                    row['packedInputTokens']=native['Histories'][row['id']]['Tokens']
            save_splits()
            break
        bad={f['Id']:f['Reason'] for f in failures}
        kept=[]
        for row in rows:
            if row['semanticFamilyId'] in bad: reject(row['source'],row['id'],'native_budget:'+bad[row['semanticFamilyId']])
            else: kept.append(row)
        if len(kept)==len(rows): raise ValueError('Native rejection could not be matched to a row')
        rows=kept
        save_splits()
    else: raise ValueError('Tokenizer budget audit did not converge')
    gates=[]
    memberships=collections.defaultdict(set)
    for row in rows: memberships[row['isolationComponent']].add(row['split'])
    if any(len(v)!=1 for v in memberships.values()):gates.append('SPLIT_ISOLATION_FAILED')
    public=[r for r in rows if r['training']['pool']=='public']
    authored=[r for r in rows if r['training']['pool']=='authored']
    if len(public)<1000: gates.append('PUBLIC_RESPONSE_POOL_BELOW_1000')
    if len({r['training']['episodeId'] for r in authored})<240: gates.append('AUTHORED_EPISODES_BELOW_240')
    required={'openings','identity','small_talk','preferences','feelings','clarification','repair','topic_changes','follow_ups','memory','corrections','tools','compound','agenda'}
    missing=required-{r['category'] for r in authored}
    if missing:gates.append('MISSING_BEHAVIORAL_COVERAGE: '+','.join(sorted(missing)))
    sampling={}
    sampling_audit={}
    for split in ('train','validation','test'):
        try: sampling[split],sampling_audit[split]=weights([r for r in rows if r['split']==split])
        except ValueError as error:
            if split=='train': gates.append('SAMPLING_INFEASIBLE: '+str(error))
            sampling_audit[split]=dict(error=str(error))
    # Validation losses use deterministic unweighted eligible rows if small held-out strata
    # cannot support production sampling weights; never weaken training sampling constraints.
    (output/'sampling.json').write_text(json.dumps(dict(config=CONFIG,weights=sampling),sort_keys=True,indent=2))
    overlaps={}
    train_final={r['turns'][-1]['text'] for r in rows if r['split']=='train'}
    for split in ('validation','test'):
        held=[r for r in rows if r['split']==split]
        overlaps[split]=dict(rows=len(held),finalUtterancesSeenInTrain=sum(r['turns'][-1]['text'] in train_final for r in held))
    audit=dict(status='PREPARATION_FAILED' if gates else 'READY_FOR_DATA_REVIEW',failures=gates,rows=len(rows),
               bySplit=collections.Counter(r['split'] for r in rows),byPool=collections.Counter(r['training']['pool'] for r in rows),
               authoredEpisodes=len({r['training']['episodeId'] for r in authored}),authoredRows=len(authored),behavioralCoverage=collections.Counter(r['category'] for r in authored),
               realizationHistoryCounts=collections.Counter(history_bucket(r) for r in rows if r['training']['responseEligible']),
               supervision=collections.Counter(h for r in rows for h in r.get('supervisedHeads',[])),
               responseCounts=collections.Counter(r['response'] for r in rows if r['training']['responseEligible']).most_common(25),
               responseQuestionFraction=sum('?' in r['response'] for r in rows if r['training']['responseEligible'])/max(1,sum(r['training']['responseEligible'] for r in rows)),
               distinctCurrentWording=len({r['turns'][-1]['text'] for r in rows}),
               frozenChallengeOverlapComponentsRemoved=len(contaminated),
               exclusions=collections.Counter(r['reason'] for r in rejected),isolation=isolation,finalUtteranceOverlap=overlaps,sampling=sampling_audit)
    (output/'audit.json').write_text(json.dumps(audit,indent=2))
    write_jsonl(output/'rejections.jsonl',rejected)
    rng=random.Random(42)
    review=[]
    for pool in ('public','authored'):
        for split in ('train','validation','test'):
            for bucket in ('fresh','short','long'):
                group=[r for r in rows if r['training']['pool']==pool and r['split']==split and history_bucket(r)==bucket]
                review+=rng.sample(group,min(8,len(group)))
    write_jsonl(output/'data-review.jsonl',review)
    metadata=dict(schema='conversation-v4',seed=42,sources=sources(),authoredHash=authored_hash,sampler=CONFIG,
                  sourceCorpusHashes={s:digest((ROOT/f'data/compiled-contextual-v3/{s}.jsonl').read_bytes()) for s in ('train','validation','test')},
                  preparationFiles={name:digest((Path(__file__).parent/name).read_text(encoding='utf-8')) for name in ('common.py','public.py','annotations.py','episodes.py','compile.py','sampling.py')},
                  dataReviewDecisionsHash=digest((ROOT/'data/conversation-v4/public-review-decisions.json').read_bytes()),
                  challengeHash=digest((ROOT/'data/conversation-v4/challenge.json').read_bytes()),
                  samplingHash=digest((output/'sampling.json').read_bytes()),
                  splitHashes={s:digest((output/(s+'.jsonl')).read_bytes()) for s in ('train','validation','test')},
                  preparationStatus=audit['status'],releaseEligible=False)
    (output/'conversation-v4.json').write_text(json.dumps(metadata,sort_keys=True,indent=2))
    print(json.dumps(audit,indent=2))
    if gates: raise SystemExit(1)


if __name__=='__main__': main()
