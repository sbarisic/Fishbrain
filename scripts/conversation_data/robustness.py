"""Paired deterministic variants of the frozen challenge, not additional independent scenarios."""
import argparse
import copy
import json
import subprocess
from pathlib import Path
from common import digest


def variants(suite):
    punctuation=copy.deepcopy(suite)
    short=copy.deepcopy(suite)
    for entry in punctuation:
        case=entry['case'];turn=case['request']['utterances'][-1]
        before=turn['text']
        turn['text']=before[:-1] if before[-1] in '.?!' else before+'.'
        delta=len(turn['text'])-len(before)
        final=case['frames'][-1]
        assert final['start']+final['length']==len(before)
        final['length']+=delta
    for entry in short:
        request=entry['case']['request']
        if len(request['utterances'])==7:
            # Keep the initial relevant exchange and current turn, retaining source sequence IDs.
            # Gold memory, agenda and antecedents still refer to that initial exchange.
            assert all(f.get('antecedent') in (None,0,1) for f in entry['case']['frames'])
            request['utterances']=[request['utterances'][i] for i in (0,1,6)]
    return {'punctuation':punctuation,'irrelevant-history-removal':short}


def main():
    parser=argparse.ArgumentParser();parser.add_argument('--suite',default='data/conversation-v4/challenge.json')
    parser.add_argument('--output',required=True);parser.add_argument('--cli');parser.add_argument('--baseline');parser.add_argument('--pilot')
    args=parser.parse_args();output=Path(args.output);output.mkdir(parents=True,exist_ok=True)
    suite=json.loads(Path(args.suite).read_text())
    for name,items in variants(suite).items():
        target=output/(name+'.json')
        encoded=json.dumps(items,indent=2).encode()
        if target.exists() and target.read_bytes()!=encoded:raise ValueError('Frozen robustness variant changed')
        if not target.exists():target.write_bytes(encoded)
        if not args.cli:continue
        reports={}
        for label,model in [('baseline',args.baseline),('pilot',args.pilot)]:
            result=output/(name+'-'+label+'.json')
            process=subprocess.run(['dotnet',args.cli,'challenge-conversation',model,str(target),str(result)])
            if process.returncode not in (0,1) or not result.exists():raise RuntimeError('Robustness evaluation failed')
            reports[label]=json.loads(result.read_text())
        selected=[i for i,r in enumerate(suite) if name=='punctuation' or len(r['case']['request']['utterances'])==7]
        summary={label:{metric:sum(report['Cases'][i][metric] is True for i in selected)/len(selected)
                       for metric in ('Frame','Plan','Tool')} for label,report in reports.items()}
        (output/(name+'-summary.json')).write_text(json.dumps(dict(rows=len(selected),variantHash=digest(encoded),results=summary,
            interpretation='Deterministic transformations of the frozen suite, not additional independent unseen cases.'),indent=2))


if __name__=='__main__':main()
