"""Audit the actual model input, including source history lost during native packing."""
import argparse,collections,hashlib,json,re
from pathlib import Path
from tokenizer import Bpe
from prepare import sha,write_json
from repair_data import ROOT,SOURCE,write_lines,ITEMS

def rows(path):return [json.loads(line) for line in path.read_text(encoding='utf8').splitlines()]
def retained_sequences(tokens,tokenizer,player_only=False):
    result=set();start=0
    while start<len(tokens):
        if tokens[start] not in (4,5,6,7):start+=1;continue
        end=start+1
        while end<len(tokens) and tokens[end]>=16:end+=1
        text=tokenizer.decode(tokens[start+1:end]);match=re.match(r'^(\d+):',text)
        if match and (not player_only or tokens[start]==4):result.add(int(match.group(1)))
        start=end
    return result
def conflicting_calls(records):
    groups=collections.defaultdict(list)
    for i,row in enumerate(records):
        if row['split']=='train':groups[tuple(row['tokens'][:row['promptLength']])].append(i)
    return [ids for ids in groups.values() if len({records[i]['target'] for i in ids if json.loads(records[i]['target'])['type']=='tool_call'})>1]

def audit(records,episodes,tokenizer):
    conflicts=conflicting_calls(records);excluded={i:'CONFLICTING_TOOL_TARGETS_FOR_IDENTICAL_INPUT' for group in conflicts for i in group}
    # These exact strings are authoring annotations in this corpus, not runtime routing rules.
    reference_questions={'Could you repeat that price?','Could you repeat that information?'}
    required={}
    for episode in episodes:
        for target in episode.get('targets',[]):
            decoded=json.loads(target['target'])
            if decoded['type']!='tool_call':continue
            history=target['history'];current=next(m for m in reversed(history) if m['role']=='player')
            if current['text'] not in reference_questions:continue
            previous_player=None;source=None
            for message in history:
                if message is current:break
                if message['role']=='player':previous_player=message['sequence']
                if message['role']=='assistantToolCall' and json.loads(message['text'])==decoded:source=previous_player
            if source is None:raise RuntimeError('Reference annotation has no preceding matching tool exchange')
            required[(episode['id'],current['sequence'],target['target'])]=source
    lost=[]
    for i,row in enumerate(records):
        present=retained_sequences(row['tokens'][:row['promptLength']],tokenizer)
        current_sequence=max(retained_sequences(row['tokens'][:row['promptLength']],tokenizer,player_only=True),default=-1)
        for (id,current,target),source in required.items():
            if id==row['id'] and target==row['target'] and current==current_sequence and source not in present:
                lost.append(dict(row=i,id=id,split=row['split'],sourceSequence=source,retainedSequences=sorted(present),target=target))
                excluded[i]='REQUIRED_REFERENCE_EXCHANGE_DROPPED'
    return conflicts,excluded,lost

def main():
    parser=argparse.ArgumentParser();parser.add_argument('--gate',action='store_true');args=parser.parse_args()
    records=rows(ROOT/'prepared/specialization.jsonl');episodes=rows(ROOT/'prepared/episodes.jsonl')
    tokenizer=Bpe(json.loads((ROOT/'prepared/tokenizer.json').read_text()))
    conflicts,excluded,lost=audit(records,episodes,tokenizer)
    clean=[r for i,r in enumerate(records) if i not in excluded]
    if conflicting_calls(clean):raise RuntimeError('Cleaned rows still conflict')
    coverage=collections.Counter()
    for row in clean:
        target=json.loads(row['target'])
        if row['split']=='train' and target['type']=='tool_call':coverage[(target['name'],target['arguments'].get('ITEM',''))]+=1
    if any(not coverage[action,item] for action in ['BUY','SELL','LOOKUP_PRICE'] for item in ITEMS):raise RuntimeError('Cleaning removed required item/action coverage')
    clean_path=ROOT/'prepared/specialization-clean.jsonl';write_lines(clean_path,clean)
    prior_path=Path('data/training/causal-v1/prepared/specialization.jsonl');prior=rows(prior_path);prior_conflicts=conflicting_calls(prior)
    prior_excluded={i for group in prior_conflicts for i in group};prior_clean=ROOT/'prepared/prior-replay-clean.jsonl'
    write_lines(prior_clean,[r for i,r in enumerate(prior) if r['split']=='train' and i not in prior_excluded])
    report=dict(status='PREPARATION_FAILED' if excluded or prior_conflicts else 'PASS',
        originalCorpusSha256=sha(ROOT/'prepared/specialization.jsonl'),identicalInputConflictGroups=len(conflicts),
        conflictingTrainingRows=sum(map(len,conflicts)),lostReferenceRows=lost,exclusions={str(k):v for k,v in excluded.items()},
        cleanRows=len(clean),cleanCorpusSha256=sha(clean_path),cleanPriorReplaySha256=sha(prior_clean),
        priorConflictGroups=len(prior_conflicts),priorConflictingRows=len(prior_excluded),
        cleanItemActionCoverage={':'.join(k):v for k,v in coverage.items() if k[1]},
        notes=['The completed 400-update diagnostic used the original corpus; the historical weights and evidence are preserved.',
            'Clean files are separately fingerprinted, untrained inputs. They exclude inconsistent tool targets and annotated references whose source exchange was dropped.',
            'Removing training rows cannot recover missing history at inference. End-to-end reference tests remain failures.',
            'The original frozen challenge and its expectations are unchanged. Fix context packing before another full pilot.',
            'Multiple valid social responses are not classified as contradictory tool supervision.'])
    write_json(SOURCE/'packed-input-audit.json',report)
    print(json.dumps({k:report[k] for k in ['status','identicalInputConflictGroups','conflictingTrainingRows','cleanRows','priorConflictingRows']},indent=2))
    if args.gate and report['status']!='PASS':raise SystemExit(1)

if __name__=='__main__':main()
