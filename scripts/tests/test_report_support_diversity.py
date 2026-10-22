from collections import Counter
from itertools import combinations
import unittest

from scripts.report_support_diversity import (FIELDS, SUPPORT_FIELDS, analyze, ancient_analysis,
    broaden_combinations, diversity_metrics, expected_richness)


class SupportDiversityTests(unittest.TestCase):
    def test_expected_richness_matches_all_subsets_of_whole_seeds(self):
        # A appears for multiple characters in seed 0. It must count as one seed
        # incidence, not multiple independent observations.
        seeds = [('A','A','B'), ('A',), ('C',)]
        incidence = Counter(combo for seed in seeds for combo in set(seed))
        for size in range(4):
            subsets = list(combinations(range(3),size))
            expected = sum(len({combo for i in sample for combo in seeds[i]}) for sample in subsets) / len(subsets)
            self.assertAlmostEqual(expected, expected_richness(3,incidence,size))
        with self.assertRaises(ValueError):
            expected_richness(3,incidence,4)

    def test_effective_diversity_distinguishes_common_from_rare(self):
        balanced = diversity_metrics(Counter(A=10,B=10))
        concentrated = diversity_metrics(Counter(A=19,B=1))
        self.assertEqual(2,balanced['distinct'])
        self.assertAlmostEqual(2,balanced['effective_combinations'])
        self.assertEqual(2,concentrated['distinct'])
        self.assertLess(concentrated['effective_combinations'],1.3)
        self.assertEqual(95,concentrated['most_common_percent'])
        self.assertEqual(1,concentrated['singleton_combinations'])

    def test_character_observations_remain_clustered_by_seed(self):
        rows = [dict(variant='current',checkpoint='Mid Act 1',seed=42,
                     character=char,role=role,combination=combo)
                for char,role,combo in [('Silent','starting',(0,1,0,0,0)),
                                        ('Defect','locked',(0,1,0,0,0)),
                                        ('Ironclad','locked',(1,0,1,0,0))]]
        summary,counts,discovery,_=analyze(rows)
        pooled=next(r for r in summary if r['character']=='all' and r['role']=='all')
        self.assertEqual((1,3,2),(pooled['seeds'],pooled['observations'],pooled['distinct']))
        first=next(r for r in counts if r['character']=='all' and r['role']=='all' and r['rank']==1)
        self.assertEqual((2,1),(first['count'],first['seeds']))
        self.assertEqual(2,next(r['expected_distinct'] for r in discovery if r['role']=='all'))

    def test_support_comparison_does_not_confuse_character_mix_with_compensation(self):
        rows=[]
        # Pooled means differ dramatically, but each character has identical
        # support with/without its Ancient. Standardization must remove that gap.
        for character,value,missing,present in [('Silent',10,9,1),('Defect',2,1,9)]:
            for ancient,count in ((0,missing),(1,present)):
                for _ in range(count):
                    rows.append(dict(variant='current',checkpoint='Mid Act 1',character=character,
                        role='starting',seed=len(rows),ancients=ancient,adjustments={},counterfactuals={},
                        **{field:value for field in SUPPORT_FIELDS}))
        contrasts,cohorts,_=ancient_analysis(rows)
        result=next(r for r in contrasts if r['character']=='all' and r['role']=='all'
                    and r['tier']==1 and r['resource']=='card_rewards')
        self.assertAlmostEqual(9.2,result['missing_mean'])
        self.assertAlmostEqual(9.2,result['standardized_present_mean'])
        self.assertAlmostEqual(0,result['difference'])
        self.assertEqual(100,result['missing_coverage_percent'])
        # Nobody has tier 2: an unavailable comparator must not become a zero.
        absent=next(r for r in contrasts if r['character']=='all' and r['role']=='all' and r['tier']==2)
        self.assertIsNone(absent['difference'])
        self.assertEqual(0,absent['matched_missing'])

    def test_within_combination_distribution_and_positive_penalty_denominator(self):
        rows=[]
        for seed,(cards,penalty,specific) in enumerate(((2,2,True),(10,2,False),(6,0,False))):
            row=dict(variant='current',checkpoint='Late Act 1',seed=seed,character='Silent',role='starting',
                **dict.fromkeys(FIELDS,0),card_rewards=cards,rare_cards=0,relics=1,
                adjustments={'Ancient support (Anytime)':penalty},
                counterfactuals={'ordinary_relics':{'ancient_specific':specific}})
            row['combination']=tuple(row[f] for f in FIELDS)
            rows.append(row)
        _,combinations,_,_=analyze(rows)
        broaden_combinations(rows,combinations)
        r=combinations[0]
        self.assertEqual(6,r['card_rewards_median'])
        self.assertAlmostEqual(2.8,r['card_rewards_p10'])
        self.assertAlmostEqual(9.2,r['card_rewards_p90'])
        self.assertEqual(3,r['card_relic_combinations'])
        _,_,evidence=ancient_analysis(rows)
        e=next(r for r in evidence if r['character']=='all' and r['role']=='all'
               and r['tier']==1 and r['source']=='ordinary_relics')
        self.assertEqual((3,2,1,50),(e['missing_observations'],e['positive_penalty_observations'],
                                  e['ancient_specific_observations'],e['ancient_specific_percent']))


if __name__=='__main__':
    unittest.main()
