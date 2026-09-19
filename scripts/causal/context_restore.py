"""Rebuild the frozen V2 corpus into another directory without touching old runs."""
import argparse,json,shutil,subprocess
from pathlib import Path
from context_data import SOURCE,DLL,rows,lines
from prepare import sha

def main():
    parser=argparse.ArgumentParser();parser.add_argument('destination',type=Path);args=parser.parse_args()
    root=args.destination
    if root.exists():raise RuntimeError('Use a new destination; existing artifacts are preserved')
    prepared=root/'prepared';prepared.mkdir(parents=True)
    freeze=json.loads((SOURCE/'freeze.json').read_text())
    if sha(SOURCE/'plans.jsonl')!=freeze['plansSha256']:raise RuntimeError('Authored source changed')
    shutil.copy2('data/causal-v1/tokenizer.json',prepared/'tokenizer.json')
    subprocess.run(['dotnet',DLL,'compile-trajectories',SOURCE/'plans.jsonl',prepared/'authored.jsonl'],check=True)
    compiled=rows(prepared/'authored.jsonl')
    replay=[r for prior in ['causal-v1','causal-repair-v1'] for r in rows(f'data/training/{prior}/prepared/episodes.jsonl') if r['split']=='train']
    lines(prepared/'episodes.jsonl',compiled+replay)
    subprocess.run(['dotnet',DLL,'pack-corpus',root,prepared/'all.jsonl'],check=True)
    lines(prepared/'specialization.jsonl',rows(prepared/'all.jsonl'))
    actual=sha(prepared/'specialization.jsonl')
    if actual!=freeze['corpusSha256']:raise RuntimeError('Restored corpus differs: '+actual)
    print('PASS restored corpus SHA256',actual)
if __name__=='__main__':main()
