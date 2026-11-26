"""M3: real C# TCP/registry, Python Agent and Lua Worker; native executor substitute."""
import asyncio
import copy
import json
from pathlib import Path
import subprocess
import tempfile
import threading
import unittest
from unittest.mock import patch
from jsonschema import Draft202012Validator

from test_agent import Model, final, event, tool
from player_goals import GoalStore
from test_lua_agent import program
from agent import Agent
from agent_run import facts_reply, goal_outcome, task_outcome
from api_result_contracts import schema_for
from control import BridgeError
from llm import LLMError
from lua_agent import ProgramStore
from lua_runtime import LuaRuntime
from program_tasks import TaskCoordinator, coordinator, definitions, validate
from server_ai import ServerAiBridge
from server_ai_contract import api_help

SUBMIT = "local t=api('submit_task',{operation='mine_target',arguments={x=10,y=20}});"


class Authorizer:
    def command(self, command, **params):
        return {'status': 'authorized', 'data': {'chatId': params['chatId'], 'replyTurn': params['replyTurn']}}


class M3ProtocolTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.directory=tempfile.TemporaryDirectory()
        self.ready=Path(self.directory.name)/'ready'
        self.stop=Path(self.directory.name)/'stop'
        Path(str(self.ready)+'.live').touch()
        if self._testMethodName.startswith('test_frontier_'):
            Path(str(self.ready)+'.advance.frontier').touch()
        self.process=subprocess.Popen(['pwsh','-NoProfile','-File',str(Path(__file__).with_name('run_api_contract_host.ps1')),
            '-ReadyPath',str(self.ready),'-StopPath',str(self.stop)],stdout=subprocess.PIPE,stderr=subprocess.PIPE,
            creationflags=getattr(subprocess,'CREATE_NO_WINDOW',0))
        async def started():
            while not self.ready.exists():
                if self.process.poll() is not None:
                    out,err=self.process.communicate();self.fail((out+err).decode(errors='replace'))
                await asyncio.sleep(.03)
        await asyncio.wait_for(started(),15)
        self.bridge=await asyncio.to_thread(ServerAiBridge,int(self.ready.read_text()),5)
        self.manager=coordinator(self.bridge)
        self.runtime=LuaRuntime(self.bridge,evidence_root=self.directory.name)
        self.agent=None

    async def asyncTearDown(self):
        if self.agent is not None:await self.agent.close()
        else:await self.manager.close()
        self.bridge.close();self.stop.write_text('stop')
        try:out,err=await asyncio.to_thread(self.process.communicate,timeout=5)
        except subprocess.TimeoutExpired:
            self.process.terminate();out,err=await asyncio.to_thread(self.process.communicate,timeout=5)
        self.assertEqual(0,self.process.returncode,(out+err).decode(errors='replace'))
        self.directory.cleanup()

    async def starts(self):
        return (await asyncio.to_thread(self.bridge._request,'state'))['data']['starts']

    async def wait_for(self, predicate):
        async def wait():
            while not predicate():await asyncio.sleep(.01)
        await asyncio.wait_for(wait(),10)

    def complete(self):Path(str(self.ready)+'.advance').touch()

    async def submit(self):
        result=await self.runtime.execute(SUBMIT+'return t')
        self.assertEqual('completed',result['status'],result)
        Draft202012Validator(schema_for('submit_task')).validate(result['value'])
        return result['value']['taskId']

    def make_agent(self, model):
        self.agent=Agent(self.bridge,model,chat_bridge=Authorizer(),lua_store=ProgramStore(Path(self.directory.name)/'programs'))
        return self.agent

    def receive(self, agent, n, text='挖掘指定矿物'):
        agent.receive(event(n,text=text,isSelf=True,source='local_ai_command'))

    async def test_program_end_wait_timeout_and_late_result_exactly_once(self):
        notices=[];self.manager.on_ready=lambda:notices.append(True)
        task_id=await self.submit()
        waited=await self.runtime.execute("return api('await_task',{taskId='"+task_id+"',milliseconds=1})")
        self.assertEqual(('completed','timeout','running'),(waited['status'],waited['value']['waitStatus'],waited['value']['status']))
        Draft202012Validator(schema_for('await_task')).validate(waited['value'])
        self.assertEqual('running',task_outcome([], active_tasks=waited['tasks']['active']))
        query=await self.runtime.execute("return api('query_task',{taskId='"+task_id+"'})")
        self.assertEqual('running',query['value']['status'])
        help_view = api_help(self.bridge, {'name': 'query_task'})
        validator = Draft202012Validator(help_view['returnsSchema'])
        validator.validate(query['value'])
        self.assertIsNone(query['value']['action']['task']['currentChild'])
        self.assertNotIn('result', query['value'])
        for field in ('action.task', 'phase', 'progress', 'currentChild', 'lastResult', 'result.task'):
            self.assertIn(field, help_view['returns'])
        for phrase in ('progress.children', 'effectVerified', 'inputsReleased', 'unknown',
                       'summaryOnly', 'resultSummaryOnly', '用原 taskId 调用 query_task'):
            self.assertIn(phrase, help_view['statusSemantics'])
        conflict=await self.runtime.execute("api('use_item',{})")
        self.assertEqual('failed',conflict['status']);self.assertEqual(1,await self.starts())
        self.complete()
        await self.wait_for(lambda:self.manager.records[task_id]['done'].is_set())
        queried=await self.runtime.execute("return api('query_task',{taskId='"+task_id+"'})")
        self.assertEqual('completed', queried['status'], queried)
        view=queried['value']
        validator.validate(view)
        task = view['action']['task']
        self.assertEqual(task, view['result']['task'])
        self.assertEqual('terminal', task['phase'])
        self.assertIsNone(task['currentChild'])
        child, = task['progress']['children']
        self.assertEqual(task_id, child['parentTaskId'])
        self.assertEqual('mine_tile', child['taskName'])
        self.assertEqual((1, 1), (task['progress']['settledChildren'], task['progress']['completedChildren']))
        self.assertEqual(child['result'], task['lastResult'])
        self.assertTrue(task['result']['effectVerified'])
        self.assertEqual('completed',view['result']['status']);self.assertTrue(view['result']['completed'])
        self.assertEqual('completed',task_outcome([view['result']],False,'unknown'))
        self.assertEqual([True],notices)
        projection=self.manager.projection();self.assertEqual(1,len(projection['results']))
        with patch('lua_action_results.ACTION_RESULTS_BYTES', 1024):
            summary, = self.manager.projection()['results']
        self.assertTrue(summary['resultSummaryOnly'])
        self.assertNotIn('task', summary['result'])
        self.manager.consume(projection['throughSequence'])
        self.assertEqual(view, await self.manager.invoke('query_task',{'taskId':summary['taskId']}))
        self.assertEqual([],self.manager.projection()['results']);self.assertEqual([True],notices)

    async def test_program_cancel_preserves_task_then_explicit_cancel_releases(self):
        task_id=await self.submit()
        waiting=asyncio.create_task(self.runtime.execute("return api('await_task',{taskId='"+task_id+"',milliseconds=30000})"))
        await asyncio.sleep(.2);waiting.cancel()
        with self.assertRaises(asyncio.CancelledError):await waiting
        self.assertEqual('running',(await self.manager.invoke('query_task',{'taskId':task_id}))['status'])
        cancelled=await self.manager.invoke('cancel_task',{'taskId':task_id})
        self.assertEqual('cancelled',cancelled['status']);self.assertTrue(cancelled['result']['inputsReleased'])
        self.assertEqual(1,await self.starts())

    async def test_mcp_program_cancel_preserves_task_but_lease_delete_stops_it(self):
        import httpx
        from agent import is_server_ai_chat
        from external_brain import BrainHost
        try:
            from external_mcp import ExternalMcp, token_digest
        except ImportError:
            self.skipTest('install python/requirements-mcp.txt to verify External Brain')
        token = 'a' * 32
        app = ExternalMcp(BrainHost(self.make_agent(None), is_server_ai_chat), 18790, token_digest(token))
        headers = {'Authorization': 'Bearer ' + token, 'Accept': 'application/json, text/event-stream'}
        transport = httpx.ASGITransport(app=app)
        try:
            async with httpx.AsyncClient(transport=transport, base_url='http://127.0.0.1:18790',
                                         headers=headers) as client:
                async def post(method, params, request_id=None):
                    body = {'jsonrpc': '2.0', 'method': method, 'params': params}
                    if request_id is not None:
                        body['id'] = request_id
                    return await asyncio.wait_for(client.post('/mcp', json=body), 10)

                opened = await post('initialize', {'protocolVersion': '2025-11-25', 'capabilities': {},
                                    'clientInfo': {'name': 'fixture', 'version': '1'}}, 1)
                self.assertEqual(200, opened.status_code, opened.text)
                client.headers.update({'Mcp-Session-Id': opened.headers['Mcp-Session-Id'],
                                       'MCP-Protocol-Version': opened.json()['result']['protocolVersion']})
                self.assertEqual(202, (await post('notifications/initialized', {})).status_code)
                submitted = await post('tools/call', {'name': 'execute_lua',
                    'arguments': {'source': SUBMIT + 'return t'}}, 2)
                task_id = submitted.json()['result']['structuredContent']['value']['taskId']
                wait_source = f"return api('await_task',{{taskId='{task_id}',milliseconds=30000}})"
                waiting = asyncio.create_task(post('tools/call', {'name': 'execute_lua',
                                             'arguments': {'source': wait_source}}, 3))
                await self.wait_for(lambda: bool(app.session.jobs))
                self.assertEqual(202, (await post('notifications/cancelled', {'requestId': 3})).status_code)
                await waiting
                await self.wait_for(lambda: not app.session.jobs)
                self.assertEqual('running', (await self.manager.invoke('query_task', {'taskId': task_id}))['status'])
                deleted = await asyncio.wait_for(client.delete('/mcp'), 10)
                self.assertEqual(200, deleted.status_code)
                self.assertEqual('native', app.brain.phase)
                terminal = await self.manager.invoke('query_task', {'taskId': task_id})
                self.assertEqual('cancelled', terminal['status'])
                self.assertTrue(terminal['result']['endEvidence']['inputsReleased'])
                self.assertEqual(1, await self.starts())
        finally:
            await app.close()

    async def test_existing_synchronous_mine_api_also_preserves_task_on_program_cancel(self):
        waiting=asyncio.create_task(self.runtime.execute("return api('mine_target',{x=10,y=20})"))
        await self.wait_for(lambda:bool(self.manager.records) and all(r['ack'].is_set() for r in self.manager.records.values()))
        waiting.cancel()
        with self.assertRaises(asyncio.CancelledError):await waiting
        record=next(iter(self.manager.records.values()))
        self.assertEqual('running',record['view']['status'])
        self.complete();await self.wait_for(record['done'].is_set)
        self.assertEqual('completed',record['view']['result']['status'])
        self.assertEqual(1,await self.starts())

    async def test_stop_action_targets_the_independent_task(self):
        await self.submit()
        result=await self.runtime.execute("return api('stop_action',{})")
        self.assertEqual('cancelled',result['lastAction']['status'])
        Draft202012Validator(schema_for('stop_action')).validate(result['lastAction'])
        self.assertTrue(result['lastAction']['inputsReleased'])
        self.assertFalse(self.manager.blocked())

    async def test_synchronous_mine_conflict_has_no_submit_receipt_in_lua_or_last_action(self):
        await self.submit()
        result=await self.runtime.execute("return api('mine_target',{x=10,y=20})")
        self.assertEqual('failed',result['status'])
        self.assertIsNone(result['value'])
        self.assertIsNone(result['lastAction'])
        self.assertEqual(1,await self.starts())

    async def test_result_notification_cannot_authorize_new_mutation(self):
        model=Model(program(SUBMIT+'return t'),final('running'),program(SUBMIT+'return t'),final('invented success'))
        agent=self.make_agent(model);self.receive(agent,1);await agent.task
        first=agent.task;self.complete();await self.wait_for(lambda:agent.task is not first)
        await agent.task
        self.assertEqual(1,await self.starts());self.assertEqual(1,len(self.manager.records))
        self.assertEqual('completed',next(iter(self.manager.records.values()))['view']['status'])
        self.assertIn('已核验动作',self.bridge.reply_receipts[-1]['text'])

    async def test_unconfirmed_release_blocks_resume_and_new_task(self):
        task_id=await self.submit()
        Path(str(self.ready)+'.advance.unreleased').touch()
        result=await self.manager.invoke('cancel_task',{'taskId':task_id})
        self.assertEqual('unknown',result['status'])
        self.manager.consume(self.manager.sequence)
        self.assertEqual('unknown',self.manager.projection()['active'][0]['status'])
        resumed=await self.manager.invoke('resume_task',{'taskId':task_id})
        self.assertEqual('not_admitted',resumed['dispatchState'])
        refused=await self.manager.invoke('submit_task',{'operation':'mine_target','arguments':{'x':10,'y':20}})
        self.assertEqual('not_admitted',refused['dispatchState']);self.assertEqual(1,await self.starts())

    async def test_explicit_resume_after_verified_danger_stop_creates_fresh_identity(self):
        task_id=await self.submit();danger=Path(str(self.ready)+'.advance.danger');danger.touch()
        await self.wait_for(lambda:self.manager.records[task_id]['done'].is_set())
        old=copy.deepcopy(self.manager.records[task_id]['view'])
        self.assertEqual('cancelled',old['status']);self.assertTrue(old['result']['inputsReleased'])
        await asyncio.sleep(.1);self.assertEqual(1,await self.starts())
        danger.unlink()
        resumed=await self.manager.invoke('resume_task',{'taskId':task_id})
        self.assertNotEqual(task_id,resumed['taskId']);self.assertEqual(task_id,resumed['resumedFrom'])
        self.complete();await self.wait_for(lambda:self.manager.records[resumed['taskId']]['done'].is_set())
        self.assertEqual('completed',self.manager.records[resumed['taskId']]['view']['status'])
        self.assertEqual(old,self.manager.records[task_id]['view']);self.assertEqual(2,await self.starts())

    async def test_identity_change_is_unknown_without_replay(self):
        task_id=await self.submit();Path(str(self.ready)+'.identity').touch()
        await self.wait_for(lambda:self.manager.records[task_id]['done'].is_set())
        self.assertEqual('unknown',self.manager.records[task_id]['view']['status'])
        self.assertTrue(self.manager.blocked())

    async def test_lost_ack_stays_unknown_and_does_not_replay(self):
        original=self.bridge.task_request
        def lose(op,*args,**kwargs):
            result=original(op,*args,**kwargs)
            if op=='start':raise BridgeError('fixture lost ACK after admission')
            return result
        with patch.object(self.bridge,'task_request',side_effect=lose):
            result=await self.runtime.execute(SUBMIT+'return t')
        self.assertEqual(('action_unknown','unknown'),(result['status'],result['actionState']))
        self.assertTrue(self.manager.blocked())
        self.assertTrue(self.bridge._closed)
        self.assertEqual('1',Path(str(self.ready)+'.advance.starts').read_text())

    async def test_synchronous_mine_lost_ack_retains_unknown_action_receipt(self):
        original=self.bridge.task_request

        def lose(op,*args,**kwargs):
            result=original(op,*args,**kwargs)
            if op=='start':
                raise BridgeError('fixture lost ACK after admission')
            return result

        with patch.object(self.bridge,'task_request',side_effect=lose):
            result=await self.runtime.execute("return api('mine_target',{x=10,y=20})")
        self.assertEqual(('action_unknown','unknown'),(result['status'],result['actionState']))
        self.assertIsNone(result['value'])
        self.assertFalse(result['lastAction']['retryAllowed'])
        Draft202012Validator(schema_for('mine_target')).validate(result['lastAction'])
        self.assertEqual('1',Path(str(self.ready)+'.advance.starts').read_text())

    async def test_cancel_before_ack_keeps_admitted_task_and_receipt(self):
        gate=threading.Event();entered=threading.Event();original=self.bridge.task_request
        def delay(op,*args,**kwargs):
            result=original(op,*args,**kwargs)
            if op=='start':entered.set();gate.wait(5)
            return result
        with patch.object(self.bridge,'task_request',side_effect=delay):
            pending=asyncio.create_task(self.runtime.execute(SUBMIT+'return t'))
            await self.wait_for(entered.is_set);pending.cancel()
            with self.assertRaises(asyncio.CancelledError):await pending
            gate.set()
            await self.wait_for(lambda:all(r['ack'].is_set() for r in self.manager.records.values()))
        view=self.manager.projection()['active'][0]
        self.assertEqual(('running','admitted'),(view['status'],view['dispatchState']))
        self.complete();await self.wait_for(lambda:bool(self.manager.projection()['results']))
        self.assertEqual('completed',self.manager.projection()['results'][0]['status'])
        self.assertEqual(1,await self.starts())

    async def test_task_completed_then_final_model_error_preserves_facts(self):
        self.complete()
        model=Model(program(SUBMIT+"return api('await_task',{taskId=t.taskId,milliseconds=2000})"),LLMError('fixture final model failure'))
        agent=self.make_agent(model);records=[]
        with patch('agent.diagnostics.emit',side_effect=lambda kind,**data:records.append((kind,data))):
            self.receive(agent,1);await agent.task
        ended=[d for kind,d in records if kind=='agent_end'][-1]
        self.assertEqual(('failed','completed','completed'),(agent.runs[-1].outcome,ended['task_outcome'],ended['program_outcome']))
        self.assertIn('已核验动作：mine_target',self.bridge.reply_receipts[-1]['text'])
        self.assertIn('已确认的执行事实保留',self.bridge.reply_receipts[-1]['text'])
        self.assertEqual(2,len(model.requests));self.assertEqual(1,await self.starts())

    async def test_agent_wakes_for_late_result_and_next_run_does_not_reconsume(self):
        model=Model(program(SUBMIT+'return t'),final('所有矿物已采集完毕'),final('late'),final('next'))
        agent=self.make_agent(model);self.receive(agent,1);await agent.task
        first=agent.task;self.assertIn('仍在执行',self.bridge.reply_receipts[-1]['text'])
        self.complete();await self.wait_for(lambda:agent.task is not first)
        await agent.task
        context=json.loads(model.requests[2][-1]['content'])['runtimeTasks']
        self.assertEqual(1,len(context['results']));self.assertEqual('completed',context['results'][0]['status'])
        self.assertIn('已核验动作',self.bridge.reply_receipts[-1]['text'])
        self.receive(agent,2,'查询状态');await agent.task
        self.assertEqual([],json.loads(model.requests[3][-1]['content'])['runtimeTasks']['results'])
        self.assertEqual(1,await self.starts())

    async def test_read_only_program_still_uses_host_facts_while_task_is_active(self):
        model=Model(program(SUBMIT+'return t'),program("return api('get_player_state',{})"),final('生命值为73。'))
        agent=self.make_agent(model);self.receive(agent,1);await agent.task
        self.assertIn('仍在执行',self.bridge.reply_receipts[-1]['text'])
        self.assertNotEqual('[AI] 生命值为73。',self.bridge.reply_receipts[-1]['text'])
        self.assertEqual(1,await self.starts())

    async def test_frontier_goal_wakes_and_reports_verified_return(self):
        args = {'radiusTiles': 10, 'maxSegments': 6}
        model = Model(tool('schedule_exploration', json.dumps(args)), final('已登记'), final('已返回'))
        store = GoalStore(Path(self.directory.name) / 'goals.sqlite3')
        self.agent = Agent(self.bridge, model, chat_bridge=Authorizer(), goal_store=store,
                           lua_store=ProgramStore(Path(self.directory.name)/'programs'))
        self.receive(self.agent, 1, '探索附近十格并安全返回，最多向外走六段')
        await self.agent.task
        await self.wait_for(lambda: any(r['ack'].is_set() for r in self.manager.records.values()))
        self.assertEqual('running', store.latest()['state'])
        self.complete()
        await self.wait_for(lambda: store.latest()['state'] == 'completed')
        view = next(iter(self.manager.records.values()))['view']
        Draft202012Validator(schema_for('query_task')).validate(view)
        self.assertIn('探索新增 3 格', facts_reply([view['result']]))
        self.assertEqual(1, await self.starts())

    async def test_frontier_lua_missing_return_is_unknown(self):
        await self.assert_frontier_unconfirmed('no_return')

    async def test_frontier_completed_off_anchor_is_unknown(self):
        await self.assert_frontier_unconfirmed('off_anchor')

    async def assert_frontier_unconfirmed(self, fixture):
        Path(str(self.ready)+'.advance.'+fixture).touch()
        run = await self.runtime.execute("return api('submit_task',{operation='explore_frontier',arguments={radiusTiles=6,maxSegments=2}})")
        self.assertEqual('completed',run['status'],run)
        task_id = run['value']['taskId']
        self.complete()
        view = await self.manager.invoke('await_task', {'taskId':task_id, 'milliseconds':5000})
        self.assertEqual('unknown',view['result']['status'],view)
        self.assertFalse(view['result']['completed'])
        self.assertEqual(1, await self.starts())

    async def test_frontier_releases_before_new_player_commission(self):
        steps = [{'operation':'mine_target','arguments':{'x':x,'y':20}} for x in (10,11)]
        model = Model(tool('schedule_exploration','{"radiusTiles":8,"maxSegments":4}'),final('已登记'),
                      tool('schedule_player_goal',json.dumps({'steps':steps})),final('已登记'),final('回执'),final('回执'))
        store = GoalStore(Path(self.directory.name)/'goals.sqlite3')
        self.agent = Agent(self.bridge,model,chat_bridge=Authorizer(),goal_store=store,
                           lua_store=ProgramStore(Path(self.directory.name)/'programs'))
        self.receive(self.agent,1,'自主探索并返回')
        await self.agent.task
        await self.wait_for(lambda: any(r['ack'].is_set() for r in self.manager.records.values()))
        first = next(iter(self.manager.records.values()))
        self.assertNotIn('action',self.manager.projection()['active'][0])
        self.receive(self.agent,2,'改为依次挖除 (10,20) 和 (11,20)')
        await self.agent.task
        self.assertTrue(first['view']['result']['inputsReleased'])
        self.assertEqual('cancelled',first['view']['status'])
        self.complete()
        await self.wait_for(lambda:store.latest()['state']=='completed')
        self.assertEqual(3,await self.starts())

    async def start_commission(self):
        steps = [{'operation':'mine_target','arguments':{'x':x,'y':20}} for x in (10,11)]
        model = Model(tool('schedule_player_goal', json.dumps({'steps':steps})),
                      final('已登记'), final('收到回执'), final('收到回执'))
        store = GoalStore(Path(self.directory.name) / 'goals.sqlite3')
        self.agent = Agent(self.bridge, model, chat_bridge=Authorizer(), goal_store=store,
                           lua_store=ProgramStore(Path(self.directory.name)/'programs'))
        self.receive(self.agent, 1, '依次挖除 (10,20) 和 (11,20) 两格')
        await self.agent.task
        await self.wait_for(lambda: any(r['ack'].is_set() for r in self.manager.records.values()))
        return store

    async def test_persistent_goal_continues_without_chat(self):
        store = await self.start_commission()
        self.assertEqual(('running', 0), (store.latest()['state'],store.latest()['progress']))
        def deliver_twice(task_id, result):
            store.settle(task_id, result)
            store.settle(task_id, result)
        self.manager.on_goal_result = deliver_twice
        self.complete()
        await self.wait_for(lambda:store.latest()['state']=='completed')
        for record in self.manager.records.values():
            store.settle(record['view']['taskId'], record['view']['result'])
        self.agent._wake_task_results()
        self.assertEqual(2, GoalStore(store.path).latest()['progress'])
        self.assertEqual(2, await self.starts())
        self.assertEqual(1, len(self.agent._seen))

    async def test_stop_revokes_pending_goal(self):
        store = await self.start_commission()
        self.receive(self.agent, 2, '停止')
        await self.agent.task
        await self.wait_for(lambda:store.latest()['state']=='cancelled')
        self.complete()
        self.agent._wake_task_results()
        self.assertEqual(1, await self.starts())
        self.assertIsNone(GoalStore(store.path).next_step(self.agent._goal_binding()))

    async def test_restart_wakes_durable_ready_goal_without_chat(self):
        path = Path(self.directory.name) / 'goals.sqlite3'
        binding = json.dumps(self.bridge.binding, sort_keys=True)
        steps = [{'operation':'mine_target','arguments':{'x':x,'y':20}} for x in (10,11)]
        GoalStore(path).create({'steps':steps}, binding, 'previous-chat', 0, '挖除两格', {'mine_target'}, 2)
        store = GoalStore(path)
        self.agent = Agent(self.bridge, Model(), chat_bridge=Authorizer(), goal_store=store,
                           lua_store=ProgramStore(Path(self.directory.name)/'programs'))
        self.complete()
        self.agent._wake_task_results()
        await self.wait_for(lambda:store.latest()['state']=='completed')
        self.assertEqual(2, await self.starts())
        self.assertEqual([], self.agent.llm.requests)
        self.assertIsNone(self.agent._active_turn_id)

    async def test_recovered_intent_without_ack_is_never_replayed(self):
        path = Path(self.directory.name) / 'goals.sqlite3'
        store = GoalStore(path)
        binding = json.dumps(self.bridge.binding, sort_keys=True)
        steps = [{'operation':'mine_target','arguments':{'x':x,'y':20}} for x in (10,11)]
        store.create({'steps':steps}, binding, 'previous-chat', 0, '依次挖除两格', {'mine_target'}, 2)
        self.assertTrue(store.claim(store.next_step(binding), binding))
        # Simulate the host dying exactly between durable admission and native ACK.
        self.agent = Agent(self.bridge, Model(), chat_bridge=Authorizer(), goal_store=GoalStore(path),
                           lua_store=ProgramStore(Path(self.directory.name)/'programs'))
        self.agent._wake_task_results()
        await self.wait_for(lambda:store.latest()['state']=='reconcile')
        self.assertEqual(0, await self.starts())
        self.assertEqual(0, store.latest()['progress'])
        self.assertTrue(store.holds_body())
        self.assertIsNone(GoalStore(store.path).next_step(self.agent._goal_binding()))

    async def test_uncertain_goal_receipt_fences_next_step(self):
        store = await self.start_commission()
        Path(str(self.ready)+'.advance.unreleased').touch()
        self.complete()
        await self.wait_for(lambda:store.latest()['state']=='reconcile')
        self.assertEqual(0, store.latest()['progress'])
        self.assertIsNone(GoalStore(store.path).next_step(self.agent._goal_binding()))
        self.assertEqual(1, await self.starts())

    async def test_new_chat_keeps_task_and_stop_cancels_without_model(self):
        model=Model(program(SUBMIT+"return api('await_task',{taskId=t.taskId,milliseconds=30000})"),final('new'))
        agent=self.make_agent(model);self.receive(agent,1);old=agent.task
        await self.wait_for(lambda:bool(self.manager.records) and all(r['ack'].is_set() for r in self.manager.records.values()))
        self.receive(agent,2,'现在状态如何');await agent.task
        with self.assertRaises(asyncio.CancelledError):await old
        self.assertTrue(self.manager.projection()['active'])
        self.receive(agent,3,'停止');await agent.task
        self.assertEqual(2,len(model.requests))
        view=next(iter(self.manager.records.values()))['view']
        self.assertEqual('cancelled',view['status']);self.assertTrue(view['result']['inputsReleased'])
        self.assertEqual(1,await self.starts())


