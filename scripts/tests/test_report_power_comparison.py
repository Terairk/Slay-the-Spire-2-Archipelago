import unittest

from scripts.report_power_comparison import score_inventory


class PowerComparisonTests(unittest.TestCase):
    def test_support_adjustment_sign_and_minimum_card_gate(self):
        class Rule:
            power_level = 10
            minimum_cards = 3

            def strength(self, state):
                return state.count('cards',1), 15, 12

            def power_adjustments(self, state):
                return {'missing support':3, 'early Ancient benefit':-1}

        rule = Rule()
        result = score_inventory(rule,True,{'cards':2})
        self.assertEqual((15,13,3), (result['power'],result['adjusted_power'],result['margin']))
        self.assertEqual(0,result['passes_power_rule'])  # Positive margin cannot waive the card floor.
        self.assertEqual(1,score_inventory(rule,True,{'cards':3})['passes_power_rule'])
        early = score_inventory(rule,False,{'cards':2})
        self.assertEqual(15,early['power'])
        for field in ('required','base_required','minimum_cards','adjusted_power','margin','passes_power_rule'):
            self.assertIsNone(early[field])


if __name__=='__main__':
    unittest.main()
