"""Observable evaluation, paired exports and reports; old classifier metrics are retired."""
import argparse,collections,hashlib,json,math,random,subprocess,time
from pathlib import Path
from prepare import write_json,sha

def make_auxiliary():
    root=Path('data/causal-v1')
    old=json.loads(Path('data/conversation-v4/acceptance.json').read_text(encoding='utf8'))
    cases=[]
    for row in old['Cases']:
        expected=row['Expected'];utterances=expected['Request']['Utterances'];steps=[]
        for u in utterances:
            if u['Speaker']!=0:continue
            name=expected['ExpectedTool'] if u is utterances[-1] else None;args={}
            if name=='BUY':args=dict(ITEM='ROPE',QUANTITY=1 if expected['Id']=='ordered-compound' else 2)
            if name=='LOOKUP_PRICE':args=dict(ITEM='HEALTH POTION')
            steps.append(dict(player=u['Text'],tool=name,arguments=args,noMutation=name not in ('BUY','SELL')))
        cases.append(dict(id='preserved-'+expected['Id'],category='preserved-acceptance',steps=steps))
    write_json(root/'preserved-acceptance.json',dict(cases=cases,note='Original 17 scenario inputs retained. Old implicit state, frames, agenda and pending-action assertions are retired. Caller memory now requires explicit tool writes.'))
    development=[['hi','who are you','what is your job?',"what's your job?",'how are you?','how much gold do i have?','what do you have for sale?'],
        ['hello','where are we?','What does a rope cost?','Buy 2 rope please.','What is your name?','What should I call you?'],
        ['Hello','How are you?','How did we get here?','Who are you?','Hello. Who are you?'],
        ['Do you have anything for sale?','What do you sell?','where are you?','kill yourself','lmao']]
    tools={'who are you':('READ_PERSONA',{'FIELD':'NAME'}),'what is your job?':('READ_PERSONA',{'FIELD':'OCCUPATION'}),"what's your job?":('READ_PERSONA',{'FIELD':'OCCUPATION'}),
        'how much gold do i have?':('GET_BALANCE',{}),'what do you have for sale?':('LIST_WARES',{}),'where are we?':('GET_CURRENT_LOCATION',{}),
        'what does a rope cost?':('LOOKUP_PRICE',{'ITEM':'ROPE'}),'buy 2 rope please.':('BUY',{'ITEM':'ROPE','QUANTITY':2}),'what is your name?':('READ_PERSONA',{'FIELD':'NAME'}),
        'what should i call you?':('READ_PERSONA',{'FIELD':'NAME'}),'who are you?':('READ_PERSONA',{'FIELD':'NAME'}),'hello. who are you?':('READ_PERSONA',{'FIELD':'NAME'}),
        'do you have anything for sale?':('LIST_WARES',{}),'what do you sell?':('LIST_WARES',{}),'where are you?':('GET_CURRENT_LOCATION',{})}
    cases=[]
    for i,inputs in enumerate(development):
        steps=[]
        for text in inputs:
            name,args=tools.get(text.casefold(),(None,{}));steps.append(dict(player=text,tool=name,arguments=args,noMutation=name not in ('BUY','SELL'),developmentOverlap=True))
        cases.append(dict(id='user-development-'+str(i),category='development',steps=steps))
    write_json(root/'development.json',dict(cases=cases,note='User transcript regressions, not unseen evidence.'))
    for path in [root/'preserved-acceptance.json',root/'development.json']:
        (Path(str(path)+'.sha256')).write_text(sha(path)+'\n')

def legacy_inputs(suite,path):
    rows=[]
    for c in json.loads(Path(suite).read_text(encoding='utf8'))['cases']:
        for i,s in enumerate(c['steps']):rows.append(dict(sessionId=c['id'],turnIndex=i+1,input=s['player'],topicSwitchApplicable=len(c['steps'])>1,memoryApplicable=c['category']=='memory',compoundApplicable=c['category']=='transaction'))
    Path(path).write_text(''.join(json.dumps(r)+'\n' for r in rows))

