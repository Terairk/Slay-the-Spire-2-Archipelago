from __future__ import annotations

from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import simulate_card_drafts as drafts  # noqa: E402


class SimulateCardDraftsTests(unittest.TestCase):
    def test_stockpiled_drafting_never_trails_one_at_a_time(self):
        curves = drafts.simulate(200, 12, drafts.Assumptions(), seed=7)
        self.assertEqual(1.0, curves["one at a time"][1])
        for suffix in ("", " + archetypes"):
            sequential, stockpiled = curves["one at a time" + suffix], curves["stockpiled" + suffix]
            for count in range(13):
                with self.subTest(curve=suffix or "plain", rewards=count):
                    self.assertGreaterEqual(stockpiled[count] + 1e-9, sequential[count])
                    if count:
                        self.assertGreaterEqual(stockpiled[count], stockpiled[count - 1])


if __name__ == "__main__":
    unittest.main()
