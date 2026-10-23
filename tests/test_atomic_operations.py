import asyncio
import copy
import json
from pathlib import Path
import sys
import unittest
from unittest.mock import patch
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'python'))
import atomic_operations as atomic
from control import BridgeError
from agent import Agent, TOOLS
from test_agent import Model, event


class Game:
    def __init__(self, outcome='completed', pending=False):
        self.hello = {'capabilities': ['atomic_operations_v1', 'atomic_extensions_v1', 'server_confirmed_interactions_v1']}
        self.action = None
        self.sent = []
        self.outcome, self.pending = outcome, pending
        self.generation = 0
        self.polls = 0

    def command(self, cmd, **params):
        self.sent.append((cmd, params))
        if cmd == 'query_atomic':
            return {'data': {'available': True, 'worldSessionId': 'world', 'gameUpdate': 2, 'data': {'sample': True}}}
        if cmd == 'start_atomic':
            self.generation += 1
            self.action = {'actionId': len(self.sent), 'operation': params['operation'], 'durationMs': 100,
                           'status': 'running', 'reason': 'started', 'worldSessionId': 'world', 'endEvidenceStatus': 'not_ended'}
            return self.reply('movement_started')
        if cmd == 'get_movement' and self.action:
            self.polls += 1
            if not self.pending:
                self.action.update(status=self.outcome, reason='verified_effect', evidence={'tileRemoved': True},
                                   endEvidenceStatus='known', endEvidence={'worldSessionId': 'world', 'gameUpdate': 3, 'phase': 'PostUpdateRunSpeeds_before_bridge_input'})
        if cmd == 'cancel_move':
            if self.action['actionId'] != params['actionId']:
                raise BridgeError('wrong owner')
            self.action.update(status='cancelled', reason='stopped', endEvidenceStatus='known',
                               endEvidence={'worldSessionId': 'world', 'gameUpdate': 3, 'phase': 'PostUpdateRunSpeeds_before_bridge_input'})
        return self.reply()

    def reply(self, status='movement'):
        return copy.deepcopy({'status': status, 'movement': {'generation': self.generation, 'action': self.action}})

    def get_terrain(self):
        return {'state': {'inWorld': True, 'position': {'x': 10, 'y': 20}, 'life': 100},
                'terrain': {'grounded': True, 'supported': True, 'playerWidth': 20, 'playerHeight': 42}}


class ValidationTests(unittest.TestCase):
    def test_schema_dispatch_and_tool_names(self):
        self.assertEqual(24, len(atomic.TOOLS))
        self.assertTrue(set(atomic.SCHEMAS) <= {t['function']['name'] for t in TOOLS})
        for name, args in [('mine_tile', {'x': 1.5, 'y': 2}), ('place_tile', {'x': True, 'y': 2}),
                           ('select_hotbar_slot', {'slot': 10}), ('navigate_to_x', {'targetX': float('nan')}),
                           ('attack_entity', {'entityRef': 1}), ('scan_entities', {'category': 'bad', 'radius': 10}),
                           ('use_item', {'target': {'x': 1, 'y': 2, 'z': 3}}), ('stop_action', {'extra': 1})]:
            with self.subTest(name=name, args=args), self.assertRaises(ValueError): atomic.validate(name, args)


