"""Exercise every packed validation target in every phase before a long GPU run."""
import argparse
import json
import time
from pathlib import Path

import torch

from data import Corpus
from model import FishbrainModel, read_fbm
from train import validate


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("corpus")
    parser.add_argument("report")
    args = parser.parse_args()
    torch.set_num_threads(2)
    torch.manual_seed(42)
    torch.use_deterministic_algorithms(True)
    corpus = Corpus(args.corpus, "validation")
    header, weights = read_fbm(Path(args.corpus) / "initial.fbm")
    model = FishbrainModel(header, weights, corpus.manifest["headSizes"]).cuda()
    results = {}
    for phase in ("MaskedLanguage", "JointUnderstanding", "JointRealization", "DecoderPolish"):
        start = time.perf_counter()
        results[phase] = validate(model, corpus, phase, "fp32")
        results[phase]["seconds"] = time.perf_counter() - start
        print(phase, results[phase], flush=True)
    Path(args.report).write_text(json.dumps(dict(passed=True, metrics=results, qualityGate=False), indent=2))
