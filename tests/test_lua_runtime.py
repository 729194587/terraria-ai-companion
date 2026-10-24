"""Real Lua workers + production dispatcher, with a simulated game backend."""
import asyncio
import copy
import json
import multiprocessing as mp
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'python'))
import agent_trace as tracing
import atomic_operations as atomic
from control import Bridge, BridgeError
from lua_runtime import LuaRuntime, Limits
from lua_worker import encode, decode, ValueLimit
from test_atomic_operations import Game


class RuntimeGame(Game):
    def __init__(self, *args, **kwargs):
        super().__init__(*args, **kwargs)
        self.closed = False
        self.hello['capabilities'].append('hotbar_keys_v1')
        self.slot = 1

    def close(self):
        self.closed = True

    def command(self, cmd, **params):
        if self.closed:
            raise BridgeError('closed')
        reply = super().command(cmd, **params)
        if cmd == 'start_atomic':
            self.action.update(session=1, generation=self.generation)
            return self.reply('movement_started')
        if cmd == 'get_movement' and self.action and self.action['status'] == 'completed':
            if self.action['operation'] == 'select_hotbar_slot':
                start = next(p for c, p in reversed(self.sent) if c == 'start_atomic')
                self.slot = start['arguments']['slot']
        if cmd == 'query_atomic':
            reply['data']['data'] = {'slot': self.slot, 'items': [{'name': '木头', 'target': None}],
                                    'emptyArray': [], 'emptyObject': {}, 'nothing': None}
        return reply


class CodecTests(unittest.TestCase):
    def test_numeric_boundaries_and_bounded_json(self):
        values = [-2**63, 2**63-1, 1e100, None, {}, [], {'nested': [False, '中文']}]
        self.assertEqual(values, decode(encode(values)))
        for value in (2**63, float('inf'), {'x': 'a'*270_000}):
            with self.assertRaises(ValueLimit):
                encode(value)


