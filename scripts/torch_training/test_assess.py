import json
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

from assess import assess


class CompletionTests(unittest.TestCase):
    def check_pipeline(self, failed=None, writes_candidate=True):
        with tempfile.TemporaryDirectory() as temporary:
            output = Path(temporary)
            calls = []
            def run(command, **_):
                calls.append(command[2])
                if command[2] == "assess-torch" and writes_candidate:
                    (output / "calibrated.fbm").write_bytes(b"fixture")
                return SimpleNamespace(returncode=1 if command[2] == failed else 0)
            with patch("assess.subprocess.run", run):
                result = assess("fixture.dll", "corpus", output, "scenarios")
            progress = json.loads((output / "progress.json").read_text())
            gates = json.loads((output / "native-gates.json").read_text())
            self.assertFalse(gates["promoted"])
            self.assertFalse(gates["humanReviewsComplete"])
            return result, progress, calls

    def test_success_still_requires_humans(self):
        result, progress, calls = self.check_pipeline()
        self.assertTrue(result)
        self.assertEqual("AWAITING_HUMAN_REVIEW", progress["status"])
        self.assertEqual(6, len(calls))

    def test_quality_failure_retains_remaining_diagnostics(self):
        result, progress, calls = self.check_pipeline("assess-torch")
        self.assertFalse(result)
        self.assertEqual("QUALITY_GATES_FAILED", progress["status"])
        self.assertIn("conversation-sample", calls)

    def test_failed_export_does_not_run_dependent_gates(self):
        result, progress, calls = self.check_pipeline("assess-torch", False)
        self.assertFalse(result)
        self.assertEqual("ASSESSMENT_FAILED", progress["status"])
        self.assertEqual(["assess-torch"], calls)


if __name__ == "__main__":
    unittest.main()
