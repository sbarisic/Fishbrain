import json,subprocess,tempfile,unittest
from pathlib import Path
from context_data import build,challenge,DLL,FORMAT

class ContextChecks(unittest.TestCase):
    def test_unannotated_source_is_conservative(self):
        with tempfile.TemporaryDirectory(prefix='fishbrain-source-') as d:
            root=Path(d);p=root/'prepared';p.mkdir()
            (p/'tokenizer.json').write_bytes(Path('data/causal-v1/tokenizer.json').read_bytes())
            plan=dict(id='missing-annotation',family='fixture',split='train',pool='tools',steps=[
                dict(player='rope '*900,text='Okay.'),
                dict(player='Repeat that price.',call=dict(name='LOOKUP_PRICE',arguments=dict(ITEM='ROPE')))])
            source=root/'plans.jsonl';source.write_text(json.dumps(plan)+'\n',encoding='utf8')
            current=Path('Fishbrain/bin/Release/net10.0/Fishbrain.dll')
            subprocess.run(['dotnet',current,'compile-trajectories',source,p/'episodes.jsonl'],check=True,capture_output=True)
            e=json.loads((p/'episodes.jsonl').read_text())
            self.assertEqual(e['targets'][1]['requiredSequences'],[0,2])
            subprocess.run(['dotnet',current,'pack-corpus',root,root/'packed.jsonl'],check=True,capture_output=True)
            self.assertEqual((root/'packed.jsonl').read_text(),'')

    def test_quantities_and_dependencies(self):
        episodes=build();coverage=set()
        for e in episodes:
            self.assertEqual(len(e['steps']),4)
            for i,s in enumerate(e['steps']):
                self.assertTrue(all(0<=n<=i for n in s['requiredSteps']))
                c=s.get('call',{});a=c.get('arguments',{})
                if c.get('name')=='BUY':coverage.add((a['ITEM'],a['QUANTITY']))
        self.assertTrue(all((item,n) in coverage for item in ['ROPE','IRON SWORD','HEALTH POTION'] for n in [1,2,3,4,5,7,10]))

    def test_whole_conversations_are_distinct(self):
        training={'\n'.join(s['player'].lower() for s in e['steps']) for e in build()}
        self.assertFalse(any('\n'.join(s['player'].lower() for s in c['steps']) in training for c in challenge()['cases']))

    def test_native_packer_masks_lost_dependency(self):
        with tempfile.TemporaryDirectory(prefix='fishbrain-context-') as d:
            root=Path(d);p=root/'prepared';p.mkdir()
            (p/'tokenizer.json').write_bytes(Path('data/causal-v1/tokenizer.json').read_bytes())
            history=[dict(role='player',text='rope '*900,sequence=0),dict(role='assistant',text='All right.',sequence=1),
                     dict(role='player',text='Repeat that price.',sequence=2)]
            target=json.dumps(dict(type='tool_call',name='LOOKUP_PRICE',arguments=dict(ITEM='ROPE')))
            e=dict(id='fixture',family='fixture',split='train',pool='tools',targets=[dict(history=history,target=target,requiredSequences=[0])])
            (p/'episodes.jsonl').write_text(json.dumps(e)+'\n',encoding='utf8')
            output=root/'packed.jsonl'
            subprocess.run(['dotnet',DLL,'pack-corpus',root,output],check=True,capture_output=True)
            self.assertEqual(output.read_text(),'')
            audit=json.loads(Path(str(output)+'.audit.json').read_text())
            self.assertEqual(audit['excluded'],{'REQUIRED_HISTORY_EVICTED':1})
            e['targets'][0]['requiredSequences']=[]
            (p/'episodes.jsonl').write_text(json.dumps(e)+'\n',encoding='utf8')
            subprocess.run(['dotnet',DLL,'pack-corpus',root,output],check=True,capture_output=True)
            row=json.loads(output.read_text())
            self.assertEqual(row['promptFormat'],FORMAT)
            self.assertEqual(row['retainedSequences'],[2])
            self.assertLessEqual(row['promptLength']+256,1024)

if __name__=='__main__':unittest.main()