class LuaTests(unittest.IsolatedAsyncioTestCase):
    async def run_lua(self, source, game=None, **kwargs):
        game = game or RuntimeGame()
        runtime = LuaRuntime(game)
        result = await runtime.execute(source, **kwargs)
        self.assertFalse(mp.active_children(), 'Worker leaked')
        return result, game, runtime

    async def wait_started(self, game):
        async with asyncio.timeout(10):
            while game.action is None:
                await asyncio.sleep(.01)

    async def test_sequence_result_branch_and_chat_identity(self):
        result, game, _ = await self.run_lua('''
            local before = api('inspect_inventory', {})
            if before.data.slot == 1 then
                local action = api('select_hotbar_slot', {slot=2})
                assert(action.completed and action.endEvidenceStatus == 'known')
            end
            local after = api('inspect_inventory', {})
            return {before=before.data.slot, after=after.data.slot,
                    name=after.data.items[1].name, null=after.data.items[1].target}
        ''', chat_id='current-chat')
        self.assertEqual('completed', result['status'], result)
        self.assertEqual({'before': 1, 'after': 2, 'name': '木头', 'null': None}, result['value'])
        self.assertEqual(3, result['calls'])
        self.assertEqual('released', result['actionState'])
        self.assertEqual('current-chat', next(p['chatId'] for c, p in game.sent if c == 'start_atomic'))

    async def test_nested_arrays_objects_and_explicit_null_round_trip(self):
        r, _, _ = await self.run_lua('''
            local r = api('inspect_inventory')
            assert(r.data.nothing == json.null)
            assert(#r.data.items == 1)
            r.data.added = json.array({json.null, true, {x=3}})
            r.data.object = json.object()
            return r.data
        ''')
        self.assertEqual('completed', r['status'], r)
        self.assertEqual([], r['value']['emptyArray'])
        self.assertEqual({}, r['value']['emptyObject'])
        self.assertEqual({}, r['value']['object'])
        self.assertEqual([None, True, {'x': 3}], r['value']['added'])

    async def test_schema_arrays_and_nested_parameters_reuse_production_validator(self):
        r, game, _ = await self.run_lua('''
            api('scan_tiles', {minX=0,minY=0,maxX=1,maxY=1,tileTypes={0,1}})
            return api('use_item', {target={x=12.5,y=20}})
        ''')
        self.assertEqual('completed', r['status'], r)
        self.assertEqual([0, 1], next(p['arguments']['tileTypes'] for c, p in game.sent if c == 'query_atomic'))
        self.assertEqual({'x': 12.5, 'y': 20}, next(p['arguments']['target'] for c, p in game.sent if c == 'start_atomic'))

    async def test_failed_action_delivered_for_branching_but_following_action_blocked(self):
        for status in ('failed', 'timeout', 'cancelled'):
            r, game, runtime = await self.run_lua('''
                local a = api('use_item')
                if not a.completed then
                    local read = api('inspect_inventory')
                    local b = api('use_item')
                    return {status=a.status, readable=read.valid, blocked=b.status}
                end
            ''', RuntimeGame(status))
            self.assertEqual('completed', r['status'])  # Program ended, task success is separate.
            self.assertEqual({'status': status, 'readable': True, 'blocked': 'blocked'}, r['value'])
            self.assertEqual(status, r['lastAction']['status'])
            self.assertEqual(1, sum(c == 'start_atomic' for c, _ in game.sent))
            self.assertEqual('released', r['actionState'])
            self.assertFalse(game.closed)
            self.assertFalse(getattr(game, '_lua_poisoned', False))
            self.assertEqual('completed', (await runtime.execute("return api('inspect_inventory')"))['status'])

    async def test_disabled_actions_are_synthetic_and_do_not_fence_bridge(self):
        replies = []
        r, game, runtime = await self.run_lua("local b=api('use_item'); return {blocked=b,read=api('inspect_inventory')}",
            allow_actions=False, on_result=lambda name, args, reply: replies.append((name, reply)))
        self.assertEqual(('completed', 'idle', 0), (r['status'], r['actionState'], r['actions']))
        self.assertTrue(r['value']['blocked']['blocked'])
        self.assertTrue(r['value']['read']['valid'])
        self.assertIsNone(r['lastAction'])
        self.assertIsNone(r['cleanup'])
        self.assertEqual('blocked', replies[0][1]['status'])
        self.assertFalse(any(c == 'start_atomic' for c, _ in game.sent))
        self.assertFalse(game.closed)
        self.assertFalse(getattr(game, '_lua_poisoned', False))
        self.assertEqual('completed', (await runtime.execute("return api('use_item')"))['status'])

    async def test_failure_barrier_allows_real_stop_without_allowing_more_mutations(self):
        for allow_actions in (False, True):
            with self.subTest(allow_actions=allow_actions):
                prefix = "api('use_item');" if allow_actions else ''
                r, game, _ = await self.run_lua(prefix + "local stop=api('stop_action'); return {stop=stop,blocked=api('use_item')}",
                                               RuntimeGame('failed'), allow_actions=allow_actions,
                                               limits=Limits(actions=int(allow_actions)))
                stopped = r['value']['stop']
                self.assertTrue(stopped['valid'], stopped)
                self.assertNotIn('blocked', stopped)
                self.assertEqual('failed' if allow_actions else 'completed', stopped['status'])
                self.assertEqual('blocked', r['value']['blocked']['status'])
                self.assertEqual(int(allow_actions), r['actions'])
                self.assertEqual(int(allow_actions), sum(c == 'start_atomic' for c, _ in game.sent))
                self.assertFalse(game.closed)

    async def test_unknown_stop_receipt_under_failure_barrier_still_fences_bridge(self):
        game = RuntimeGame('failed')
        original = game.command
        stop_unknown = False
        def command(cmd, **params):
            reply = original(cmd, **params)
            if stop_unknown and cmd == 'get_movement' and game.action:
                reply['movement']['action']['endEvidenceStatus'] = 'unknown'
            return reply
        def observe(name, args, reply):
            nonlocal stop_unknown
            if name == 'use_item':
                stop_unknown = True
        game.command = command
        r, _, _ = await self.run_lua("api('use_item'); api('stop_action'); api('use_item')", game, on_result=observe)
        self.assertEqual(('action_unknown', 'unknown'), (r['status'], r['actionState']))
        self.assertEqual('unknown', r['lastAction']['status'])
        self.assertTrue(game.closed)
        self.assertTrue(game._lua_poisoned)
        self.assertEqual(1, sum(c == 'start_atomic' for c, _ in game.sent))
        self.assertEqual('blocked', (await LuaRuntime(game).execute('return 1'))['status'])

    async def test_bad_parameters_and_unknown_api_never_start_action(self):
        for source in ("api('mine_tile', {x=true,y=1})", "api('unknown')",
                       "api('scan_tiles',{minX=0,minY=0,maxX=1,maxY=1,tileTypes={1,1}})",
                       "api('use_item',{target={x=1,y=json.null}})"):
            r, game, _ = await self.run_lua(source + "; api('use_item')")
            self.assertEqual('bad_argument', r['status'])
            self.assertFalse(any(c == 'start_atomic' for c, _ in game.sent))

    async def test_unknown_release_fences_whole_bridge_across_runtime_instances(self):
        game = RuntimeGame()
        original = game.command
        def missing(cmd, **params):
            reply = original(cmd, **params)
            if cmd == 'get_movement' and game.action:
                reply['movement']['action']['endEvidenceStatus'] = 'unknown'
            return reply
        game.command = missing
        r, _, _ = await self.run_lua("api('use_item'); api('use_item')", game)
        self.assertEqual(('action_unknown', 'unknown'), (r['status'], r['actionState']))
        self.assertTrue(game.closed)
        self.assertEqual(1, r['calls'])
        self.assertEqual('blocked', (await LuaRuntime(game).execute('return 1'))['status'])
        with self.assertRaises(BridgeError):
            await atomic.dispatch(game, 'use_item', {})

    async def test_capability_rejection_allows_reads_but_blocks_later_mutation(self):
        game = RuntimeGame()
        game.hello['capabilities'].remove('server_confirmed_interactions_v1')
        r, _, runtime = await self.run_lua('''
            local rejected = api('interact_tile', {x=5,y=5})
            assert(not rejected.valid and not rejected.completed)
            local read = api('inspect_inventory')
            local blocked = api('use_item')
            return {rejected=rejected, readable=read.valid, blocked=blocked.status}
        ''', game)
        self.assertEqual(('completed', 'idle'), (r['status'], r['actionState']), r)
        rejection = r['value']['rejected']
        self.assertEqual('failed', rejection['status'])
        self.assertEqual('not_dispatched', rejection['dispatchState'])
        self.assertIn('server_confirmed_interactions_v1', rejection['reason'])
        self.assertTrue(r['value']['readable'])
        self.assertEqual('blocked', r['value']['blocked'])
        self.assertEqual(rejection, r['lastAction'])
        self.assertEqual(3, r['calls'])
        self.assertFalse(any(c in ('start_atomic', 'cancel_move') for c, _ in game.sent))
        self.assertFalse(game.closed)
        self.assertFalse(getattr(game, '_lua_poisoned', False))
        second = await runtime.execute("return api('inspect_inventory').valid")
        self.assertEqual(('completed', True), (second['status'], second['value']))

    async def test_capability_rejection_preserves_prior_release_evidence(self):
        game = RuntimeGame()
        game.hello['capabilities'].remove('server_confirmed_interactions_v1')
        r, _, _ = await self.run_lua('''
            assert(api('use_item').completed)
            local rejected = api('interact_tile', {x=5,y=5})
            return {status=rejected.status, readable=api('inspect_inventory').valid,
                    blocked=api('use_item').status}
        ''', game)
        self.assertEqual(('completed', 'released'), (r['status'], r['actionState']), r)
        self.assertEqual({'status': 'failed', 'readable': True, 'blocked': 'blocked'}, r['value'])
        self.assertEqual(1, sum(c == 'start_atomic' for c, _ in game.sent))
        self.assertFalse(game.closed)

    async def test_prestart_query_fault_is_not_a_capability_rejection(self):
        for error in (BridgeError('query timeout'), OSError('connection lost'),
                      BridgeError('query rejected', status='not_ready')):
            with self.subTest(error=str(error)):
                game = RuntimeGame()
                original = game.command
                queries = 0
                def fault(cmd, **params):
                    nonlocal queries
                    if cmd == 'get_movement':
                        queries += 1
                        if queries == 2:  # Runtime preflight succeeds; dispatcher query fails.
                            raise error
                    return original(cmd, **params)
                game.command = fault
                r, _, _ = await self.run_lua("api('use_item'); return api('inspect_inventory')", game)
                self.assertEqual(('action_unknown', 'unknown'), (r['status'], r['actionState']), r)
                self.assertFalse(any(c == 'start_atomic' for c, _ in game.sent))
                self.assertTrue(game.closed)
                self.assertTrue(game._lua_poisoned)

    async def test_start_without_ack_still_fences_bridge(self):
        game = RuntimeGame(pending=True)
        original = game.command
        def lost_ack(cmd, **params):
            reply = original(cmd, **params)
            if cmd == 'start_atomic':
                raise BridgeError('start acknowledgement lost')
            return reply
        game.command = lost_ack
        r, _, _ = await self.run_lua("api('use_item'); return api('inspect_inventory')", game)
        self.assertEqual(('action_unknown', 'unknown'), (r['status'], r['actionState']), r)
        self.assertEqual('running', game.action['status'])
        self.assertEqual(1, sum(c == 'start_atomic' for c, _ in game.sent))
        self.assertFalse(any(c == 'query_atomic' for c, _ in game.sent))
        self.assertTrue(game.closed)
        self.assertTrue(game._lua_poisoned)

    async def test_changed_action_identity_still_fences_bridge(self):
        game = RuntimeGame()
        original = game.command
        def replaced(cmd, **params):
            reply = original(cmd, **params)
            if cmd == 'get_movement' and game.action:
                reply['movement']['action']['actionId'] += 1
            return reply
        game.command = replaced
        r, _, _ = await self.run_lua("api('use_item'); return api('inspect_inventory')", game)
        self.assertEqual(('action_unknown', 'unknown'), (r['status'], r['actionState']), r)
        self.assertEqual('action_ownership_changed', r['lastAction']['reason'])
        self.assertTrue(game.closed)
        self.assertTrue(game._lua_poisoned)

    async def test_result_text_cannot_claim_safe_predispatch_rejection(self):
        async def ambiguous(*args, **kwargs):
            return {'valid': False, 'status': 'failed', 'completed': False,
                    'dispatchState': 'not_dispatched', 'reason': 'untrusted claim'}
        with patch.object(atomic, 'dispatch', ambiguous):
            r, game, _ = await self.run_lua("return api('use_item')")
        self.assertEqual(('action_unknown', 'unknown'), (r['status'], r['actionState']), r)
        self.assertTrue(game.closed)
        self.assertTrue(game._lua_poisoned)

    async def test_event_cancel_waits_for_release(self):
        game, cancel = RuntimeGame(pending=True), asyncio.Event()
        runtime = LuaRuntime(game)
        task = asyncio.create_task(runtime.execute("api('use_item'); api('use_item')", cancel=cancel))
        await self.wait_started(game)
        cancel.set()
        r = await task
        self.assertEqual(('cancelled', 'released'), (r['status'], r['actionState']))
        self.assertEqual('cancelled', r['cleanup']['status'])
        self.assertFalse(game.closed)
        self.assertEqual(1, sum(c == 'start_atomic' for c, _ in game.sent))

    async def test_task_cancel_during_start_and_repeated_cancel_preserves_cleanup(self):
        game = RuntimeGame(pending=True)
        runtime = LuaRuntime(game)
        original = atomic.command
        entered, release = asyncio.Event(), asyncio.Event()
        async def delayed(bridge, cmd, **params):
            if cmd == 'start_atomic':
                entered.set()
                await release.wait()
            return await original(bridge, cmd, **params)
        with patch.object(atomic, 'command', delayed):
            task = asyncio.create_task(runtime.execute("api('use_item')"))
            await asyncio.wait_for(entered.wait(), 10)
            task.cancel()
            await asyncio.sleep(.05)
            task.cancel()
            release.set()
            with self.assertRaises(asyncio.CancelledError):
                await task
        self.assertEqual('released', runtime.last_result['actionState'])
        self.assertEqual('cancelled', game.action['status'])

    async def test_deadline_cancels_running_action(self):
        game = RuntimeGame(pending=True)
        # Start action, then force the runtime's clock beyond its script deadline.
        runtime = LuaRuntime(game)
        original = __import__('time').monotonic
        offset = 0
        with patch('lua_runtime.monotonic', side_effect=lambda: original() + offset):
            task = asyncio.create_task(runtime.execute("api('use_item'); api('use_item')"))
            await self.wait_started(game)
            offset = 100
            r = await task
        self.assertEqual(('timeout', 'released'), (r['status'], r['actionState']))
        self.assertEqual('cancelled', game.action['status'])

    async def test_worker_death_during_action_still_cancels_game_action(self):
        game = RuntimeGame(pending=True)
        task = asyncio.create_task(LuaRuntime(game).execute("api('use_item')"))
        await self.wait_started(game)
        for child in mp.active_children():
            child.terminate()
        r = await task
        self.assertEqual(('worker_error', 'released'), (r['status'], r['actionState']))
        self.assertEqual('cancelled', game.action['status'])
        self.assertFalse(mp.active_children())

    async def test_cleanup_deadline_closes_session_and_never_claims_stop(self):
        game = RuntimeGame(pending=True)
        runtime = LuaRuntime(game)
        cancel = asyncio.Event()
        original = atomic.command
        release = asyncio.Event()
        async def delayed(bridge, cmd, **params):
            if cmd == 'cancel_move':
                await release.wait()
            return await original(bridge, cmd, **params)
        with patch.object(atomic, 'command', delayed):
            task = asyncio.create_task(runtime.execute("api('use_item')", cancel=cancel,
                limits=Limits(cleanup_seconds=.05)))
            await self.wait_started(game)
            cancel.set()
            r = await task
            self.assertEqual('unknown', r['actionState'])
            self.assertEqual('cleanup_deadline', r['cleanup']['reason'])
            self.assertTrue(game.closed)
            self.assertEqual('running', game.action['status'])
            release.set()
            await asyncio.sleep(.05)

    async def test_cancellation_missing_evidence_is_unknown(self):
        game = RuntimeGame(pending=True)
        original = game.command
        def command(cmd, **params):
            reply = original(cmd, **params)
            if cmd == 'cancel_move':
                reply['movement']['action']['endEvidenceStatus'] = 'unknown'
            return reply
        game.command = command
        cancel = asyncio.Event()
        task = asyncio.create_task(LuaRuntime(game).execute("api('use_item')", cancel=cancel))
        await self.wait_started(game)
        cancel.set()
        r = await task
        self.assertEqual('unknown', r['actionState'])
        self.assertFalse(r['cleanup']['valid'])
        self.assertTrue(game.closed)

    async def test_concurrent_runs_and_agent_dispatch_cannot_interleave(self):
        game, cancel = RuntimeGame(pending=True), asyncio.Event()
        task = asyncio.create_task(LuaRuntime(game).execute("api('use_item')", cancel=cancel))
        await self.wait_started(game)
        self.assertEqual('busy', (await LuaRuntime(game).execute('return 1'))['status'])
        with self.assertRaises(BridgeError):
            await atomic.dispatch(game, 'use_item', {})
        cancel.set()
        await task

    async def test_preexisting_running_action_is_not_taken_over(self):
        game = RuntimeGame(pending=True)
        game.command('start_atomic', operation='use_item', arguments={})
        r, _, _ = await self.run_lua("api('use_item')", game)
        self.assertEqual('busy', r['status'])
        self.assertFalse(any(c == 'cancel_move' for c, _ in game.sent))

    async def test_inflight_agent_dispatch_prevents_lua_takeover(self):
        game = RuntimeGame(pending=True)
        entered, release = asyncio.Event(), asyncio.Event()
        original = atomic.command
        async def delayed(bridge, cmd, **params):
            if cmd == 'get_movement' and not game.action:
                entered.set()
                await release.wait()
            return await original(bridge, cmd, **params)
        with patch.object(atomic, 'command', delayed):
            task = asyncio.create_task(atomic.dispatch(game, 'use_item', {}))
            await entered.wait()
            r = await LuaRuntime(game).execute('return 1')
            self.assertEqual('busy', r['status'])
            task.cancel()
            with self.assertRaises(asyncio.CancelledError):
                await task
        self.assertEqual(0, game._atomic_inflight)

    async def test_bridge_fences_legacy_commands_and_unknown_session(self):
        bridge = Bridge.__new__(Bridge)
        bridge._lua_owner = object()
        with self.assertRaisesRegex(BridgeError, 'owned'):
            bridge.command('move_for', direction=1, durationMs=100)
        bridge._lua_owner = None
        bridge._lua_poisoned = True
        with self.assertRaisesRegex(BridgeError, 'unresolved'):
            bridge.command('start_atomic')

    async def test_precancel_and_pure_lua_wall_timeout(self):
        cancel = asyncio.Event()
        cancel.set()
        r, game, _ = await self.run_lua("api('use_item')", cancel=cancel)
        self.assertEqual('cancelled', r['status'])
        self.assertEqual([], game.sent)
        r, _, _ = await self.run_lua('while true do end',
            limits=Limits(seconds=.05, instructions=10_000_000))
        self.assertEqual('timeout', r['status'])
        self.assertEqual('idle', r['actionState'])

    async def test_source_call_instruction_memory_and_value_budgets(self):
        for source, limits, expected in (
            ('x' * 16385, Limits(), 'source_limit'),
            ("api('inspect_inventory'); api('inspect_inventory')", Limits(calls=1), 'call_limit'),
            ('while true do end', Limits(instructions=1000), 'instruction_limit'),
            ('local s="12345678"; for i=1,30 do s=s..s end; return s', Limits(memory=100_000), 'memory_limit'),
            ('local t={}; t.self=t; return t', Limits(), 'value_limit'),
            ('return {[1]=1,[3]=3}', Limits(), 'value_limit'),
            ('return {x=1,[1]=2}', Limits(), 'value_limit'),
            ('return 0/0', Limits(), 'value_limit'),
            ('return function() end', Limits(), 'value_limit')):
            r, _, _ = await self.run_lua(source, limits=limits)
            self.assertEqual(expected, r['status'], source[:40])

    async def test_oversized_response_is_explicit_not_silently_truncated(self):
        game = RuntimeGame()
        original = game.command
        def command(cmd, **params):
            r = original(cmd, **params)
            if cmd == 'query_atomic':
                r['data']['huge'] = 'a' * 270_000
            return r
        game.command = command
        r, _, _ = await self.run_lua("api('inspect_inventory'); api('use_item')", game)
        self.assertEqual('result_limit', r['status'])
        self.assertEqual('idle', r['actionState'])
        self.assertFalse(any(c == 'start_atomic' for c, _ in game.sent))

    async def test_permissions_and_text_only_loader(self):
        r, _, _ = await self.run_lua('''
            return {type(os),type(io),type(package),type(require),type(debug),type(python),
                    type(load),type(dofile),type(pcall),type(coroutine),type(getmetatable)}
        ''')
        self.assertEqual(['nil'] * 11, r['value'])
        for source in ("return ('x').rep", 'return api.__globals__', '\x1bLua', 'not valid lua'):
            r, _, _ = await self.run_lua(source)
            self.assertEqual('script_error', r['status'])

    async def test_fresh_run_and_trace_correlation(self):
        with tempfile.TemporaryDirectory() as directory:
            trace = tracing.Trace(Path(directory) / 'lua.jsonl', enabled=True)
            with patch.object(tracing, 'trace', trace):
                r, game, runtime = await self.run_lua("x=12; return api('inspect_inventory')")
                second = await runtime.execute('return type(x)')
            records = [json.loads(line) for line in trace.path.read_text(encoding='utf-8').splitlines()]
        self.assertEqual('nil', second['value'])
        self.assertTrue(all(row['lua_run_id'] in (r['runId'], second['runId']) for row in records))
        self.assertIn('lua_api_result', [row['type'] for row in records])
        self.assertEqual({}, tracing.context.get())


if __name__ == '__main__':
    unittest.main()
