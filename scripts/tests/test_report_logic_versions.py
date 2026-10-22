import sqlite3
import tempfile
import unittest
from pathlib import Path

from scripts.report_logic_versions import old_logic_fingerprints


class LogicVersionsTests(unittest.TestCase):
    def test_old_logic_fingerprints_ignore_new_logic_and_row_order(self):
        rows = [('old', 1, 'A', 'x', 0), ('old', 1, 'B', 'y', 1), ('current', 1, 'A', 'z', 0), ('old', 2, 'A', 'x', 0)]
        with tempfile.TemporaryDirectory() as folder:
            paths = [Path(folder) / 'first.sqlite', Path(folder) / 'second.sqlite']
            for path, ordered in zip(paths, (rows, list(reversed(rows)))):
                with sqlite3.connect(path) as db:
                    db.execute('CREATE TABLE placements(variant,seed,location,item,sphere)')
                    db.executemany('INSERT INTO placements VALUES (?,?,?,?,?)', ordered)
            first, second = (old_logic_fingerprints(path) for path in paths)
            self.assertEqual(first, second)
            self.assertNotEqual(first[1], first[2])
            with sqlite3.connect(paths[1]) as db:
                db.execute("UPDATE placements SET sphere=2 WHERE variant='old' AND location='B'")
            self.assertNotEqual(first[1], old_logic_fingerprints(paths[1])[1])


if __name__ == '__main__':
    unittest.main()
