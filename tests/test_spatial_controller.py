import asyncio
from pathlib import Path
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import AsyncMock, patch

sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'python'))
import directional_explorer as explorer
from exploration_session import ExplorationSession
from test_exploration_v2 import scene, window


class ControllerTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        self.tmp=tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.view=scene([('a',1,4,8),('b',6,10,8)],['a'])
        self.view['coverage']={'geometryComplete':True}
        self.view['bounds']=dict(minX=0,maxX=23,minY=0,maxY=10)
        self.visits=[]
        self.receipts=[]
        self.behaviors=[]
        self.now=0
        self.active=False
        self.resource=None
        self.bridge=SimpleNamespace(hello={'capabilities':['server_ai_exploration_v2','server_ai_passages_v1']},
                                    binding={'generation':1},timeout=1)
        self.mode=SimpleNamespace(version=1,objective='survey',progress={},agent=SimpleNamespace(bridge=self.bridge),
                                  mailbox=SimpleNamespace(invoke=AsyncMock()),read_scene=AsyncMock(
                                      return_value={'valid':True,'data':self.view}),submit_owned=self.submit,
                                  terminal=self.terminal,resource_scan=self.scan,receipt_fields=dict)
        async def map_read(*args):
            return window(feet=self.visits)
        for name, replacement in [
                ('map_window',map_read),('ExplorationSession',lambda ids:ExplorationSession(ids,self.tmp.name)),
                ('time',SimpleNamespace(monotonic=lambda:self.now))]:
            patcher=patch('spatial_explorer.'+name,replacement)
            patcher.start()
            self.addCleanup(patcher.stop)

    async def submit(self,op,args,version):
        self.assertFalse(self.active)
        self.active=True
        status,visit,release,duration=self.behaviors.pop(0)
        task=str(len(self.receipts)+1)
        done=asyncio.Event()
        receipt=dict(actionId=task,status=status,reason='fixture',inputsReleased=release,endEvidenceStatus='known')
        self.receipts.append(receipt)
        def end():
            if visit:
                self.visits.append((args['x']//16,args['y']//16))
                self.view['actor']['boundsWorldPixels']['x']=args['x']-10
            self.now+=duration
            done.set()
        asyncio.get_running_loop().call_later(.01,end)
        return dict(done=done,view=dict(taskId=task,result=receipt,action={'task':{'phase':'paused'}})),None

    async def terminal(self,record):
        self.assertTrue(record['done'].is_set())
        self.active=False
        return record['view']['result']

    async def scan(self,version):
        self.assertFalse(self.active)
        hits=[self.resource] if self.resource else []
        return dict(position=explorer.position(self.view['actor']),items=hits,discoveries=hits)

    async def trip(self,seek=None):
        return await explorer.run(self.mode,None,1,await self.scan(1),seek=seek)

    async def test_terminal_is_not_a_visit_then_other_arm_crosses_checkpoint(self):
        self.behaviors=[('completed',False,True,2),('completed',True,True,60)]
        result=(await self.trip())['explorationResult']
        self.assertEqual(result['returnReason'],'budget_reached')
        first,second=result['segments']
        self.assertNotEqual(first['goalId'],second['goalId'])
        self.assertEqual([first['surveyed'],second['surveyed']],[False,True])
        self.assertEqual(second['phases'],['paused'])
        self.assertEqual(result['progress']['surveyedGoals'],1)
        self.mode.mailbox.invoke.assert_not_awaited()

    async def test_release_unknown_prevents_next_dispatch(self):
        self.behaviors=[('failed',False,False,1)]
        result=await self.trip()
        self.assertFalse(result['inputsReleased'])
        self.assertEqual(result['explorationResult']['returnReason'],'outcome_unknown')
        self.assertEqual(len(self.receipts),1)

    async def test_interrupt_reports_position_after_body_release(self):
        self.behaviors=[('cancelled',True,True,18)]
        result=(await self.trip())['explorationResult']
        self.assertEqual(result['returnReason'],'interrupted')
        self.assertNotEqual(result['start'],result['finish'])
        self.assertEqual(result['finish'],result['segments'][0]['finish'])
        self.assertEqual(result['progress']['newFootTiles'],1)
        self.assertEqual(result['progress']['surveyedGoals'],0)

    async def test_investigation_then_resume_without_repeating_discovery(self):
        self.resource=dict(kind='chest',anchor=dict(x=2,y=7))
        first=(await self.trip('chest'))['explorationResult']
        self.assertEqual((first['returnReason'],first['segments']),('discovery',[]))
        # Another tool handled this finding. A fresh host can use the same database.
        self.behaviors=[('completed',True,True,50)]
        second=(await self.trip('chest'))['explorationResult']
        self.assertEqual(second['progress']['surveyedGoals'],1)
        self.assertEqual(second['findings'],[])

    async def test_failure_switches_arm_and_manual_cancel_preserves_target(self):
        self.behaviors=[('failed',False,True,1),('cancelled',False,True,1)]
        first=(await self.trip())['explorationResult']
        self.assertEqual(first['returnReason'],'interrupted')
        self.behaviors=[('completed',True,True,50)]
        second=(await self.trip())['explorationResult']
        self.assertEqual(second['segments'][0]['goalId'],first['segments'][1]['goalId'])
