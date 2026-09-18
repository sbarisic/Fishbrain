"""Causal V1 layout shared with the dependency-free C# runtime. No classification heads."""
import hashlib, json, math, struct
from pathlib import Path
import torch
from torch import nn
from torch.nn import functional as F

DEFAULT = dict(layers=8, width=384, heads=6, feedForward=1536, context=1024, vocabulary=8192, seed=42)
MAGIC = b'FISHBRAIN CAUSAL V1\n'
ARCHITECTURE = 'CAUSAL_RMS_GELU_TIED_BPE_V1'

def layout(c):
    d, f = c['width'], c['feedForward']
    yield 'token', c['vocabulary'], d
    yield 'position', c['context'], d
    for i in range(c['layers']):
        for n in ('norm1', 'norm2'): yield f'layers.{i}.{n}', 1, d
        for n in ('q', 'k', 'v', 'o'): yield f'layers.{i}.{n}', d, d
        yield f'layers.{i}.up', d, f
        yield f'layers.{i}.down', f, d
    yield 'norm', 1, d

class Model(nn.Module):
    def __init__(self, config=DEFAULT):
        super().__init__()
        self.config = dict(config)
        self.weights = nn.ParameterDict()
        for name, r, c in layout(config):
            p = torch.ones(r, c) if 'norm' in name else torch.randn(r, c) * .02
            self.weights[name.replace('.', '_')] = nn.Parameter(p)

    def p(self, name): return self.weights[name.replace('.', '_')]
    @staticmethod
    def norm(x, scale): return x * torch.rsqrt(x.square().mean(-1, keepdim=True) + 1e-5) * scale

    def forward(self, ids):
        c = self.config
        x = F.embedding(ids, self.p('token')) + self.p('position')[:ids.shape[1]]
        for i in range(c['layers']):
            prefix = f'layers.{i}.'
            n = self.norm(x, self.p(prefix + 'norm1'))
            q, k, v = [(n @ self.p(prefix + t)).view(ids.shape[0], ids.shape[1], c['heads'], c['width']//c['heads']).transpose(1, 2) for t in ('q','k','v')]
            a = F.scaled_dot_product_attention(q, k, v, is_causal=True).transpose(1,2).contiguous().view_as(x)
            x = x + a @ self.p(prefix + 'o')
            n = self.norm(x, self.p(prefix + 'norm2'))
            x = x + F.gelu(n @ self.p(prefix+'up'), approximate='tanh') @ self.p(prefix+'down')
        return self.norm(x, self.p('norm')) @ self.p('token').T

    def loss(self, ids, targets):
        return F.cross_entropy(self(ids).flatten(0,1), targets.flatten(), ignore_index=-100)

def export(path, model, tokenizer, tools, fingerprint, updates, phase, training):
    header = dict(architecture=ARCHITECTURE, config=model.config, tokenizer=tokenizer, tools=tools,
                  parameters=[dict(name=n,rows=r,columns=c) for n,r,c in layout(model.config)],
                  trainingFingerprint=fingerprint, updates=updates, phase=phase, training=training)
    raw = json.dumps(header, ensure_ascii=True, separators=(',',':')).encode()
    path = Path(path); temporary = path.with_suffix('.partial'); sha = hashlib.sha256()
    with temporary.open('wb') as f:
        def write(b): f.write(b); sha.update(b)
        write(MAGIC); write(struct.pack('<i', len(raw))); write(raw)
        for name, _, _ in layout(model.config): write(model.p(name).detach().cpu().numpy().astype('<f4').tobytes())
        f.write(sha.digest())
    temporary.replace(path)

def load_artifact(path, device='cpu'):
    raw = Path(path).read_bytes()
    if raw[:len(MAGIC)] != MAGIC or hashlib.sha256(raw[:-32]).digest() != raw[-32:]: raise ValueError('Not an intact causal V1 artifact')
    size = struct.unpack_from('<i',raw,len(MAGIC))[0]; start = len(MAGIC)+4
    h = json.loads(raw[start:start+size]); model = Model(h['config']); start += size
    import numpy as np
    with torch.no_grad():
        for n,r,c in layout(h['config']):
            count = r*c*4
            model.p(n).copy_(torch.from_numpy(np.frombuffer(raw[start:start+count],dtype='<f4').copy().reshape(r,c)))
            start += count
    if start != len(raw)-32: raise ValueError('Layout mismatch')
    return model.to(device), h
