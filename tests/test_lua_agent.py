"""Agent/model integration with real Lua workers, simulated game and HTTP model.

No external model requests and no Terraria game acceptance claims.
"""
import asyncio
import hashlib
import io
import json
import multiprocessing as mp
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

import httpx

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'python'))
import agent_trace as tracing
import atomic_operations as atomic
from agent import Agent, TOOLS, SYSTEM
from llm import LLMClient
from lua_agent import ProgramStore, TOOL, validate_arguments, summarize
from lua_runtime import Limits
from test_agent import Model, event, final, tool
from test_lua_runtime import RuntimeGame


def program(source):
    return tool('execute_lua', json.dumps({'source': source}, ensure_ascii=False))


def lua_results(model):
    return [json.loads(message['content']) for request in model.requests
            for message in request if message.get('role') == 'tool'
            and 'programCompleted' in json.loads(message['content'])]


class LocalGame(RuntimeGame):
    def __init__(self, *args, **kwargs):
        super().__init__(*args, **kwargs)
        self.hello['capabilities'].append('local_terminal_input_v1')
        self.turn = 0

    def command(self, cmd, **params):
        if cmd == 'begin_local_turn' and not self.closed:
            self.sent.append((cmd, params))
            self.turn += 1
            return {'status': 'local_turn_started', 'data': {'chatId': f'local:{self.turn}'}}
        return super().command(cmd, **params)


class StoreTests(unittest.TestCase):
    def test_summary_includes_action_completed_only_during_cleanup(self):
        result = summarize({'status': 'timeout', 'actionState': 'released',
                            'lastAction': {'valid': True, 'status': 'cancelled'}}, [])
        self.assertFalse(result['programCompleted'])
        self.assertFalse(result['actionSuccess'])
        self.assertEqual('not_completed', result['taskOutcome'])

    def test_archive_is_bounded_deduplicated_and_secret_redacted(self):
        with tempfile.TemporaryDirectory() as directory:
            store = ProgramStore(directory, maximum=2)
            for source in ('return 1', 'return 2', 'return 3', 'return 3'):
                record = store.save(source)
            files = list(Path(directory).glob('*.lua'))
            self.assertEqual(2, len(files))
            self.assertEqual('return 3', Path(record['path']).read_text(encoding='utf-8'))
            self.assertEqual(hashlib.sha256(b'return 3').hexdigest(), record['sourceSha256'])
            secret = 'lua-stage2-private-fixture'
            tracing.trace.add_secret(secret)
            record = store.save('return "' + secret + '"')
            self.assertTrue(record['redacted'])
            self.assertNotIn(secret, Path(record['path']).read_text(encoding='utf-8'))

    def test_model_contract_uses_existing_atomic_schemas(self):
        self.assertIn(TOOL, TOOLS)
        description = TOOL['function']['description']
        for name in atomic.SCHEMAS:
            self.assertIn(name, description)
        self.assertIn('json.null', SYSTEM)
        for args in ({}, {'source': ''}, {'source': 'return 1', 'seconds': 300},
                     {'source': '中'*6000}, {'source': 1}):
            with self.assertRaises(ValueError):
                validate_arguments(args)


class AgentLuaTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.store = ProgramStore(Path(self.directory.name) / 'sources')
        self.stderr = patch('sys.stderr', new_callable=io.StringIO)
        self.stderr.start()

    async def asyncTearDown(self):
        self.stderr.stop()
        self.directory.cleanup()
        self.assertFalse(mp.active_children())

    async def wait_started(self, game):
        async def wait():
            while game.action is None:
                await asyncio.sleep(.01)
        await asyncio.wait_for(wait(), 10)

    async def run_agent(self, *answers, game=None, **options):
        game = game or RuntimeGame()
        model = Model(*answers)
        agent = Agent(game, model, lua_store=self.store, **options)
        agent.receive(event(1, text='按授权完成测试任务'))
        await agent.task
        return agent, model, game

    async def test_http_client_model_program_real_lua_result_feedback_and_costs(self):
        source = '''
            local before=api('inspect_inventory',{mode='selected'})
            local selected=api('select_hotbar_slot',{slot=2})
            if not selected.valid or not selected.completed then return {action=selected} end
            return {action=selected,after=api('inspect_inventory',{mode='selected'})}
        '''
        requests, records = [], []
        def handler(request):
            body = json.loads(request.content)
            requests.append(body)
            self.assertEqual(2048, body['max_tokens'])
            self.assertIn(TOOL, body['tools'])
            if len(requests) == 1:
                answer, reason = program(source), 'tool_calls'
            else:
                result = json.loads(body['messages'][-2]['content'])
                self.assertTrue(result['programCompleted'])
                self.assertTrue(result['actionSuccess'])
                self.assertEqual('unknown', result['taskOutcome'])
                self.assertEqual(2, result['value']['after']['data']['slot'])
                self.assertEqual(6, result['movementLoop']['remainingToolCalls'])
                self.assertEqual(61, json.loads(body['messages'][-1]['content'])['runtimeLua']['remainingApiCallsIfExecutedNow'])
                answer, reason = final('快捷栏已切换并重新读取'), 'stop'
            return httpx.Response(200, json={'choices': [{'message': answer, 'finish_reason': reason}],
                'usage': {'prompt_tokens': 100, 'completion_tokens': 30}})
        client = LLMClient('https://model.test/v1', 'fixture', 'fixture', transport=httpx.MockTransport(handler))
        game = RuntimeGame()
        trace = tracing.Trace(Path(self.directory.name) / 'trace.jsonl', enabled=True)
        try:
            with patch.object(tracing, 'trace', trace), patch('cost_metrics.emit',
                    side_effect=lambda kind, **data: records.append((kind, data))):
                agent = Agent(game, client, lua_store=self.store)
                agent.receive(event(1))
                await agent.task
            summary = next(data for kind, data in records if kind == 'chat_cost')
            self.assertEqual(4, summary['tool_calls'])
            self.assertEqual(3, summary['lua_api_calls'])
            self.assertEqual(200, summary['tokens']['input_tokens']['known_sum'])
            self.assertEqual('unknown', summary['task_outcome'])
            self.assertEqual('completed', summary['action_outcome'])
            self.assertEqual('1', next(p['chatId'] for c, p in game.sent if c == 'start_atomic'))
            self.assertEqual(source, next(self.store.directory.glob('*.lua')).read_text(encoding='utf-8'))
            saved = json.loads(next(m['content'] for m in agent.history if m['role'] == 'tool'))
            self.assertNotIn('value', saved)
            self.assertTrue(saved['valueExpired'])
            events = [json.loads(line) for line in trace.path.read_text(encoding='utf-8').splitlines()]
            self.assertTrue(next(row for row in events if row['type'] == 'tool_result')['success'])
            self.assertTrue(all(row.get('lua_run_id') for row in events if row['type'] == 'lua_tool_result'))
        finally:
            await client.close()

    async def test_failed_action_cannot_be_hidden_by_script_return_or_followup_tool(self):
        trace = tracing.Trace(Path(self.directory.name) / 'trace.jsonl', enabled=True)
        with patch.object(tracing, 'trace', trace):
            _, model, game = await self.run_agent(program('''
                api('mine_tile',{x=1,y=1})
                api('select_hotbar_slot',{slot=2})
                return {success=true}
            '''), tool('use_item'), final('已完成'), game=RuntimeGame('failed'))
        result = lua_results(model)[0]
        self.assertTrue(result['programCompleted'])
        self.assertFalse(result['actionSuccess'])
        self.assertEqual('not_completed', result['taskOutcome'])
        self.assertEqual(1, sum(c == 'start_atomic' for c, _ in game.sent))
        self.assertIn('行动未完成', next(p['text'] for c, p in game.sent if c == 'send_chat'))
        records = [json.loads(line) for line in trace.path.read_text(encoding='utf-8').splitlines()]
        wrapper = next(row for row in records if row['type'] == 'tool_result')
        self.assertFalse(wrapper['success'])
        self.assertFalse(wrapper['action_success'])

    async def test_prior_direct_failure_cannot_be_bypassed_by_a_new_program(self):
        _, model, game = await self.run_agent(tool('use_item'), program('''
            local blocked=api('select_hotbar_slot',{slot=2})
            return {blocked=blocked,read=api('inspect_inventory')}
        '''), final('完成'), game=RuntimeGame('failed'))
        result = lua_results(model)[0]
        self.assertEqual('blocked', result['value']['blocked']['status'])
        self.assertTrue(result['value']['read']['valid'])
        self.assertEqual(1, sum(c == 'start_atomic' for c, _ in game.sent))

    async def test_predispatch_rejection_preserves_agent_barrier_without_poison(self):
        game = RuntimeGame()
        game.hello['capabilities'].remove('server_confirmed_interactions_v1')
        agent, model, _ = await self.run_agent(program('''
            local rejected = api('interact_tile', {x=5,y=5})
            return {rejected=rejected, readable=api('inspect_inventory').valid}
        '''), tool('use_item'), program('''
            local blocked = api('use_item')
            return {blocked=blocked, readable=api('inspect_inventory').valid}
        '''), final('完成'), game=game)
        receipts = lua_results(model)
        first = next(r for r in receipts if r.get('value', {}).get('rejected'))
        self.assertTrue(first['programCompleted'])
        self.assertFalse(first['actionSuccess'])
        self.assertEqual('idle', first['actionState'])
        self.assertEqual('not_dispatched', first['value']['rejected']['dispatchState'])
        self.assertEqual('failed', first['lastAction']['status'])
        self.assertTrue(first['value']['readable'])
        direct = json.loads(model.requests[2][-2]['content'])
        self.assertTrue(direct['blocked'])
        self.assertEqual(first['lastAction'], direct['previousFailure'])
        following = next(r for r in receipts if r.get('value', {}).get('blocked'))
        self.assertTrue(following['value']['blocked']['blocked'])
        self.assertTrue(following['value']['readable'])
        self.assertFalse(any(c in ('start_atomic', 'cancel_move') for c, _ in game.sent))
        self.assertFalse(game.closed)
        self.assertFalse(getattr(game, '_lua_poisoned', False))
        self.assertIn('server_confirmed_interactions_v1', agent.history[-1]['content'])

    async def test_script_error_after_action_stops_further_programs_and_preserves_action_status(self):
        records = []
        with patch('cost_metrics.emit', side_effect=lambda kind, **data: records.append((kind, data))):
            _, model, game = await self.run_agent(
                program("api('select_hotbar_slot',{slot=2}); error('after action')"),
                program("return api('use_item')"), final('全部完成'))
        self.assertEqual(1, sum(c == 'start_atomic' for c, _ in game.sent))
        self.assertEqual('script_error', lua_results(model)[0]['status'])
        self.assertIn('Lua 程序未完成', next(p['text'] for c, p in game.sent if c == 'send_chat'))
        summary = next(data for kind, data in records if kind == 'chat_cost')
        self.assertEqual('completed', summary['action_outcome'])
        self.assertEqual('not_completed', summary['task_outcome'])

    async def test_syntax_only_error_can_be_corrected_before_any_action(self):
        _, model, game = await self.run_agent(program('not valid lua'),
            program("return api('inspect_inventory')"), final('读取完成'))
        self.assertEqual('script_error', lua_results(model)[0]['status'])
        self.assertEqual('读取完成', next(p['text'] for c, p in game.sent if c == 'send_chat'))
        self.assertFalse(any(c == 'start_atomic' for c, _ in game.sent))

    async def test_lua_call_budget_stops_internal_loop_and_later_direct_action(self):
        records = []
        with patch('cost_metrics.emit', side_effect=lambda kind, **data: records.append((kind, data))):
            _, model, game = await self.run_agent(program('''
                for i=1,10 do api('inspect_inventory') end
                api('use_item')
            '''), tool('use_item'), final('budget exhausted'), max_tool_calls=3, lua_limits=Limits(calls=2))
        result = lua_results(model)[0]
        self.assertEqual(('call_limit', 2), (result['status'], result['calls']))
        self.assertEqual(2, result['movementLoop']['remainingToolCalls'])
        self.assertFalse(any(c == 'start_atomic' for c, _ in game.sent))
        self.assertEqual(4, next(data for kind, data in records if kind == 'chat_cost')['tool_calls'])

    async def test_one_model_tool_can_use_multiple_internal_api_calls(self):
        _, model, game = await self.run_agent(program("for i=1,10 do api('inspect_inventory') end"), final('read'), max_tool_calls=1)
        self.assertEqual('completed', lua_results(model)[0]['status'])
        self.assertEqual(10,sum(c=='query_atomic' and p['operation']=='inspect_inventory' and p['arguments'].get('mode')!='full' for c,p in game.sent))

    async def test_exact_lua_api_exhaustion_blocks_direct_actions_but_allows_reads_and_stop(self):
        for name, arguments in (('use_item', '{}'), ('move', '{"direction":"right","duration_ms":100}')):
            with self.subTest(tool=name):
                _, model, game = await self.run_agent(
                    program("api('inspect_inventory'); return api('inspect_inventory')"),
                    tool(name, arguments), tool('inspect_inventory'), tool('stop_action'),
                    tool(name, arguments), final('stopped'), lua_limits=Limits(calls=2))
                receipt = lua_results(model)[0]
                self.assertEqual(('completed', 2), (receipt['status'], receipt['calls']))
                self.assertTrue(receipt['programCompleted'])
                self.assertEqual(0, json.loads(model.requests[1][-1]['content'])['runtimeLua']['remainingLuaApiCalls'])
                for request in (model.requests[2], model.requests[5]):
                    blocked = json.loads(request[-2]['content'])
                    self.assertTrue(blocked['blocked'], blocked)
                    self.assertEqual('lua_api_budget_exhausted', blocked.get('reason', blocked.get('error')))
                self.assertTrue(json.loads(model.requests[3][-2]['content'])['valid'])
                stopped = json.loads(model.requests[4][-2]['content'])
                self.assertTrue(stopped['valid'])
                self.assertTrue(stopped['noOp'])
                self.assertFalse(any(c in ('start_atomic', 'move_for') for c, _ in game.sent))
                self.assertFalse(game.closed)

    async def test_wall_budget_is_shared_across_programs(self):
        class SlowRuntime:
            last_result = None
            def __init__(self, bridge):
                pass
            async def execute(self, *args, **kwargs):
                await asyncio.sleep(.06)
                return {'runId': 'fixture', 'status': 'completed', 'calls': 0, 'value': None,
                        'actionState': 'idle', 'lastAction': None, 'cleanup': None, 'trace': []}
        with patch('agent.LuaRuntime', SlowRuntime):
            _, model, _ = await self.run_agent(program('return 1'), program('return 2'),
                                             lua_limits=Limits(seconds=.05))
        self.assertEqual(2, len(model.requests))
        self.assertEqual(1, len(list(self.store.directory.glob('*.lua'))))

        for name, arguments in (('use_item', '{}'), ('move', '{"direction":"right","duration_ms":100}')):
            with self.subTest(tool=name), patch('agent.LuaRuntime', SlowRuntime):
                _, model, game = await self.run_agent(program('return 1'), tool(name, arguments),
                                                     final('stopped'), lua_limits=Limits(seconds=.05))
                blocked = json.loads(model.requests[-1][-2]['content'])
                self.assertTrue(blocked['blocked'], blocked)
                self.assertEqual('lua_time_budget_exhausted', blocked.get('reason', blocked.get('error')))
                self.assertFalse(any(c in ('start_atomic', 'move_for') for c, _ in game.sent))

    async def test_invalid_arguments_and_archive_failure_do_not_start_worker(self):
        for arguments in ({'source': 'return 1', 'seconds': 1}, {'source': '中'*6000}):
            with patch('agent.LuaRuntime') as runtime:
                await self.run_agent(tool('execute_lua', json.dumps(arguments)), final('完成'))
            runtime.assert_not_called()
        with patch.object(self.store, 'save', side_effect=OSError('archive unavailable')), patch('agent.LuaRuntime') as runtime:
            _, _, game = await self.run_agent(program("api('use_item')"), final('完成'))
        runtime.assert_not_called()
        self.assertFalse(any(c == 'start_atomic' for c, _ in game.sent))

    async def test_use_item_effect_warning_survives_later_successful_action(self):
        _, _, game = await self.run_agent(program('''
            api('use_item'); return api('select_hotbar_slot',{slot=2})
        '''), final('召唤成功'))
        text = next(p['text'] for c, p in game.sent if c == 'send_chat')
        self.assertIn('实际效果', text)
        self.assertIn('未确认', text)

    async def test_new_chat_waits_for_lua_cleanup_and_discards_old_batch(self):
        game = RuntimeGame(pending=True)
        model = Model(program("api('use_item'); api('use_item')"), final('新消息'))
        agent = Agent(game, model, lua_store=self.store)
        entered, release = asyncio.Event(), asyncio.Event()
        original = atomic.command
        async def delayed(bridge, cmd, **params):
            if cmd == 'cancel_move':
                entered.set()
                await release.wait()
            return await original(bridge, cmd, **params)
        with patch.object(atomic, 'command', delayed):
            agent.receive(event(1))
            await self.wait_started(game)
            old = agent.task
            agent.receive(event(2))
            await asyncio.wait_for(entered.wait(), 5)
            await asyncio.sleep(.05)
            self.assertEqual(1, len(model.requests))
            release.set()
            await agent.task
            with self.assertRaises(asyncio.CancelledError):
                await old
        self.assertEqual('cancelled', game.action['status'])
        self.assertEqual(['新消息'], [p['text'] for c, p in game.sent if c == 'send_chat'])
        self.assertFalse(any(m['role'] == 'tool' for m in agent.history))
        self.assertEqual(1, len(list(self.store.directory.glob('*.lua'))))

    async def test_new_local_instruction_registers_only_after_lua_release(self):
        game = LocalGame(pending=True)
        agent = Agent(game, Model(program("api('use_item')"), final('新指令')), local_input=True, lua_store=self.store)
        entered, release = asyncio.Event(), asyncio.Event()
        original = atomic.command
        async def delayed(bridge, cmd, **params):
            if cmd == 'cancel_move':
                entered.set()
                await release.wait()
            return await original(bridge, cmd, **params)
        with patch.object(atomic, 'command', delayed), patch('sys.stdout', new_callable=io.StringIO) as output:
            await agent.receive_local('旧指令')
            await self.wait_started(game)
            old = agent.task
            replacement = asyncio.create_task(agent.receive_local('新指令'))
            await asyncio.wait_for(entered.wait(), 5)
            self.assertEqual(1, game.turn)
            release.set()
            await replacement
            await agent.task
            with self.assertRaises(asyncio.CancelledError):
                await old
        self.assertEqual(2, game.turn)
        self.assertEqual('Agent: 新指令\n', output.getvalue())

    async def test_close_waits_for_running_program_action_release(self):
        game = RuntimeGame(pending=True)
        agent = Agent(game, Model(program("api('use_item')")), lua_store=self.store)
        agent.receive(event(1))
        await self.wait_started(game)
        await agent.close()
        self.assertEqual('cancelled', game.action['status'])
        self.assertIsNone(agent._lua_task)

    async def test_unknown_action_state_blocks_new_program_and_warns_local_user(self):
        game = LocalGame()
        original = game.command
        def missing(cmd, **params):
            result = original(cmd, **params)
            if cmd == 'get_movement' and game.action:
                result['movement']['action']['endEvidenceStatus'] = 'unknown'
            return result
        game.command = missing
        model = Model(program("api('use_item')"), program("api('use_item')"), final('完成'))
        agent = Agent(game, model, local_input=True, lua_store=self.store)
        with patch('sys.stdout', new_callable=io.StringIO) as output:
            await agent.receive_local('测试')
            await agent.task
        self.assertTrue(game.closed)
        self.assertIn('行动未完成', output.getvalue())
        self.assertEqual(1, sum(c == 'start_atomic' for c, _ in game.sent))
        self.assertEqual('unknown', lua_results(model)[0]['actionState'])

    async def test_truncated_http_program_is_never_executed(self):
        def handler(request):
            return httpx.Response(200, json={'choices': [{'message': program("api('use_item')"),
                                                        'finish_reason': 'length'}]})
        client = LLMClient('https://model.test/v1', 'fixture', 'fixture', transport=httpx.MockTransport(handler))
        try:
            game = RuntimeGame()
            agent = Agent(game, client, lua_store=self.store)
            agent.receive(event(1))
            await agent.task
            self.assertFalse(self.store.directory.exists())
            self.assertFalse(any(c == 'start_atomic' for c, _ in game.sent))
        finally:
            await client.close()


if __name__ == '__main__':
    unittest.main()
