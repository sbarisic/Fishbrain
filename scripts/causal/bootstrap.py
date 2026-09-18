"""Restore the pinned fresh tokenizer and reviewed metadata without replacing another experiment."""
import argparse,hashlib,json,shutil
from pathlib import Path

def main():
    p=argparse.ArgumentParser();p.add_argument('root');a=p.parse_args();root=Path(a.root);(root/'prepared').mkdir(parents=True,exist_ok=True)
    for source,destination in [(Path('data/causal-v1/tokenizer.json'),root/'prepared/tokenizer.json'),(Path('data/causal-v1/sources.json'),root/'sources.json')]:
        if destination.exists() and hashlib.sha256(destination.read_bytes()).digest()!=hashlib.sha256(source.read_bytes()).digest():
            raise RuntimeError('Refusing to overwrite a different tokenizer: '+str(destination))
        if not destination.exists():shutil.copyfile(source,destination)
    print('Pinned tokenizer ready. Prepare/audit the corpus and record data review before training.')
if __name__=='__main__':main()
