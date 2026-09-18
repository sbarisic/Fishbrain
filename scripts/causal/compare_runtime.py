"""Record the allocation fix against preserved replay evidence, without training."""
import argparse,json
from pathlib import Path
from prepare import sha,write_json

def read(path):return json.loads(path.read_text(encoding='utf8'))
def rows(path):return [json.loads(line) for line in path.read_text(encoding='utf8').splitlines()]
def behavior(row):
    result=row.get('result') or {}
    return {key:row.get(key) for key in ['error','balanceBefore','balanceAfter','inventory','memoryBefore','memoryAfter']} | {
        key:result.get(key) for key in ['text','messagesToAppend','toolOutcomes']}

def main():
    parser=argparse.ArgumentParser();parser.add_argument('root');args=parser.parse_args();root=Path(args.root)
    previous=root/'before-memory-fix';out=Path('data/causal-v1/results');comparisons={}
    for name in ['pilot-challenge','pilot-preserved','pilot-development','pilot-invariance']:
        before=rows(previous/(name+'.jsonl'));after=rows(root/(name+'.jsonl'))
        if [(r['id'],r['turn']) for r in before]!=[(r['id'],r['turn']) for r in after]:
            raise RuntimeError('Replay inputs or ordering differ: '+name)
        changed=[dict(id=a['id'],turn=a['turn']) for a,b in zip(before,after) if behavior(a)!=behavior(b)]
        comparisons[name]=dict(turns=len(after),changed=changed)
    probes={}
    for name in ['kv-memory-probe','kv-bucket-probe','block-memory-probe']:
        probe=rows(root/(name+'.jsonl'));reference=rows(previous/'pilot-challenge.jsonl')[:len(probe)]
        probes[name]=dict(turns=len(probe),maxSampledIncrementalResidentMiB=max(r['replyResidentMiB'] for r in probe),
            changedReplies=sum(behavior(a)!=behavior(b) for a,b in zip(reference,probe)))
    before=read(previous/'quality.json');after=read(root/'quality.json')
    prior_seconds=read(previous/'evaluation-progress.json')['elapsedSeconds']
    final_seconds=read(root/'evaluation-progress.json')['elapsedSeconds']
    report=dict(status='BEHAVIOR_UNCHANGED' if not any(c['changed'] for c in comparisons.values()) else 'BEHAVIOR_CHANGED',
        modelSha256=sha(root/'pilot/specialization-final.fbc'),comparisons=comparisons,probes=probes,
        before=before['runtimeResources'],after=after['runtimeResources'],
        beforeBenchmark=read(previous/'resources.json'),afterBenchmark=read(root/'resources.json'),
        originalEvaluationSeconds=prior_seconds,finalEvaluationSeconds=final_seconds,totalEvaluationSeconds=prior_seconds+final_seconds,
        notes=['The original candidate package and complete replay files remain in before-memory-fix.',
            'No weights, tokenizer, training data, prompt packing, thresholds, or routing were changed after the first evaluation.',
            'Thirty-two-token prefill blocks reuse a bounded KV workspace and avoid full quadratic attention temporaries.',
            'The failed cache-only and shape-bucketing probes remain preserved. RSS samples are process measurements, not allocation peaks.',
            'The timing total covers two full evaluation passes; isolated memory probes and engineering work are additional.'])
    write_json(out/'runtime-comparison.json',report)
    inspection=dict(reviewer='Codex agent',humanReleaseApproval=False,
        inspectedConversations=50,developmentTurns=23,
        sampleSha256=sha(root/'review-50.jsonl'),observations='agent-inspection.md',
        method='Inspected all 50 original paired conversations and 23 development turns; compared final replay behavior after the allocation fix.',
        originalToFinalReplay=report['status'])
    write_json(out/'agent-inspection.json',inspection)
    print(json.dumps(report,indent=2))

if __name__=='__main__':main()
