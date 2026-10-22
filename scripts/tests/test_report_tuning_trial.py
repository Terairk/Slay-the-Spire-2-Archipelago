import unittest

from scripts.report_tuning_trial import CHECKPOINTS, gap_scorecard


class TuningGapTests(unittest.TestCase):
    def test_opposite_gaps_do_not_cancel_and_checkpoints_have_equal_weight(self):
        rows=[]
        for role in ('all','starting','locked'):
            for index,cp in enumerate(CHECKPOINTS[1:]):
                for variant,power in (('release-1.1.2',100),('before-tuning',95),
                                      ('candidate',80 if index==0 else 120 if index==1 else 100)):
                    rows.append(dict(variant=variant,checkpoint=cp,character='all',role=role,
                                     adjusted_power_mean=power,observations=index+1))
        results=gap_scorecard(rows)
        candidate=next(r for r in results if r['variant']=='candidate' and r['role']=='all')
        self.assertAlmostEqual(0,candidate['mean_signed_gap_percent'])
        self.assertAlmostEqual(40/11,candidate['mean_absolute_gap_percent'])
        self.assertAlmostEqual(20,candidate['maximum_absolute_gap_percent'])
        self.assertEqual(2,candidate['checkpoints_outside_15_percent'])


if __name__=='__main__':
    unittest.main()
