import copy
import sys
import unittest
from pathlib import Path

sys.path.insert(0,str(Path(__file__).parent))
from common import public_response_reason, public_context_reason
from compile import isolate
from sampling import weights
from episodes import all_episodes


class CorpusTests(unittest.TestCase):
    def test_authored_diversity_and_roles(self):
        episodes=all_episodes()
        self.assertGreaterEqual(len({tuple(e['turns']) for e in episodes}),240)
        self.assertEqual(len({e['id'] for e in episodes}),len(episodes))
        self.assertTrue(all(len(e['turns'])%2==1 for e in episodes))

    def test_claims_and_technical_context_are_excluded(self):
        for text in ('MY NAME IS ANDY.','I LIVE IN FRANCE.','YOUR BALANCE IS TEN GOLD.','YOU CAN CALL ME OPEN ASSISTANT.',
                     'HE WAS AN ENGLISH POET. DOES THAT HELP?','THE SYMBOL FOR NITROGEN IS N. ANY QUESTIONS?'):
            self.assertIsNotNone(public_response_reason(text),text)
        self.assertIsNone(public_response_reason('THAT SOUNDS INTERESTING. WHAT DO YOU ENJOY ABOUT IT?'))
        self.assertIsNotNone(public_context_reason(['PLEASE HELP ME WITH PYTHON']))

    def test_actual_sampling_cap_and_marginals(self):
        rows=[]
        for pool in ('public','authored'):
            for history in (1,3,7):
                for i in range(100):
                    rows.append(dict(id=f'{pool}-{history}-{i}',training=dict(pool=pool,responseEligible=True),
                                     turns=['x']*history,response=f'{pool} {history} {i}'))
        # Flood a response with duplicate rows: probability remains bounded, not row-balanced.
        for i in range(400): rows.append(dict(rows[0],id='duplicate-'+str(i)))
        actual,report=weights(rows)
        self.assertAlmostEqual(sum(actual.values()),1,places=8)
        self.assertLessEqual(report['maximumExactResponseMass'],.005+1e-10)
        self.assertAlmostEqual(report['sourceMass']['public'],.6,places=8)
        self.assertAlmostEqual(report['historyMass']['fresh'],.25,places=8)

    def test_infeasible_distribution_fails(self):
        rows=[dict(id=str(i),training=dict(pool='public',responseEligible=True),turns=['x'],response='SAME') for i in range(1000)]
        with self.assertRaises(ValueError): weights(rows)

    def test_alternate_answers_stay_together(self):
        def row(id,text,response,split=None):
            return dict(id=id,source='TEST',turns=[dict(speaker='PLAYER',text=text)],response=response,
                        publishedSplit=split,training=dict(augmentationFamily=id,pool='public'))
        rows=[row('a','HELLO','HELLO THERE'),row('b','HELLO','GOOD MORNING')]
        accepted,_=isolate(rows,lambda *args:None)
        self.assertEqual(accepted[0]['split'],accepted[1]['split'])
        self.assertEqual(accepted[0]['isolationComponent'],accepted[1]['isolationComponent'])
        rows=[row('a','HELLO','HELLO THERE','train'),row('b','HELLO','GOOD MORNING','test')]
        rejected=[]
        accepted,_=isolate(rows,lambda *args:rejected.append(args))
        self.assertEqual(accepted,[])
        self.assertEqual(len(rejected),2)

    def test_near_duplicate_family_isolation(self):
        prefix='WE DISCUSSED OUR JOURNEY BY THE RIVER THEN CHANGED THE SUBJECT TO MUSIC AND WALKING BEFORE TURNING TO STORIES ABOUT FRIENDSHIP AND PATIENCE DURING A LONG QUIET EVENING WITH NO PARTICULAR HURRY TO LEAVE THE COMFORTABLE QUIET PLACE '
        rows=[dict(id=str(i),source='TEST',turns=[dict(speaker='PLAYER',text=prefix+end)],response='THAT SOUNDS NICE',
                   publishedSplit=None,training=dict(augmentationFamily=str(i),pool='authored')) for i,end in enumerate(['TODAY','YESTERDAY'])]
        accepted,report=isolate(rows,lambda *args:None)
        self.assertEqual(accepted[0]['isolationComponent'],accepted[1]['isolationComponent'])
        self.assertGreater(report['nearDuplicatePairs'],0)


if __name__=='__main__': unittest.main()
