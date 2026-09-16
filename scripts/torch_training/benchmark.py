import argparse
import json
import time
from pathlib import Path

import torch

from claims import ClaimMiner
from data import Corpus, RandomState, collate
from model import FishbrainModel, read_fbm
from trainer import train_update


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("corpus")
    parser.add_argument("report")
    parser.add_argument("--updates", type=int, default=20)
    parser.add_argument("--precision", choices=("fp32", "bf16"), default="fp32")
    parser.add_argument("--cli", required=True, help="Built Fishbrain.dll used for rejected-candidate screening")
    args = parser.parse_args()
    if args.updates < 1:
        raise ValueError("At least one measured update is required")
    if not torch.cuda.is_available():
        raise RuntimeError("GPU backend unavailable")
    torch.set_num_threads(2)
    torch.manual_seed(42)
    torch.use_deterministic_algorithms(True)
    corpus = Corpus(args.corpus)
    header, weights = read_fbm(Path(args.corpus) / "initial.fbm")
    miner = ClaimMiner(args.cli, Path(args.corpus) / "initial.fbm", corpus.manifest)
    results = []
    for phase in ("MaskedLanguage", "JointUnderstanding", "JointRealization", "DecoderPolish"):
        model = FishbrainModel(header, weights, corpus.manifest["headSizes"]).cuda()
        optimizer = torch.optim.AdamW(model.parameters(), lr=.0003, weight_decay=.01, fused=True)
        rng = RandomState(corpus.manifest["initialRandomState"])
        timings, losses = [], []
        torch.cuda.reset_peak_memory_stats()
        # Collation, transfer, backward and optimizer are all timed. No cached GPU batches.
        for update in range(args.updates + 3):
            torch.cuda.synchronize()
            start = time.perf_counter()
            step = 100001 + update
            rows = corpus.batch(step, phase)
            batch = collate(rows, corpus.manifest, "cuda", phase, rng)
            loss = train_update(model, optimizer, batch, phase, step, args.precision, miner)
            torch.cuda.synchronize()
            seconds = time.perf_counter() - start
            if update >= 3:
                timings.append(seconds)
                losses.append(loss.item())
            print(f"GPU BENCHMARK {phase} {update} {seconds:.4f}s LOSS {loss.item():.5f}", flush=True)
        results.append(dict(phase=phase, meanSeconds=sum(timings) / len(timings), seconds=timings, losses=losses,
                            peakAllocatedBytes=torch.cuda.max_memory_allocated()))
        del optimizer, model, batch, loss
        torch.cuda.empty_cache()
    report = dict(torch=torch.__version__, hip=torch.version.hip, gpu=torch.cuda.get_device_name(0),
                  corpusHash=corpus.manifest["corpusHash"], precision=args.precision, batchSize=32,
                  warmupUpdates=3, deterministic=True, generatedRejectedCandidates=miner.rejected, results=results)
    Path(args.report).write_text(json.dumps(report, indent=2))
    miner.close()


if __name__ == "__main__":
    main()
