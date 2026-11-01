"""Real Lua workers and simulated Bridge; no Terraria or external model requests."""
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch
import zipfile

sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'python'))
import evidence
import agent_diagnostics as diagnostics
import agent_trace as tracing
from lua_runtime import LuaRuntime, Limits
from lua_agent import ProgramStore
from agent import Agent
from test_lua_runtime import RuntimeGame
from test_agent import Model, tool, final, event


class EvidenceTests(unittest.TestCase):
    def test_bad_optional_observation_metadata_cannot_break_logging(self):
        summary=diagnostics.observation_summary('observe_exploration',{'exploration':7})
        self.assertIn('summaryUnavailable',summary)

    def test_sources_are_pinned_redacted_and_export_is_passive(self):
        with tempfile.TemporaryDirectory() as directory, patch.object(tracing,'trace',tracing.Trace(enabled=False)):
            tracing.trace.add_secret('private-fixture')
            bundle=evidence.Bundle(Path(directory)/'tasks')
            digest=bundle.source('return "private-fixture"')
            bundle.record('sample',value='private-fixture')
            bundle.finish(status='failed')
            manifest=json.loads((bundle.path/'manifest.json').read_text(encoding='utf-8'))
            self.assertTrue(manifest['finished'])
            self.assertTrue(manifest['sources'][0]['redacted'])
            self.assertIn('[REDACTED]',(bundle.path/('source-'+digest+'.lua')).read_text(encoding='utf-8'))
            (bundle.path/'.env').write_text('unrelated-private-file',encoding='utf-8')
            output=evidence.export(bundle.path,Path(directory)/'evidence.zip')
            with zipfile.ZipFile(output) as archive:
                self.assertEqual({'manifest.json','events.jsonl','source-'+digest+'.lua'},set(archive.namelist()))
                self.assertNotIn(b'private-fixture',b''.join(archive.read(n) for n in archive.namelist()))
            with self.assertRaises(FileExistsError):
                evidence.export(bundle.path,output)

    def test_drop_counts_health_and_retention_preserve_active_and_user_files(self):
        with tempfile.TemporaryDirectory() as directory:
            root=Path(directory)
            active=evidence.Bundle(root,maximum=1)
            protected=evidence.Bundle(root,maximum=1)
            (protected.path/'notes.txt').write_text('keep',encoding='utf-8')
            protected.finish(status='failed')
            old=evidence.Bundle(root,maximum=1)
            old.finish(status='completed')
            latest=evidence.Bundle(root,maximum=1,max_bytes=200)
            latest.record('too_big',value='x'*1000)
            latest.finish(status='failed')
            self.assertEqual(1,latest.receipt()['eventsDropped'])
            self.assertTrue(active.path.exists())
            self.assertTrue(protected.path.exists())
            self.assertFalse(old.path.exists())
            self.assertTrue(latest.path.exists())

    def test_default_observation_summaries_include_semantics_not_raw_payloads(self):
        with tempfile.TemporaryDirectory() as directory, patch.object(diagnostics,'log',tracing.Trace(enabled=False)):
            path=diagnostics.configure(Path(directory)/'run.jsonl')
            diagnostics.tool_result('scan_tiles',{},dict(valid=True,worldSessionId='world',gameUpdate=12,
                data=dict(status='partial_unloaded',matchedCount=0,unknownCount=9,truncated=False,items=[],secretPayload='not-loggable')))
            diagnostics.tool_result('resolve_content',{},dict(valid=True,data=dict(status='ambiguous',matchedCount=2,
                candidates=[dict(typeId=2,name='Stone'),dict(typeId=3,name='Stone')],secretPayload='not-loggable')))
            diagnostics.tool_result('observe_exploration',{},dict(valid=True,exploration=dict(status='stale',gameUpdate=13,
                camera=dict(status='stale',ageGameUpdates=20),projectionCoverage=dict(omittedEntities=8),
                terrain=dict(bounds=dict(x=1,y=2,width=3,height=3),unknownCount=9,regions=['not-loggable']))))
            raw=path.read_text(encoding='utf-8')
            self.assertNotIn('not-loggable',raw)
            rows=[json.loads(line)['result']['observation'] for line in raw.splitlines()]
            self.assertEqual(('partial_unloaded',9),(rows[0]['status'],rows[0]['unknownCount']))
            self.assertEqual('ambiguous',rows[1]['status'])
            self.assertEqual(20,rows[2]['camera']['ageGameUpdates'])

    def test_trace_drop_preserves_lua_identity_and_reports_write_failure(self):
        with tempfile.TemporaryDirectory() as directory:
            trace=tracing.Trace(Path(directory)/'trace.jsonl',enabled=True,max_bytes=1024)
            token=tracing.context.set(dict(task_id='task',lua_run_id='run',lua_call_id=4,lua_line=8,source_sha256='sha'))
            try:
                trace.emit('large',payload='x'*5000)
            finally:
                tracing.context.reset(token)
            row=json.loads(trace.path.read_text(encoding='utf-8'))
            self.assertEqual(('run',4,8),(row['lua_run_id'],row['lua_call_id'],row['lua_line']))
            self.assertEqual(1,trace.health()['recordsDropped'])
            bad=tracing.Trace(trace.path/'bad.jsonl',enabled=True)
            bad.emit('sample')
            self.assertEqual(1,bad.health()['writeErrors'])


class LuaObservationTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.directory=tempfile.TemporaryDirectory()
        self.root=Path(self.directory.name)

    async def asyncTearDown(self):
        self.directory.cleanup()

    async def run_lua(self,source,game=None,**options):
        return await LuaRuntime(game or RuntimeGame(),evidence_root=self.root/'tasks').execute(source,**options)

    def rows(self,result):
        return [json.loads(line) for line in (Path(result['evidence']['path'])/'events.jsonl').read_text(encoding='utf-8').splitlines()]

    async def test_api_lines_tail_calls_progress_and_default_evidence(self):
        source="local r=api('inspect_inventory',{mode='selected'})\nlog('observed')\nreturn api('inspect_inventory',{mode='selected'})"
        result=await self.run_lua(source)
        self.assertEqual('completed',result['status'],result)
        self.assertEqual((2,0),(result['calls'],result['actions']))
        self.assertEqual('observed',result['logs'][0]['message'])
        starts=[r for r in self.rows(result) if r['type']=='lua_api_start']
        self.assertEqual([(1,1),(2,3)],[(r['lua_call_id'],r['lua_line']) for r in starts])
        self.assertTrue(all(r['lua_run_id']==result['runId'] for r in starts))
        self.assertTrue(list(Path(result['evidence']['path']).glob('source-*.lua')))

    async def test_bad_argument_has_source_call_and_terminal_event(self):
        game=RuntimeGame()
        result=await self.run_lua("log('check')\nlocal n=1\napi('mine_tile',{x=true,y=2})",game)
        self.assertEqual('bad_argument',result['status'],result)
        self.assertEqual(3,result['failure']['line'])
        self.assertEqual('mine_tile',result['failure']['call']['operation'])
        self.assertFalse(result['failure']['retryAllowed'])
        self.assertEqual(1,len([r for r in self.rows(result) if r['type']=='lua_api_error']))
        self.assertFalse(any(c=='start_atomic' for c,_ in game.sent))

    async def test_script_error_does_not_blame_previously_completed_api(self):
        result=await self.run_lua("api('inspect_inventory',{mode='selected'})\nerror('fixture')")
        self.assertEqual('script_error',result['status'],result)
        self.assertEqual(2,result['failure']['line'])
        self.assertIsNone(result['failure']['call'])

    async def test_action_budget_independent_of_queries_and_unknown_is_recorded(self):
        game=RuntimeGame()
        result=await self.run_lua("api('select_hotbar_slot',{slot=2}); api('inspect_inventory',{mode='selected'}); api('use_item')",
                                  game,limits=Limits(actions=1))
        self.assertEqual(('action_limit',3,1),(result['status'],result['calls'],result['actions']),result)
        self.assertEqual(1,sum(c=='start_atomic' for c,_ in game.sent))
        unknown=await self.run_lua("return api('use_item')",RuntimeGame('unknown'))
        self.assertEqual('action_unknown',unknown['status'])
        row=next(r for r in self.rows(unknown) if r['type']=='lua_api_result')
        self.assertEqual('unknown',row['action']['status'])
        self.assertEqual(1,unknown['failure']['line'])

    async def test_logs_are_bounded_and_do_not_prove_task_success(self):
        result=await self.run_lua("for i=1,33 do log('claimed success') end")
        self.assertEqual('log_limit',result['status'],result)
        self.assertEqual(32,len(result['logs']))
        self.assertEqual(0,result['calls'])
        self.assertIsNone(result['lastAction'])

    async def test_unwritable_artifacts_do_not_change_game_execution(self):
        blocked=self.root/'file'
        blocked.write_text('file',encoding='utf-8')
        result=await LuaRuntime(RuntimeGame(),evidence_root=blocked).execute("return api('inspect_inventory',{mode='selected'})")
        self.assertEqual('completed',result['status'])
        self.assertFalse(result['evidence']['available'])
        self.assertGreater(result['evidence']['writeErrors'],0)

    async def test_agent_shares_action_budget_across_direct_and_lua(self):
        source="api('select_hotbar_slot',{slot=3}); api('use_item')"
        model=Model(tool('select_hotbar_slot','{"slot":2}'),tool('execute_lua',json.dumps(dict(source=source))),final('stopped'))
        game=RuntimeGame()
        agent=Agent(game,model,lua_limits=Limits(actions=2),lua_store=ProgramStore(self.root/'sources'))
        agent.receive(event(1))
        await agent.task
        self.assertEqual(2,sum(c=='start_atomic' for c,_ in game.sent))
        result=json.loads(model.requests[-1][-2]['content'])
        self.assertEqual('action_limit',result['status'],result)
        self.assertEqual(1,result['actions'])
        self.assertEqual(0,json.loads(model.requests[-1][-1]['content'])['runtimeLua']['remainingActionAttempts'])
        manifest=json.loads((Path(result['evidence']['path'])/'manifest.json').read_text(encoding='utf-8'))
        self.assertEqual(2,manifest['actionAttempts'])

    async def test_agent_internal_api_budget_is_shared_across_programs(self):
        def program(source):
            return tool('execute_lua',json.dumps(dict(source=source)))
        model=Model(program("api('inspect_inventory',{mode='selected'}); api('inspect_inventory',{mode='selected'})"),
                    program("api('inspect_inventory',{mode='selected'}); api('inspect_inventory',{mode='selected'})"),final('limit'))
        game=RuntimeGame()
        agent=Agent(game,model,lua_limits=Limits(calls=3),lua_store=ProgramStore(self.root/'sources'))
        agent.receive(event(1));await agent.task
        result=json.loads(model.requests[-1][-2]['content'])
        self.assertEqual(('call_limit',1),(result['status'],result['calls']))
        self.assertEqual(0,json.loads(model.requests[-1][-1]['content'])['runtimeLua']['remainingApiCallsIfExecutedNow'])

    async def test_stop_is_not_charged_as_world_mutation(self):
        result=await self.run_lua("api('select_hotbar_slot',{slot=2}); return api('stop_action')",limits=Limits(actions=1))
        self.assertEqual(('completed',2,1),(result['status'],result['calls'],result['actions']),result)


if __name__=='__main__':
    unittest.main()
