from __future__ import annotations

import contextlib
import importlib.util
import io
import tempfile
import unittest
from pathlib import Path


ANALYZE_PATH = Path(__file__).parents[1] / "analyze.py"


def _load_analyze_module():
    spec = importlib.util.spec_from_file_location("placement_stats_analyze_tested", ANALYZE_PATH)
    module = importlib.util.module_from_spec(spec)
    assert spec.loader
    spec.loader.exec_module(module)
    return module


analyze = _load_analyze_module()


class AnalyzeTests(unittest.TestCase):
    def test_count_must_be_positive(self):
        with contextlib.redirect_stderr(io.StringIO()):
            with self.assertRaises(SystemExit):
                analyze._parser().parse_args(
                    ["item-nth-sphere", "unused", "--item", "Relic", "--count", "0"]
                )

    @unittest.skipUnless(importlib.util.find_spec("duckdb"), "DuckDB is not installed")
    def test_nth_item_sphere_groups_each_seed_by_reachable_copy(self):
        with tempfile.TemporaryDirectory() as temporary_dir:
            data_dir = Path(temporary_dir)
            runs_dir = data_dir / "runs"
            runs_dir.mkdir()
            (runs_dir / "placements.csv").write_text(
                "generation_id,item_name,item_player,reachable,sphere,"
                "location_player,location_name,location_address\n"
                "seed-a,Relic,1,true,1,1,A1,1\n"
                "seed-a,Relic,1,true,2,1,A2,2\n"
                "seed-a,Relic,1,true,3,1,A3,3\n"
                "seed-b,Relic,1,true,2,1,B1,4\n"
                "seed-b,Relic,1,true,10,1,B2,5\n"
                "seed-b,Relic,1,true,10,1,B3,6\n"
                "seed-c,Relic,1,true,1,1,C1,7\n"
                "seed-c,Relic,1,false,,1,C2,8\n",
                encoding="utf-8",
            )

            output = io.StringIO()
            with contextlib.redirect_stdout(output):
                result = analyze.main(
                    [
                        "item-nth-sphere",
                        str(data_dir),
                        "--item",
                        "Relic",
                        "--count",
                        "3",
                    ]
                )

            self.assertEqual(result, 0)
            self.assertEqual(
                output.getvalue().splitlines(),
                [
                    "sphere\tseeds\tpercent_of_all_seeds\tcumulative_percent_by_sphere",
                    "3\t1\t33.333\t33.333",
                    "10\t1\t33.333\t66.667",
                    "not_reached\t1\t33.333\t",
                ],
            )


if __name__ == "__main__":
    unittest.main()
