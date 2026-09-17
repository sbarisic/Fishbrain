"""Exploratory failure decomposition; never changes release gates or calibration."""
import argparse
import collections
import json
from pathlib import Path


def fact_key(fact):
    return tuple(fact.get(k) for k in ('Subject','Kind','Value','Negated','SourceUtterance','Provenance'))


def analyze(path):
    report=json.loads(Path(path).read_text());cases=report['Cases']
    fields={'bounds':('Start','Length'),'speechActs':('SpeechAct',),'participants':('Subject','Target'),
            'tools':('ToolName',),'antecedents':('Antecedent',),'status':('Status',)}
    components={}
    for name,keys in fields.items():
        components[name]=sum(len(c['Expected']['Frames'])==len(c['Actual']['Contextual']['Frames']) and
            all(tuple(a.get(k) for k in keys)==tuple(b.get(k) for k in keys)
                for a,b in zip(c['Expected']['Frames'],c['Actual']['Contextual']['Frames'])) for c in cases)/len(cases)
    single=[c for c in cases if c['Expected']['SelectedFacts'] is not None and len(c['Expected']['SelectedFacts'])==1]
    top=sum(bool(c['Actual']['Contextual']['Memory']) and fact_key(c['Actual']['Contextual']['Memory'][0]['Fact'])==
            fact_key(c['Expected']['SelectedFacts'][0]) for c in single)
    surplus=[c for c in single if len(c['Actual']['Contextual']['Memory'])>1]
    return dict(frameComponentExact=components,singleRelevantMemoryCases=len(single),
                correctTopRankedMemory=top,selectedExtraMemories=len(surplus),
                extraMemoryExamples=[dict(id=c['Id'],expected=c['Expected']['SelectedFacts'],actual=c['Actual']['Contextual']['Memory']) for c in surplus[:3]],
                note='Exploratory decomposition added after the 45k checkpoint. These diagnostics are not substituted for exact-set memory or exact-frame release metrics.')


if __name__=='__main__':
    parser=argparse.ArgumentParser();parser.add_argument('report');parser.add_argument('output');args=parser.parse_args()
    Path(args.output).write_text(json.dumps(analyze(args.report),indent=2))
