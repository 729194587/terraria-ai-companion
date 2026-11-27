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
    def test_brief_reports_an_observed_ore_tile_and_retires_it_when_absent(self):
        anchor = dict(x=2175, y=291)
        tiles = [dict(x=2176, y=291), dict(x=2176, y=292), dict(x=2175, y=292)]
        ore = dict(kind='ore_component', contentKey='Terraria/Tin', anchor=anchor, tiles=tiles)
        events = [dict(items=[ore])]
        result = dict(direction='right', progress={}, returnReason='discovery', findings=events)
        finding, = explorer.brief(result)['findings']
        self.assertEqual(finding['anchor'], anchor)
        self.assertEqual(finding['targetTile'], tiles[0])
        self.assertNotIn(anchor, tiles)

        events.append(dict(items=[dict(ore, tiles=tiles[1:])]))
        updated, = explorer.brief(result)['findings']
        self.assertEqual(updated['targetTile'], tiles[1])
        events.append(dict(items=[], changes=[dict(ore, state='absent_at_last_location')]))
        self.assertTrue(all('targetTile' not in item for item in explorer.brief(result)['findings']))

    def test_brief_reserves_room_for_distinct_kinds_then_materials(self):
        def ore(material, x):
            return dict(kind='ore_component', contentKey=material, anchor=dict(x=x, y=20))

        copper = [ore('Terraria/Copper', x) for x in range(10)]
        chest = dict(kind='chest', contentKey='Terraria/Containers', anchor=dict(x=42, y=20))
        iron = ore('Terraria/Iron', 11)
        events = [dict(items=copper), dict(items=[iron, chest, copper[0]])]
        result = dict(direction='right', progress={}, returnReason='budget_reached', findings=events)
        summary = explorer.brief(result)
        self.assertEqual(summary['findingCount'], 12)
        self.assertEqual(summary['findings'], [copper[0], chest, iron, *copper[1:6]])
        # Even more than eight different ore types must not displace a different kind.
        events[0]['items'] = [ore(str(x), x) for x in range(10)]
        self.assertIn(chest, explorer.brief(result)['findings'])

    def test_brief_keeps_observation_order_when_all_findings_fit(self):
        hits = [dict(kind=kind, anchor=dict(x=x, y=10))
                for x, kind in enumerate(('ore_component', 'ore_component', 'chest'))]
        result = dict(direction='right', progress={}, returnReason='discovery',
                      findings=[dict(items=hits[:2]), dict(items=hits[1:])])
        self.assertEqual(explorer.brief(result)['findings'], hits)

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
                   seek=None, statuses=(), offsets=(), movement=None):
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
                    x=args['x']-10 if movement is None else x+movement
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
            scene.setdefault('coverage',dict(geometryComplete=True))
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
        result=await self.trip(found_at=1,seek='any')
        self.assertEqual(result['returnReason'],'discovery')
        self.assertEqual(self.order,['submit','terminal','scan'])
        self.assertEqual(result['lastActionResult']['status'],'completed')
        self.assertEqual((await self.trip(found_at=2,release=False))['returnReason'],'outcome_unknown')

    async def test_confirmed_collision_stops_on_the_native_receipt(self):
        result=await self.trip(offsets=(128,256),status='failed')
        self.assertEqual((len(result['segments']),result['returnReason'],result['reason']), (1,'blocked','collision'))
        self.assertEqual(self.order,['submit','terminal','scan'])
        for movement in (0,-40):
            result=await self.trip(movement=movement)
            self.assertEqual((len(result['segments']),result['returnReason']), (1,'blocked'))

    async def test_default_keeps_walking_after_finding(self):
        result=await self.trip(found_at=1)
        self.assertEqual((result['returnReason'],len(result['segments'])),('budget_reached',2))
        self.assertEqual(explorer.brief(result)['findings'][0]['kind'],'chest')
        self.assertNotIn('segments',explorer.brief(result))

    async def test_seek_records_unrelated_findings_and_continues(self):
        result=await self.trip(found_at=1,seek='Terraria/Iron')
        self.assertEqual((len(result['segments']),result['returnReason']), (2,'budget_reached'))
        self.assertEqual(result['findings'][0]['items'][0]['kind'],'chest')
        self.assertEqual((await self.trip(found_at=1,seek='chest'))['returnReason'],'discovery')

    async def test_timed_out_wide_scan_retains_sightings_and_uses_one_local_sample(self):
        host=explorer.ExplorerContext()
        wide=dict(complete=False, gameUpdate=10,requestedTiles=8160,samplingBudgetReached=True,
                  discoveries=[dict(kind='chest',anchor=dict(x=50,y=5))], changes=[])
        local=dict(complete=True, gameUpdate=11,requestedTiles=2040,samplingBudgetReached=False,
                   discoveries=[],changes=[])
        host._resource_window=AsyncMock(side_effect=[wide,local])
        sample=await host.resource_scan('v')
        self.assertEqual([call.args for call in host._resource_window.await_args_list],[('v',120,68),('v',60,34)])
        self.assertTrue(sample['complete'])
        self.assertEqual(sample['widerSample']['discoveries'],wide['discoveries'])
        coverage=explorer.scan_coverage(sample)
        self.assertEqual((coverage['requestedTiles'],coverage['widerCoverage']['complete']),(2040,False))

    async def test_partial_resource_coverage_does_not_block_confirmed_landings(self):
        result=await self.trip(complete=False,end_complete=False)
        self.assertEqual((len(result['segments']),result['returnReason']), (2,'budget_reached'))
        self.assertTrue(explorer.brief(result)['partialResourceScan'])

    async def test_observation_and_navigation_failures_are_distinct(self):
        self.assertEqual((await self.trip(empty_geometry=True))['returnReason'],'blocked')
        self.assertEqual((await self.trip(empty_geometry=False))['returnReason'],'observation_incomplete')
        result=await self.trip(status='failed')
        self.assertEqual((result['returnReason'],result['reason']),('blocked','collision'))
        self.assertEqual((await self.trip(status='cancelled'))['returnReason'],'interrupted')

    async def test_empty_truncated_window_is_not_a_proven_obstruction(self):
        result=await self.trip(truncated=(1,))
        self.assertEqual(self.caps,[24])
        self.assertEqual(result['returnReason'],'observation_incomplete')
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
