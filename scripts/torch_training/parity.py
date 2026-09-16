import argparse
import json
from pathlib import Path

import numpy as np
import torch

from data import RandomState, collate
from model import FishbrainModel, export_fbm, read_fbm
from trainer import loss_for, set_phase, train_update


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("corpus")
    parser.add_argument("reference")
    parser.add_argument("report")
    args = parser.parse_args()
    torch.set_num_threads(2)
    directory = Path(args.corpus)
    manifest = json.loads((directory / "manifest.json").read_text())
    header, weights = read_fbm(directory / "initial.fbm")
    model = FishbrainModel(header, weights, manifest["headSizes"]).cuda()
    results = []
    references = [json.loads(line) for line in Path(args.reference).read_text().splitlines()]
    for example in references:
        batch = collate([example["row"]], manifest, "cuda", "JointUnderstanding", RandomState(manifest["initialRandomState"]))
        with torch.no_grad():
            outputs, _, _ = model.understand(batch)
            outputs["encoded"] = model.encode(batch["input"])
        maximum = 0
        for name, expected in example["logits"].items():
            actual = outputs[name][0].float().cpu().numpy()
            desired = np.array(expected["data"], np.float32).reshape(expected["rows"], expected["columns"])
            maximum = max(maximum, float(np.max(np.abs(actual - desired))))
            np.testing.assert_allclose(actual, desired, atol=1e-4, rtol=2e-4, err_msg=name)
        phases = {}
        for phase, expected in example["losses"].items():
            set_phase(model, phase)
            model.zero_grad(set_to_none=True)
            batch = collate([example["row"]], manifest, "cuda", phase, RandomState(manifest["initialRandomState"]))
            loss = loss_for(model, batch, phase)
            loss.backward()
            np.testing.assert_allclose(loss.item(), expected["loss"], atol=2e-4, rtol=2e-4, err_msg=phase)
            gradient_error = 0
            for name, target in expected["gradients"].items():
                gradient = model.p(name).grad
                actual = 0 if gradient is None else gradient.flatten()[target["index"]].item()
                gradient_error = max(gradient_error, abs(actual - target["value"]))
                np.testing.assert_allclose(actual, target["value"], atol=3e-4, rtol=3e-3, err_msg=phase + " " + name)
            phases[phase] = dict(loss=loss.item(), referenceLoss=expected["loss"], maximumGradientError=gradient_error)
        results.append(dict(family=example["row"]["semanticFamilyId"], maximumLogitError=maximum, phases=phases))
        print("PASS C# / GPU", results[-1]["family"], maximum, flush=True)
    # Padding and batched attention must preserve a row's interpretation.
    rows = [r["row"] for r in references]
    batch = collate(rows, manifest, "cuda", "JointUnderstanding", RandomState(manifest["initialRandomState"]))
    with torch.no_grad():
        outputs, _, _ = model.understand(batch)
    for i, example in enumerate(references):
        for name, expected in example["logits"].items():
            if name == "encoded":
                continue
            actual = outputs[name][i, :expected["rows"], :expected["columns"]].float().cpu().numpy()
            desired = np.array(expected["data"], np.float32).reshape(expected["rows"], expected["columns"])
            np.testing.assert_allclose(actual, desired, atol=2e-4, rtol=3e-4, err_msg="Batched " + name)
    export_path = Path(args.report).with_suffix(".fbm")
    export_fbm(model, export_path, 0)
    optimizer = torch.optim.AdamW(model.parameters(), lr=.0001, weight_decay=.01, fused=True)
    project = next(r for r in rows if r["projectResponse"])
    batch = collate([project], manifest, "cuda", "DecoderPolish", RandomState(manifest["initialRandomState"]))
    frozen = {n: p.detach().clone() for n, p in model.weights.items() if not n.startswith("decoder__")}
    train_update(model, optimizer, batch, "DecoderPolish", 220000)
    assert all(torch.equal(p, model.weights[n]) for n, p in frozen.items()), "Polishing changed encoder/planner"
    Path(args.report).write_text(json.dumps(dict(passed=True, torch=torch.__version__, gpu=torch.cuda.get_device_name(0),
                                               export=str(export_path), cases=results, batchedParity=True, frozenPolish=True), indent=2))
    print("PASS BATCHED PADDING, FROZEN POLISH, EXPORT", export_path, flush=True)


if __name__ == "__main__":
    main()
