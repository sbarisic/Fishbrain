"""Run against the installed GPU backend; checks exact continuation and phase isolation."""
import argparse
import json
import tempfile
from pathlib import Path

import torch

from checkpoints import fingerprint, lease, restore, save
from data import RandomState, collate
from model import FishbrainModel, read_fbm
from trainer import train_update


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("corpus")
    parser.add_argument("reference")
    parser.add_argument("report")
    args = parser.parse_args()
    torch.set_num_threads(2)
    torch.manual_seed(42)
    torch.use_deterministic_algorithms(True)
    root = Path(args.report).resolve().parent
    root.mkdir(parents=True, exist_ok=True)
    manifest = json.loads((Path(args.corpus) / "manifest.json").read_text())
    header, weights = read_fbm(Path(args.corpus) / "initial.fbm")
    model = FishbrainModel(header, weights, manifest["headSizes"]).cuda()
    optimizer = torch.optim.AdamW(model.parameters(), lr=.0003, weight_decay=.01, fused=True)
    rows = [json.loads(line)["row"] for line in Path(args.reference).read_text().splitlines()]
    rows = [r for r in rows if r["projectResponse"]][:3]
    rng = RandomState(manifest["initialRandomState"])
    binding = fingerprint(args.corpus)
    checked = []
    with tempfile.TemporaryDirectory(prefix="gpu-tests-", dir=root) as temporary:
        temporary = Path(temporary).resolve()
        assert temporary.parent == root
        checkpoint = temporary / "training.pt"
        with lease(temporary / "writer.lock"):
            try:
                with lease(temporary / "writer.lock"):
                    raise AssertionError("Duplicate writer was accepted")
            except RuntimeError:
                pass
        for index, phase in enumerate(("MaskedLanguage", "JointUnderstanding", "JointRealization", "DecoderPolish")):
            step = 100001 + index * 2
            batch = collate(rows, manifest, "cuda", phase, rng)
            train_update(model, optimizer, batch, phase, step)
            save(checkpoint, model, optimizer, rng, step + 1, binding, "fp32", {})
            draws = torch.rand(16, device="cuda")
            batch = collate(rows, manifest, "cuda", phase, rng)
            expected_loss = train_update(model, optimizer, batch, phase, step + 1)
            resumed = FishbrainModel(header, weights, manifest["headSizes"]).cuda()
            resumed_optimizer = torch.optim.AdamW(resumed.parameters(), lr=.0003, weight_decay=.01, fused=True)
            resumed_rng = RandomState(1)
            state = restore(checkpoint, resumed, resumed_optimizer, resumed_rng, binding, "fp32")
            assert state["completedSteps"] == step + 1
            assert torch.equal(draws, torch.rand(16, device="cuda")), "CUDA RNG did not resume"
            batch = collate(rows, manifest, "cuda", phase, resumed_rng)
            actual_loss = train_update(resumed, resumed_optimizer, batch, phase, step + 1)
            assert torch.equal(actual_loss, expected_loss), "Resumed loss differs"
            assert resumed_rng.state == rng.state, "Masking RNG did not resume"
            for name, parameter in model.weights.items():
                other = resumed.weights[name]
                assert torch.equal(parameter, other), "Resumed weight differs: " + name
                for field, expected in optimizer.state.get(parameter, {}).items():
                    actual = resumed_optimizer.state[other][field]
                    assert torch.equal(actual, expected) if isinstance(expected, torch.Tensor) else actual == expected
            checked.append(phase)
            del resumed, resumed_optimizer
            torch.cuda.empty_cache()
            print("PASS EXACT GPU RESUME", phase, flush=True)
        try:
            restore(checkpoint, model, optimizer, rng, "wrong-corpus", "fp32")
            raise AssertionError("Mismatched corpus accepted")
        except ValueError:
            pass
        corrupt = bytearray((Path(args.corpus) / "initial.fbm").read_bytes())
        corrupt[-40] ^= 1
        damaged = temporary / "corrupt.fbm"
        damaged.write_bytes(corrupt)
        try:
            read_fbm(damaged)
            raise AssertionError("Corrupt inference checkpoint accepted")
        except ValueError:
            pass
    Path(args.report).write_text(json.dumps(dict(passed=True, exactResumePhases=checked, cudaRng=True,
                                               duplicateWriterRejected=True, corpusMismatchRejected=True,
                                               corruptArtifactRejected=True), indent=2))


if __name__ == "__main__":
    main()
