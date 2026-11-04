"""Pre-move threat-envelope regression; synthetic entities, real Lua and loopback TCP."""
import unittest
import test_autonomous_exploration as fixture


class ThreatEnvelopeTests(unittest.IsolatedAsyncioTestCase):
    setUp = fixture.ExplorationTests.setUp
    tearDown = fixture.ExplorationTests.tearDown
    run_loop = fixture.ExplorationTests.run_loop
    starts = fixture.ExplorationTests.starts

    def scans(self):
        return [r for r in self.host.sent if r['op']=='query' and r['operation']=='scan_entities']

    async def test_old_96px_blind_region_and_motion_buffer_stop_before_navigation(self):
        for distance in (96, 96.25, 120, 128, 200, 236, 267.75):
            with self.subTest(distance=distance):
                self.host.entities=[dict(distancePixels=distance,classification='hostile_creature')]
                before_scans=len(self.scans())
                r=await self.run_loop('{direction="right",maxSteps=1}')
                self.assertEqual('completed',r['status'],r)
                self.assertEqual('nearby_hostile',r['value']['reason'])
                self.assertEqual(before_scans+1,len(self.scans()))
                self.assertEqual([],self.starts())
        self.assertFalse(any(q.get('operation')=='scan_tiles' for q in self.host.sent))

    async def test_radius_boundary_and_distance_cap_do_not_round_up_stride(self):
        for distance, expected_step in ((268,32),(300,64),(363.75,127.75),(364,128),(364.25,128)):
            with self.subTest(distance=distance):
                self.host.entities=[dict(distancePixels=distance,classification='hostile_creature')]
                start_x=self.host.x
                before_scans=len(self.scans())
                before_starts=len(self.starts())
                r=await self.run_loop('{direction="right",maxSteps=1}')
                self.assertEqual('step_limit',r['value']['reason'],r)
                self.assertEqual(before_starts+1,len(self.starts()))
                target=self.starts()[-1]['arguments']['targetX']
                self.assertAlmostEqual(expected_step,target-start_x)
                self.assertEqual(before_scans+1,len(self.scans()))
                self.assertEqual(364,self.scans()[-1]['arguments']['radius'])
                check=r['value']['lastThreatCheck']
                self.assertEqual('pre_move_snapshot',check['phase'])
                self.assertEqual(236,check['marginPixels'])
                if distance<=364:
                    self.assertLessEqual(expected_step+236,distance)
                self.assertLessEqual(expected_step+236,check['radiusPixels'])

    async def test_scan_radius_tracks_stride_and_remaining_travel_budget(self):
        for options, radius, step in (
            ('{direction="right",maxSteps=1,stepPixels=64}',300,64),
            ('{direction="right",maxSteps=1,stepPixels=96}',332,96),
            ('{direction="right",maxSteps=1,maxDistance=64}',288,52)):
            with self.subTest(options=options):
                start=self.host.x
                r=await self.run_loop(options)
                self.assertEqual('step_limit',r['value']['reason'],r)
                self.assertEqual(radius,self.scans()[-1]['arguments']['radius'])
                self.assertAlmostEqual(step,self.starts()[-1]['arguments']['targetX']-start)

    async def test_nearest_hostile_not_friendly_or_result_order_bounds_step(self):
        self.host.entities=[dict(distancePixels=340,classification='hostile_creature'),
                            dict(distancePixels=20,classification='friendly_npc'),
                            dict(distancePixels=300,classification='hostile_creature')]
        r=await self.run_loop('{direction="right",maxSteps=1}')
        self.assertEqual(64,r['value']['steps'][0]['movedPixels'],r)
        self.assertEqual(300,r['value']['lastThreatCheck']['nearestDistancePixels'])
        self.assertEqual(dict(category='npc',npcFilter='hostile_creature',radius=364,maxResults=1),
                         self.scans()[0]['arguments'])

    async def test_shortened_step_can_reverse_using_same_radial_snapshot(self):
        self.host.terrain_mode='right_wall'
        self.host.entities=[dict(distancePixels=300,classification='hostile_creature')]
        r=await self.run_loop('{maxSteps=1}')
        self.assertEqual(-1,r['value']['steps'][0]['direction'],r)
        self.assertEqual(64,r['value']['steps'][0]['movedPixels'])
        self.assertEqual(1,len(self.scans()))

    async def test_no_threat_adds_no_api_calls_or_duplicate_scans(self):
        r=await self.run_loop('{direction="right",maxSteps=3}')
        self.assertEqual('step_limit',r['value']['reason'],r)
        self.assertEqual(18,r['calls'])  # 3 initial reads + 5 calls per completed segment, unchanged.
        self.assertEqual(3,len(self.starts()))
        self.assertEqual(3,len(self.scans()))
        before=len(self.scans())
        r=await self.run_loop('{direction="right",maxSteps=3,maxDistance=64}')
        self.assertEqual('distance_limit',r['value']['reason'],r)
        self.assertEqual(before+1,len(self.scans()))
        self.assertEqual(8,r['calls'])  # No NPC/landmark scan after remaining budget cannot support a move.

    async def test_missing_nearest_distance_fails_closed_without_extra_scan(self):
        original=self.host.query
        def query(operation,args):
            result=original(operation,args)
            if operation=='scan_entities':
                result['data'].update(matchedCount=1,items=[])
            return result
        self.host.query=query
        r=await self.run_loop()
        self.assertEqual('entity_observation_unavailable',r['value']['reason'],r)
        self.assertEqual([],self.starts())
        self.assertEqual(1,len(self.scans()))

    async def test_new_threat_is_observed_at_next_action_boundary(self):
        original=self.host.query
        def query(operation,args):
            result=original(operation,args)
            if self.host.applied:
                self.host.entities=[dict(distancePixels=64,classification='hostile_creature')]
            return result
        self.host.query=query
        r=await self.run_loop('{direction="right",maxSteps=3}')
        self.assertEqual('nearby_hostile',r['value']['reason'],r)
        self.assertEqual(1,len(self.starts()))
        self.assertEqual(2,len(self.scans()))
        self.assertEqual('released',r['actionState'])
