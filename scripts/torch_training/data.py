"""Canonical packed rows exported by C#; missing supervision remains absent."""
import collections
import functools
import hashlib
import json
import math
from pathlib import Path

import numpy as np
import torch


class RandomState:
    def __init__(self, state):
        self.state = state

    def next(self):
        x = self.state
        x ^= x >> 12
        x ^= (x << 25) & ((1 << 64) - 1)
        x ^= x >> 27
        self.state = x
        return x * 2685821657736338717 & ((1 << 64) - 1)

    def double(self):
        return (self.next() >> 11) / 9007199254740992.0


class Corpus:
    def __init__(self, directory, split="train"):
        self.directory = Path(directory)
        self.manifest = json.loads((self.directory / "manifest.json").read_text())
        self.conversation = self.manifest['format'] == 4 and self.manifest['sampler'] == 'CONVERSATION_V4_BALANCED'
        if not self.conversation and (self.manifest["format"] != 1 or self.manifest["sampler"] != "COPRIME_FAMILY_PERMUTATION_MEMBER_ROTATION"):
            raise ValueError("Unsupported packed corpus schema or sampler")
        sampling = None
        if self.conversation:
            data = (self.directory / 'sampling.json').read_bytes()
            if hashlib.sha256(data).hexdigest() != self.manifest['samplingHash']:
                raise ValueError('Conversation sampler fingerprint mismatch')
            sampling = json.loads(data)
            if sampling['config']['version'] != 4 or sampling['config']['seed'] != 42:
                raise ValueError('Unsupported conversation sampler configuration')
        self.path = self.directory / (split + ".jsonl")
        self.stream = self.path.open("rb")
        digest = hashlib.sha256()
        self.offsets = []
        full = collections.defaultdict(list)
        language = collections.defaultdict(list)
        semantic = collections.defaultdict(lambda: collections.defaultdict(list))
        realization = collections.defaultdict(list)
        while True:
            offset = self.stream.tell()
            line = self.stream.readline()
            if not line:
                break
            digest.update(line)
            row = json.loads(line)
            index = len(self.offsets)
            self.offsets.append(offset)
            key = row["input"].encode("utf-16-be")
            full[row["semanticFamilyId"]].append((key, index))
            if row["projectResponse"]:
                language[row["semanticFamilyId"]].append((key, index))
            if self.conversation:
                meta=row['training']
                pool=meta['pool']
                if pool != 'public' and (row['targets'] or row['multi'] or row['memoryTargets'] is not None):
                    semantic[pool][meta['augmentationFamily']].append((key,index))
                if meta['responseEligible']:
                    mass=sampling['weights'].get(split,{}).get(meta['exampleId'])
                    if mass is None and split == 'train': raise ValueError('Missing training realization mass')
                    realization[pool].append((index, mass if mass is not None else 1.0))
        if digest.hexdigest() != self.manifest["splitHashes"][split]:
            raise ValueError("Packed corpus digest mismatch")
        if len(self.offsets) != self.manifest["counts"][split]:
            raise ValueError("Packed corpus row count mismatch")
        def families(groups):
            return [[i for _, i in sorted(groups[f], key=lambda item: item[0])]
                    for f in sorted(groups, key=lambda key: key.encode("utf-16-be"))]
        self.families = families(full)
        self.language_families = families(language)
        if self.conversation:
            self.semantic = {pool:families(group) for pool,group in semantic.items()}
            if split=='train' and set(self.semantic) != set(sampling['config']['semanticPools']):
                raise ValueError('Required semantic sampling pool is empty')
            self.realization={}
            for pool,group in realization.items():
                indices,masses=zip(*group)
                cumulative=np.cumsum(np.asarray(masses,dtype=float))
                self.realization[pool]=(indices,cumulative/cumulative[-1])
            if set(self.realization) != {'public','authored'}:
                raise ValueError('Required realization pool is empty')

    @functools.lru_cache(maxsize=2048)
    def row(self, index):
        self.stream.seek(self.offsets[index])
        return json.loads(self.stream.readline())

    def batch(self, step, phase, size=32):
        if self.conversation and phase != 'MaskedLanguage':
            rng=RandomState((42 + step * 0x9E3779B97F4A7C15) & ((1<<64)-1) or 42)
            if phase in ('JointRealization','DecoderPolish'):
                # Three of every five realization updates are public. Each batch is homogeneous.
                # Joint realization occupies residues 7,8,9 in the ten-update curriculum.
                ordinal=((step-40000)//10*3 + max(0,(step-40000)%10-7)) if step>=40000 else step
                pool='public' if ordinal % 5 < 3 else 'authored'
                indices,cumulative=self.realization[pool]
                return [self.row(indices[min(len(indices)-1,int(np.searchsorted(cumulative,rng.double())))]) for _ in range(size)]
            pools=sorted(self.semantic)
            result=[]
            for i in range(size):
                families=self.semantic[pools[(step*size+i)%len(pools)]]
                family=families[rng.next()%len(families)]
                result.append(self.row(family[rng.next()%len(family)]))
            return result
        families = self.language_families if phase in ("JointRealization", "DecoderPolish") else self.families
        stride = max(1, 85 % len(families))
        while math.gcd(stride, len(families)) != 1:
            stride += 1
        rows = []
        for ordinal in range(step * 32, step * 32 + size):
            epoch = ordinal // len(families)
            family = families[(ordinal % len(families) * stride + epoch % len(families)) % len(families)]
            rows.append(self.row(family[epoch % len(family)]))
        return rows


def collate(rows, manifest, device, phase, rng):
    def tensor(values, dtype=None):
        return torch.as_tensor(values, dtype=dtype, device=device)

    def padded(sequences, fill=0):
        width = max(1, max(map(len, sequences), default=0))
        data = np.full((len(sequences), width), fill, dtype=np.int64)
        mask = np.zeros((len(sequences), width), dtype=np.bool_)
        for i, sequence in enumerate(sequences):
            data[i, :len(sequence)] = sequence
            mask[i, :len(sequence)] = True
        return data, mask

    def packed(name):
        items = [r[name] for r in rows]
        tokens, mask = padded([r["tokens"] for r in items])
        segments, _ = padded([r["segments"] for r in items])
        current, current_mask = padded([r["current"] for r in items])
        count = max(len(r["utterances"]) for r in items)
        pools = np.zeros((len(rows), count, tokens.shape[1]), dtype=np.float32)
        valid = np.zeros((len(rows), count + 1), dtype=np.bool_)
        valid[:, 0] = True
        for i, item in enumerate(items):
            for u, indices in enumerate(item["utterances"]):
                if not indices:
                    raise ValueError("Empty packed utterance")
                pools[i, u, indices] = 1 / len(indices)
                valid[i, u + 1] = True
        return {"tokens": tensor(tokens), "segments": tensor(segments), "mask": tensor(mask),
                "current": tensor(current), "current_mask": tensor(current_mask),
                "utterance_pool": tensor(pools), "utterance_mask": tensor(valid)}

    result = {"input": packed("inputData"), "rows": rows}
    b = len(rows)
    if phase == "MaskedLanguage":
        positions, labels, weights = [], [], []
        for i, row in enumerate(rows):
            selected = [p for p in row["inputData"]["current"] if rng.double() < .15]
            if not selected:
                current = row["inputData"]["current"]
                selected = [current[rng.next() % len(current)]]
            for position in selected:
                positions.append([i, position])
                labels.append(row["inputData"]["tokens"][position])
                weights.append(1 / (b * len(selected)))
        result["masked_indices"] = tensor(positions)
        result["masked_labels"] = tensor(labels)
        result["masked_weights"] = tensor(weights, torch.float32)
        indices = result["masked_indices"]
        result["input"]["tokens"][indices[:, 0], indices[:, 1]] = manifest["unknownToken"]
        result["input"]["segments"][indices[:, 0], indices[:, 1]] = manifest["maskSegment"]
        return result

    frames = np.full((b, 3, 2), -1, dtype=np.int64)
    plans = np.full((b, 3), -1, dtype=np.int64)
    for i, row in enumerate(rows):
        if row["teacherFrames"] is not None:
            frames[i, :len(row["teacherFrames"])] = np.array(row["teacherFrames"], dtype=np.int64).reshape(-1, 2)
        if row["teacherPlan"] is not None:
            plans[i] = 0
            plans[i, :len(row["teacherPlan"])] = row["teacherPlan"]
    result["teacher_frames"] = tensor(frames)
    result["teacher_plan"] = tensor(plans)
    if phase in ("JointRealization", "DecoderPolish"):
        response, mask = padded([r["responseTargets"] for r in rows], -100)
        decoder, _ = padded([[manifest["bosToken"]] + r["response"][:-1] for r in rows])
        result.update(decoder=tensor(decoder), response=tensor(response), response_weights=tensor(mask / mask.sum(1)[:, None] / b, torch.float32))
        return result

    targets = collections.defaultdict(list)
    multi = collections.defaultdict(list)
    for i, row in enumerate(rows):
        for target in row["targets"]:
            targets[target["name"]].append((i, target["row"], target["label"], target["weight"] / b))
        for target in row["multi"]:
            multi[target["name"]].append((i, target["labels"]))
    result["targets"] = {name: (tensor([t[:3] for t in values]), tensor([t[3] for t in values], torch.float32)) for name, values in targets.items()}
    result["multi"] = {}
    for name, values in multi.items():
        count = manifest["headSizes"][name.removeprefix("head.")]
        labels = np.zeros((len(values), count), dtype=np.float32)
        for i, (_, positives) in enumerate(values):
            labels[i, positives] = 1
        result["multi"][name] = (tensor([i for i, _ in values]), tensor(labels))

    for suffix, key in (("positive", "claimPositive"), ("negative", "claimNegative")):
        tokens, mask = padded([r[key] for r in rows])
        result["claim_" + suffix] = tensor(tokens)
        result["claim_" + suffix + "_mask"] = tensor(mask)
        result["claim_" + suffix + "_valid"] = tensor(mask.any(1), torch.float32)

    # A one-class NONE memory loss is exactly zero and needs no second encoder pass.
    memory = [(i, r) for i, r in enumerate(rows) if r["memoryTargets"] is not None and len(r["facts"]) > 0]
    if memory:
        result["retrieval"] = packed("retrieval")
        fact_count = max(len(r["facts"]) for r in rows)
        fact_length = max(len(f) for r in rows for f in r["facts"])
        facts = np.zeros((b, fact_count, fact_length), dtype=np.int64)
        fact_tokens = np.zeros_like(facts, dtype=np.bool_)
        fact_mask = np.zeros((b, fact_count + 1), dtype=np.bool_)
        fact_mask[:, 0] = True
        memory_targets = []
        for i, row in enumerate(rows):
            for j, fact in enumerate(row["facts"]):
                facts[i, j, :len(fact)] = fact
                fact_tokens[i, j, :len(fact)] = True
                fact_mask[i, j + 1] = True
        for i, row in memory:
            for label in row["memoryTargets"]:
                memory_targets.append((i, label, 1 / (b * len(row["memoryTargets"]))))
        result.update(facts=tensor(facts), fact_token_mask=tensor(fact_tokens), fact_mask=tensor(fact_mask),
                      memory_indices=tensor([[i, label] for i, label, _ in memory_targets]),
                      memory_weights=tensor([w for _, _, w in memory_targets], torch.float32))
    return result
