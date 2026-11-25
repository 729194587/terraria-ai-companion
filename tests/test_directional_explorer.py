import asyncio
import sys
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import AsyncMock, patch

sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'python'))
import directional_explorer as explorer


def local(x=100, y=200, targets=None):
    targets = targets if targets is not None else [(x-300,x+300,y)]
    return dict(actor=dict(boundsWorldPixels=dict(x=x,width=20),feetY=y),
                landingRegions=dict(items=[dict(target=dict(minPlayerLeftX=a,maxPlayerLeftX=b,surfaceY=h))
                                           for a,b,h in targets]))


class DirectionTests(unittest.IsolatedAsyncioTestCase):
    def test_direction_is_relative_to_actual_left_and_feet(self):
        scene=local(targets=[(0,450,200),(80,115,160),(20,76,264)])
        self.assertGreater(explorer.destination(scene,'right')['x']-20,100)
        self.assertLess(explorer.destination(scene,'left')['x'],100)
        self.assertEqual(explorer.destination(scene,'down')['y'],264)
        self.assertEqual(explorer.destination(scene,'up')['y'],160)
        self.assertIsNone(explorer.destination(local(),'up'))

    async def trip(self, status='completed', found_at=None, complete=True, release=True, revoke=False):
        x=100
        records=[]
        samples=0
        mode=SimpleNamespace(agent=SimpleNamespace(bridge=SimpleNamespace(binding={'generation':1})),
                             version='v', objective='survey',progress={},receipt_fields=dict)
        async def scan(version):
            nonlocal samples
            samples+=1
            return dict(position=dict(leftX=x,feetY=200), complete=complete, changes=[],
                        discoveries=[dict(anchor=dict(x=60,y=1))] if samples==found_at else [])
        mode.resource_scan=scan
        async def submit(operation,args,version):
            nonlocal x
            self.assertEqual((operation,version),('navigate_to','v'))
            done=asyncio.Event()
            task_id=str(len(records)+1)
            proof=dict(actionId=task_id,status=status,reason='collision',inputsReleased=release,endEvidenceStatus='known')
            record=dict(done=done,view=dict(taskId=task_id,result=proof))
            records.append(record)
            if found_at is None and not revoke:
                if status=='completed':
                    x=args['x']-10
                done.set()
            return record,None
        async def cancel(name, args):
            self.assertEqual(args['taskId'], records[-1]['view']['taskId'])
            records[-1]['view']['result'].update(status='cancelled',reason='explicit_cancel')
            records[-1]['done'].set()
        mode.mailbox=SimpleNamespace(invoke=AsyncMock(side_effect=cancel))
        mode.submit_owned=submit
        mode.terminal=AsyncMock(side_effect=lambda r:r['view']['result'])
        async def read(*args):
            return dict(valid=True,data=local(x))
        with patch('directional_explorer.atomic.dispatch',side_effect=read), patch.object(explorer,'MAX_SEGMENTS',2), \
                patch.object(explorer,'SCAN_SECONDS',.001):
            task=asyncio.create_task(explorer.run(mode,'right','v'))
            if revoke:
                while not records:
                    await asyncio.sleep(0)
                mode.version=None
                task.cancel()
                with self.assertRaises(asyncio.CancelledError):
                    await task
                self.assertTrue(records[-1]['done'].is_set())
                self.assertEqual(len(records),1)
                return
            return (await task)['explorationResult']

    async def test_multiple_segments_recompute_then_return_budget(self):
        result=await self.trip()
        self.assertEqual(result['returnReason'],'budget_reached')
        self.assertEqual(result['progress']['completedSegments'],2)
        first, second=result['segments']
        self.assertGreater(second['target']['x'],first['target']['x'])
        self.assertEqual(first['finish'],second['start'])

    async def test_mid_segment_discovery_requires_release(self):
        self.assertEqual((await self.trip(found_at=2))['returnReason'],'discovery')
        self.assertEqual((await self.trip(found_at=2,release=False))['returnReason'],'outcome_unknown')

    async def test_observation_and_navigation_failures_are_distinct(self):
        self.assertEqual((await self.trip(complete=False))['segments'],[])
        result=await self.trip(status='failed')
        self.assertEqual((result['returnReason'],result['reason']),('blocked','collision'))
        self.assertEqual((await self.trip(status='cancelled'))['returnReason'],'interrupted')

    async def test_segment_deadline_stops_movement_and_collects_terminal(self):
        with patch.object(explorer,'SEGMENT_SECONDS',.003):
            result = await self.trip(found_at=10000)
        self.assertEqual(result['returnReason'],'budget_reached')
        self.assertEqual(result['lastActionResult']['status'],'cancelled')
        self.assertTrue(result['lastActionResult']['inputsReleased'])
        self.assertEqual(len(result['segments']),1)

    async def test_revocation_cleans_up_independent_task(self):
        await self.trip(revoke=True)