class M3ResultTests(unittest.TestCase):
    def test_task_contract_requires_capabilities_and_explicit_transport(self):
        from types import SimpleNamespace
        from lua_agent import server_contract
        from server_ai import operations_for_capabilities
        from server_ai_contract import capability_manifest
        caps=['server_ai_atomic_v1','server_ai_actions_v1','server_ai_tasks_v1','server_ai_program_tasks_v1']
        bridge=SimpleNamespace(hello={'capabilities':caps},supported_atomic_operations=operations_for_capabilities(caps),task_request=lambda:None)
        prompt,tools=server_contract(bridge)
        names={op for row in capability_manifest(bridge)['categories'] for op in row['callableApis']}
        self.assertTrue({'submit_task','await_task','resume_task'}<=names)
        self.assertIn('不自动恢复',prompt);self.assertEqual(['execute_lua','read_skill'],[t['function']['name'] for t in tools])
        bridge.hello['capabilities']=caps[:-1]  # Old task support is not M3 safety support.
        self.assertNotIn('submit_task',{op for row in capability_manifest(bridge)['categories'] for op in row['callableApis']})

    def test_preparation_is_not_goal_partial_and_feedback_preserves_multiple_facts(self):
        prepare={'valid':True,'status':'completed','operation':'select_hotbar_slot','actionId':'prepare'}
        use={'valid':True,'status':'completed','operation':'use_item','actionId':'use'}
        failure={'valid':False,'status':'unknown','operation':'mine_target','actionId':'mine','reason':'release_unconfirmed'}
        self.assertEqual('unknown',goal_outcome([prepare],False,'unknown'))
        text=facts_reply([prepare,use,failure],{'status':'failed'},error='model error')
        for fact in ('已核验动作','unknown','释放无法确认','实际效果未确认','Lua 程序未完成','执行事实保留'):
            self.assertIn(fact,text)
        self.assertNotIn('已采集完毕',text)

    def test_host_task_schema_is_strict_and_keeps_native_target_validation(self):
        self.assertEqual(5,len(definitions()))
        for name,args in (('submit_task',{'operation':'use_item','arguments':{}}),
                          ('submit_task',{'operation':'mine_target','arguments':{'x':True,'y':2}}),
                          ('query_task',{'taskId':'bad'}),('await_task',{'taskId':'a'*32,'milliseconds':True})):
            with self.assertRaises(ValueError):validate(name,args)


