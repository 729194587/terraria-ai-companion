"""HTTP model substitute + two loopback bridges + real Agent/Lua; no external inference/game."""
import json
import os
from pathlib import Path
import shutil
import sys
import tempfile
import unittest
from unittest.mock import patch
sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'python'))
import agent_trace as tracing
import evidence
import atomic_operations as atomic
import test_server_ai_agent as fixtures

class FailureTraceTests(unittest.IsolatedAsyncioTestCase):
    asyncSetUp=fixtures.ServerAiAgentTests.asyncSetUp
    asyncTearDown=fixtures.ServerAiAgentTests.asyncTearDown
    program=fixtures.ServerAiAgentTests.program
    receive=fixtures.ServerAiAgentTests.receive
    host_type=fixtures.AiHost

    async def test_connection_loss_is_logged_without_replay(self):
        self.aihost.mode='drop_start'
        trace=tracing.Trace(Path(self.directory.name)/'lost.jsonl',enabled=True)
        with patch.object(tracing,'trace',trace):
            result=await atomic.dispatch(self.ai,'select_hotbar_slot',{'slot':2})
        self.assertEqual('unknown',result['status'])
        rows=[json.loads(line) for line in trace.path.read_text(encoding='utf-8').splitlines()]
        request=next(r for r in rows if r['type']=='server_ai_ipc_request' and r['request']['op']=='start')
        error=next(r for r in rows if r['type']=='server_ai_ipc_error')
        self.assertEqual(request['request']['id'],error['request']['id'])
        self.assertEqual(request['connection_id'],error['connection_id'])
        self.assertEqual('connection',error['category'])
        self.assertEqual(1,sum(r['op']=='start' for r in self.aihost.sent))

    async def test_failure_decision_to_terminal_and_readonly_followup(self):
        detail='search time budget reached; solid wall (12 candidates)'
        def fail(action):
            action.update(status='failed',reason='navigation_search_timeout',reasonDetail=detail)
            action['endEvidence']['effectVerified']=False
            action['evidence'].update(arrived=False,navigation=dict(session=41,generation=1,planNumber=1,
                planStart=dict(px=100,py=200,vx=0,vy=0,grounded=True),targetX=200,
                reachedGoal=False,timeBudgetReached=True,searchFailureReason=detail))
        self.aihost.action_transform=fail
        first="local state=api('get_player_state',{})\nreturn api('navigate_to_x',{targetX=200})"
        self.answers=[self.program(first),self.program("return api('get_player_state',{})"),
                      dict(role='assistant',content='搜索超时，未确认到达。')]
        trace=tracing.Trace(Path(self.directory.name)/'trace.jsonl',enabled=True)
        trace.add_secret('offline-test-key')
        with patch.object(tracing,'trace',trace):
            await self.receive(text='前进并报告失败 offline-test-key')
            await self.agent.task
        root=Path(self.directory.name)/'evidence'
        bundles=list(root.iterdir());self.assertEqual(1,len(bundles))
        bundle=bundles[0]
        rows=[json.loads(line) for line in (bundle/'events.jsonl').read_text(encoding='utf-8').splitlines()]
        requests=[r for r in rows if r['type']=='llm_request']
        self.assertEqual(3,len(requests))
        for saved,actual in zip(requests,self.requests):
            self.assertEqual(trace._redact(actual),saved['body'])
        responses=[r for r in rows if r['type']=='llm_return']
        self.assertEqual(3,len(responses))
        self.assertEqual(3,len({r['model_request_id'] for r in requests}))
        self.assertTrue(all(r['task_id']==bundle.name for r in requests+responses))
        self.assertIn('navigation_search_timeout',json.dumps(requests[1]['body']))
        self.assertIn(detail,json.dumps(requests[1]['body']))
        ipc=[r for r in rows if r['type']=='server_ai_ipc_request']
        starts=[r for r in ipc if r['request']['op']=='start']
        self.assertEqual(1,len(starts))
        start=starts[0];self.assertEqual(2,start['lua_call_id'])
        self.assertTrue(start['lua_run_id'] and start['tool_call_id'] and start['source_sha256'])
        replies=[r for r in rows if r['type']=='server_ai_ipc_reply']
        self.assertTrue(any(r['request_id']==start['request']['id'] and r['connection_id']==start['connection_id'] for r in replies))
        terminal=[r for r in replies if (r['reply'].get('data') or {}).get('action',{}).get('reason')=='navigation_search_timeout']
        self.assertTrue(terminal)
        self.assertEqual(start['request']['actionId'],terminal[-1]['reply']['data']['action']['actionId'])
        followups=[r for r in rows if r['type']=='lua_api_start' and r['lua_run_id']!=start['lua_run_id']]
        self.assertTrue(followups);self.assertTrue(all(r['operation']=='get_player_state' for r in followups))
        self.assertTrue(any(r['type']=='send_chat_result' for r in rows))
        self.assertIn('navigation_search_timeout',self.aihost.published[-1]['text'])
        self.assertNotEqual(responses[-1]['assistant']['content'],self.aihost.published[-1]['text'])
        self.assertNotIn('offline-test-key',(bundle/'events.jsonl').read_text(encoding='utf-8'))
        target=os.environ.get('TRACE_FAILURE_EXAMPLE')
        if target:
            out=Path(target);out.mkdir(parents=True,exist_ok=True)
            shutil.copytree(bundle,out/bundle.name)
            shutil.copyfile(trace.path,out/'agent-trace.jsonl')
            evidence.export(bundle,out/'task-evidence.zip')
            (out/'fixture.json').write_text(json.dumps(dict(taskId=bundle.name,
                scope='HTTP and game/IPC substitutes; real Agent, Lua worker and dispatcher; navigation sample is synthetic',
                navigationActionId=start['request']['actionId'],luaRunId=start['lua_run_id']),indent=2),encoding='utf-8')

class FullPayloadPolicyTests(unittest.TestCase):
    def test_oversized_payload_keeps_join_ids_and_redaction(self):
        with tempfile.TemporaryDirectory() as directory:
            trace=tracing.Trace(Path(directory)/'trace.jsonl',enabled=True,max_bytes=1000)
            trace.add_secret('private-fixture')
            bundle=evidence.Bundle(Path(directory)/'tasks')
            bt=evidence.current.set(bundle)
            ct=tracing.context.set(dict(task_id=bundle.id,model_request_id='model-7',connection_id='conn-2',request_id='3'))
            try:
                with patch.object(tracing,'trace',trace):
                    tracing.emit_full('llm_request',body='private-fixture'+'x'*70000)
            finally:
                evidence.current.reset(bt);tracing.context.reset(ct)
            for path in (trace.path,bundle.path/'events.jsonl'):
                raw=path.read_text(encoding='utf-8');self.assertNotIn('private-fixture',raw)
                row=json.loads(raw)
                self.assertEqual(('model-7','conn-2','3'),(row['model_request_id'],row['connection_id'],row['request_id']))
            self.assertEqual((1,1),(trace.dropped_records,bundle.dropped))

    def test_opt_in_and_diagnostic_failure_do_not_affect_execution(self):
        with patch.object(tracing,'trace',tracing.Trace(enabled=False)),patch.object(evidence,'record') as record:
            tracing.emit_full('llm_request',body={'private':'not-recorded'})
            record.assert_not_called()
        trace=tracing.Trace(enabled=True)
        with patch.object(tracing,'trace',trace),patch.object(trace,'emit'),patch.object(evidence,'record',side_effect=OSError('fixture')):
            tracing.emit_full('llm_request',body={})
        self.assertEqual(1,trace.write_errors)

if __name__=='__main__':unittest.main()
