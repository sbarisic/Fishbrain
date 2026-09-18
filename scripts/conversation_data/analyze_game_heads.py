"""Diagnostic categorical errors with gold selected memory; not a native release gate."""
import argparse
import collections
import json
from pathlib import Path
import torch
from train_game_lessons import groups,draw,Corpus,RandomState,collate,FishbrainModel,read_fbm


@torch.no_grad()
def main():
    p=argparse.ArgumentParser();p.add_argument('packed');p.add_argument('checkpoint');p.add_argument('output');a=p.parse_args()
    torch.set_num_threads(2)
    header,weights=read_fbm(a.checkpoint)
    report={}
    for split in ('train','validation'):
        corpus=Corpus(a.packed,split);rng=RandomState(42);pools=groups(corpus)
        rows=draw(corpus,pools,rng)+draw(corpus,pools,rng)+draw(corpus,pools,rng)
        model=FishbrainModel(header,weights,corpus.manifest['headSizes']).cuda()
        counts=collections.defaultdict(lambda:[0,0]);examples=[]
        for offset in range(0,len(rows),32):
            selected=rows[offset:offset+32]
            batch=collate(selected,corpus.manifest,'cuda','JointUnderstanding',rng)
            output,_,_=model.understand(batch,teacher=False)
            for i,row in enumerate(selected):
                wrong=[]
                for target in row['targets']:
                    name=target['name'];actual=output[name][i,target['row']].argmax().item()
                    counts[name][0]+=actual==target['label'];counts[name][1]+=1
                    if actual!=target['label']:wrong.append(dict(head=name,expected=target['label'],actual=actual))
                if wrong:examples.append(dict(input=row['input'],errors=wrong))
        report[split]=dict(heads={k:dict(correct=v[0],total=v[1],accuracy=v[0]/v[1]) for k,v in counts.items()},errors=examples)
        del model
    Path(a.output).write_text(json.dumps(report,indent=2))
    for split,result in report.items():
        print(split,[(k,round(v['accuracy'],3)) for k,v in result['heads'].items() if k in
            ('head.knowledgeTarget','frame.0.act','frame.0.tool','frame.0.start','frame.0.end','frame.0.antecedent','frame.0.factSubject','plan.0')])


if __name__=='__main__':main()
