from __future__ import annotations

import importlib.util
import tempfile
import unittest
from pathlib import Path


RUNNER_PATH = Path(__file__).parents[1] / "run_experiment.py"


def _load_runner_module():
    spec = importlib.util.spec_from_file_location("run_experiment_tested", RUNNER_PATH)
    module = importlib.util.module_from_spec(spec)
    assert spec.loader
    spec.loader.exec_module(module)
    return module


runner = _load_runner_module()


class RunExperimentTests(unittest.TestCase):
    def test_sync_replaces_matching_generated_copy_without_backup(self):
        with tempfile.TemporaryDirectory() as temporary_dir:
            root = Path(temporary_dir)
            local_world = root / "local" / "spire2"
            target_world = root / "Archipelago" / "worlds" / "spire2"
            local_world.mkdir(parents=True)
            target_world.mkdir(parents=True)
            (local_world / "world.py").write_text("canonical\n", encoding="utf-8")
            (target_world / "world.py").write_text("canonical\n", encoding="utf-8")
            cache = target_world / "__pycache__"
            cache.mkdir()
            (cache / "world.pyc").write_bytes(b"generated")

            backup = runner._sync_world(local_world, root / "Archipelago")

            self.assertIsNone(backup)
            self.assertEqual((target_world / "world.py").read_text(encoding="utf-8"), "canonical\n")
            self.assertFalse(cache.exists())
            self.assertEqual(list(target_world.parent.glob(".spire2.backup-*")), [])

    def test_sync_preserves_differing_target_before_replacement(self):
        with tempfile.TemporaryDirectory() as temporary_dir:
            root = Path(temporary_dir)
            local_world = root / "local" / "spire2"
            target_world = root / "Archipelago" / "worlds" / "spire2"
            local_world.mkdir(parents=True)
            target_world.mkdir(parents=True)
            (local_world / "world.py").write_text("canonical\n", encoding="utf-8")
            (target_world / "world.py").write_text("local edit\n", encoding="utf-8")

            backup = runner._sync_world(local_world, root / "Archipelago")

            self.assertIsNotNone(backup)
            assert backup is not None
            self.assertEqual((backup / "world.py").read_text(encoding="utf-8"), "local edit\n")
            self.assertEqual((target_world / "world.py").read_text(encoding="utf-8"), "canonical\n")


if __name__ == "__main__":
    unittest.main()
