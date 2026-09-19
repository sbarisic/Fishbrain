"""Package the exact experimental candidate and runtime, with a mandatory real chat smoke."""
import json,shutil,subprocess
from pathlib import Path
from context_data import ROOT,SOURCE,DLL,storage
from prepare import sha,write_json

def main():
    (SOURCE/'results').mkdir(exist_ok=True)
    result=json.loads((ROOT/'result.json').read_text())
    binding=json.loads((ROOT/'binding.json').read_text())
    if result['status']!='COMPLETE_NOT_PROMOTED' or sha(ROOT/'candidate.fbc')!=result['modelSha256']:raise RuntimeError('Candidate mismatch')
    for path,digest in binding['code'].items():
        if sha(path)==digest:continue
        saved={'Fishbrain.CausalCli/Program.cs':'Program.cs','Fishbrain.CausalDemo/Authorization.cs':'Authorization.cs'}
        snapshot=SOURCE/'bound-code'/saved[path.replace('\\','/')] if path.replace('\\','/') in saved else Path(path)
        if sha(snapshot)!=digest:raise RuntimeError('Bound code changed: '+path)
    destination=ROOT/'candidate-package'
    if destination.exists():raise RuntimeError('Preserve the existing package')
    shutil.copytree(ROOT/'tools',destination);shutil.copy2(ROOT/'candidate.fbc',destination/'candidate.fbc')
    trace=ROOT/'package-smoke.jsonl'
    prior_lines=len(trace.read_text(encoding='utf8').splitlines()) if trace.exists() else 0
    text='hi\nwho are you?\nwhat do you have for sale?\nhow much money for iron sword?\nhow much do i have?\nsell me 10 rope\nI would like to buy 10 rope\nWhere are we?\nMy name is Steve\nWhat is my name?\n\n'
    smoke=subprocess.run(['dotnet',destination/'Fishbrain.dll','chat',destination/'candidate.fbc',ROOT/'package-smoke.jsonl'],
        input=text,encoding='utf8',capture_output=True,check=True)
    (ROOT/'package-smoke.txt').write_text(smoke.stdout,encoding='utf8')
    (ROOT/'package-smoke.stderr.txt').write_text(smoke.stderr,encoding='utf8')
    manifest=dict(status='EXPERIMENTAL_NOT_PROMOTED',modelSha256=result['modelSha256'],
        files={p.name:sha(p) for p in sorted(destination.iterdir()) if p.is_file()},smokeSha256=sha(ROOT/'package-smoke.txt'))
    write_json(destination/'manifest.json',manifest)
    shutil.copy2(ROOT/'package-smoke.txt',SOURCE/'results/package-smoke.txt')
    (SOURCE/'results/package-smoke.jsonl').write_text('\n'.join(trace.read_text(encoding='utf8').splitlines()[prior_lines:])+'\n',encoding='utf8')
    write_json(SOURCE/'results/package-manifest.json',manifest);storage()
    print(smoke.stdout)
if __name__=='__main__':main()
