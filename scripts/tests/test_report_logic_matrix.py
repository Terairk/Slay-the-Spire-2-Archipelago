import sqlite3
from pathlib import Path
import tempfile
import unittest

from scripts.progression_stats import SCHEMA
from scripts.report_logic_matrix import combine_databases


class LogicMatrixTests(unittest.TestCase):
    def test_same_seed_in_different_configurations_keeps_its_own_inventory_and_denominator(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            cases = [dict(name='on', starters=True, campfires=True),
                     dict(name='off', starters=False, campfires=False)]
            for case, total in zip(cases, (10, 20)):
                folder = root / case['name']
                (folder / 'power-report').mkdir(parents=True)
                with sqlite3.connect(folder / 'results.sqlite') as db:
                    db.executescript(SCHEMA)
                    db.execute('INSERT INTO runs(variant,seed,status,starting_character,location_count) VALUES (?,?,?,?,?)',
                               ('old', 42, 'success', 'Silent', total))
                    db.execute('INSERT INTO spheres VALUES (?,?,?,?,?,?)', ('old', 42, 1, 3, 5, 0))
                    db.execute('INSERT INTO checkpoints(variant,seed,sphere,character,checkpoint,first_reached) VALUES (?,?,?,?,?,?)',
                               ('old', 42, 1, 'Silent', 'Mid Act 1', 1))
                    db.execute('INSERT INTO inventory VALUES (?,?,?,?,?)', ('old', 42, 1, 'Silent Relic', total))
                with sqlite3.connect(folder / 'power-report/power_comparison.sqlite') as db:
                    db.execute('CREATE TABLE checkpoint_power(variant TEXT,seed INTEGER,character TEXT,checkpoint TEXT,power REAL)')
                    db.execute('INSERT INTO checkpoint_power VALUES (?,?,?,?,?)', ('old', 42, 'Silent', 'Mid Act 1', total))
            combine_databases(root, cases)
            with sqlite3.connect(root / 'results.sqlite') as db:
                self.assertEqual([('off', .25), ('on', .5)], db.execute(
                    'SELECT configuration,check_fraction_before FROM first_checkpoints ORDER BY configuration').fetchall())
                self.assertEqual([('off', 20), ('on', 10)], db.execute(
                    'SELECT configuration,count FROM inventory ORDER BY configuration').fetchall())
                self.assertEqual(2, db.execute('SELECT count(*) FROM converted_power').fetchone()[0])


if __name__ == '__main__':
    unittest.main()
