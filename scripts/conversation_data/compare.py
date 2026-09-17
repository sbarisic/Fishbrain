"""Paired scenario results; uncertainty resamples related scenario families together."""
import json
import collections
import numpy as np
from pathlib import Path


def compare(baseline,pilot):
    baseline,pilot=[json.loads(Path(p).read_text()) for p in (baseline,pilot)]
    old={r['Id']:r for r in baseline['Cases']};new={r['Id']:r for r in pilot['Cases']}
    if old.keys()!=new.keys():raise ValueError('Comparison scenarios differ')
    if any(old[k]['Expected']!=new[k]['Expected'] for k in old):raise ValueError('Comparison labels or histories changed')
    paired={}
    rng=np.random.default_rng(42)
    for metric in ('Frame','Plan','Knowledge','Memory','Facts','Agenda','Tool'):
        keys=[k for k in old if old[k].get(metric) is not None and new[k].get(metric) is not None]
        groups={}
        for key in keys:
            category,index=key.rsplit('-',1)
            # Counterfactual memory pairs and their corrections share an uncertainty cluster.
            group=('memory',int(index)//2) if category in ('memory','correction') else ('agenda',index) if category in ('agenda','topic_change') else (category,index)
            groups.setdefault(group,[]).append(key)
        if not groups:continue
        clusters=list(groups.values())
        samples=[]
        for _ in range(5000):
            draw=[key for i in rng.integers(0,len(clusters),size=len(clusters)) for key in clusters[int(i)]]
            samples.append(np.mean([int(new[k][metric])-int(old[k][metric]) for k in draw]))
        paired[metric]=dict(rows=len(keys),baseline=float(np.mean([old[k][metric] for k in keys])),
                            pilot=float(np.mean([new[k][metric] for k in keys])),
                            difference=float(np.mean([int(new[k][metric])-int(old[k][metric]) for k in keys])),
                            pairedClusterBootstrap95=[float(x) for x in np.quantile(samples,[.025,.975])])
    def compact(report):
        result={k:v for k,v in report.items() if k!='Cases'}
        result['UnintendedMutations']=sum(r['UnintendedMutation'] for r in report['Cases'])
        result['AuthorityAlterations']=sum(not r['Authority'] for r in report['Cases'])
        result['GeneratedTextScreenFlags']=sum(r.get('GeneratedTextScreenPassed') is False for r in report['Cases'])
        result['UnsupportedClaimsHumanReview']=None
        sources=['RankedCandidate','RankedVariation','ToolTemplate','PersonaTemplate','CapabilityTemplate',
                 'ClarificationTemplate','Fallback','ConversationalRepair','ConversationalGenerated']
        result['ResponseSourceCounts']=dict(collections.Counter(sources[r['Actual']['Diagnostics']['ResponseSource']] for r in report['Cases']))
        generated=[r['Actual']['Text'] for r in report['Cases'] if r['Actual']['Diagnostics']['ResponseSource']==8]
        result['GeneratedQuestionFraction']=sum('?' in text for text in generated)/len(generated) if generated else None
        result['MostRepeatedResponses']=collections.Counter(r['Actual']['Text'] for r in report['Cases']).most_common(10)
        requested=[r for r in report['Cases'] if r['Expected']['ExpectedTool'] is not None]
        result['ExplicitToolRequestCases']=len(requested)
        result['ExplicitToolRequestSuccess']=sum(r['Tool'] for r in requested)/len(requested) if requested else None
        return result
    return dict(baseline=compact(baseline),pilot=compact(pilot),paired=paired,
                uncertainty='Seed-42 paired cluster bootstrap, 5000 draws; 120 controlled-context cases, not a population guarantee.',
                claimMeasurement='Deterministic final-text screening is reported separately from human unsupported-claim review.',promoted=False)