class AtomicTests(unittest.IsolatedAsyncioTestCase):
    async def test_reads_reuse_terrain_and_native_query(self):
        game = Game()
        self.assertEqual(100, (await atomic.dispatch(game, 'get_player_state', {}))['player']['life'])
        self.assertTrue((await atomic.dispatch(game, 'observe_terrain', {}))['terrain']['grounded'])
        self.assertTrue((await atomic.dispatch(game, 'inspect_inventory', {}))['available'])
        self.assertEqual(['query_atomic'], [c for c, _ in game.sent])

    async def test_ack_not_success_requires_terminal_evidence(self):
        game = Game()
        result = await atomic.dispatch(game, 'mine_tile', {'x': 2, 'y': 3}, 'chat')
        self.assertEqual('completed', result['status'])
        self.assertTrue(result['evidence']['tileRemoved'])
        self.assertGreater(game.polls, 0)
        start = next(p for c, p in game.sent if c == 'start_atomic')
        self.assertEqual('chat', start['chatId'])
        self.assertEqual(0, start['expectedGeneration'])

    async def test_failed_timeout_cancelled_preserved(self):
        for status in ('failed', 'timeout', 'cancelled'):
            result = await atomic.dispatch(Game(status), 'use_item', {})
            self.assertEqual(status, result['status'])
            self.assertFalse(result['completed'])

    async def test_missing_release_never_completed(self):
        game = Game()
        game.action = {'actionId': 1, 'status': 'completed', 'reason': 'effect', 'endEvidenceStatus': 'unknown'}
        result = await atomic.terminal(game, game.action, 1)
        self.assertEqual('unknown', result['status'])
        self.assertFalse(result['completed'])

    async def test_cancel_exact_action_on_task_cancellation(self):
        game = Game(pending=True)
        task = asyncio.create_task(atomic.dispatch(game, 'use_item', {}))
        while game.action is None: await asyncio.sleep(0.01)
        task.cancel()
        with self.assertRaises(asyncio.CancelledError): await task
        self.assertEqual('cancelled', game.action['status'])
        self.assertIn(('cancel_move', {'actionId': game.action['actionId']}), game.sent)

    async def test_cancellation_during_start_waits_then_cancels_owned_action(self):
        game = Game(pending=True)
        original = atomic.command
        entered, release = asyncio.Event(), asyncio.Event()
        async def delayed(bridge, cmd, **args):
            if cmd == 'start_atomic':
                entered.set()
                await release.wait()
            return await original(bridge, cmd, **args)
        with patch.object(atomic, 'command', delayed):
            task = asyncio.create_task(atomic.dispatch(game, 'use_item', {}))
            await entered.wait()
            task.cancel()
            await asyncio.sleep(0)
            release.set()
            with self.assertRaises(asyncio.CancelledError): await task
        self.assertEqual('cancelled', game.action['status'])

    async def test_capability_and_validation_precede_action(self):
        game = Game(); game.hello = {}
        with self.assertRaises(BridgeError): await atomic.dispatch(game, 'use_item', {})
        self.assertEqual([], game.sent)

    async def test_drop_and_hotbar_capabilities_prevent_old_protocol_semantics(self):
        for name, args, capability in [('select_hotbar_slot', {'slot': 1}, 'hotbar_keys_v1'),
                                       ('drop_through_platform', {}, 'platform_drop_v1')]:
            game = Game()
            with self.assertRaisesRegex(BridgeError, capability):
                await atomic.dispatch(game, name, args)
            self.assertEqual([], game.sent)
            game.hello['capabilities'].append(capability)
            result = await atomic.dispatch(game, name, args)
            self.assertTrue(result['completed'])
            start = next(p for c,p in game.sent if c == 'start_atomic')
            self.assertEqual(args, start['arguments'])

    async def test_agent_uses_drop_tool_and_reports_its_result(self):
        from test_agent import tool, final
        game = Game()
        game.hello['capabilities'].append('platform_drop_v1')
        model = Model(tool('drop_through_platform', '{}'), final('已从平台直接下穿。'))
        agent = Agent(game, model)
        agent.receive(event(1))
        await agent.task
        self.assertEqual('drop_through_platform', next(p['operation'] for c,p in game.sent if c == 'start_atomic'))
        self.assertTrue(any(c == 'send_chat' for c,_ in game.sent))

    async def test_agent_dispatch_and_halts_after_failure(self):
        def call(n, name, args):
            return {'role': 'assistant', 'tool_calls': [{'id': str(n), 'type': 'function', 'function': {'name': name, 'arguments': json.dumps(args)}}]}
        game = Game('failed')
        model = Model(call(1, 'use_item', {}), call(2, 'use_item', {}), {'role': 'assistant', 'content': 'done'})
        agent = Agent(game, model)
        agent.receive(event(1))
        await agent.task
        self.assertEqual(1, sum(c == 'start_atomic' for c, _ in game.sent))
        self.assertIn('verified_effect', next(p['text'] for c, p in game.sent if c == 'send_chat'))
        blocked = json.loads(model.requests[-1][-2]['content'])
        self.assertTrue(blocked['blocked'])
        self.assertEqual('verified_effect', blocked['previousFailure']['reason'])


if __name__ == '__main__': unittest.main()
