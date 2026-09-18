"""Collect small pilot evidence for Git. Never trains, changes thresholds, or promotes."""
import argparse,json,shutil,time
from pathlib import Path
from prepare import sha,write_json,budget
from evaluation import summarize
from sources import usage,BUDGET

def main():
    parser=argparse.ArgumentParser();parser.add_argument('root');args=parser.parse_args();root=Path(args.root);out=Path('data/causal-v1/results');out.mkdir(exist_ok=True)
    def read(path):return json.loads(Path(path).read_text(encoding='utf8'))
    training=read(root/'pilot/result.json');evaluation=read(root/'evaluation-progress.json')
    if training['status']!='COMPLETE_NOT_PROMOTED' or evaluation['status']!='COMPLETE_NOT_PROMOTED':
        raise RuntimeError('Complete the bounded pilot and evaluation before collecting final evidence')
    budget(root)
    files=['quality.json','resources.json','final-parity.json','gpu-checkpoint-probe.json','machine.json',
        'evaluation-progress.json','review-50.jsonl','conversations-50.md','human-review-unrated.jsonl',
        'pilot-challenge.jsonl','pilot-preserved.jsonl','pilot-development.jsonl','pilot-invariance.jsonl','invariance.json',
        'packaged-smoke.jsonl','packaged-chat.log','old-artifact-rejection.log',
        'final-build.log','final-runtime-tests.log','portable-runtime-tests.log','reporting-tests.log','supplemental-summary.json','supplemental-results.jsonl',
        'preserved-comparison-hashes.json','preserved-binary-chat.log','legacy-adapter-chat.log']
    for name in files:
        source=root/name
        if source.stat().st_size>5*1024**2:raise RuntimeError('Review unexpectedly large evidence before adding it to Git: '+name)
        destination=name[:-4]+'.txt' if name.endswith('.log') else name
        shutil.copy2(source,out/destination)
    for name in ['result.json','validation.json','pause.json','binding.json']:
        shutil.copy2(root/'pilot'/name,out/('training-'+name))
    ancillary={}
    for label in ['preserved','development']:
        rows=[json.loads(line) for line in (root/('pilot-'+label+'.jsonl')).read_text(encoding='utf8').splitlines()]
        ancillary[label]=summarize(rows)
    write_json(out/'ancillary-quality.json',ancillary)
    checkpoints=[]
    for path in sorted((root/'pilot').glob('*.pt')):
        sidecar=read(str(path)+'.json');digest=sha(path)
        if digest!=sidecar['sha256']:raise RuntimeError('Checkpoint checksum failed: '+str(path))
        checkpoints.append(dict(path=str(path),bytes=path.stat().st_size,sha256=digest,elapsed=sidecar['elapsed']))
    write_json(out/'checkpoints.json',checkpoints)
    package=root/'candidate-package';manifest=read(package/'manifest.json')
    for name,digest in manifest['files'].items():
        if sha(package/name)!=digest:raise RuntimeError('Candidate package hash mismatch: '+name)
    shutil.copy2(package/'manifest.json',out/'candidate-package-manifest.json')
    quality=read(root/'quality.json');resources=read(root/'resources.json');language=read('data/causal-v1/language-audit.json')
    comparison=read(out/'runtime-comparison.json') if (out/'runtime-comparison.json').exists() else None
    delivery=dict(status='EXPERIMENTAL_NOT_PROMOTED',trainingFingerprint=training['fingerprint'],updates=training['updates'],
        phaseElapsedSeconds=training['elapsedSeconds'],trainingElapsedSeconds=sum(training['elapsedSeconds']),
        languagePreparationSeconds=language['elapsedSeconds'],finalEvaluationSeconds=evaluation['elapsedSeconds'],
        experimentStorageBytes=usage(root),experimentStorageLimitBytes=BUDGET,
        automatedQualityPassed=quality['unseenOnly']['automatedQualityPassed'],rawGenerationGate=resources['generationGate'],memoryGate=resources['memoryGate'],
        sampledReplyMemoryGate=quality['runtimeResources']['maxSampledIncrementalResidentMiB']<=512,
        humanReview='PENDING_TWO_INDEPENDENT_HUMANS',promoted=False,
        checkpointCount=len(checkpoints),modelSha256=sha(root/'pilot/specialization-final.fbc'),packageManifestSha256=sha(package/'manifest.json'),
        collectedUtc=time.strftime('%Y-%m-%dT%H:%M:%SZ',time.gmtime()),
        notes=['Training elapsed budgets include checkpoint and validation overhead; the requested pause is excluded.',
            'The pause discarded 165 unsaved language updates. Resume retained their elapsed cost and replayed from update 3000.',
            'Preparation timing covers the measured language preparation pass; source download, authoring and review are separate.',
            'Raw 64-token decoding and end-to-end reply latency are different measurements. Read both resource and quality reports.',
            'Agent conversation inspection is separate from the pending human release gate.'])
    if comparison:
        delivery['originalEvaluationSeconds']=comparison['originalEvaluationSeconds']
        delivery['totalEvaluationSeconds']=comparison['totalEvaluationSeconds']
        delivery['runtimeComparison']=comparison['status']
    write_json(out/'delivery.json',delivery)
    def score(value):
        return 'not applicable' if value['accuracy'] is None else f"{value['correct']}/{value['applicable']} ({value['accuracy']:.1%})"
    def percentage(value):return 'not applicable' if value is None else f'{value:.1%}'
    unseen=quality['unseenOnly'];pair=quality['paired']['sharedGameTools'];stress=read(root/'supplemental-summary.json')
    lines=['# Causal pilot results','',
        '**Experimental candidate; not promoted.** The bounded run and its evaluation are complete. '
        +('Automated quality gates passed.' if unseen['automatedQualityPassed'] else '**Automated quality gates failed.**')
        +' Two independent human reviews remain pending.','',
        '## Measured outcome','',
        'The unseen subset excludes both complete conversations containing previously supplied job questions. The full frozen suite and the development transcripts are also retained separately.','',
        '| Metric | Unseen result | Requirement |','|---|---:|---:|',
        f"| Exact tool and arguments | {score(unseen['tool'])} | 90% |",
        f"| Tool task completion | {score(unseen['completion'])} | 90% |",
        f"| Memory operation and state | {score(unseen['memory'])} | 95% |",
        f"| Unintended world mutations | {unseen['unintendedMutations']} | 0 |",
        f"| Altered authoritative fields | {unseen['authoritativeAlterations']} | 0 |",'',
        f"Valid tool requests left unanswered or refused: **{unseen['validRequestsUnanswered']}**. "
        f"Possible unsupported-claim screening flags: **{unseen['possibleUnsupportedClaims']}**. "
        'The latter is an imperfect lexical screen, not proof of factuality. Zero altered fields means the typed renderer preserved tool values; it does not establish that all free prose is grounded.','',
        '## Per-category results','',
        '| Category | Exact tool/arguments | Completion | Memory |','|---|---:|---:|---:|']
    for category,values in unseen['categories'].items():lines.append(f"| {category} | {score(values['tool'])} | {score(values['completion'])} | {score(values['memory'])} |")
    lines+=['','Social relevance is reviewed from actual conversations; it is not counted as a correct tool task merely because no tool was called.','',
        '## Paired comparison','',
        'Both models received identical player turns and built their own conversation histories. Shared game tools are compared below; explicit memory tools did not exist in the preserved candidate.','',
        '| Shared game-tool metric | New candidate | Preserved candidate | Difference, percentage points (95% interval) |',
        '|---|---:|---:|---:|']
    for label,key in [('Exact tool/arguments','exactTool'),('Task completion','completion')]:
        value=pair[key];lo,hi=value['difference95PercentInterval']
        lines.append(f"| {label} | {value['candidate']:.1%} | {value['preservedCandidate']:.1%} | {value['difference']*100:+.1f} ({lo*100:+.1f} to {hi*100:+.1f}) |")
    lines+=['','Intervals use 2,000 seed-42 bootstrap samples of complete conversations. Related wording within the challenge still limits how broadly these results generalize.','',
        f"Displayed-response repetition on unseen turns: {percentage(unseen['repetitionFraction'])}; free-response repetition: {percentage(unseen['freeResponseRepetitionFraction'])}. Repeated typed answers can be legitimate.",'',
        f"The separate 12-conversation stress set scored {score(stress['exactCalls'])} on exact call sequences, {score(stress['completion'])} on completion, and {score(stress['memory'])} on checked memory states. It recorded {stress['unintendedMutations']} unintended world mutations and {stress['unexpectedMemoryChanges']} unexpected memory changes.",'',
        '## Time and resources','',
        f"The fresh seed-42 FP32 run completed **{training['updates']:,} updates**: language and specialization consumed **{training['elapsedSeconds'][0]/60:.1f}** and **{training['elapsedSeconds'][1]/60:.1f} minutes** respectively. Total active training time was **{sum(training['elapsedSeconds'])/3600:.2f} hours**, including validation and checkpoints. The user-requested pause is excluded; its 165 unsaved updates were replayed from the durable update-3,000 checkpoint without resetting elapsed budgets.",'',
        f"Measured language preparation took {language['elapsedSeconds']/60:.1f} minutes. Downloads, authoring and data review are additional preparation work. Final evaluation and packaging took {evaluation['elapsedSeconds']/60:.1f} minutes; they are outside the training cap.",'',
        'CPU: Ryzen 5 3600XT (6 cores/12 threads), Windows 11, .NET 10. GPU training: RX 9070 XT, PyTorch 2.13.0+ROCm 10.0.0.','',
        '| Measurement | Result | Target |','|---|---:|---:|',
        f"| Cold model load | {resources['coldLoadMilliseconds']:.1f} ms | Report only |",
        f"| Prompt processing p95 ({resources['promptTokens']} tokens) | {resources['promptP95Milliseconds']:.1f} ms | Report only |",
        f"| Raw cached 64-token generation p95 | {resources['generation64P95Milliseconds']:.1f} ms | ≤1,000 ms |",
        f"| Incremental steady resident memory | {resources['incrementalResidentMiB']:.1f} MiB | ≤512 MiB |",
        f"| Per-call allocation p95 | {resources['steadyCallAllocatedP95Bytes']/1048576:.1f} MiB | Report only |",
        f"| Four concurrent prompts plus 64-token decodes, total | {resources['concurrentFourMilliseconds']:.1f} ms | Report separately |",
        f"| Actual challenge reply p95, including tools and grammar | {unseen['replyP95Milliseconds']:.1f} ms | Separate end-to-end measurement |",'',
        'Raw decoding excludes prompt processing and schema checks. The complete reply measurement includes them and may contain several tool exchanges. Cold load uses a fresh process without flushing the filesystem cache; twenty steady samples follow one warm-up. The separate understanding/planning metric is retired.','',
        f"During sequential real challenge replies, the maximum sampled incremental resident memory was {quality['runtimeResources']['maxSampledIncrementalResidentMiB']:.1f} MiB and per-reply allocated memory p95 was {quality['runtimeResources']['replyAllocatedP95Bytes']/1048576:.1f} MiB. These samples include retained scratch and execution journals; they do not measure a within-call peak.",'',
        f"Retained experiment storage: **{delivery['experimentStorageBytes']/1024**3:.2f} GiB**, below the 12-GiB cap. {len(checkpoints)} complete checkpoints are indexed with checksums. Existing datasets, candidates and comparison binaries were preserved.",'',
        '## Implementation and data evidence','',
        '- One 17,701,248-parameter causal model; no retired classification heads or learned relationship state.',
        '- C#/GPU forward, gradient, exported-weight and cached-decoding checks passed. Exact checkpoint restoration was probed with discarded updates.',
        '- Runtime checks cover malformed artifacts, schemas, tool budgets, concurrent retries, idempotency, Unicode, quoted evidence and memory ownership.',
        '- Language training has 129,362,702 WikiText and 375,744,555 TinyStories training tokens. Published evaluation splits remain separate.',
        '- The public pool has 2,335 Blended Skill Talk and 15 OpenAssistant episodes. This is a substantial source imbalance.',
        '- The 300 composed authored episodes use 264 families and reusable components. They are not 300 independently collected human conversations; entity and social variety remain limited.',
        '- The packed corpus has 3,882 targets. Packing excluded 127 oversized required exchanges and 23 oversized responses. Approximate deduplication is not an exhaustive proof of uniqueness.',
        '- No automatic extension, full training run, old-weight migration, or model promotion occurred.','',
        '## Inspect and run','',
        '- [Fifty paired conversations](data/causal-v1/results/conversations-50.md)',
        '- [Quality metrics and definitions](data/causal-v1/results/quality.json)',
        '- [Supplemental stress results](data/causal-v1/results/supplemental-results.jsonl)',
        '- [Resource measurements](data/causal-v1/results/resources.json)',
        '- [Delivery and timing record](data/causal-v1/results/delivery.json)',
        '- [Reproduction, migration and human-review instructions](CAUSAL_MODEL.md)','',
        '```powershell',
        'dotnet data/training/causal-v1/candidate-package/Fishbrain.dll chat data/training/causal-v1/candidate-package/candidate.fbc',
        '```','',
        'The package contains matching runtime/model files, source notices and a checksum manifest. Its shipped-candidate smoke test is separate from deliberate rejection of old `.fbm` artifacts. Large checkpoints and datasets remain outside Git.']
    if comparison:
        before=comparison['beforeBenchmark'];after=comparison['afterBenchmark']
        turns=sum(c['turns'] for c in comparison['comparisons'].values())
        lines+=['','## Runtime memory correction','',
            f"The first full evaluation sampled {comparison['before']['maxSampledIncrementalResidentMiB']:.1f} MiB of incremental resident memory, exceeding the 512-MiB target. The final runtime processes prompts in 32-token blocks and reuses an exclusively owned KV cache. The repeated full evaluation sampled {comparison['after']['maxSampledIncrementalResidentMiB']:.1f} MiB.",'',
            f"This trades prompt speed for bounded temporary storage: the same {after['promptTokens']}-token benchmark changed from {before['promptP95Milliseconds']:.1f} to {after['promptP95Milliseconds']:.1f} ms p95. Raw 64-token generation remains separately measured above. No process-wide forced collection is used.",'',
            f"The {turns} replayed turns across challenge, preserved acceptance, development and history-removal suites have comparison status **{comparison['status']}** for displayed text, returned messages, tools, world values and memory. Trained weights and all fingerprint-bound input code remain unchanged. Long-prefix C#/GPU parity also passed.",'',
            f"Both complete evaluation passes took {comparison['totalEvaluationSeconds']/60:.1f} minutes in total; isolated probes and engineering work are additional. The original package and reports remain under `data/training/causal-v1/before-memory-fix`. See [the recorded comparison](data/causal-v1/results/runtime-comparison.json)."]
    lines+=['','## Conversation inspection and conclusion','',
        'I inspected all 50 paired conversations and the 23 development turns. Direct prices and some transactions work, but social replies remain malformed, irrelevant or repetitive. Persona fields are confused after shopping, and corrections often repeat an old value or fail to write memory. The candidate is not ready for game conversations.','',
        'See [the agent inspection](data/causal-v1/results/agent-inspection.md) for concrete examples and limitations. This does not replace either human review. No further training or promotion was started.']
    Path('CAUSAL_RESULTS.md').write_text('\n'.join(lines)+'\n',encoding='utf8')
    print(json.dumps(delivery,indent=2))

if __name__=='__main__':main()
