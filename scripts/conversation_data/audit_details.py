"""Supplementary descriptive audit. Does not change corpus rows or training weights."""
import argparse
import collections
import json
from pathlib import Path
from common import read_jsonl,digest
from sampling import history_bucket


def audit(root):
    root=Path(root)
    rows=[r for split in ('train','validation','test') for r in read_jsonl(root/(split+'.jsonl'))]
    masses=json.loads((root/'sampling.json').read_text())['weights']['train']
    sources=collections.Counter(r['source'] for r in rows)
    source_mass=collections.defaultdict(float)
    history_mass=collections.defaultdict(float)
    patterns=collections.Counter()
    unmatched_spans=0
    masks={}
    for row in rows:
        mass=masses.get(row['id'],0)
        source_mass[row['source']]+=mass
        history_mass[(row['training']['pool'],history_bucket(row))]+=mass
        pool=row['training']['pool']
        masks.setdefault(pool,collections.Counter())
        masks[pool]['rows']+=1
        masks[pool]['responseEligible']+=row['training']['responseEligible']
        masks[pool]['semanticHeadSupervision']+=bool(row.get('supervisedHeads'))
        masks[pool]['frameSupervision']+=(row.get('contextual') or {}).get('frames') is not None
        masks[pool]['memorySupervision']+=(row.get('contextual') or {}).get('relevantFacts') is not None
        masks[pool]['agendaSupervision']+=(row.get('contextual') or {}).get('agenda') is not None
        masks[pool]['claimPositiveSupervision']+=row['training'].get('claimPositiveEligible',False)
        masks[pool]['claimNegativeSupervision']+=row['training'].get('claimNegativeEligible',False)
        text=row['turns'][-1]['text']
        spans=[]
        for slot in row.get('structuredPerception',{}).get('slots',[]):
            start,length=slot['start'],slot['length']
            if text[start:start+length]!=slot['value']:
                start-=row['input'].rfind(text)
            if start<0 or text[start:start+length]!=slot['value']:
                unmatched_spans+=1
                continue
            spans.append(dict(start=start,length=length))
        for frame in (row.get('contextual') or {}).get('frames',[]) or []:
            span=(frame.get('fact') or {}).get('factValueSpan')
            if span:spans.append(dict(start=span['start'],length=span['length']))
        for span in sorted({(s['start'],s['length']) for s in spans},reverse=True):
            start,length=span;text=text[:start]+'VALUE'+text[start+length:]
        patterns[text]+=1
    return dict(corpusMetadataHash=digest((root/'conversation-v4.json').read_bytes()),sourceCounts=sources,
                sourceProportions={k:v/len(rows) for k,v in sources.items()},effectiveRealizationMassBySource=source_mass,
                effectiveRealizationMassByPoolAndHistory={pool+':'+hist:mass for (pool,hist),mass in history_mass.items()},
                supervisionCoverage=masks,distinctWordingPatterns=len(patterns),unmatchedAnnotatedSpans=unmatched_spans,mostCommonWordingPatterns=patterns.most_common(30),
                patternDefinition='Current wording with annotated entity/fact spans replaced by VALUE; descriptive, not evidence of independent scenario diversity.',
                nearDuplicateDefinition='Exact contexts plus word-trigram Jaccard >= .85 with exact verification; short contexts use exact matching. This does not establish semantic novelty.')


if __name__=='__main__':
    parser=argparse.ArgumentParser();parser.add_argument('corpus');parser.add_argument('output');args=parser.parse_args()
    Path(args.output).write_text(json.dumps(audit(args.corpus),indent=2),encoding='utf-8')
