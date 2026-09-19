"""Package the unpromoted focused candidate and write its measured outcome."""
import json,shutil,subprocess
from pathlib import Path
from prepare import sha,write_json
from repair_data import ROOT,SOURCE,storage

def read(path):return json.loads(path.read_text(encoding='utf8'))
def main():
    result=read(ROOT/'evaluation.json');training=read(ROOT/'result.json');fixture=read(ROOT/'overfit.json');out=SOURCE/'results';audit=read(SOURCE/'packed-input-audit.json')
    for name in ['checkpoint-parity.json','training.log','tests.log','positive-controls.jsonl']:
        shutil.copy2(ROOT/name,out/(name.replace('.log','.txt')))
    package=ROOT/'candidate-package'
    if package.exists():raise RuntimeError('Preserve an existing candidate package instead of overwriting it')
    package.mkdir();original=Path('data/training/causal-v1/candidate-package')
    for path in original.iterdir():
        if path.suffix in ('.dll','.exe','.pdb') or path.name.endswith(('.deps.json','.runtimeconfig.json')):shutil.copy2(path,package/path.name)
    shutil.copytree(original/'source-notices',package/'source-notices')
    shutil.copy2(ROOT/'candidate.fbc',package/'candidate.fbc');shutil.copy2(ROOT/'evaluation.json',package/'evaluation.json')
    (package/'CANDIDATE.txt').write_text('Focused data-intervention check. Not promoted. Full training/release is a separate decision.\n',encoding='utf8')
    with (ROOT/'package-smoke.log').open('w',encoding='utf8') as log:
        subprocess.run(['dotnet',str(package/'Fishbrain.dll'),'chat',str(package/'candidate.fbc'),str(ROOT/'package-smoke.jsonl')],
            input='Hello\nWhat?\nHow much for 1 iron sword?\n\n',text=True,stdout=log,stderr=subprocess.STDOUT,check=True)
    smoke=[json.loads(line) for line in (ROOT/'package-smoke.jsonl').read_text(encoding='utf8').splitlines()]
    if len(smoke)!=3 or any(not r['result']['text'] or len(r['result']['text'])>256 for r in smoke):raise RuntimeError('Packaged chat smoke failed')
    if 'STATE RAPPORT=' in (ROOT/'package-smoke.log').read_text():raise RuntimeError('Retired diagnostics appeared')
    manifest={str(p.relative_to(package)):sha(p) for p in package.rglob('*') if p.is_file()}
    write_json(package/'manifest.json',dict(status='EXPERIMENTAL_NOT_PROMOTED',files=manifest))
    shutil.copy2(package/'manifest.json',out/'package-manifest.json');shutil.copy2(ROOT/'package-smoke.jsonl',out/'package-smoke.jsonl')
    shutil.copy2(ROOT/'package-smoke.log',out/'package-smoke.txt')
    checkpoints=[dict(path=str(p),sha256=sha(p),bytes=p.stat().st_size) for p in ROOT.glob('focused-*.pt')]
    write_json(out/'checkpoints.json',checkpoints)
    focus=result['focusTurns'];pair=result['pairedFocus'];before=result['preservedBefore'];after=result['preservedAfter']
    focused_pass=all(focus[k]['accuracy'] is not None and focus[k]['accuracy']>=.9 for k in ['tool','completion']) and focus['unintendedMutations']==0 and focus['authoritativeAlterations']==0
    write_json(out/'delivery.json',dict(status='EXPERIMENTAL_NOT_PROMOTED',focusedToolGatePassed=focused_pass,releaseEligible=False,
        modelSha256=training['modelSha256'],packageManifestSha256=sha(package/'manifest.json'),combinedStorageBytes=storage(),
        fullTrainingStarted=False,packedInputGate='FAILED_CONTEXT_LOSS_AND_CONTRADICTORY_TARGETS',humanReview='NOT_PERFORMED_AGENT_INSPECTION_ONLY'))
    def metric(s,k):
        r=s[k];return 'not applicable' if r['accuracy'] is None else f"{r['correct']}/{r['applicable']} ({r['accuracy']:.1%})"
    lines=['# Focused data-repair results','',
        '**Experimental candidate; not promoted.** '+('The focused tool gate passed.' if focused_pass else 'The focused tool gate failed.')+' This small check does not establish full release eligibility.','',
        '## What changed','',
        'Added 181 composed tool/repair episodes with complete item/action coverage. The model architecture, tokenizer, prompt packer, native runtime, schemas and authorization remain unchanged. No routing patch was added.','',
        f"The 17-target fitting check passed after {fixture['updates']} updates ({fixture['elapsedSeconds']:.1f} seconds). Its weights were discarded. The separate data intervention started from the preserved causal candidate and stopped after {training['updates']} updates in {training['elapsedSeconds']/60:.2f} minutes.",'',
        '## Packed-input defect found during inspection','',
        'The source-level split checks passed, but the model-input audit found **40 training rows in four groups with identical packed prompts and different tool targets**. Whole-exchange truncation had removed the antecedent. For example, a price repetition request lost its item, and otherwise identical rows required either a sword price or a potion price. The previous corpus also has four conflicting memory rows.','',
        f"The new gate checks packed token prefixes and author-annotated reference dependencies. It excludes {len(audit['exclusions'])} rows across training/validation/test from a separate untrained clean corpus, leaving {audit['cleanRows']:,} rows with full item/action coverage. The original corpus, weights, frozen evaluation and expected answers remain unchanged.",'',
        'This is a data/packing defect, not evidence that additional layers would recover information absent from the input. The next implementation step should compact tool declarations/exchanges or otherwise preserve required context, then regenerate and audit the packed corpus. Merely repeating training is blocked. See [the audit](packed-input-audit.json).','',
        '## Held-out formulation results','',
        'The primary turn is the held-out question/order, excluding repeated controls. Sale questions follow an inventory-stocking purchase. Transaction quantities are 3 in validation/test and 1 or 2 in the new training families. New wording and unseen quantity composition are tested together.','',
        '| Metric | Before | After | Required |','|---|---:|---:|---:|',
        f"| Exact tool and arguments | {pair['exactTool']['baseline']:.1%} | {metric(focus,'tool')} | 90% |",
        f"| Task completion | {pair['completed']['baseline']:.1%} | {metric(focus,'completion')} | 90% |",
        f"| Unintended world mutations, all held-out turns | {result['baselineAll']['unintendedMutations']} | {result['allTurns']['unintendedMutations']} | 0 |",
        f"| Altered authoritative fields, all held-out turns | {result['baselineAll']['authoritativeAlterations']} | {result['allTurns']['authoritativeAlterations']} | 0 |",'',
        f"Refused or unanswered valid requests across all held-out turns: {result['baselineAll']['validRequestsUnanswered']} before, {result['allTurns']['validRequestsUnanswered']} after.",'',
        '| Category | Exact tool/arguments | Completion |','|---|---:|---:|']
    for category,value in focus['categories'].items():lines.append(f"| {category} | {metric(value,'tool')} | {metric(value,'completion')} |")
    lines+=['','Paired 95% intervals for the change resample wording families (2,000 seed-42 draws), keeping entity substitutions together:','']
    for key,label in [('exactTool','Exact tool/arguments'),('completed','Completion')]:
        p=pair[key];lo,hi=p['difference95Interval'];lines.append(f"- {label}: {(p['candidate']-p['baseline'])*100:+.1f} percentage points; interval {lo*100:+.1f} to {hi*100:+.1f}; {p['families']} families.")
    lines+=['','## Previous acceptance and implementation checks','',
        '| Previous 17-case suite | Before | After |','|---|---:|---:|',
        f"| Exact tool/arguments | {metric(before,'tool')} | {metric(after,'tool')} |",
        f"| Completion | {metric(before,'completion')} | {metric(after,'completion')} |",
        f"| Memory operations/state | {metric(before,'memory')} | {metric(after,'memory')} |",'',
        '- C#/GPU full forward and native cached/prefill parity passed with the new weights.',
        '- Exported weights equal the complete final checkpoint bitwise.',
        '- The frozen corpus was independently reconstructed with the same SHA-256.',
        '- The packaged runtime/model completed an actual chat smoke test.',
        '- Packaging smoke checks loading and response bounds, not answer correctness. Its shorter Hello → What? → sword-price sequence still returns a greeting for the price question; inspect package-smoke.jsonl.',
        '- Baseline replay overlapped training; recorded latencies are diagnostic, not a controlled performance comparison. The runtime itself is unchanged.',
        '- Existing full quality/resource requirements and two-human review remain in force. No full pilot was started.','',
        '## Inspect and test','',
        '[All 38 paired conversations](results/conversations.md) · [Machine-readable results](results/evaluation.json) · [Agent inspection](results/inspection.md)','',
        'From the project directory:','',
        '```powershell',
        'dotnet data/training/causal-repair-v1/candidate-package/Fishbrain.dll chat data/training/causal-repair-v1/candidate-package/candidate.fbc',
        '```','',
        'The previous candidate package remains unchanged. Large model/checkpoint files stay outside Git.']
    (SOURCE/'RESULTS.md').write_text('\n'.join(lines)+'\n',encoding='utf8')
    print(json.dumps(read(out/'delivery.json'),indent=2))

if __name__=='__main__':main()
