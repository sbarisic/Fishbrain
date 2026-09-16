import argparse
import datetime
import gc
import os
import signal
import time
from pathlib import Path

import torch

from checkpoints import atomic_json, fingerprint, lease, restore, save
from claims import ClaimMiner
from data import Corpus, RandomState, collate
from model import FishbrainModel, export_fbm, phase_at, read_fbm
from trainer import loss_for, train_update


def validate(model, corpus, phase, precision):
    rng = RandomState(corpus.manifest["initialRandomState"])
    total, count = 0.0, 0
    pending = []
    def measure(rows):
        batch = collate(rows, corpus.manifest, "cuda", phase, rng)
        with torch.no_grad(), torch.autocast("cuda", dtype=torch.bfloat16, enabled=precision == "bf16"):
            return loss_for(model, batch, phase).item()
    for index in range(len(corpus.offsets)):
        row = corpus.row(index)
        if phase in ("JointRealization", "DecoderPolish") and not row["projectResponse"]:
            continue
        pending.append(row)
        if len(pending) == 32:
            total += measure(pending) * len(pending)
            count += len(pending)
            pending.clear()
    if pending:
        total += measure(pending) * len(pending)
        count += len(pending)
    return dict(rows=count, loss=total / count)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("corpus")
    parser.add_argument("output")
    parser.add_argument("--cli", required=True)
    parser.add_argument("--native-corpus")
    parser.add_argument("--scenarios")
    parser.add_argument("--until", type=int, default=260000)
    parser.add_argument("--precision", choices=("fp32", "bf16"), default="fp32")
    args = parser.parse_args()
    if not 1 <= args.until <= 260000:
        raise ValueError("Endpoint must be within 1-260000")
    if args.until == 260000 and (not args.native_corpus or not args.scenarios):
        raise ValueError("A full run requires --native-corpus and --scenarios for native completion gates")
    for required in (args.cli, args.native_corpus, args.scenarios):
        if required and not Path(required).exists():
            raise FileNotFoundError(required)
    if not torch.cuda.is_available():
        raise RuntimeError("A working GPU backend is required")
    output = Path(args.output).resolve()
    output.mkdir(parents=True, exist_ok=True)
    torch.set_num_threads(2)
    torch.manual_seed(42)
    torch.use_deterministic_algorithms(True)
    with lease(output / "training.lock"):
        corpus = Corpus(args.corpus)
        validation = Corpus(args.corpus, "validation")
        binding = fingerprint(args.corpus)
        header, weights = read_fbm(Path(args.corpus) / "initial.fbm")
        model = FishbrainModel(header, weights, corpus.manifest["headSizes"]).cuda()
        optimizer = torch.optim.AdamW(model.parameters(), lr=.0003, weight_decay=.01, fused=True)
        rng = RandomState(corpus.manifest["initialRandomState"])
        checkpoint = output / "training.pt"
        step, best = 0, {}
        rejected = 0
        if checkpoint.exists():
            state = restore(checkpoint, model, optimizer, rng, binding, args.precision)
            step, best = state["completedSteps"], state["bestValidation"]
            rejected = state["generatedRejectedCandidates"]
            del state
        if step >= args.until:
            raise ValueError("Endpoint must exceed saved progress")
        miner = ClaimMiner(args.cli, Path(args.corpus) / "initial.fbm", corpus.manifest)
        miner.rejected = rejected
        start_step = step
        start_time = time.perf_counter()
        stopping = False
        status = "RUNNING"
        last_loss = None
        def stop(*_):
            nonlocal stopping
            stopping = True
        signal.signal(signal.SIGINT, stop)
        signal.signal(signal.SIGTERM, stop)

        def progress():
            atomic_json(output / "progress.json", dict(status=status, processId=os.getpid(), completedSteps=step,
                        endpoint=args.until, nextPhase=phase_at(step) if step < 260000 else "COMPLETE",
                        secondsPerUpdate=(time.perf_counter() - start_time) / max(1, step - start_step), loss=last_loss,
                        gpu=torch.cuda.get_device_name(0), precision=args.precision, binding=binding,
                        checkpoint=str(checkpoint), lastDurableCheckpointUtc=datetime.datetime.fromtimestamp(checkpoint.stat().st_mtime, datetime.timezone.utc).isoformat() if checkpoint.exists() else None,
                        updatedUtc=datetime.datetime.now(datetime.timezone.utc).isoformat(),
                        generatedRejectedCandidates=miner.rejected))
        def durable():
            save(checkpoint, model, optimizer, rng, step, binding, args.precision, best, miner.rejected)
        try:
            if not checkpoint.exists():
                durable()
            progress()
            while step < args.until and not stopping:
                if (output / "STOP").exists():
                    stopping = True
                    break
                phase = phase_at(step)
                rows = corpus.batch(step, phase)
                batch = collate(rows, corpus.manifest, "cuda", phase, rng)
                loss = train_update(model, optimizer, batch, phase, step, args.precision, miner)
                step += 1
                if step == start_step + 1 or step % 100 == 0:
                    last_loss = loss.item()
                    if not torch.isfinite(loss).item():
                        raise ArithmeticError("Nonfinite training loss")
                    progress()
                    print(f"GPU STEP {step} PHASE {phase} LOSS {last_loss:.5f} SECONDS_PER_UPDATE {(time.perf_counter()-start_time)/(step-start_step):.4f}", flush=True)
                if step % 1000 == 0:
                    durable()
                    progress()
                if step % 5000 == 0:
                    status = "VALIDATING"
                    progress()
                    phases = ["MaskedLanguage"] if step <= 40000 else ["JointUnderstanding", "JointRealization"] if step < 220000 else ["JointUnderstanding", "DecoderPolish"]
                    metrics = {p: validate(model, validation, p, args.precision) for p in phases}
                    atomic_json(output / f"validation-{step}.json", dict(completedSteps=step, metrics=metrics,
                                releaseEligible=False, note="Teacher-forced validation losses; native operational and release gates remain mandatory."))
                    export_fbm(model, output / "latest.fbm", step)
                    candidates = output / "candidates"
                    candidates.mkdir(exist_ok=True)
                    export_fbm(model, candidates / f"step-{step:06}.fbm", step)
                    key = phase_at(step - 1)
                    score = sum(m["loss"] for m in metrics.values())
                    if score < best.get(key, float("inf")):
                        best[key] = score
                        export_fbm(model, output / ("best-" + key + ".fbm"), step)
                        save(output / ("best-" + key + ".pt"), model, optimizer, rng, step, binding, args.precision, best, miner.rejected)
                    durable()
                    status = "RUNNING"
                    progress()
            status = "STOPPED" if stopping else "ENDPOINT_REACHED"
        except BaseException:
            status = "FAILED"
            raise
        finally:
            # An interrupted/failed optimizer operation must not replace the last complete state.
            if status != "FAILED":
                durable()
                export_fbm(model, output / "latest.fbm", step)
            progress()
            miner.close()
        print(f"GPU {status} {step}; no model promoted", flush=True)
        if status == "ENDPOINT_REACHED" and step == 260000:
            del model, optimizer, batch, loss
            gc.collect()
            torch.cuda.empty_cache()
            from assess import assess
            if not assess(args.cli, args.native_corpus, output, args.scenarios):
                raise SystemExit(1)


if __name__ == "__main__":
    main()
