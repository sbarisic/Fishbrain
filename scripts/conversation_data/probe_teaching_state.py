"""Post-training development ablation: hold the dialogue fixed, vary valid state.

This diagnoses sensitivity, not unseen quality. None of these rows is trained on.
"""
import argparse
import copy
import json
import subprocess
import tempfile
from pathlib import Path
from common import read_jsonl, write_jsonl


def main():
    parser=argparse.ArgumentParser()
    parser.add_argument('--cli',required=True)
    parser.add_argument('--model',required=True)
    parser.add_argument('--lessons',required=True)
    parser.add_argument('--report',required=True)
    args=parser.parse_args()
    base=next(r for r in read_jsonl(Path(args.lessons)/'train.jsonl') if r['id']=='TEACHING:arrival:1')
    changes={
        'gold_state':{},
        'friendly_mood':dict(mood='FRIENDLY'),
        'rapport_goal':dict(activeGoals=['RAPPORT']),
        'social_summary':dict(topicSummaries=[dict(topic='SOCIAL',sourceUtterance=0)]),
        'friendly_goal_summary':dict(mood='FRIENDLY',activeGoals=['RAPPORT'],
                                     topicSummaries=[dict(topic='SOCIAL',sourceUtterance=0)]),
    }
    report=[]
    with tempfile.TemporaryDirectory() as temporary:
        for name,state in changes.items():
            row=copy.deepcopy(base)
            row['initialDialogueState'].update(state)
            path=Path(temporary)/'probe.jsonl'
            write_jsonl(path,[row])
            result=subprocess.run(['dotnet',args.cli,'evaluate',str(path),args.model],capture_output=True,text=True,encoding='utf-8')
            try:metrics=json.loads(result.stdout)
            except json.JSONDecodeError:raise RuntimeError(result.stderr or result.stdout)
            report.append(dict(name=name,stateChanges=state,metrics=metrics))
    target=Path(args.report);target.parent.mkdir(parents=True,exist_ok=True)
    target.write_text(json.dumps(dict(baseExample=base,experiments=report,trainedOn=False,
        note='Post-training single-example sensitivity test; same utterances and meaning in every condition. Not an independent evaluation.'),indent=2))
    print(json.dumps([{k:r[k] for k in ('name','stateChanges')}|dict(frame=r['metrics']['FrameExact'],plan=r['metrics']['PlanExact'],
                          knowledge=r['metrics']['Operational']['knowledgeTarget']) for r in report],indent=2))


if __name__=='__main__':main()