class M3MailboxTests(unittest.IsolatedAsyncioTestCase):
    async def test_ordinary_monitor_exceptions_set_unknown_keep_identity_and_never_replay(self):
        from types import SimpleNamespace
        for phase in ('start', 'action'):
            with self.subTest(phase=phase):
                requests=[];notices=[];events=[]
                def request(op, task_id, **params):
                    requests.append((op,task_id))
                    if op == phase:
                        raise RuntimeError('fixture unexpected monitor failure')
                    return {'actionId':task_id,'operation':'mine_target','status':'running',
                            'targetTask':{'x':10,'y':20}}
                caps=['server_ai_atomic_v1','server_ai_actions_v1','server_ai_tasks_v1','server_ai_program_tasks_v1']
                bridge=SimpleNamespace(hello={'capabilities':caps},supported_atomic_operations={'mine_target'},task_request=request)
                manager=TaskCoordinator(bridge,on_ready=lambda:notices.append(True))
                with patch('program_tasks.diagnostics.emit',side_effect=lambda kind,**data:events.append((kind,data))):
                    view=await manager.invoke('submit_task',{'operation':'mine_target','arguments':{'x':10,'y':20}})
                    record=manager.records[view['taskId']]
                    await asyncio.wait_for(asyncio.shield(record['monitor']),2)
                self.assertTrue(record['ack'].is_set());self.assertTrue(record['done'].is_set())
                result=record['view']['result']
                self.assertEqual('unknown',result['status']);self.assertEqual(view['taskId'],result['actionId'])
                self.assertFalse(result['retryAllowed']);self.assertTrue(manager.blocked())
                self.assertEqual([True],notices);self.assertEqual(1,manager.sequence)
                event_data=next(data for kind,data in events if kind=='game_task_monitor_failed')
                self.assertEqual('RuntimeError',event_data['error_type']);self.assertEqual(view['taskId'],event_data['taskId'])
                refused=await manager.invoke('submit_task',{'operation':'mine_target','arguments':{'x':10,'y':20}})
                self.assertEqual('not_admitted',refused['dispatchState'])
                self.assertEqual(1,sum(op=='start' for op,_ in requests))
                await manager.close()

    async def test_bounded_batches_keep_unread_results_and_late_errors_do_not_rewrite_terminal(self):
        manager=TaskCoordinator(object())
        for i in range(12):
            key=f'{i:032x}'
            record={'done':asyncio.Event(),'view':{'taskId':key,'operation':'mine_target','origin':None,'status':'running'}}
            manager.records[key]=record
            manager._finish(record,{'valid':True,'completed':True,'status':'completed','operation':'mine_target',
                'actionId':key,'reason':'verified','evidence':{'large':'x'*10000}})
        first=manager.projection()
        self.assertEqual(8,len(first['results']));self.assertEqual(4,first['remainingResultCount'])
        self.assertTrue(any(row['resultSummaryOnly'] for row in first['results']))
        manager.consume(first['throughSequence'])
        self.assertEqual(4,len(manager.projection()['results']))
        record=manager.records[f'{0:032x}'];before=copy.deepcopy(record['view'])
        manager._unknown(record,BridgeError('late error'))
        manager._finish(record,{'status':'unknown'})
        self.assertEqual(before,record['view']);self.assertEqual(12,manager.sequence)


if __name__=='__main__':unittest.main()
