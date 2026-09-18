"""Prepare a separate causal corpus. Never modifies prior datasets or model artifacts."""
import argparse, collections, gzip, hashlib, json, re, tarfile, time, unicodedata
from pathlib import Path
import numpy as np
import pyarrow.parquet as pq
from tokenizer import Bpe, train
from sources import usage, BUDGET

def sha(path):
    h=hashlib.sha256()
    with open(path,'rb') as f:
        while block:=f.read(4*1024**2): h.update(block)
    return h.hexdigest()
def norm(s): return ' '.join(s.upper().split())
def legacy_key(text):
    # Lookup only: reproduce the old compiler's lossy normalization to find the
    # original source string. Returned training text is never normalized this way.
    translate=str.maketrans({'‘':"'",'’':"'",'“':'"','”':'"','‐':'-','‑':'-','‒':'-','–':'-','—':'-'})
    raw=unicodedata.normalize('NFD',text).translate(translate).upper();out=[];space=False;quoted=False
    for c in raw:
        if unicodedata.category(c)=='Mn':continue
        if c.isspace():space=bool(out);continue
        if c not in 'ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789.,?!\'"-:':continue
        if c in '.?!':
            if out and out[-1]==' ':out.pop()
            if out and out[-1] in '.?!':out[-1]='?' if '?' in (out[-1],c) else '!' if '!' in (out[-1],c) else '.'
            else:out.append(c)
            space=True;continue
        if c in ',:':
            if out and out[-1]==' ':out.pop()
            if not out or out[-1]!=c:out.append(c)
            space=True;continue
        if c=='"':
            if not quoted and space and out:out.append(' ')
            elif quoted and out and out[-1]==' ':out.pop()
            out.append(c);quoted=not quoted;space=not quoted;continue
        if c in "'-":
            if out and out[-1]==' ':out.pop()
            out.append(c);space=False;continue
        if space and out and out[-1] not in "'-":out.append(' ')
        out.append(c);space=False
    return ''.join(out)
def digest(s): return hashlib.sha256(norm(s).encode()).hexdigest()
def write_json(path,value):
    path=Path(path);temporary=path.with_name(path.name+'.partial')
    temporary.write_text(json.dumps(value,ensure_ascii=False,indent=2),encoding='utf8');temporary.replace(path)
def budget(root,extra=0):
    if usage(root)+extra>BUDGET: raise RuntimeError('12 GiB experiment budget exceeded; existing artifacts retained')

def documents(root, source, split):
    if source=='wikitext':
        chunks=[]
        for path in sorted((root/'raw/wikitext/wikitext-103-raw-v1').glob(split+'-*.parquet')):
            for batch in pq.ParquetFile(path).iter_batches(columns=['text']):
                for text in batch.column(0).to_pylist():
                    if re.match(r'^\s*= [^=].*[^=] =\s*$',text) and chunks:
                        yield ''.join(chunks); chunks=[]
                    chunks.append(text)
        if chunks: yield ''.join(chunks)
    elif split!='test':
        path=root/'raw/tinystories'/('TinyStoriesV2-GPT4-'+('valid' if split=='validation' else 'train')+'.txt')
        chunks=[]
        with path.open(encoding='utf8') as f:
            for line in f:
                if '<|endoftext|>' in line:
                    before,after=line.split('<|endoftext|>',1);chunks.append(before)
                    if ''.join(chunks).strip():yield ''.join(chunks).strip()
                    chunks=[after]
                else:chunks.append(line)
        if ''.join(chunks).strip():yield ''.join(chunks).strip()

def public_rows():
    # Restore original casing from raw files without changing prior eligibility or splits.
    needed=set(); rows=[]
    for split in ('train','validation','test'):
        for line in Path(f'data/compiled-conversation-v4-release/{split}.jsonl').open(encoding='utf8'):
            r=json.loads(line); meta=r.get('training',{})
            if meta.get('pool')!='public' or not meta.get('responseEligible'):continue
            needed.add(norm(r['response']));needed.update(norm(t['text']) for t in r['turns']);rows.append((split,r))
    originals={}
    def remember(s):
        if isinstance(s,str):
            key=legacy_key(s)
            if key in needed:originals.setdefault(key,s)
    def walk(v):
        if isinstance(v,str):remember(v)
        elif isinstance(v,list):
            for x in v:walk(x)
        elif isinstance(v,dict):
            for x in v.values():walk(x)
    for name in ('oasst1.jsonl.gz','oasst2-ready.jsonl.gz'):
        with gzip.open('data/raw/'+name,'rt',encoding='utf8') as f:
            for line in f:remember(json.loads(line).get('text'))
    with tarfile.open('data/raw/blended_skill_talk.tar.gz') as tar:
        for member in tar:
            if member.isfile() and member.name.endswith('.json'):
                with tar.extractfile(member) as f:walk(json.load(f))
    for split,r in rows:
        texts=[t['text'] for t in r['turns']]+[r['response']]
        if any(norm(t) not in originals for t in texts):continue
        messages=[dict(role='player' if t['speaker']=='PLAYER' else 'assistant',text=originals[norm(t['text'])],sequence=i) for i,t in enumerate(r['turns'])]
        messages.append(dict(role='assistant',text=originals[norm(r['response'])],sequence=len(messages)))
        yield dict(id=r['id'],family=r['training']['episodeId'],split=split,pool='public',messages=messages,provenance=r['training'])

