from __future__ import annotations

import csv
import importlib.util
import os
import sys
import tempfile
import types
import unittest
from pathlib import Path


HOOK_PATH = Path(__file__).parents[1] / "placement_stats_hook.py"


def _load_hook_module():
    fake_fuzz = types.ModuleType("fuzz")
    fake_fuzz.BaseHook = object
    old_fuzz = sys.modules.get("fuzz")
    sys.modules["fuzz"] = fake_fuzz
    try:
        spec = importlib.util.spec_from_file_location("placement_stats_hook_tested", HOOK_PATH)
        module = importlib.util.module_from_spec(spec)
        sys.modules[spec.name] = module
        assert spec.loader
        spec.loader.exec_module(module)
        return module
    finally:
        if old_fuzz is None:
            del sys.modules["fuzz"]
        else:
            sys.modules["fuzz"] = old_fuzz


hook_module = _load_hook_module()


class FakeRegion:
    def __init__(self, name):
        self.name = name


class FakeItem:
    game = "Test Game"

    def __init__(self, name, player, classification=1):
        self.name = name
        self.player = player
        self.classification = classification
        self.code = 100
        self.advancement = bool(classification & 1)
        self.useful = bool(classification & 2)
        self.trap = bool(classification & 4)


class FakeLocation:
    game = "Test Game"
    locked = False

    def __init__(self, name, item, region, address):
        self.name = name
        self.player = 1
        self.item = item
        self.parent_region = FakeRegion(region)
        self.address = address
        self.is_event = address is None


class FakeMultiWorld:
    seed = 12345
    seed_name = "12345"
    players = 1

    def __init__(self):
        self.early = FakeLocation("Early Check", FakeItem("Key", 1), "Act 1", 1)
        self.unreachable = FakeLocation("Blocked Check", FakeItem("Junk", 1, 0), "Act 3", 2)

    def get_filled_locations(self):
        return [self.unreachable, self.early]

    def get_spheres(self):
        yield {self.early}
        yield set()
        yield {self.unreachable}

    def get_player_name(self, player):
        return f"Player{player}"

    def get_all_ids(self):
        return (1,)


class PlacementStatsHookTests(unittest.TestCase):
    def test_writes_one_atomic_csv_with_spheres(self):
        with tempfile.TemporaryDirectory() as temporary_dir:
            old_output = os.environ.get(hook_module.OUTPUT_DIR_ENV)
            os.environ[hook_module.OUTPUT_DIR_ENV] = temporary_dir
            try:
                hook = hook_module.Hook()
                hook.setup_worker(None)
                hook.after_generate(FakeMultiWorld(), "unused")
            finally:
                if old_output is None:
                    del os.environ[hook_module.OUTPUT_DIR_ENV]
                else:
                    os.environ[hook_module.OUTPUT_DIR_ENV] = old_output

            shards = list((Path(temporary_dir) / "runs").glob("*.csv"))
            self.assertEqual(len(shards), 1)
            self.assertEqual(list((Path(temporary_dir) / "runs").glob("*.tmp")), [])

            with shards[0].open(encoding="utf-8", newline="") as input_file:
                rows = {row["location_name"]: row for row in csv.DictReader(input_file)}

            self.assertEqual(rows["Early Check"]["sphere"], "1")
            self.assertEqual(rows["Early Check"]["reachable"], "True")
            self.assertEqual(rows["Blocked Check"]["sphere"], "")
            self.assertEqual(rows["Blocked Check"]["reachable"], "False")

    def test_none_multiworld_writes_nothing(self):
        with tempfile.TemporaryDirectory() as temporary_dir:
            old_output = os.environ.get(hook_module.OUTPUT_DIR_ENV)
            os.environ[hook_module.OUTPUT_DIR_ENV] = temporary_dir
            try:
                hook = hook_module.Hook()
                hook.setup_worker(None)
                hook.after_generate(None, "unused")
            finally:
                if old_output is None:
                    del os.environ[hook_module.OUTPUT_DIR_ENV]
                else:
                    os.environ[hook_module.OUTPUT_DIR_ENV] = old_output

            self.assertEqual(list((Path(temporary_dir) / "runs").glob("*.csv")), [])


if __name__ == "__main__":
    unittest.main()
