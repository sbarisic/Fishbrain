"""Unsegmented byte BPE, shared exactly with C#. Literal protocol token spellings are ordinary text."""
import json
from pathlib import Path
from tokenizers import Tokenizer, models, trainers, pre_tokenizers

RESERVED = [f'<reserved_{i}>' for i in range(16)]

def byte_alphabet():
    values = list(range(33,127)) + list(range(161,173)) + list(range(174,256))
    chars = values.copy(); extra = 0
    for b in range(256):
        if b not in values: values.append(b); chars.append(256+extra); extra += 1
    return dict(zip(values,map(chr,chars)))

class Bpe:
    def __init__(self, definition):
        self.definition = definition
        alphabet = byte_alphabet(); self.alphabet = alphabet
        pieces = definition['pieces']; strings = []
        for i, p in enumerate(pieces): strings.append(RESERVED[i] if i < 16 else ''.join(alphabet[b] for b in bytes.fromhex(p)))
        self.tokenizer = Tokenizer(models.BPE(vocab={s:i for i,s in enumerate(strings)}, merges=[(strings[a],strings[b]) for a,b,_ in definition['merges']]))
        self.tokenizer.pre_tokenizer = pre_tokenizers.ByteLevel(add_prefix_space=False,use_regex=False)
        # No added tokens: reserved spellings inside user text cannot acquire control IDs.
    def encode(self,text): return self.tokenizer.encode(text,add_special_tokens=False).ids
    def decode(self,ids): return b''.join(bytes.fromhex(self.definition['pieces'][i]) for i in ids if i>=16).decode('utf8',errors='strict')

def train(texts, path):
    tok = Tokenizer(models.BPE())
    tok.pre_tokenizer = pre_tokenizers.ByteLevel(add_prefix_space=False,use_regex=False)
    trainer = trainers.BpeTrainer(vocab_size=8192, special_tokens=RESERVED, initial_alphabet=pre_tokenizers.ByteLevel.alphabet(),show_progress=True, min_frequency=2)
    tok.train_from_iterator(texts,trainer=trainer)
    data=json.loads(tok.to_str())['model']; inverse={v:k for k,v in data['vocab'].items()}; rev={c:b for b,c in byte_alphabet().items()}
    pieces=['' if i<16 else bytes(rev[c] for c in inverse[i]).hex() for i in range(len(inverse))]
    merges=[]
    for pair in data['merges']:
        a,b=pair if isinstance(pair,list) else pair.split(' ')
        merges.append([data['vocab'][a],data['vocab'][b],data['vocab'][a+b]])
    definition=dict(pieces=pieces,merges=merges)
    Path(path).write_text(json.dumps(definition,separators=(',',':')),encoding='utf8')
    return Bpe(definition)
