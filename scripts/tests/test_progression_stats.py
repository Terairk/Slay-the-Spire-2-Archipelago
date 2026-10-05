import sqlite3
import unittest

from scripts.progression_stats import CountOverride, SCHEMA, ingest, power_observation
from scripts.report_progression_stats import TIER_FIELDS, checkpoint_availability


class State:
    def __init__(self, cards):
        self.cards = cards

    def count(self, item, player):
        return self.cards if item == 'Silent Card Reward' else 0


class PowerRule:
    minimum_cards = 0

    def power_adjustments(self, state):
        return {'Ancient support (Anytime)': 2}

    def strength(self, state):
        cards = state.count('Silent Card Reward', 1)
        return cards, cards + 3, 5


class ProgressionStatsTests(unittest.TestCase):
    def test_disabled_families_are_omitted_rather_than_reported_missing(self):
        checkpoint = dict(variant='old', seed=7, character='Silent', role='starting',
                          checkpoint='Mid Act 1', sphere=2, ancients=1,
                          starter_cards=0, starter_relics=0, rests=0, smiths=0)
        arrivals = [dict(variant='old', seed=7, item_character='Silent',
                         family='Progressive Ancient', tier=tier, sphere=tier)
                    for tier in (1, 2, 3)]
        rows = checkpoint_availability([checkpoint], arrivals, (TIER_FIELDS[0],))
        self.assertEqual({'Progressive Ancient'}, {row['family'] for row in rows})
        self.assertEqual(100, next(row for row in rows if row['tier'] == 1)['held_percent'])
        with self.assertRaises(KeyError):
            checkpoint_availability([checkpoint], arrivals)

    def test_checkpoint_availability_excludes_same_sphere_rewards(self):
        checkpoints = []
        for checkpoint, sphere, count in [('Late Act 1', 5, 1), ('Act 1 Boss Arena', 6, 2)]:
            checkpoints.append(dict(variant='current', seed=42, character='Silent', role='starting',
                checkpoint=checkpoint, sphere=sphere, **{field: count for _,field,_ in TIER_FIELDS}))
        acquisitions = [dict(variant='current', seed=42, item_character='Silent', family=family,
                             tier=tier, sphere=(2,5,8)[tier-1])
                        for family,_,tiers in TIER_FIELDS for tier in range(1,tiers+1)]
        result = checkpoint_availability(checkpoints, acquisitions)
        pooled = {(r['checkpoint'],r['family'],r['tier']): r
                  for r in result if r['character']=='all' and r['role']=='all'}
        card = pooled[('Late Act 1','Progressive Starter Card',2)]
        self.assertEqual((0,100,0), tuple(card[k+'_percent'] for k in ('held','same_sphere','later')))
        card = pooled[('Act 1 Boss Arena','Progressive Starter Card',2)]
        self.assertEqual(100, card['held_percent'])
        self.assertEqual(100, pooled[('Act 1 Boss Arena','Progressive Smith',3)]['later_percent'])
        for row in result:
            self.assertEqual(1, row['observations'])
            self.assertEqual(100, sum(row[k+'_percent'] for k in ('held','same_sphere','later')))
        checkpoints[0]['starter_cards'] = 2
        with self.assertRaises(AssertionError):
            checkpoint_availability(checkpoints, acquisitions)

    def test_ablation_distinguishes_replacement_from_baseline_power(self):
        state = State(2)
        *_, cf = power_observation(PowerRule(), state, 'Silent')
        self.assertTrue(cf['ordinary_and_rare_cards']['ancient_specific'])
        self.assertFalse(cf['ordinary_and_rare_cards']['passes'])
        self.assertEqual(2, state.cards)
        self.assertEqual(0, CountOverride(state, {('Silent Card Reward', 1): 0}).count('Silent Card Reward', 1))

        class StrongerGate(PowerRule):
            def strength(self, state):
                cards = state.count('Silent Card Reward', 1)
                return cards, cards + 3, 7
        *_, cf = power_observation(StrongerGate(), State(4), 'Silent')
        self.assertFalse(cf['ordinary_and_rare_cards']['ancient_specific'])

    def test_same_sphere_copies_have_same_acquisition_timing(self):
        db = sqlite3.connect(':memory:')
        db.executescript(SCHEMA)
        rows = [[location, n, 'Silent Mid Act 1', 'Silent', 'Silent Progressive Ancient', 4,
                 'Silent', 1, sphere, 0, 0] for n, (location, sphere) in enumerate((('A', 2), ('B', 2), ('C', 4)))]
        ingest(db, ('current', 42, 'success', 1, None, {'placements': rows}))
        self.assertEqual([(1, 2), (2, 2), (3, 4)], db.execute(
            'SELECT tier,sphere FROM tier_acquisition ORDER BY tier').fetchall())

    def test_ingestion_is_atomic(self):
        db = sqlite3.connect(':memory:')
        db.executescript(SCHEMA)
        with self.assertRaises(sqlite3.OperationalError):
            ingest(db, ('current', 42, 'success', 1, None, {'placements': [['invalid row']]}))
        self.assertEqual(0, db.execute('SELECT count(*) FROM runs').fetchone()[0])


if __name__ == '__main__':
    unittest.main()
