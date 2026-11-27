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
                   hang=False, truncated=(), end_complete=True, empty_geometry=None,
                   seek='any', statuses=(), offsets=(), memory=None):
        x=100
        records=[]
        order=[]
        caps=[]
        binding={'generation':1}
        bridge=SimpleNamespace(binding=binding, hello={'capabilities':['server_ai_directional_landings_v1']})
        mode=SimpleNamespace(agent=SimpleNamespace(bridge=bridge), version='v', objective='survey',
                             progress={},receipt_fields=dict,passage_memory=memory or explorer.PassageMemory())
        baseline=dict(position=dict(leftX=x,feetY=200),complete=complete,gameUpdate=100)
        async def scan(version):
            self.assertTrue(records[-1]['done'].is_set())
            self.assertTrue(release)
            self.assertEqual(order[-1],'terminal')
            order.append('scan')
            return dict(position=dict(leftX=x,feetY=200), complete=end_complete, changes=[],
                        discoveries=[dict(kind='chest',anchor=dict(x=60,y=1))] if len(records)==found_at else [])
        mode.resource_scan=scan
        async def submit(operation,args,version):
            nonlocal x
            self.assertEqual((operation,version),('navigate_to','v'))
            done=asyncio.Event()
            task_id=str(len(records)+1)
            outcome = statuses[len(records)] if statuses else status
            proof=dict(actionId=task_id,status=outcome,reason='collision',inputsReleased=release,endEvidenceStatus='known')
            record=dict(done=done,view=dict(taskId=task_id,result=proof))
            records.append(record)
            order.append('submit')
            if not hang and not revoke:
                if outcome=='completed':
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
            scene=local(x,targets=[(x+d,x+d,200) for d in offsets] if offsets else None)
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
            task=asyncio.create_task(explorer.run(mode,'right','v',baseline,seek=seek))
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

    async def test_confirmed_collision_tries_a_different_nearer_landing(self):
        result=await self.trip(offsets=(128,256),statuses=('failed','completed'))
        self.assertEqual([s['target']['x'] for s in result['segments']], [366,238])
        self.assertEqual((result['returnReason'],result['progress']['newStances']),('budget_reached',1))
        self.assertEqual(self.order,['submit','terminal','scan']*2)
        unknown=await self.trip(offsets=(128,256), release=False, status='failed')
        self.assertEqual((unknown['returnReason'],len(unknown['segments'])),('outcome_unknown',1))

    async def test_seek_records_unrelated_findings_and_continues(self):
        result=await self.trip(found_at=1,seek='Terraria/Iron')
        self.assertEqual((len(result['segments']),result['returnReason']), (2,'budget_reached'))
        self.assertEqual(result['findings'][0]['items'][0]['kind'],'chest')
        self.assertEqual((await self.trip(found_at=1,seek='chest'))['returnReason'],'discovery')

    def test_branch_memory_retains_return_and_untried_sibling(self):
        memory=explorer.PassageMemory()
        fork=dict(junction=True,returnTarget=dict(x=320,y=160),exits=[
            dict(direction=side,kind='covered_passage',target=dict(x=x,y=160)) for side,x in [('left',160),('right',480)]])
        self.assertTrue(memory.observe(fork,100))
        before=memory.summary()
        memory.arrived(fork['exits'][0]['target'])
        self.assertFalse(memory.observe(fork,110))
        self.assertEqual([e['state'] for e in memory.summary()[0]['exits']],['arrived','untried'])
        self.assertEqual(before[0]['exits'][0]['state'],'untried')
        origin,target=fork['returnTarget'],fork['exits'][1]['target']
        memory.reject(origin,target,120)
        self.assertTrue(memory.failed(origin,target,121))
        self.assertFalse(memory.failed(target,origin,121))
        self.assertFalse(memory.failed(origin,target,420))
        memory.observe(fork,420)
        self.assertEqual(memory.summary()[0]['exits'][1]['state'],'untried')

    def test_dense_resource_list_allows_free_exploration_and_passage_goal(self):
        scan=dict(complete=False,truncated=True,unknownCells=0,scannedTiles=8160,requestedTiles=8160,
                  matchedCount=43,items=[{}]*32,objectGroupingTruncated=False,samplingBudgetReached=False)
        self.assertTrue(explorer.sufficient_scan(scan,'passage'))
        self.assertTrue(explorer.sufficient_scan(scan,'any'))
        self.assertFalse(explorer.sufficient_scan(scan,'chest'))
        self.assertFalse(explorer.sufficient_scan(dict(scan,unknownCells=1),'passage'))
        self.assertFalse(explorer.sufficient_scan(dict(scan,objectGroupingTruncated=True),'passage'))

    async def test_timed_out_wide_scan_retains_sightings_and_uses_one_local_sample(self):
        host=explorer.ExplorerContext()
        wide=dict(complete=False, gameUpdate=10,requestedTiles=8160,samplingBudgetReached=True,
                  discoveries=[dict(kind='chest',anchor=dict(x=50,y=5))], changes=[])
        local=dict(complete=True, gameUpdate=11,requestedTiles=2040,samplingBudgetReached=False,
                   discoveries=[],changes=[])
        host._resource_window=AsyncMock(side_effect=[wide,local])
        sample=await host.resource_scan('v')
        self.assertEqual([call.args for call in host._resource_window.await_args_list],[('v',120,68),('v',60,34)])
        self.assertTrue(explorer.sufficient_scan(sample,'any'))
        self.assertEqual(sample['widerSample']['discoveries'],wide['discoveries'])
        coverage=explorer.scan_coverage(sample)
        self.assertEqual((coverage['requestedTiles'],coverage['widerCoverage']['complete']),(2040,False))

    def test_investigation_keeps_original_heading_until_resumed_from_that_area(self):
        memory=explorer.PassageMemory()
        def trip(direction, start, end, reason):
            return dict(direction=direction,objective='look around',returnReason=reason,
                        start=dict(leftX=start[0],feetY=start[1]),finish=dict(leftX=end[0],feetY=end[1]),
                        passages=dict(returnTarget=dict(x=end[0]+10,y=end[1])))
        first=memory.finish_trip(trip('right',(100,160),(260,160),'discovery'))
        self.assertEqual(first[0]['returnTarget'],dict(x=270,y=160))
        memory.finish_trip(trip('down',(260,160),(260,320),'discovery'))
        other_level=memory.finish_trip(trip('right',(260,320),(420,320),'budget_reached'))
        self.assertEqual([r['direction'] for r in other_level],['down','right'])
        resumed=memory.finish_trip(trip('right',(265,160),(480,160),'budget_reached'))
        self.assertEqual([r['direction'] for r in resumed],['down'])

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
