import asyncio
import json
from pathlib import Path
import tempfile
import threading
import unittest
from unittest.mock import patch

from exploration_fixture import ExplorationHost
from server_ai import ServerAiBridge
from lua_runtime import LuaRuntime, Limits
from lua_agent import server_contract
import agent_diagnostics as diagnostics


class ExplorationTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        self.host = ExplorationHost()
        self.thread = threading.Thread(target=self.host.serve_forever, daemon=True)
        self.thread.start()
        self.bridge = ServerAiBridge(self.host.server_address[1], timeout=2)
        self.directory = tempfile.TemporaryDirectory()

    def tearDown(self):
        self.bridge.close()
        self.host.shutdown(); self.host.server_close(); self.thread.join()
        self.directory.cleanup()

    async def run_loop(self, options="{maxSteps=3}", **limits):
        self.runtime = LuaRuntime(self.bridge, evidence_root=self.directory.name)
        return await self.runtime.execute('return explore('+options+')', limits=Limits(**limits))

    def starts(self):
        return [r for r in self.host.sent if r['op'] == 'start']

    async def test_continuous_flat_three_segments_and_bound_evidence(self):
        with patch.object(diagnostics, 'emit') as log:
            result = await self.run_loop()
        self.assertEqual('completed', result['status'], result)
        report = result['value']
        self.assertEqual('step_limit', report['reason'])
        self.assertEqual(3, len(report['steps']))
        self.assertEqual(384, report['distancePixels'])
        self.assertEqual(1984, self.host.x)
        self.assertEqual(3, len(self.starts()))
        self.assertTrue(all(r['operation']=='navigate_to_x' for r in self.starts()))
        self.assertEqual('released', result['actionState'])
        self.assertEqual('known', result['lastAction']['endEvidenceStatus'])
        self.assertLessEqual(result['calls'], 64)
        self.assertTrue(any(r[0][0]=='lua_exploration_report' for r in log.call_args_list))
        self.assertEqual('tile cells, not trees', report['lastTileCoverage']['countUnit'])
        manifest=json.loads((Path(result['evidence']['path'])/'manifest.json').read_text(encoding='utf-8'))
        self.assertGreaterEqual(len(manifest['sources']), 2)

    async def test_simple_obstacle_delegates_to_existing_navigator(self):
        self.host.terrain_mode = 'obstacle'
        r = await self.run_loop('{direction="right",maxSteps=2}')
        self.assertEqual('step_limit', r['value']['reason'], r)
        self.assertEqual(2, len(self.starts()))  # Synthetic kinematics; native jump is a separate check.

    async def test_auto_reverses_before_action_at_wall(self):
        self.host.terrain_mode = 'right_wall'
        r = await self.run_loop('{maxSteps=2}')
        self.assertEqual(1, r['value']['reversals'], r)
        self.assertTrue(all(s['direction']==-1 for s in r['value']['steps']))
        self.assertEqual(2, len(self.starts()))

    async def test_gaps_hazards_unknowns_and_walls_never_start_right_action(self):
        for mode in ('right_gap','right_unknown','right_hazard','both_walls'):
            self.host.terrain_mode = mode
            r = await self.run_loop('{direction="right"}')
            self.assertEqual('no_safe_direction', r['value']['reason'], r)
        self.assertEqual([], self.starts())

    async def test_no_progress_stops_after_one_completed_action(self):
        self.host.frozen = True
        r = await self.run_loop()
        self.assertEqual('no_progress', r['value']['reason'], r)
        self.assertEqual(1, len(self.starts()))

    async def test_health_decrease_stops_and_nearby_threat_prevents_action(self):
        self.host.threat = True
        r = await self.run_loop()
        self.assertEqual('nearby_hostile', r['value']['reason'], r)
        self.assertFalse(self.starts())
        self.host.threat = False; self.host.damage = True
        r = await self.run_loop()
        self.assertEqual('health_decreased', r['value']['reason'], r)
        self.assertEqual(1, len(self.starts()))

    async def test_action_budget_stops_without_second_start(self):
        r = await self.run_loop(actions=1)
        self.assertEqual('action_limit', r['status'], r)
        self.assertEqual(1, len(self.starts()))
        self.assertEqual('released', r['actionState'])

    async def test_shortens_step_before_wall_and_distance_budget_stops_early(self):
        self.host.terrain_mode='right_soon_wall'
        r=await self.run_loop('{direction="right",maxSteps=1}')
        self.assertEqual(64,r['value']['steps'][0]['movedPixels'],r)
        self.host.terrain_mode='flat'
        r=await self.run_loop('{direction="right",maxDistance=200}')
        self.assertEqual('distance_limit',r['value']['reason'],r)
        self.assertLessEqual(r['value']['distancePixels'],200)
        self.assertEqual(2,len(r['value']['steps']))

    async def test_revisit_memory_prevents_corridor_ping_pong(self):
        self.host.terrain_mode='corridor'
        r=await self.run_loop('{maxSteps=8}')
        self.assertIn(r['value']['reason'],('revisit_limit','reversal_limit','no_safe_direction','no_new_ground'),r)
        self.assertLess(len(self.starts()),8)
        self.assertLessEqual(r['value']['reversals'],2)

    async def test_api_and_wall_clock_budgets_prevent_further_movement(self):
        r=await self.run_loop(calls=5)
        self.assertEqual('call_limit',r['status'],r)
        self.assertEqual([],self.starts())
        r=await self.run_loop(seconds=.05)
        self.assertEqual('timeout',r['status'],r)
        self.assertEqual([],self.starts())

    async def test_unknown_action_outcome_never_retries_or_reconnects(self):
        self.host.mode='no_release'
        r=await self.run_loop()
        self.assertEqual('unknown',r['actionState'],r)
        self.assertEqual(1,len(self.starts()))
        self.assertTrue(self.bridge._closed)
        self.assertEqual(1,sum(q['op']=='bind' for q in self.host.sent))

    async def test_action_timeout_cancels_owned_action_and_verifies_release(self):
        self.host.mode = 'pending'
        r = await self.run_loop()
        self.assertEqual('completed', r['status'], r)
        self.assertEqual('action_timeout', r['value']['reason'])
        self.assertEqual('released', r['actionState'])
        self.assertTrue(r['lastAction']['endEvidence']['inputsReleased'])
        self.assertEqual(1, sum(q['op']=='cancel' for q in self.host.sent))

    async def test_cancellation_releases_pending_navigation(self):
        self.host.mode = 'pending'
        task = asyncio.create_task(self.run_loop())
        for _ in range(300):
            if self.starts(): break
            await asyncio.sleep(.01)
        self.assertTrue(self.starts())
        task.cancel()
        with self.assertRaises(asyncio.CancelledError): await task
        self.assertEqual('released', self.runtime.last_result['actionState'])
        self.assertEqual(1, sum(q['op']=='cancel' for q in self.host.sent))

    async def test_options_are_bounded_and_loop_cannot_reset_memory(self):
        for options in ('{maxSteps=9}','{stepPixels=1000}','{direction="up"}','{seconds=1000}'):
            r = await self.run_loop(options)
            self.assertEqual('script_error', r['status'], r)
        self.assertFalse(self.starts())
        r=await LuaRuntime(self.bridge,evidence_root=self.directory.name).execute('explore({maxSteps=1}); return explore({maxSteps=1})')
        self.assertEqual('script_error', r['status'], r)
        self.assertEqual(1,len(self.starts()))

    async def test_contract_only_advertises_explore_when_backend_supports_reads_and_actions(self):
        prompt,_=server_contract(self.bridge)
        self.assertIn('return explore(',prompt)
        self.bridge.supported_atomic_operations -= {'observe_terrain'}
        self.assertNotIn('return explore(',server_contract(self.bridge)[0])

    async def test_worker_uses_the_same_pinned_helper_source_as_evidence(self):
        source="return function() return {marker='pinned helper'} end"
        with patch('lua_runtime.EXPLORATION_SOURCE',source):
            r=await self.run_loop()
        self.assertEqual('completed',r['status'],r)
        self.assertEqual('pinned helper',r['value']['marker'])
        files=list(Path(r['evidence']['path']).glob('source-*.lua'))
        self.assertIn(source,[p.read_text(encoding='utf-8') for p in files])
        self.assertFalse(self.starts())
