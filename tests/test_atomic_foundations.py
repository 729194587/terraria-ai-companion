"""Bounded projections and real Lua workers with recorded/synthetic observations.

These are offline tests; they never connect to or control Terraria.
"""
import asyncio
import copy
import json
import multiprocessing as mp
from pathlib import Path
import sys
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'python'))
import atomic_operations as atomic
from agent import tool_result_metrics, history_receipts
from control import BridgeError
from exploration import read_bounded_exploration
from lua_runtime import LuaRuntime, Limits
from lua_worker import encode, VALUE_BYTES
from test_exploration import recorded, synthetic, cell
from test_lua_runtime import RuntimeGame


class ObservedGame(RuntimeGame):
    def __init__(self, observation=None):
        super().__init__()
        self.hello['capabilities'].append('atomic_foundations_v1')
        self.observation = observation if observation is not None else recorded()
        self.reads = 0

    def get_observation(self):
        self.reads += 1
        return dict(observation=self.observation, controlGeneration=4)


class FoundationProjectionTests(unittest.TestCase):
    def test_direct_bounded_observation_expires_in_history(self):
        value = read_bounded_exploration(ObservedGame())
        messages = [dict(role='assistant', tool_calls=[dict(id='1', function=dict(name='observe_exploration'))]),
                    dict(role='tool', tool_call_id='1', content=json.dumps(value))]
        expired = json.loads(history_receipts(messages)[1]['content'])
        self.assertTrue(expired['expired'])
        self.assertIn('exploration', json.loads(messages[1]['content']))

    def test_crop_preserves_original_freshness_unknowns_and_coverage(self):
        game = ObservedGame()
        original = copy.deepcopy(game.observation)
        r = read_bounded_exploration(game, radiusTiles=2, maxEntities=1)
        self.assertEqual(1, game.reads)
        self.assertTrue(r['valid'])
        p = r['exploration']
        self.assertEqual(original['gameUpdate'], p['gameUpdate'])
        self.assertEqual(original['camera'], p['camera'])
        self.assertLessEqual(p['terrain']['bounds']['width'], 5)
        self.assertLessEqual(p['terrain']['bounds']['height'], 5)
        self.assertLessEqual(len(p['entities']['items']), 1)
        coverage = p['entities']['coverage']
        self.assertEqual(coverage['matchedCount'], coverage['returnedCount']+coverage['omittedCount'])
        self.assertFalse(p['completeness']['completeWithinScope'])
        self.assertGreater(p['projectionCoverage']['omittedEntities'], 0)
        self.assertGreater(p['investigate']['omittedCount'], 0)
        self.assertEqual(p['investigate']['candidateCount'],
                         p['investigate']['returnedCount']+p['investigate']['omittedCount'])
        self.assertEqual(original, game.observation)
        encode(r, VALUE_BYTES)

    def test_unloaded_cells_and_hazards_are_not_dropped(self):
        source = synthetic([cell(knowledge='unloaded'), cell(hasTile=True, solid=True, hot=True,
            liquidAmount=255, liquidType=1)], width=2)
        r = read_bounded_exploration(ObservedGame(source), radiusTiles=8)
        terrain = r['exploration']['terrain']
        self.assertEqual(1, terrain['unknownCount'])
        states = [region[-1] for region in terrain['regions']]
        self.assertIn({'knowledge': 'unloaded'}, states)
        hazard = next(s for s in states if s.get('hot'))
        self.assertEqual(255, hazard['liquidAmount'])
        self.assertTrue(hazard['solid'])

    def test_oversized_content_returns_small_error_without_partial_safety_claim(self):
        game = ObservedGame()
        game.observation['player']['mount']['oversized'] = 'x' * VALUE_BYTES
        r = read_bounded_exploration(game)
        self.assertFalse(r['valid'])
        self.assertEqual('exploration_result_limit', r['reason'])
        self.assertNotIn('exploration', r)

    def test_unavailable_is_not_empty_world(self):
        source = recorded()
        source.update(status='unavailable', player=None, terrain=None, entities=None,
                      camera=None, environment=None, interactables=None)
        source['completeness']['completeWithinScope'] = False
        r = read_bounded_exploration(ObservedGame(source))
        self.assertFalse(r['valid'])
        self.assertIsNone(r['exploration']['terrain'])

    def test_strict_schema_and_action_classification(self):
        for name, args in [('wait', {'milliseconds': True}), ('wait', {'milliseconds': 10001}),
                           ('observe_exploration', {'radiusTiles': 9}),
                           ('observe_exploration', {'maxEntities': 17}),
                           ('resolve_content', {'kind': 'npc', 'query': 'x'}),
                           ('scan_entities', {'category': 'all', 'radius': 100, 'typeIds': [1]}),
                           ('scan_entities', {'category': 'item', 'radius': 100, 'npcFilter': 'critter'})]:
            with self.subTest(name=name), self.assertRaises(ValueError):
                atomic.validate(name, args)
        self.assertNotIn('wait', atomic.ACTIONS)
        self.assertNotIn('wait', atomic.READS)
        self.assertIsNone(tool_result_metrics('wait', {'valid': True, 'status': 'completed'})['action_success'])


