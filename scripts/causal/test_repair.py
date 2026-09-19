"""Check supervision alignment and curriculum boundaries before the small learning run."""
import collections,unittest
from repair_data import build,ITEMS
from repair_train import batch
from repair_evaluate import paired
from repair_audit import conflicting_calls,audit

class RepairChecks(unittest.TestCase):
    def test_mask_shift_and_padding(self):
        x,y=batch([dict(tokens=[1,4,29,8,5,40,41,2],promptLength=5),dict(tokens=[1,4,8,5,50,2],promptLength=4)])
        self.assertEqual(x[0].tolist(),[1,4,29,8,5,40,41])
        self.assertEqual(y[0].tolist(),[-100,-100,-100,-100,40,41,2])
        self.assertEqual(y[1].tolist(),[-100,-100,-100,50,2,-100,-100])

    def test_item_coverage_and_family_isolation(self):
        families=collections.defaultdict(set);coverage=set()
        for e in build():
            families[e['family']].add(e['split'])
            self.assertTrue(4<=len(e['steps'])<=8)
            for s in e['steps']:
                c=s.get('call',{})
                if e['split']=='train':coverage.add((c.get('name'),c.get('arguments',{}).get('ITEM')))
        self.assertTrue(all(len(s)==1 for s in families.values()))
        self.assertTrue(all((action,item) in coverage for action in ['BUY','SELL','LOOKUP_PRICE'] for item in ITEMS))

    def test_focus_excludes_controls_and_groups_augmentation(self):
        def row(id,turn,correct):return dict(id=id,turn=turn,input='fixture',expectedTool='LOOKUP_PRICE',expectedArguments={'ITEM':'ROPE'},exactTool=correct,completed=correct)
        candidate=[row('a',0,True),row('a',1,False),row('b',0,True)]
        baseline=[row('a',0,False),row('a',1,True),row('b',0,False)]
        result=paired(candidate,baseline,{('a',0),('b',0)},{'a':'one-style','b':'one-style'})['exactTool']
        self.assertEqual(result['applicable'],2);self.assertEqual(result['families'],1)
        self.assertEqual(result['candidate'],1);self.assertEqual(result['baseline'],0)
        self.assertEqual(result['difference95Interval'],[1,1])
        baseline[0]['expectedTool']='GET_BALANCE'
        with self.assertRaises(RuntimeError):paired(candidate,baseline,{('a',0),('b',0)},{'a':'one-style','b':'one-style'})

    def test_packed_context_conflicts_are_not_hidden_by_episode_ids(self):
        import json
        def record(id,item):return dict(id=id,split='train',tokens=[1,4,20,8,5,30],promptLength=5,
            target=json.dumps(dict(type='tool_call',name='LOOKUP_PRICE',arguments={'ITEM':item})))
        examples=[record('sword-history','IRON SWORD'),record('rope-history','ROPE')]
        self.assertEqual(conflicting_calls(examples),[[0,1]])
        examples[1]['tokens'][2]=21
        self.assertEqual(conflicting_calls(examples),[])
        examples=[record('same','ROPE'),record('same2','ROPE')]
        self.assertEqual(conflicting_calls(examples),[])

    def test_reference_audit_checks_current_turn_not_an_older_repetition(self):
        import json
        class Tokenizer:
            def decode(self,ids):return ''.join(chr(i-16) for i in ids)
        def player(sequence,text):return [4]+[ord(c)+16 for c in str(sequence)+':'+text]+[8]
        target=json.dumps(dict(type='tool_call',name='LOOKUP_PRICE',arguments={'ITEM':'ROPE'}))
        history=[dict(role='player',sequence=0,text='Price of rope?'),dict(role='assistantToolCall',sequence=1,text=target),
            dict(role='player',sequence=4,text='Could you repeat that price?')]
        earlier=player(4,history[-1]['text']);later=earlier+player(8,'Price of rope, please.')
        records=[dict(id='case',split='train',tokens=prefix+[5,90],promptLength=len(prefix)+1,target=target) for prefix in [later,earlier]]
        conflicts,excluded,lost=audit(records,[dict(id='case',targets=[dict(history=history,target=target)])],Tokenizer())
        self.assertFalse(conflicts);self.assertEqual(set(excluded),{1});self.assertEqual(len(lost),1)

if __name__=='__main__':unittest.main()