def derive_invariance(suite,path):
    source=json.loads(Path(suite).read_text(encoding='utf8'));cases=[]
    prefix=['I have been thinking about the journey.','That was just conversation. I have a different question.']
    for case in source['cases']:
        if [s['player'] for s in case['steps'][:2]]==prefix:
            cases.append(dict(case,steps=case['steps'][2:]))
    if len(cases)!=40:raise ValueError('Frozen irrelevant-history subset changed')
    write_json(path,dict(cases=cases,sourceSha256=sha(suite),note='Derived from the frozen challenge by removing only its two explicitly irrelevant opening turns. This is a paired invariance check, not 40 additional independently authored scenarios.'))

def invariance(full,short):
    last={}
    for row in full:last[row['id']]=row
    pairs=[(last[r['id']],r) for r in short if not r.get('developmentOverlap')]
    if any(a['input']!=b['input'] or a['expectedTool']!=b['expectedTool'] or a['expectedArguments']!=b['expectedArguments'] for a,b in pairs):
        raise ValueError('Invariance pair changed the final question or expected action')
    def signature(row):return [(o['name'],o['arguments'],o.get('veto')) for o in (row.get('result') or {}).get('toolOutcomes',[])]
    return dict(applicable=len(pairs),bothToolCorrect=sum(a['exactTool'] is True and b['exactTool'] is True for a,b in pairs),
        bothCompleted=sum(a['completed'] is True and b['completed'] is True for a,b in pairs),
        sameToolSequence=sum(signature(a)==signature(b) for a,b in pairs),
        longHistoryCorrect=sum(a['exactTool'] is True for a,b in pairs),freshCorrect=sum(b['exactTool'] is True for a,b in pairs),
        note='Identical wrong outputs can agree; agreement is reported alongside exact correctness. Development-overlap questions are excluded.')

def summarize(rows):
    def rate(key,subset=None):
        values=[r[key] for r in (rows if subset is None else subset) if r.get(key) is not None]
        return dict(correct=sum(v is True for v in values),applicable=len(values),accuracy=sum(v is True for v in values)/len(values) if values else None)
    times=[r['result']['diagnostics']['milliseconds'] for r in rows if r.get('result')];texts=[r['result']['text'] for r in rows if r.get('result')]
    counts=collections.Counter(texts);category={}
    for c in sorted({r['category'] for r in rows}):
        selected=[r for r in rows if r['category']==c];category[c]=dict(tool=rate('exactTool',selected),completion=rate('completed',selected),memory=rate('memoryCorrect',selected))
    metrics=dict(turns=len(rows),tool=rate('exactTool'),completion=rate('completed'),memory=rate('memoryCorrect'),categories=category,
        unintendedMutations=sum(r['unintendedMutation'] for r in rows),authoritativeAlterations=sum(r['authoritativeAlteration'] for r in rows),
        possibleUnsupportedClaims=sum(r['possibleUnsupportedClaim'] for r in rows),validRequestsUnanswered=sum(r['validRequestUnanswered'] for r in rows),
        unexpectedToolTurns=sum(bool(r.get('unexpectedToolRequest')) for r in rows),unexpectedSuccessfulToolTurns=sum(bool(r.get('unexpectedSuccessfulTool')) for r in rows),
        memoryChangesOutsideExpectedOperations=sum(bool(r.get('memoryChangeOutsideExpectedOperation')) for r in rows),
        errors=sum(r.get('error') is not None for r in rows),repetitionFraction=(len(texts)-len(set(texts)))/len(texts) if texts else None,
        mostCommonResponses=counts.most_common(15),replyP95Milliseconds=sorted(times)[math.ceil(.95*len(times))-1] if times else None)
    metrics['automatedQualityPassed']=all(metrics[k]['accuracy'] is not None and metrics[k]['accuracy']>=threshold for k,threshold in [('tool',.9),('completion',.9),('memory',.95)]) and metrics['unintendedMutations']==0 and metrics['authoritativeAlterations']==0 and metrics['memoryChangesOutsideExpectedOperations']==0
    free=[r['result']['text'] for r in rows if r.get('result') and not any(o.get('result',{}).get('success') for o in r['result'].get('toolOutcomes',[]) if o.get('result'))]
    metrics['freeResponseRepetitionFraction']=(len(free)-len(set(free)))/len(free) if free else None
    resident=[r['replyResidentMiB'] for r in rows if 'replyResidentMiB' in r];allocations=[r['replyAllocatedBytes'] for r in rows if 'replyAllocatedBytes' in r]
    metrics['runtimeResources']=dict(maxSampledIncrementalResidentMiB=max(resident) if resident else None,
        replyAllocatedP95Bytes=sorted(allocations)[math.ceil(.95*len(allocations))-1] if allocations else None,
        note='One loaded Brain, sequential real replies, process resident memory sampled after each reply relative to the pre-load process. Includes retained scratch and execution journals; not a within-call allocation peak.')
    metrics['metricDefinitions']=dict(tool='One proposed call with the exact expected name and complete argument map, without a veto; applicable labeled tool turns only.',
        completion='Exact call succeeds and its authoritative reply is not omitted by the display limit. Social relevance requires conversation review.',
        memory='Expected memory call plus resulting records, ownership, polarity, source evidence and recall content; applicable memory turns only.',
        repetition='Duplicate displayed responses divided by displayed turns. Includes legitimate repeated typed answers; free-response repetition is also reported.',
        latency='End-to-end Brain.Reply time, including packing, constrained decoding and tools; cold first replies are retained.')
    metrics['releaseEligible']=False;metrics['humanReview']='Two independent human reviews remain required';return metrics

