"""Real GPU checks for public freezing, masked labels, deterministic draws, and resume binding."""
import argparse
import copy
import json
import sys
import tempfile
from pathlib import Path

sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'torch_training'))
import torch
from data import Corpus, RandomState, collate
from model import FishbrainModel, read_fbm
from trainer import train_update
from checkpoints import fingerprint,save,restore


def main():
    parser=argparse.ArgumentParser();parser.add_argument('corpus');parser.add_argument('report');args=parser.parse_args()
    torch.set_num_threads(2);torch.manual_seed(42);torch.use_deterministic_algorithms(True)
    corpus=Corpus(args.corpus)
    assert corpus.conversation
    header,weights=read_fbm(Path(args.corpus)/'initial.fbm')
    model=FishbrainModel(header,weights,corpus.manifest['headSizes']).cuda()
    optimizer=torch.optim.AdamW(model.parameters(),lr=.0003,weight_decay=.01,fused=True)
    rng=RandomState(corpus.manifest['initialRandomState'])
    semantic=corpus.batch(40000,'JointUnderstanding',3)
    assert all(r['training']['pool']!='public' for r in semantic)
    train_update(model,optimizer,collate(semantic,corpus.manifest,'cuda','JointUnderstanding',rng),'JointUnderstanding',40000)
    public=corpus.batch(40007,'JointRealization',3)
    assert all(r['training']['pool']=='public' for r in public)
    assert all(not r['targets'] and not r['multi'] and r['teacherFrames'] is None and r['teacherPlan'] is None and r['memoryTargets'] is None for r in public)
    assert all(not r['claimPositive'] and not r['claimNegative'] for r in public)
    focused=[r for i in range(len(corpus.offsets)) if (r:=corpus.row(i))['training']['claimNegativeEligible']]
    assert focused and all(r['claimNegative'] and not r['response'] for r in focused)
    before={shape['Name']:model.p(shape['Name']).detach().clone() for shape in header['Parameters']}
    optimizer_before={shape['Name']:copy.deepcopy(optimizer.state.get(model.p(shape['Name']),{})) for shape in header['Parameters'] if not shape['Name'].startswith('decoder.')}
    batch=collate(public,corpus.manifest,'cuda','JointRealization',rng)
    train_update(model,optimizer,batch,'JointRealization',40007)
    assert any(not torch.equal(model.p(n),v) for n,v in before.items() if n.startswith('decoder.'))
    for name,value in before.items():
        if name.startswith('decoder.'):continue
        assert torch.equal(model.p(name),value),'Public generation changed '+name
        for key,value in optimizer_before[name].items():
            actual=optimizer.state[model.p(name)][key]
            assert torch.equal(actual,value) if isinstance(value,torch.Tensor) else actual==value
    assert [r['training']['exampleId'] for r in public]==[r['training']['exampleId'] for r in corpus.batch(40007,'JointRealization',3)]
    authored=corpus.batch(40017,'JointRealization',3)
    assert all(r['training']['pool']=='authored' for r in authored)
    encoder_before=model.p('encoder.embedding').detach().clone()
    train_update(model,optimizer,collate(authored,corpus.manifest,'cuda','JointRealization',rng),'JointRealization',40017)
    assert not torch.equal(model.p('encoder.embedding'),encoder_before),'Authored realization failed to restore encoder learning'
    binding=fingerprint(args.corpus)
    report=Path(args.report);report.parent.mkdir(parents=True,exist_ok=True)
    with tempfile.TemporaryDirectory(dir=report.parent) as temporary:
        checkpoint=Path(temporary)/'resume.pt'
        save(checkpoint,model,optimizer,rng,40008,binding,'fp32',{},sampler=corpus.manifest['sampler'])
        loss=train_update(model,optimizer,batch,'JointRealization',40008)
        expected={s['Name']:model.p(s['Name']).detach().clone() for s in header['Parameters']}
        restore(checkpoint,model,optimizer,rng,binding,'fp32',corpus.manifest['sampler'])
        resumed=train_update(model,optimizer,batch,'JointRealization',40008)
        assert torch.equal(loss,resumed)
        assert all(torch.equal(model.p(n),v) for n,v in expected.items())
        for changed_binding,sampler in [(binding+'changed',corpus.manifest['sampler']),(binding,'OLD_SAMPLER')]:
            try:restore(checkpoint,model,optimizer,rng,changed_binding,'fp32',sampler)
            except ValueError:pass
            else:raise AssertionError('Incompatible checkpoint resume was accepted')
    report.write_text(json.dumps(dict(passed=True,checks=['masked public supervision','frozen encoder and planner weights',
        'frozen optimizer moments','decoder updated','authored encoder learning restored','explicit negative claim labels retained',
        'deterministic sampler','exact public-batch resume','incompatible binding rejected']),indent=2))
    print(report.read_text())


if __name__=='__main__':main()
