"""GPU implementation of the current Fishbrain architecture, with canonical C# weight names."""
import hashlib
import json
import os
import struct
from pathlib import Path

import numpy as np
import torch
from torch import nn
from torch.nn import functional as F

MAGIC = b"FISHBRAIN CONTEXT\n"


def read_fbm(path):
    raw = Path(path).read_bytes()
    if raw[:len(MAGIC)] != MAGIC or hashlib.sha256(raw[:-32]).digest() != raw[-32:]:
        raise ValueError("Invalid Fishbrain checkpoint signature or digest")
    size = struct.unpack_from("<i", raw, len(MAGIC))[0]
    offset = len(MAGIC) + 4
    header = json.loads(raw[offset:offset + size])
    offset += size
    weights = {}
    for shape in header["Parameters"]:
        count = shape["Rows"] * shape["Columns"]
        weights[shape["Name"]] = np.frombuffer(raw, dtype="<f4", count=count, offset=offset).copy().reshape(shape["Rows"], shape["Columns"])
        offset += count * 4
    expected = offset + (2 * sum(x.size for x in weights.values()) * 4 if header["Training"] else 0) + 32
    if expected != len(raw):
        raise ValueError("Checkpoint tensor length mismatch")
    return header, weights


def phase_at(step):
    if step < 40000:
        return "MaskedLanguage"
    if step >= 220000:
        return "DecoderPolish"
    return "JointUnderstanding" if (step - 40000) % 10 < 7 else "JointRealization"


def export_fbm(model, path, step, thresholds=None):
    header = dict(model.header)
    header.update(Training=False, CompletedSteps=step, RandomState=0, ParameterUpdates={},
                  NextPhase="COMPLETE" if step == 260000 else phase_at(step),
                  ExecutionThresholds=thresholds or {})
    encoded = json.dumps(header, separators=(",", ":"), ensure_ascii=True).encode("utf-8")
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(path.name + ".tmp")
    digest = hashlib.sha256()
    with temporary.open("wb") as stream:
        def write(data):
            digest.update(data)
            stream.write(data)
        write(MAGIC + struct.pack("<i", len(encoded)) + encoded)
        for shape in header["Parameters"]:
            values = model.p(shape["Name"]).detach().float().cpu().numpy()
            if not np.isfinite(values).all():
                raise ValueError("Nonfinite exported weights")
            write(values.astype("<f4", copy=False).tobytes())
        stream.write(digest.digest())
        stream.flush()
        os.fsync(stream.fileno())
    temporary.replace(path)


