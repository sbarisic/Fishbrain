"""Fetch only the pinned public conversation sources; preserve verified existing files."""
import hashlib
import json
import urllib.request
from common import ROOT
from public import BST_SHA


def main():
    sources=json.loads((ROOT/'data/sources.json').read_text())['sources']
    files=[f for source in sources if source['name'] in ('OASST1','OASST2') for f in source['files']]
    files.append(dict(path='blended_skill_talk.tar.gz',sha256=BST_SHA,url='https://parl.ai/downloads/blended_skill_talk/blended_skill_talk.tar.gz'))
    (ROOT/'data/raw').mkdir(exist_ok=True,parents=True)
    for item in files:
        destination=ROOT/'data/raw'/item['path']
        if destination.exists():
            if hashlib.sha256(destination.read_bytes()).hexdigest()!=item['sha256']:
                raise ValueError('Existing source checksum mismatch: '+str(destination))
            print('VERIFIED',destination)
            continue
        temporary=destination.with_name(destination.name+'.download')
        digest=hashlib.sha256()
        with urllib.request.urlopen(item['url'],timeout=120) as response,temporary.open('wb') as output:
            while block:=response.read(1024*1024):
                digest.update(block);output.write(block)
        if digest.hexdigest()!=item['sha256']:
            raise ValueError('Downloaded source checksum mismatch: '+str(temporary))
        temporary.replace(destination)
        print('FETCHED AND VERIFIED',destination)


if __name__=='__main__':main()