def paired(rows,baseline):
    """Paired observations, with conversation-cluster resampling (not independent turns)."""
    lookup={(r['sessionId'],r['turnIndex']-1):r for r in baseline}
    shared={'LIST_WARES','LIST_INVENTORY','GET_BALANCE','LOOKUP_PRICE','GET_CURRENT_LOCATION','LOOKUP_LOCATION','LOOKUP_WORLD_FACT','BUY','SELL'}
    comparisons=[]
    for row in rows:
        old=lookup[(row['id'],row['turn'])]
        if old['input']!=row['input']:raise ValueError('Paired input mismatch')
        if row.get('developmentOverlap'):continue
        comparisons.append((row,old))
    def comparison(metric,selected):
        groups=collections.defaultdict(list)
        for current,old in selected:
            if current.get(metric) is not None and old.get(metric) is not None:
                groups[current['id']].append((int(current[metric] is True),int(old[metric] is True)))
        observations=[v for group in groups.values() for v in group]
        if not observations:return dict(applicable=0)
        rng=random.Random(42);keys=sorted(groups);deltas=[]
        for _ in range(2000):
            sampled=[v for key in rng.choices(keys,k=len(keys)) for v in groups[key]]
            deltas.append(sum(a-b for a,b in sampled)/len(sampled))
        deltas.sort()
        return dict(applicable=len(observations),conversations=len(groups),candidate=sum(a for a,b in observations)/len(observations),
            preservedCandidate=sum(b for a,b in observations)/len(observations),difference=sum(a-b for a,b in observations)/len(observations),
            difference95PercentInterval=[deltas[49],deltas[1949]])
    common=[pair for pair in comparisons if pair[0].get('expectedTool') in shared]
    return dict(sharedGameTools=dict(exactTool=comparison('exactTool',common),completion=comparison('completed',common)),
        allTasks=dict(completion=comparison('completed',comparisons)),
        preservedCandidate=dict(turns=len(baseline),errors=sum(r.get('error') is not None for r in baseline),
            unintendedMutations=sum(r['unintendedMutation'] for r in baseline),
            replyP95Milliseconds=sorted(r['replyMilliseconds'] for r in baseline)[math.ceil(.95*len(baseline))-1]),
        note='Identical player inputs; each runtime builds its own history. Explicit memory tools did not exist in the preserved candidate. Persona task completion is compared by displayed identity; protocol accuracy is only paired for shared game tools. Intervals use 2,000 seed-42 conversation-cluster bootstrap samples; development overlaps are excluded.')

