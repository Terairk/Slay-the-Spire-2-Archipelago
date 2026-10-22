import unittest

from scripts.report_power_comparison import score_inventory, score_path_inventory


class Rule:
    power_level = 10
    minimum_cards = 3

    def __init__(self, relic_cap=None, power=15):
        self.relic_cap, self.power = relic_cap, power

    def strength(self, state):
        return state.count('cards',1), self.power, 12

    def power_adjustments(self, state):
        return {'missing support':3, 'early Ancient benefit':-1}


class PowerComparisonTests(unittest.TestCase):
    def test_support_adjustment_sign_and_minimum_card_gate(self):
        branches = {None: Rule()}
        result = score_inventory(branches,True,{'cards':2})
        self.assertEqual((15,13,3), (result['power'],result['adjusted_power'],result['margin']))
        self.assertEqual(0,result['passes_power_rule'])  # Positive margin cannot waive the card floor.
        self.assertEqual(1,score_inventory(branches,True,{'cards':3})['passes_power_rule'])
        early = score_inventory(branches,False,{'cards':2})
        self.assertEqual(15,early['power'])
        for field in ('required','base_required','minimum_cards','adjusted_power','margin','passes_power_rule'):
            self.assertIsNone(early[field])

    def test_relic_cap_branches(self):
        # Walking around elites counts fewer relics than after fighting them.
        branches = {4: Rule(4, power=11), 6: Rule(6, power=14)}
        self.assertEqual((14,6), tuple(score_inventory(branches,True,{'cards':3})[k] for k in ('power','relic_cap')))
        self.assertEqual(11, score_inventory(branches,True,{'cards':3},4)['power'])
        path = score_path_inventory(branches,True,{'cards':3})
        self.assertEqual((11,-1,0,4), tuple(path[k] for k in ('path_power','path_margin','path_passes_power_rule','path_relic_cap')))


if __name__=='__main__':
    unittest.main()
