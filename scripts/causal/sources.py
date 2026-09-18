"""Pinned public language sources; all writes stay inside the experiment budget."""
import argparse, hashlib, json, os, time, urllib.request
from pathlib import Path

BUDGET=12*1024**3

def usage(root):
    return sum(p.stat().st_size for p in Path(root).rglob('*') if p.is_file())

def json_get(url):
    with urllib.request.urlopen(urllib.request.Request(url,headers={'User-Agent':'Fishbrain-research/1.0'}),timeout=60) as response:
        return json.load(response)

def fetch(url,path,root,expected=None):
    path=Path(path);path.parent.mkdir(parents=True,exist_ok=True)
    if path.exists():
        digest=hashlib.sha256()
        with path.open('rb') as existing:
            while chunk:=existing.read(4*1024**2):digest.update(chunk)
        return digest.hexdigest()
    temporary=path.with_suffix(path.suffix+'.partial')
    if expected and usage(root)+expected>BUDGET:raise RuntimeError('12 GiB experiment storage budget would be exceeded')
    for attempt in range(3):
        try:
            digest=hashlib.sha256()
            with urllib.request.urlopen(urllib.request.Request(url,headers={'User-Agent':'Fishbrain-research/1.0'}),timeout=120) as response,temporary.open('wb') as output:
                while chunk:=response.read(4*1024**2):
                    if usage(root)+len(chunk)>BUDGET:raise RuntimeError('12 GiB experiment storage budget exceeded')
                    output.write(chunk);digest.update(chunk)
            temporary.replace(path)
            return digest.hexdigest()
        except (OSError,TimeoutError):
            if attempt==2:raise
            time.sleep(2)

def main():
    p=argparse.ArgumentParser();p.add_argument('root');a=p.parse_args();root=Path(a.root);root.mkdir(parents=True,exist_ok=True)
    lock=root/'sources.json'
    prior=json.loads(lock.read_text()) if lock.exists() else {}
    for repo,alias in [('Salesforce/wikitext','wikitext'),('roneneldan/TinyStories','tinystories')]:
        entry=prior.get(alias)
        if entry is None:
            revision=json_get('https://huggingface.co/api/datasets/'+repo)['sha']
            files=json_get(f'https://huggingface.co/api/datasets/{repo}/tree/{revision}?recursive=true&limit=1000')
            selected=[f for f in files if f['type']=='file' and (f['path']=='README.md' or
                alias=='wikitext' and f['path'].startswith('wikitext-103-raw-v1/') and f['path'].endswith('.parquet') or
                alias=='tinystories' and f['path'] in ['TinyStoriesV2-GPT4-train.txt','TinyStoriesV2-GPT4-valid.txt'])]
            if len(selected)<3:raise RuntimeError('Missing required source files: '+repo)
            entry=dict(repository=repo,revision=revision,files=selected,
                attribution='Salesforce / Stephen Merity et al.; Wikipedia contributors' if alias=='wikitext' else 'Ronen Eldan and Yuanzhi Li; GPT-4-generated stories',
                licenseNotices='Preserve pinned README; CC-BY-SA and GFDL notices' if alias=='wikitext' else 'CDLA-Sharing-1.0')
            prior[alias]=entry;lock.write_text(json.dumps(prior,indent=2))
        for file in entry['files']:
            path=root/'raw'/alias/file['path'];url=f"https://huggingface.co/datasets/{repo}/resolve/{entry['revision']}/{file['path']}"
            print('FETCH',alias,file['path'],flush=True)
            actual=fetch(url,path,root,file.get('size'));expected=file.get('sha256') or file.get('lfs',{}).get('oid')
            if expected and actual!=expected:raise RuntimeError('Pinned source checksum mismatch: '+str(path))
            file['sha256']=actual;file['local']=str(path.relative_to(root));file['url']=url
            lock.write_text(json.dumps(prior,indent=2))
    print('SOURCES COMPLETE',usage(root),flush=True)

if __name__=='__main__':main()