class FoundationAsyncTests(unittest.IsolatedAsyncioTestCase):
    async def asyncTearDown(self):
        self.assertFalse(mp.active_children(), 'Lua Worker leaked')

    async def test_new_native_parameters_require_capability_before_rpc(self):
        game = RuntimeGame()
        for name, args in [('get_player_status', {}),
                           ('resolve_content', {'kind': 'item', 'query': 'Stone'}),
                           ('scan_entities', {'category': 'npc', 'radius': 100, 'maxResults': 1})]:
            with self.assertRaisesRegex(BridgeError, 'atomic_foundations_v1'):
                await atomic.dispatch(game, name, args)
        self.assertEqual([], game.sent)

    async def test_dispatch_native_payload_and_non_action_wait(self):
        game = ObservedGame()
        self.assertTrue((await atomic.dispatch(game, 'get_player_status', {}))['valid'])
        self.assertEqual('get_player_status', game.sent[0][1]['operation'])
        r = await atomic.dispatch(game, 'wait', {'milliseconds': 1})
        self.assertGreaterEqual(r['elapsedMilliseconds'], 1)
        self.assertEqual('host_monotonic', r['clock'])
        self.assertFalse(any(c == 'start_atomic' for c, _ in game.sent))

    async def test_lua_observation_wait_reobservation_with_no_action_receipt(self):
        game = ObservedGame()
        r = await LuaRuntime(game).execute('''
            local before=api('observe_exploration',{radiusTiles=2,maxEntities=1})
            assert(before.valid and before.exploration.terrain.bounds.width <= 5)
            local pause=api('wait',{milliseconds=1})
            assert(pause.valid and pause.clock == 'host_monotonic')
            local after=api('observe_exploration',{radiusTiles=2})
            return {world=after.exploration.worldSessionId, waited=pause.elapsedMilliseconds}
        ''', allow_actions=False)
        self.assertEqual('completed', r['status'], r)
        self.assertIsNone(r['lastAction'])
        self.assertEqual('idle', r['actionState'])
        self.assertEqual(2, game.reads)
        self.assertFalse(any(c == 'start_atomic' for c, _ in game.sent))

    async def test_cancel_or_deadline_during_wait_never_starts_following_action(self):
        for mode in ('event', 'task', 'deadline'):
            with self.subTest(mode=mode):
                game, entered, cancel = ObservedGame(), asyncio.Event(), asyncio.Event()
                runtime = LuaRuntime(game)
                original = atomic.dispatch
                async def dispatch(bridge, name, args, *pos, **kw):
                    if name == 'wait':
                        entered.set()
                    return await original(bridge, name, args, *pos, **kw)
                # Set the trusted clock after the Worker is ready, independent of spawn latency.
                clock = __import__('time').monotonic
                offset = 0
                with patch.object(atomic, 'dispatch', dispatch), patch('lua_runtime.monotonic', side_effect=lambda: clock()+offset):
                    task = asyncio.create_task(runtime.execute("api('wait',{milliseconds=10000}); api('use_item')", cancel=cancel))
                    await asyncio.wait_for(entered.wait(), 10)
                    if mode == 'event':
                        cancel.set()
                    elif mode == 'task':
                        task.cancel()
                    else:
                        offset = 100
                    if mode == 'task':
                        with self.assertRaises(asyncio.CancelledError):
                            await task
                        r = runtime.last_result
                    else:
                        r = await asyncio.wait_for(task, 3)
                self.assertEqual('timeout' if mode == 'deadline' else 'cancelled', r['status'], r)
                self.assertEqual('idle', r['actionState'])
                self.assertFalse(game.closed)
                self.assertFalse(any(c == 'start_atomic' for c, _ in game.sent))

    async def test_wait_does_not_clear_failure_barrier_or_last_action(self):
        game = RuntimeGame('failed')
        r = await LuaRuntime(game).execute('''
            api('use_item')
            api('wait',{milliseconds=1})
            return api('use_item')
        ''')
        self.assertEqual('completed', r['status'], r)
        self.assertTrue(r['value']['blocked'])
        self.assertEqual('failed', r['lastAction']['status'])
        self.assertEqual(1, sum(c == 'start_atomic' for c, _ in game.sent))


if __name__ == '__main__':
    unittest.main()
