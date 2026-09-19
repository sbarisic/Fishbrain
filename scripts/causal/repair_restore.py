"""Restore the frozen focused-check corpus on a fresh checkout; never regenerate the challenge."""
import json,shutil,subprocess
from pathlib import Path
from prepare import sha
from repair_data import ROOT,SOURCE,DLL,write_lines,storage

def main():
    frozen=json.loads((SOURCE/'freeze.json').read_text())
    if sha(SOURCE/'plans.jsonl')!=frozen['plansSha256'] or sha(SOURCE/'challenge.json')!=frozen['challengeSha256']:
        raise RuntimeError('Frozen plans or challenge changed')
    prepared=ROOT/'prepared';corpus=prepared/'specialization.jsonl'
    if corpus.exists():
        if sha(corpus)!=frozen['corpusSha256']:raise RuntimeError('Existing corpus differs; preserve it for investigation')
        print('Frozen corpus already present and verified.');return
    if (ROOT/'progress.json').exists():raise RuntimeError('Missing corpus in an existing run; inspect before rebuilding')
    prepared.mkdir(parents=True,exist_ok=True);shutil.copy2('data/causal-v1/tokenizer.json',prepared/'tokenizer.json')
    subprocess.run(['dotnet',DLL,'compile-trajectories',str(SOURCE/'plans.jsonl'),str(prepared/'authored.jsonl')],check=True)
    compiled=[json.loads(line) for line in (prepared/'authored.jsonl').read_text(encoding='utf8').splitlines()]
    write_lines(prepared/'episodes.jsonl',compiled)
    subprocess.run(['dotnet',DLL,'pack-corpus',str(ROOT),str(corpus)],check=True)
    if sha(corpus)!=frozen['corpusSha256']:raise RuntimeError('Rebuilt corpus failed exact fingerprint comparison')
    storage();print('Frozen corpus restored with exact fingerprint match.')

if __name__=='__main__':main()
