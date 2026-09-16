"""Run native development gates after GPU training. Never publishes a model."""
import argparse
import datetime
import json
import subprocess
from pathlib import Path

from checkpoints import atomic_json, lease


def assess(cli, corpus, output, scenarios):
    output = Path(output).resolve()
    results = {}
    progress_path = output / "progress.json"
    progress = json.loads(progress_path.read_text()) if progress_path.exists() else {}

    def status(value, gate=None):
        progress.update(status=value, activeGate=gate, updatedUtc=datetime.datetime.now(datetime.timezone.utc).isoformat())
        atomic_json(progress_path, progress)

    def run(name, arguments, stdout=None):
        status("ASSESSING", name)
        print("NATIVE GATE " + name, flush=True)
        with (output / (stdout or name + ".log")).open("w", encoding="utf-8") as log:
            with (output / (name + ".stderr.log")).open("w", encoding="utf-8") as errors:
                result = subprocess.run(["dotnet", str(Path(cli).resolve()), *map(str, arguments)], stdout=log, stderr=errors)
        results[name] = result.returncode
        atomic_json(output / "native-gates.json", dict(exitCodes=results, promoted=False, humanReviewsComplete=False))
        return result.returncode

    try:
        # A failed assessment must not accidentally reuse an earlier calibrated artifact.
        candidate = output / "calibrated.fbm"
        previous_time = candidate.stat().st_mtime_ns if candidate.exists() else None
        run("assessment", ["assess-torch", Path(corpus).resolve(), output])
        if not candidate.exists() or candidate.stat().st_mtime_ns == previous_time:
            status("ASSESSMENT_FAILED")
            return False
        run("artifact", ["artifact-smoke", candidate])
        run("acceptance", ["acceptance-contextual", candidate, output / "acceptance.json"])
        run("resources", ["profile-contextual", candidate, "32"], "resources.json")
        run("comparison", ["compare-contextual", Path(corpus).resolve(), candidate, output / "comparison.json"])
        run("review-sample", ["conversation-sample", candidate, Path(scenarios).resolve(), output / "human-review.jsonl"])
        passed = all(code == 0 for code in results.values())
        status("AWAITING_HUMAN_REVIEW" if passed else "QUALITY_GATES_FAILED")
        return passed
    except BaseException:
        status("ASSESSMENT_FAILED")
        raise


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("corpus")
    parser.add_argument("output")
    parser.add_argument("--cli", required=True)
    parser.add_argument("--scenarios", required=True)
    args = parser.parse_args()
    with lease(Path(args.output) / "training.lock"):
        raise SystemExit(0 if assess(args.cli, args.corpus, args.output, args.scenarios) else 1)
