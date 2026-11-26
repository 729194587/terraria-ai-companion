import asyncio
import sys
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import AsyncMock, patch

sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'python'))
import directional_explorer as explorer


def local(x=100, y=200, targets=None):
    targets = targets if targets is not None else [(x-600,x+600,y)]
    return dict(actor=dict(boundsWorldPixels=dict(x=x,width=20),feetY=y),
                landingRegions=dict(truncated=False, items=[dict(target=dict(minPlayerLeftX=a,maxPlayerLeftX=b,surfaceY=h))
                                           for a,b,h in targets]))


class DirectionTests(unittest.IsolatedAsyncioTestCase):
    def test_direction_is_relative_to_actual_left_and_feet(self):
        scene=local(targets=[(0,450,200),(80,115,160),(20,76,264)])
        self.assertGreater(explorer.destination(scene,'right')['x']-20,100)
        self.assertLess(explorer.destination(scene,'left')['x'],100)
        self.assertEqual(explorer.destination(scene,'down')['y'],264)
        self.assertEqual(explorer.destination(scene,'up')['y'],160)
        self.assertIsNone(explorer.destination(local(),'up'))

    def test_all_directions_prefer_32_tiles(self):
        scene=local(800,800,[(0,1600,800),(750,850,768),(750,850,288),(750,850,832),(750,850,1312)])
        for direction, target in [('right',(1322,800)),('left',(298,800)),('up',(810,288)),('down',(810,1312))]:
            with self.subTest(direction=direction):
                self.assertEqual(explorer.destination(scene,direction),dict(zip(('x','y'),target)))

    async def trip(self, status='completed', found_at=None, complete=True, release=True, revoke=False,
                   hang=False, truncated=(), end_complete=True, empty_geometry=None):
        x=100
        records=[]
        order=[]
        caps=[]
        binding={'generation':1}
        bridge=SimpleNamespace(binding=binding, hello={'capabilities':['server_ai_directional_landings_v1']})
        mode=SimpleNamespace(agent=SimpleNamespace(bridge=bridge), version='v', objective='survey',
                             progress={},receipt_fields=dict)
        baseline=dict(position=dict(leftX=x,feetY=200),complete=complete,gameUpdate=100)
        async def scan(version):
            self.assertTrue(records[-1]['done'].is_set())
            self.assertTrue(release)
            self.assertEqual(order[-1],'terminal')
            order.append('scan')
            return dict(position=dict(leftX=x,feetY=200), complete=end_complete, changes=[],
                        discoveries=[dict(anchor=dict(x=60,y=1))] if len(records)==found_at else [])
        mode.resource_scan=scan
        async def submit(operation,args,version):
            nonlocal x
            self.assertEqual((operation,version),('navigate_to','v'))
            done=asyncio.Event()
            task_id=str(len(records)+1)
            proof=dict(actionId=task_id,status=status,reason='collision',inputsReleased=release,endEvidenceStatus='known')
            record=dict(done=done,view=dict(taskId=task_id,result=proof))
            records.append(record)
            order.append('submit')
            if not hang and not revoke:
                if status=='completed':
                    x=args['x']-10
                asyncio.get_running_loop().call_later(.005,done.set)
            return record,None
        async def cancel(name, args):
            self.assertEqual(name,'cancel_task')
            self.assertEqual(args['taskId'], records[-1]['view']['taskId'])
            order.append('cancel')
            records[-1]['view']['result'].update(status='cancelled',reason='explicit_cancel')
            records[-1]['done'].set()
        async def terminal(record):
            order.append('terminal')
            return record['view']['result']
        mode.mailbox=SimpleNamespace(invoke=AsyncMock(side_effect=cancel))
        mode.submit_owned=submit
        mode.terminal=terminal
        async def read(bridge,name,args):
            scene=local(x)
            self.assertEqual(args['landingDirection'],'right')
            if empty_geometry is not None:
                scene['landingRegions']['items'] = []
                scene['coverage'] = dict(geometryComplete=empty_geometry)
            caps.append(args['maxRegions'])
            scene['landingRegions']['truncated']=len(caps) in truncated
            if scene['landingRegions']['truncated']:
                scene['landingRegions']['items']=[]
            return dict(valid=True,data=scene)
        with patch('directional_explorer.atomic.dispatch',side_effect=read), patch.object(explorer,'MAX_SEGMENTS',2):
            task=asyncio.create_task(explorer.run(mode,'right','v',baseline))
            if revoke:
                while not records:
                    await asyncio.sleep(0)
                mode.version=None
                task.cancel()
                with self.assertRaises(asyncio.CancelledError):
                    await task
                self.assertTrue(records[-1]['done'].is_set())
                self.assertEqual(order,['submit','cancel'])
                return
            self.receipt = await task
            result=self.receipt['explorationResult']
            self.assertEqual(result['baseline']['gameUpdate'],100)
            self.order, self.caps = order, caps
            return result

    async def test_multiple_segments_recompute_then_return_budget(self):
        result=await self.trip()
        self.assertEqual(result['returnReason'],'budget_reached')
        self.assertEqual(self.receipt['status'],'completed')
        self.assertTrue(all(s['receipt']['status']=='completed' for s in result['segments']))
        self.assertEqual(result['progress']['completedSegments'],2)
        self.assertEqual(self.order,['submit','terminal','scan']*2)
        self.assertEqual(len(result['coverage']),2)
        first, second=result['segments']
        self.assertGreater(second['target']['x'],first['target']['x'])
        self.assertEqual(first['finish'],second['start'])

    async def test_discovery_returns_after_first_release(self):
        result=await self.trip(found_at=1)
        self.assertEqual(result['returnReason'],'discovery')
        self.assertEqual(self.order,['submit','terminal','scan'])
        self.assertEqual(result['lastActionResult']['status'],'completed')
        self.assertEqual((await self.trip(found_at=2,release=False))['returnReason'],'outcome_unknown')

    async def test_observation_and_navigation_failures_are_distinct(self):
        self.assertEqual((await self.trip(complete=False))['segments'],[])
        self.assertEqual(self.order,[])
        self.assertEqual((await self.trip(end_complete=False))['returnReason'],'observation_incomplete')
        self.assertEqual(self.order,['submit','terminal','scan'])
        self.assertEqual((await self.trip(empty_geometry=True))['returnReason'],'blocked')
        self.assertEqual((await self.trip(empty_geometry=False))['returnReason'],'observation_incomplete')
        result=await self.trip(status='failed')
        self.assertEqual((result['returnReason'],result['reason']),('blocked','collision'))
        self.assertEqual((await self.trip(status='cancelled'))['returnReason'],'interrupted')

    async def test_candidate_cap_expands_once_then_reports_truncation(self):
        result=await self.trip(truncated=(1,))
        self.assertEqual(self.caps,[12,24,12])
        self.assertEqual(result['returnReason'],'budget_reached')
        result=await self.trip(truncated=(1,2))
        self.assertEqual(self.caps,[12,24])
        self.assertEqual(result['returnReason'],'observation_incomplete')
        self.assertIn('landingRegions.truncated',result['reason'])
        self.assertEqual(self.order,[])

    async def test_segment_deadline_stops_movement_and_collects_terminal(self):
        with patch.object(explorer,'SEGMENT_SECONDS',.003):
            result = await self.trip(hang=True, found_at=1)
        self.assertEqual(result['returnReason'],'segment_time_limit')
        self.assertEqual(self.receipt['status'],'timeout')
        self.assertFalse(self.receipt['completed'])
        self.assertEqual(result['lastActionResult']['status'],'cancelled')
        self.assertTrue(result['lastActionResult']['inputsReleased'])
        self.assertEqual(len(result['findings']),1)
        self.assertEqual(self.order,['submit','cancel','terminal','scan'])

    async def test_trip_deadline_expires_during_navigation(self):
        # Keep admission inside the trip budget even on a slow test host.
        with patch.object(explorer,'time',SimpleNamespace(monotonic=lambda: 0.)), patch.object(explorer,'TRIP_SECONDS',.003):
            result = await self.trip(hang=True)
        self.assertEqual(result['returnReason'],'budget_reached')
        self.assertEqual(result['progress']['completedSegments'],0)
        self.assertEqual(self.order,['submit','cancel','terminal','scan'])

    async def test_segment_timeout_requires_release_before_observation(self):
        with patch.object(explorer,'SEGMENT_SECONDS',.003):
            result = await self.trip(hang=True, release=False)
        self.assertEqual(result['returnReason'],'outcome_unknown')
        self.assertEqual(self.order,['submit','cancel','terminal'])

    async def test_revocation_cleans_up_independent_task(self):
        await self.trip(revoke=True)
