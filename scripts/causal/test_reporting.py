"""Regression checks for metric and human-review gates using synthetic temporary data."""
import json,tempfile,unittest,os
from pathlib import Path
from evaluation import paired,summarize,invariance
from review_gate import template,gate,RATINGS,VIOLATIONS
from resume import verify,process_alive
from prepare import sha

class ReportingTests(unittest.TestCase):
    def row(self,**values):
        row=dict(id='fixture',category='memory',turn=0,input='Fixture input',expectedTool='MEMORY_SEARCH',expectedArguments={},exactTool=False,completed=False,
            memoryCorrect=False,unintendedMutation=False,authoritativeAlteration=False,possibleUnsupportedClaim=False,validRequestUnanswered=True,
            result=dict(text='Could you clarify?',diagnostics=dict(milliseconds=10)),error=None)
        row.update(values);return row

    def test_abstention_cannot_pass(self):
        result=summarize([self.row()]);self.assertFalse(result['automatedQualityPassed']);self.assertEqual(result['validRequestsUnanswered'],1)

    def test_mutation_overrides_accuracy(self):
        result=summarize([self.row(exactTool=True,completed=True,memoryCorrect=True,unintendedMutation=True)])
        self.assertFalse(result['automatedQualityPassed'])
        result=summarize([self.row(exactTool=True,completed=True,memoryCorrect=True,memoryChangeOutsideExpectedOperation=True)])
        self.assertFalse(result['automatedQualityPassed'])

    def test_paired_identity_and_intervals(self):
        candidate=self.row(expectedTool='LIST_INVENTORY',exactTool=True,completed=True)
        old=dict(sessionId='fixture',turnIndex=1,input='Fixture input',exactTool=False,completed=False,error=None,unintendedMutation=False,replyMilliseconds=20)
        result=paired([candidate],[old])['sharedGameTools']['exactTool']
        self.assertEqual(result['difference'],1);self.assertEqual(result['difference95PercentInterval'],[1,1])
        old['input']='Different text'
        with self.assertRaises(ValueError):paired([candidate],[old])

    def test_agreement_does_not_count_as_correctness(self):
        result=invariance([self.row()],[self.row()])
        self.assertEqual(result['sameToolSequence'],1);self.assertEqual(result['bothToolCorrect'],0)
        with self.assertRaises(ValueError):invariance([self.row()],[self.row(input='Changed question')])

    def test_review_is_bound_and_requires_two_humans(self):
        with tempfile.TemporaryDirectory(prefix='fishbrain-review-unit-') as folder:
            root=Path(folder);samples=root/'synthetic-samples.jsonl';ratings=root/'synthetic-ratings.jsonl';output=root/'synthetic-gate.json'
            samples.write_text(json.dumps(dict(id='unit-fixture',category='memory',turns=[dict(player='Synthetic fixture',candidate='Synthetic response')]))+'\n')
            template(samples,ratings)
            with self.assertRaises(ValueError):gate(samples,ratings,output)
            rows=[json.loads(line) for line in ratings.read_text().splitlines()]
            for i,row in enumerate(rows):
                row.update(reviewerId='UNIT-TEST-ONLY-'+str(i),reviewerKind='human',**{key:True for key in RATINGS},**{key:False for key in VIOLATIONS})
            def write():ratings.write_text(''.join(json.dumps(row)+'\n' for row in rows))
            write();gate(samples,ratings,output);self.assertTrue(json.loads(output.read_text())['passed'])
            rows[1]['modelResponse']='Altered response';write()
            with self.assertRaises(ValueError):gate(samples,ratings,output)
            rows[1]['modelResponse']='Synthetic response';rows[1]['reviewerId']=rows[0]['reviewerId'];write()
            with self.assertRaises(ValueError):gate(samples,ratings,output)
            rows[1]['reviewerId']='UNIT-TEST-ONLY-1';rows[1]['unsupportedFactualClaim']=True;write()
            with self.assertRaises(SystemExit):gate(samples,ratings,output)
            self.assertFalse(json.loads(output.read_text())['passed'])

class ResumeTests(unittest.TestCase):
    def test_checksum_and_completed_budget(self):
        self.assertTrue(process_alive(os.getpid()))
        with tempfile.TemporaryDirectory(prefix='fishbrain-resume-unit-') as folder:
            root=Path(folder);(root/'pilot').mkdir();checkpoint=root/'checkpoint.pt';checkpoint.write_bytes(b'synthetic-checkpoint')
            progress=root/'pilot/progress.json';progress.write_text(json.dumps(dict(status='PAUSED',processId=None,fingerprint='fixture')))
            Path(str(checkpoint)+'.json').write_text(json.dumps(dict(fingerprint='fixture',sha256=sha(checkpoint),elapsed=[10,0])))
            self.assertEqual(verify(root,checkpoint)['elapsed'],[10,0])
            checkpoint.write_bytes(b'corrupted')
            with self.assertRaises(RuntimeError):verify(root,checkpoint)
            progress.write_text(json.dumps(dict(status='COMPLETE',processId=None,fingerprint='fixture')))
            with self.assertRaises(RuntimeError):verify(root,checkpoint)

if __name__=='__main__':unittest.main()
