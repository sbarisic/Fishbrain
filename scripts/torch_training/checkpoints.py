import contextlib
import hashlib
import json
import os
from pathlib import Path

import torch

TRAINER_SCHEMA = "CONTEXTUAL_GPU_FP32_V1"


def atomic_json(path, value):
    path = Path(path)
    temporary = path.with_name(path.name + ".tmp")
    temporary.write_text(json.dumps(value, indent=2), encoding="utf-8")
    temporary.replace(path)


def fingerprint(directory):
    root = Path(directory)
    digest = hashlib.sha256()
    for name in ("manifest.json", "initial.fbm"):
        with (root / name).open("rb") as stream:
            while block := stream.read(1024 * 1024):
                digest.update(block)
    return digest.hexdigest()


@contextlib.contextmanager
def lease(path):
    import msvcrt
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("a+b") as stream:
        if stream.tell() == 0:
            stream.write(b"0")
            stream.flush()
        stream.seek(0)
        try:
            msvcrt.locking(stream.fileno(), msvcrt.LK_NBLCK, 1)
        except OSError as error:
            raise RuntimeError("Another GPU trainer owns this output directory") from error
        try:
            yield
        finally:
            stream.seek(0)
            msvcrt.locking(stream.fileno(), msvcrt.LK_UNLCK, 1)


def save(path, model, optimizer, rng, step, binding, precision, best, rejected=0, sampler="COPRIME_FAMILY_PERMUTATION_MEMBER_ROTATION"):
    path = Path(path)
    temporary = path.with_name(path.name + ".tmp")
    state = dict(format=1, trainerSchema=TRAINER_SCHEMA, torchVersion=str(torch.__version__),
                 binding=binding, precision=precision, completedSteps=step,
                 model=model.state_dict(), optimizer=optimizer.state_dict(), maskingRng=rng.state,
                 cpuRng=torch.get_rng_state(), cudaRng=torch.cuda.get_rng_state_all(),
                 bestValidation=best, generatedRejectedCandidates=rejected,
                 sampler=sampler, effectiveBatchSize=32)
    with temporary.open("wb") as stream:
        torch.save(state, stream)
        stream.flush()
        os.fsync(stream.fileno())
    temporary.replace(path)


def restore(path, model, optimizer, rng, binding, precision, sampler="COPRIME_FAMILY_PERMUTATION_MEMBER_ROTATION"):
    state = torch.load(path, map_location="cuda", weights_only=True)
    if state["format"] != 1 or state["binding"] != binding or state["precision"] != precision:
        raise ValueError("GPU checkpoint schema, corpus, initial model or precision mismatch")
    if state.get("trainerSchema") != TRAINER_SCHEMA or state.get("torchVersion") != str(torch.__version__):
        raise ValueError("GPU trainer schema or PyTorch version mismatch")
    if not 0 <= state["completedSteps"] <= 260000:
        raise ValueError("GPU checkpoint step is outside the curriculum")
    if state["sampler"] != sampler or state["effectiveBatchSize"] != 32:
        raise ValueError("GPU checkpoint sampler mismatch")
    model.load_state_dict(state["model"], strict=True)
    optimizer.load_state_dict(state["optimizer"])
    rng.state = state["maskingRng"]
    torch.set_rng_state(state["cpuRng"].cpu())
    torch.cuda.set_rng_state_all([value.cpu() for value in state["cudaRng"]])
    return state