class FishbrainModel(nn.Module):
    def __init__(self, header, weights, head_sizes):
        super().__init__()
        self.header = header
        self.config = header["Config"]
        self.head_sizes = head_sizes
        self.weights = nn.ParameterDict({name.replace(".", "__"): nn.Parameter(torch.from_numpy(values.copy()))
                                        for name, values in weights.items()})

    def p(self, name):
        return self.weights[name.replace(".", "__")]

    @staticmethod
    def norm(x):
        return x * torch.rsqrt(x.float().square().mean(-1, keepdim=True) + 1e-5).to(x.dtype)

    @staticmethod
    def attention(query, key, value, heads, mask=None, causal=False):
        batch, qlen, width = query.shape
        klen = key.shape[1]
        q = query.reshape(batch, qlen, heads, width // heads).transpose(1, 2)
        k = key.reshape(batch, klen, heads, width // heads).transpose(1, 2)
        v = value.reshape(batch, klen, heads, width // heads).transpose(1, 2)
        allowed = None if mask is None else mask[:, None, None, :]
        if causal:
            triangle = torch.ones(qlen, klen, dtype=torch.bool, device=q.device).tril()
            allowed = triangle if allowed is None else allowed & triangle
        output = F.scaled_dot_product_attention(q, k, v, attn_mask=allowed)
        return output.transpose(1, 2).reshape(batch, qlen, width)

    def layer(self, x, prefix, mask, memory=None, memory_mask=None, causal=False):
        def attend(kind, query, source, source_mask, autoregressive=False):
            return self.attention(query @ self.p(prefix + "." + kind + ".q"),
                                  source @ self.p(prefix + "." + kind + ".k"),
                                  source @ self.p(prefix + "." + kind + ".v"),
                                  self.config["Heads"], source_mask, autoregressive) @ self.p(prefix + "." + kind + ".o")
        norm = self.norm(x)
        x = x + attend("self", norm, norm, mask, causal)
        if memory is not None:
            x = x + attend("cross", self.norm(x), memory, memory_mask)
        return x + F.relu(self.norm(x) @ self.p(prefix + ".in")) @ self.p(prefix + ".out")

    def encode(self, packed):
        tokens, segments, mask = packed["tokens"], packed["segments"], packed["mask"]
        x = F.embedding(tokens, self.p("encoder.embedding"))
        x = x + self.p("encoder.position")[:tokens.shape[1]] + F.embedding(segments, self.p("encoder.segment"))
        for layer in range(self.config["EncoderLayers"]):
            x = self.layer(x, f"encoder.layer{layer}", mask)
        return self.norm(x)

    @staticmethod
    def gather(x, indices):
        return x.gather(1, indices[..., None].expand(-1, -1, x.shape[-1]))

    @staticmethod
    def pointer(query, entries, mask):
        return (query @ entries.transpose(1, 2)).masked_fill(~mask[:, None, :], -1e9)

    def understand(self, batch, teacher=True):
        packed = batch["input"]
        encoded = self.encode(packed)
        current = self.gather(encoded, packed["current"])
        b = encoded.shape[0]
        output = {}
        for name in self.head_sizes:
            query = self.p(f"head.{name}.query").expand(b, -1, -1)
            output["head." + name] = self.attention(query, current, current, 1, packed["current_mask"]) @ self.p(f"head.{name}.output")
        antecedents = torch.cat([self.p("head.antecedent.none").expand(b, -1, -1), packed["utterance_pool"].to(encoded.dtype) @ encoded], dim=1)
        pointer_query = self.attention(self.p("head.antecedent.query").expand(b, -1, -1), current, current, 1, packed["current_mask"])
        output["antecedents"] = self.pointer(pointer_query, antecedents, packed["utterance_mask"])
        frame_states = []
        previous = None
        for i in range(3):
            query = self.p("frame.query")[i:i + 1].expand(b, -1, -1)
            if previous is not None:
                query = query + previous
            frame = self.attention(query, encoded, encoded, self.config["Heads"], packed["mask"])
            prefix = f"frame.{i}."
            for field in ("active", "act", "subject", "target", "status", "tool", "factAct", "factKind", "factPolarity", "factSubject", "factTarget"):
                output[prefix + field] = frame @ self.p("frame." + field)
            for field in ("start", "end"):
                output[prefix + field] = self.pointer(frame @ self.p("frame." + field), current, packed["current_mask"])
            output[prefix + "antecedent"] = self.pointer(frame @ self.p("frame.antecedent"), antecedents, packed["utterance_mask"])
            output[prefix + "factSpans"] = (current + frame.expand(-1, current.shape[1], -1)) @ self.p("frame.factSpans")
            status = output[prefix + "status"].argmax(-1)
            tool = output[prefix + "tool"].argmax(-1)
            if teacher:
                given = batch["teacher_frames"][:, i]
                status = torch.where(given[:, :1] >= 0, given[:, :1], status)
                tool = torch.where(given[:, 1:] >= 0, given[:, 1:], tool)
            previous = frame + F.embedding(status, self.p("frame.status.embedding")) + F.embedding(tool, self.p("frame.tool.embedding"))
            frame_states.append(previous)
        memory = torch.cat([encoded, *frame_states], dim=1)
        memory_mask = F.pad(packed["mask"], (0, 3), value=True)
        previous = None
        plan_states = []
        for i in range(3):
            query = self.p("planner.query")[i:i + 1].expand(b, -1, -1)
            if previous is not None:
                query = query + previous
            state = self.attention(query, memory, memory, self.config["Heads"], memory_mask)
            output[f"plan.{i}"] = state @ self.p("planner.output")
            output[f"planFrame.{i}"] = state @ self.p("planner.frame")
            act = output[f"plan.{i}"].argmax(-1)
            if teacher:
                given = batch["teacher_plan"][:, i:i + 1]
                act = torch.where(given >= 0, given, act)
            previous = state + F.embedding(act, self.p("planner.act.embedding"))
            plan_states.append(previous)
        memory = torch.cat([memory, *plan_states], dim=1)
        memory_mask = F.pad(memory_mask, (0, 3), value=True)
        previous = None
        for i in range(4):
            query = self.p("agenda.query")[i:i + 1].expand(b, -1, -1)
            if previous is not None:
                query = query + previous
            previous = self.attention(query, memory, memory, self.config["Heads"], memory_mask)
            for field in ("kind", "status", "subject"):
                output[f"agenda.{i}.{field}"] = previous @ self.p("agenda." + field)
        output["slots"] = current @ self.p("head.slots")
        output["factSpans"] = current @ self.p("head.factSpans")
        return output, memory, memory_mask

    def decode(self, tokens, memory, memory_mask):
        x = F.embedding(tokens, self.p("decoder.embedding")) + self.p("decoder.position")[:tokens.shape[1]]
        for layer in range(self.config["DecoderLayers"]):
            x = self.layer(x, f"decoder.layer{layer}", None, memory, memory_mask, causal=True)
        return self.norm(x) @ self.p("decoder.output")

    def claim(self, tokens, token_mask, memory, memory_mask):
        response = F.embedding(tokens, self.p("encoder.embedding"))
        query = self.p("claim.query") + (response * token_mask[..., None]).sum(1, keepdim=True) / token_mask.sum(1)[:, None, None].clamp_min(1)
        return self.attention(query, memory, memory, self.config["Heads"], memory_mask) @ self.p("claim.output")

    def retrieve(self, batch):
        encoded = self.encode(batch["retrieval"])
        current = self.gather(encoded, batch["retrieval"]["current"])
        mask = batch["retrieval"]["current_mask"]
        query = (current * mask[..., None]).sum(1, keepdim=True) / mask.sum(1)[:, None, None]
        words = F.embedding(batch["facts"], self.p("encoder.embedding"))
        entries = (words * batch["fact_token_mask"][..., None]).sum(2) / batch["fact_token_mask"].sum(2)[..., None].clamp_min(1)
        entries = torch.cat([self.p("memory.none").expand(encoded.shape[0], -1, -1), entries], 1)
        return self.pointer(query @ self.p("memory.query"), entries, batch["fact_mask"])
