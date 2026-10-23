import asyncio
import copy
import io
import json
from pathlib import Path
import sys
import unittest
from unittest.mock import patch

import httpx

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'python'))
from agent import Agent, run, terminal_loop
from control import BridgeError
from llm import LLMClient
from test_agent import Game, Model, event, final, tool
from test_atomic_operations import Game as AtomicGame


class LocalGame(AtomicGame):
    def __init__(self, **kwargs):
        super().__init__(**kwargs)
        self.hello['capabilities'].append('local_terminal_input_v1')
        self._failure = None
        self.turn = 0

    def command(self, cmd, **args):
        if cmd == 'begin_local_turn':
            self.sent.append((cmd, args))
            self.turn += 1
            return {'status': 'local_turn_started', 'data': {'chatId': f'local:1:{self.turn}'}}
        return super().command(cmd, **args)


class LocalInputTests(unittest.IsolatedAsyncioTestCase):
    async def test_same_model_tool_loop_terminal_reply_and_history(self):
        game = LocalGame()
        model = Model(tool('inspect_inventory', '{"mode":"hotbar"}'),
                      tool('hammer_tile', '{"x":5,"y":5}'), final('锤击已核验'), final('第二轮'))
        agent = Agent(game, model, local_input=True)
        with patch('sys.stdout', new_callable=io.StringIO) as output:
            await agent.receive_local('检查工具后锤击指定格')
            await agent.task
            await agent.receive_local('刚才做了什么？')
            await agent.task
        self.assertIn('Agent: 锤击已核验', output.getvalue())
        self.assertIn('Agent: 第二轮', output.getvalue())
        self.assertFalse(any(c == 'send_chat' for c, _ in game.sent))
        start = next(a for c, a in game.sent if c == 'start_atomic')
        self.assertEqual('local:1:1', start['chatId'])
        self.assertEqual(0, start['expectedGeneration'])
        self.assertGreater(game.polls, 0)
        user = json.loads(model.requests[0][1]['content'])
        self.assertEqual('local_terminal', user['source'])
        self.assertIsNone(user['senderId'])
        self.assertEqual(4, len(model.requests))
        self.assertTrue(any(m['role'] == 'tool' for m in model.requests[-1]))

    async def test_invalid_disabled_and_old_mod_input_never_starts_model(self):
        game = LocalGame()
        agent = Agent(game, Model(), local_input=True)
        for text in ('', '  ', 'x' * 2001, 'one\ntwo', 'bad\0text', None):
            with self.subTest(text=repr(text)[:30]), self.assertRaises(ValueError):
                await agent.receive_local(text)
        self.assertEqual([], game.sent)
        with self.assertRaises(ValueError):
            await Agent(game, Model()).receive_local('test')
        game.hello['capabilities'].remove('local_terminal_input_v1')
        with self.assertRaisesRegex(BridgeError, 'local_terminal_input_v1'):
            await agent.receive_local('test')
        self.assertEqual([], game.sent)

    async def test_registration_failure_never_falls_back_to_unguarded_actions(self):
        for reply in ({'status': 'local_turn_started', 'data': {}},
                      {'status': 'local_turn_started', 'data': []},
                      {'status': 'wrong', 'data': {'chatId': 'local:1:1'}}):
            game = LocalGame()
            game.command = lambda *a, **k: reply
            model = Model()
            agent = Agent(game, model, local_input=True)
            with self.assertRaises(BridgeError):
                await agent.receive_local('移动')
            self.assertEqual([], model.requests)
            self.assertIsNone(agent.task)

    async def test_late_uncancellable_model_cannot_reply_or_call_tool(self):
        entered, release = asyncio.Event(), asyncio.Event()
        class Delayed(Model):
            async def complete(self, messages, tools):
                self.requests.append(copy.deepcopy(messages))
                if len(self.requests) == 1:
                    entered.set()
                    try:
                        await release.wait()
                    except asyncio.CancelledError:
                        await release.wait()
                    return tool('use_item')
                return final('新指令')
        game = LocalGame()
        agent = Agent(game, Delayed(), local_input=True)
        with patch('sys.stdout', new_callable=io.StringIO) as output:
            await agent.receive_local('旧指令')
            await entered.wait()
            old = agent.task
            await agent.receive_local('新指令')
            await agent.task
            release.set()
            await old
        self.assertEqual('Agent: 新指令\n', output.getvalue())
        self.assertFalse(any(c in ('send_chat', 'start_atomic') for c, _ in game.sent))

    async def test_cancel_running_action_on_new_input_and_close(self):
        game = LocalGame(pending=True)
        model = Model(tool('mine_wall', '{"x":5,"y":5}'), final('停止旧动作'))
        agent = Agent(game, model, local_input=True)
        with patch('sys.stdout', new_callable=io.StringIO):
            await agent.receive_local('挖墙')
            while game.action is None:
                await asyncio.sleep(.01)
            action_id = game.action['actionId']
            old = agent.task
            await agent.receive_local('先停下')
            await agent.task
            with self.assertRaises(asyncio.CancelledError):
                await old
            await agent.close()
        self.assertIn(('cancel_move', {'actionId': action_id}), game.sent)
        self.assertEqual('cancelled', game.action['status'])

    async def test_close_cancels_current_native_action(self):
        game = LocalGame(pending=True)
        agent = Agent(game, Model(tool('place_wall', '{"x":5,"y":5}')), local_input=True)
        await agent.receive_local('放墙')
        while game.action is None:
            await asyncio.sleep(.01)
        action_id = game.action['actionId']
        await agent.close()
        self.assertIn(('cancel_move', {'actionId': action_id}), game.sent)
        self.assertEqual('cancelled', game.action['status'])

    async def test_real_llm_usage_accounting_preserved(self):
        def handler(request):
            body = json.loads(request.content)
            self.assertIn('tools', body)
            return httpx.Response(200, json={'choices': [{'finish_reason': 'stop', 'message': final('你好')}],
                                            'usage': {'prompt_tokens': 100, 'completion_tokens': 7,
                                                      'prompt_tokens_details': {'cached_tokens': 60}}})
        client = LLMClient('https://example.test/v1', 'fake-key', 'test', transport=httpx.MockTransport(handler))
        agent = Agent(LocalGame(), client, local_input=True)
        records = []
        try:
            with patch('cost_metrics.emit', side_effect=lambda kind, **data: records.append((kind, data))), patch('sys.stdout', new_callable=io.StringIO):
                await agent.receive_local('你好')
                await agent.task
            cost = next(d for k, d in records if k == 'llm_cost')
            self.assertEqual(100, cost['input_tokens'])
            self.assertEqual(7, cost['output_tokens'])
            self.assertEqual(40, cost['non_cached_input_tokens'])
            summary = next(d for k, d in records if k == 'chat_cost')
            self.assertEqual(1, summary['model_calls'])
            self.assertEqual(100, summary['tokens']['input_tokens']['known_sum'])
        finally:
            await agent.close()
            await client.close()

    async def test_local_mode_ignores_remote_chat(self):
        game, model = LocalGame(), Model()
        agent = Agent(game, model, local_input=True)
        agent.receive(event(1))
        self.assertIsNone(agent.task)
        self.assertEqual([], game.sent)

    async def test_local_move_still_requires_fresh_observation(self):
        game = LocalGame()
        model = Model(tool('move', '{"direction":"right","duration_ms":100}'), final('完成'))
        agent = Agent(game, model, local_input=True)
        with patch('sys.stdout', new_callable=io.StringIO):
            await agent.receive_local('向右移动一小段距离')
            await agent.task
        self.assertFalse(any(c == 'move_for' for c, _ in game.sent))
        result = json.loads(model.requests[1][-1]['content'])
        self.assertFalse(result['valid'])

    async def test_local_atomic_failure_still_blocks_followup_action(self):
        game = LocalGame(outcome='failed')
        model = Model(tool('mine_wall', '{"x":5,"y":5}'), tool('use_item'), final('完成'))
        agent = Agent(game, model, local_input=True)
        with patch('sys.stdout', new_callable=io.StringIO) as output:
            await agent.receive_local('挖墙')
            await agent.task
        self.assertEqual(1, sum(c == 'start_atomic' for c, _ in game.sent))
        self.assertIn('行动未完成', output.getvalue())
        self.assertTrue(json.loads(model.requests[-1][-1]['content'])['blocked'])

    async def test_terminal_blank_oversize_and_eof(self):
        class Sink:
            def __init__(self): self.lines = []
            def receive(self, event): pass
            async def receive_local(self, text): self.lines.append(text)
        sink = Sink()
        with patch('sys.stderr', new_callable=io.StringIO) as errors:
            await asyncio.wait_for(terminal_loop(sink, LocalGame(), asyncio.Queue(),
                io.StringIO('\n' + 'x' * 5000 + '\nhello\n/quit\nignored\n')), 2)
        self.assertEqual(['hello'], sink.lines)
        self.assertIn('exceeds 2000', errors.getvalue())
        await asyncio.wait_for(terminal_loop(sink, LocalGame(), asyncio.Queue(), io.StringIO('')), 2)

    async def test_disconnect_while_waiting_for_input(self):
        game = LocalGame()
        game._failure = BridgeError('disconnected')
        with self.assertRaisesRegex(BridgeError, 'disconnected'):
            await terminal_loop(Agent(game, Model(), local_input=True), game, asyncio.Queue(), io.StringIO(''))

    async def test_run_closes_agent_bridge_and_model_on_eof(self):
        game, model = LocalGame(), Model()
        game.close = lambda: setattr(game, 'closed', True)
        async def close_model(): model.closed = True
        model.close = close_model
        with patch('agent.Bridge', return_value=game), patch('agent.LLMClient', return_value=model), \
                patch('sys.stdin', io.StringIO('')), patch('sys.stdout', new_callable=io.StringIO):
            await asyncio.wait_for(run(18777, 2, local_input=True), 2)
        self.assertTrue(game.closed)
        self.assertTrue(model.closed)
