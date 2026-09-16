import contextlib
import math

import torch
from torch.nn import functional as F


def categorical(logits, targets, **kwargs):
    raw = F.cross_entropy(logits.float(), targets, reduction="none", **kwargs)
    # Native TensorGraph reports -log(max(p, 1e-30)), while its gradient remains p-y.
    # Preserve both contracts rather than changing saturated-logit training semantics.
    return raw + (raw.clamp_max(30 * math.log(10)) - raw).detach()


def active(name, phase):
    if phase == "MaskedLanguage":
        return name.startswith("encoder.")
    if phase == "DecoderPolish":
        return name.startswith("decoder.")
    if phase == "JointUnderstanding":
        return not name.startswith("decoder.")
    return True


def set_phase(model, phase):
    parameters = []
    for shape in model.header["Parameters"]:
        parameter = model.p(shape["Name"])
        enabled = active(shape["Name"], phase)
        parameter.requires_grad_(enabled)
        if enabled:
            parameters.append(parameter)
    return parameters


def loss_for(model, batch, phase, mine_claims=None):
    size = len(batch["rows"])
    if phase == "MaskedLanguage":
        encoded = model.encode(batch["input"])
        indices = batch["masked_indices"]
        logits = encoded[indices[:, 0], indices[:, 1]] @ model.p("encoder.embedding").T
        return (categorical(logits, batch["masked_labels"]) * batch["masked_weights"]).sum()
    with torch.no_grad() if phase == "DecoderPolish" else contextlib.nullcontext():
        output, memory, memory_mask = model.understand(batch)
    if phase in ("JointRealization", "DecoderPolish"):
        logits = model.decode(batch["decoder"], memory, memory_mask)
        losses = categorical(logits.transpose(1, 2), batch["response"], ignore_index=-100)
        return (losses * batch["response_weights"]).sum()
    loss = output["slots"].float().sum() * 0
    for name, (indices, weights) in batch["targets"].items():
        logits = output[name][indices[:, 0], indices[:, 1]]
        loss = loss + (categorical(logits, indices[:, 2]) * weights).sum()
    for name, (indices, labels) in batch["multi"].items():
        logits = output[name][indices, 0]
        loss = loss + F.binary_cross_entropy_with_logits(logits.float(), labels, reduction="none").mean(-1).sum() / size
    if "retrieval" in batch:
        scores = model.retrieve(batch)[:, 0]
        indices = batch["memory_indices"]
        loss = loss + (categorical(scores[indices[:, 0]], indices[:, 1]) * batch["memory_weights"]).sum()
    for suffix, label in (("positive", 0), ("negative", 1)):
        logits = model.claim(batch["claim_" + suffix], batch["claim_" + suffix + "_mask"], memory, memory_mask)[:, 0]
        labels = torch.full((size,), label, dtype=torch.long, device=logits.device)
        loss = loss + (categorical(logits, labels) * batch["claim_" + suffix + "_valid"]).sum() / size
    if mine_claims is not None:
        mined = mine_claims(model, memory, memory_mask, batch["rows"])
        if mined is not None:
            indices, tokens, mask = mined
            logits = model.claim(tokens, mask, memory[indices], memory_mask[indices])[:, 0]
            loss = loss + categorical(logits, torch.ones(len(indices), dtype=torch.long, device=logits.device)).sum() / size
    return loss


def learning_rate(step):
    start = 0 if step < 40000 else 40000 if step < 220000 else 220000
    length = 180000 if start == 40000 else 40000
    local = step - start
    peak = .0001 if start == 220000 else .0003
    return peak * min(1, (local + 1) / 2000) * (.1 + .9 * .5 * (1 + math.cos(math.pi * min(1, local / length))))


def train_update(model, optimizer, batch, phase, step, precision="fp32", mine_claims=None):
    parameters = set_phase(model, phase)
    optimizer.zero_grad(set_to_none=True)
    for group in optimizer.param_groups:
        group["lr"] = learning_rate(step)
    with torch.autocast("cuda", dtype=torch.bfloat16, enabled=precision == "bf16"):
        loss = loss_for(model, batch, phase, mine_claims if phase == "JointUnderstanding" and step % 10 == 0 else None)
    loss.backward()
    # The native trainer updates every active parameter, even when its supervision is masked.
    for parameter in parameters:
        if parameter.grad is None:
            parameter.grad = torch.zeros_like(parameter)
    torch.nn.utils.clip_grad_norm_(parameters, 1, error_if_nonfinite=True, foreach=True)
    optimizer.step()
    return loss.detach()