def report(root):
    root=Path(root);rows=[json.loads(l) for l in (root/'pilot-challenge.jsonl').read_text(encoding='utf8').splitlines()];summary=summarize(rows)
    baseline=[json.loads(l) for l in (root/'baseline-review.jsonl').read_text(encoding='utf8').splitlines()];lookup={(r['sessionId'],r['turnIndex']-1):r for r in baseline}
    development_cases={r['id'] for r in rows if r.get('developmentOverlap')}
    summary['unseenOnly']=summarize([r for r in rows if r['id'] not in development_cases])
    summary['developmentOverlapCases']=sorted(development_cases)
    summary['developmentOverlapTurns']=sum(bool(r.get('developmentOverlap')) for r in rows)
    summary['claimScreeningNote']='Possible unsupported claims are lexical review flags, not a factual guarantee. Inspect the actual paired conversations.'
    summary['paired']=paired(rows,baseline)
    short=root/'pilot-invariance.jsonl'
    if short.exists():summary['irrelevantHistoryRemoval']=invariance(rows,[json.loads(line) for line in short.read_text(encoding='utf8').splitlines()])
    write_json(root/'quality.json',summary)
    grouped=collections.defaultdict(list)
    for row in rows:grouped[row['id']].append(row)
    # Deterministic stratified 50 conversations, with no selection based on passing.
    chosen=[]
    buckets=collections.defaultdict(list)
    for key,values in grouped.items():buckets[values[0]['category']].append(key)
    rng=random.Random(42)
    for b in buckets.values():rng.shuffle(b)
    while len(chosen)<min(50,len(grouped)):
        for c in sorted(buckets):
            if buckets[c] and len(chosen)<50:chosen.append(buckets[c].pop())
    exports=[];markdown=['# Fifty paired conversations','', 'Agent inspection is not independent human release approval.','',
        '## Review context','',
        'Each conversation starts a fresh demo world and empty session memory. The caller supplies Arin as the name, traveler as the role, road warden as the occupation, the old mill as home, and this village as origin.',
        '', 'The player starts with 100 gold, 2 rope, and 1 health potion. The location is VILLAGE MARKET. Listed prices are 3 gold for rope, 8 for a health potion, and 25 for an iron sword. The inn is NORTH BY THE FOUNTAIN. Successful transactions change subsequent balances and inventory.',
        '', 'Player reports about the NPC are attributed memories; they do not replace the supplied persona. Judge relevance, continuity, task completion, and invented claims from the whole exchange. Development-overlap questions are marked. Tool/state traces are in pilot-challenge.jsonl.', '']
    for key in chosen:
        turns=[];markdown.extend(['## '+key,''])
        for r in grouped[key]:
            old=lookup[(r['id'],r['turn'])];text=r['result']['text'] if r.get('result') else r.get('error')
            turns.append(dict(player=r['input'],candidate=text,baseline=old['modelResponse'],toolCorrect=r['exactTool'],completed=r['completed'],developmentOverlap=bool(r.get('developmentOverlap'))))
            marker=' *(development-overlap question)*' if r.get('developmentOverlap') else ''
            markdown.extend(['**Player:** '+r['input']+marker,'','**Candidate:** '+str(text),'','**Preserved candidate:** '+old['modelResponse'],''])
        exports.append(dict(id=key,category=grouped[key][0]['category'],turns=turns))
    (root/'review-50.jsonl').write_text(''.join(json.dumps(r,ensure_ascii=False)+'\n' for r in exports),encoding='utf8')
    (root/'conversations-50.md').write_text('\n'.join(markdown),encoding='utf8')
    print(json.dumps(summary,indent=2))

if __name__=='__main__':
    p=argparse.ArgumentParser();p.add_argument('command',choices=['auxiliary','legacy-inputs','derive-invariance','report']);p.add_argument('paths',nargs='*');a=p.parse_args()
    if a.command=='auxiliary':make_auxiliary()
    elif a.command=='legacy-inputs':legacy_inputs(*a.paths)
    elif a.command=='derive-invariance':derive_invariance(*a.paths)
    else:report(*a.paths)