def main():
    p=argparse.ArgumentParser();p.add_argument('root');p.add_argument('--tokenizer-only',action='store_true');a=p.parse_args();root=Path(a.root)
    start=time.monotonic();out=root/'prepared';out.mkdir(exist_ok=True);budget(root)
    public=list(public_rows());write_json(out/'public-restored.json',public)
    authored=Path('data/causal-v1/authored.jsonl')
    episodes=public+([json.loads(l) for l in authored.read_text(encoding='utf8').splitlines()] if authored.exists() else [])
    def training_texts():
        for row in episodes:
            if row['split']=='train':
                for m in row['messages']:yield m['text']
        # Deterministic sample from training documents only, alternating both sources.
        streams=[iter(documents(root,s,'train')) for s in ('wikitext','tinystories')];size=[0,0]
        while min(size)<24*1024**2:
            for i,stream in enumerate(streams):
                if size[i]>=24*1024**2:continue
                text=next(stream);size[i]+=len(text.encode());yield text
    tp=out/'tokenizer.json'
    tokenizer=Bpe(json.loads(tp.read_text())) if tp.exists() else train(training_texts(),tp)
    if len(tokenizer.definition['pieces'])!=8192:raise RuntimeError('Tokenizer did not reach 8192 entries')
    if a.tokenizer_only:return
    # Hold out published evaluation documents first. Whole-document near-duplicate detection
    # uses 64-bit SimHash and four 16-bit bands, with an explicit Hamming <=3 check.
    exact={};bands=collections.defaultdict(list);excluded=collections.Counter();counts={}
    def signature(text):
        words=re.findall(r'\w+',text.casefold());grams={' '.join(words[i:i+5]) for i in range(max(1,len(words)-4))}
        accum=[0]*64
        for gram in grams:
            value=int.from_bytes(hashlib.blake2b(gram.encode(),digest_size=8).digest(),'little')
            for bit in range(64):accum[bit]+=1 if value>>bit&1 else -1
        return sum(1<<i for i,v in enumerate(accum) if v>=0)
    # Efficient document-level signatures sample up to 128 evenly spaced shingles for long articles.
    def sig(text):
        words=re.findall(r'\w+',text.casefold()); stride=max(1,(len(words)-4)//128)
        hashes=[hashlib.blake2b(' '.join(words[i:i+5]).encode(),digest_size=8).digest() for i in range(0,max(1,len(words)-4),stride)]
        bits=np.unpackbits(np.frombuffer(b''.join(hashes),dtype=np.uint8).reshape(-1,8),axis=1,bitorder='little').sum(axis=0)
        return int.from_bytes(np.packbits(bits*2>=len(hashes),bitorder='little').tobytes(),'little')
    for split in ('test','validation','train'):
        for source in ('wikitext','tinystories'):
            key=source+'-'+split; count=tokens=0
            with (out/(key+'.bin')).open('wb') as f,(out/(key+'.index.jsonl')).open('w',encoding='utf8') as index:
                for ordinal,text in enumerate(documents(root,source,split)):
                    if not text.strip():continue
                    h=digest(text)
                    if h in exact:excluded['exact_document']+=1;continue
                    s=sig(text); candidates=set()
                    for b in range(4):candidates.update(bands[(b,(s>>(16*b))&65535)])
                    if any((s^other).bit_count()<=3 for other in candidates):excluded['near_document']+=1;continue
                    exact[h]=key
                    for b in range(4):bands[(b,(s>>(16*b))&65535)].append(s)
                    ids=[1,9]+tokenizer.encode(text)+[2]
                    if len(ids)<8:excluded['short_document']+=1;continue
                    if count%1000==0:budget(root,2*len(ids));print(key,count,tokens,flush=True)
                    f.write(np.asarray(ids,dtype='<u2').tobytes());index.write(json.dumps(dict(id=f'{source}:{split}:{ordinal}',hash=h,offset=tokens,length=len(ids)))+'\n')
                    tokens+=len(ids);count+=1
            counts[key]=dict(documents=count,tokens=tokens)
    write_json(out/'language-audit.json',dict(counts=counts,exclusions=excluded,elapsedSeconds=time.monotonic()-start,
        nearDuplicateMethod='64-bit SimHash over sampled word shingles; Hamming <=3; four bands. Approximate detection, not an exhaustive similarity proof.',
        bytes=usage(root),tokenizerTrainOnly=True,tokenizerHash=sha(tp)))
    print('LANGUAGE PREPARATION COMPLETE',flush=True)
if __name__=='__main__':main()
