import asyncio
import json
import sys
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import AsyncMock, Mock, patch

sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'python'))
from autonomous_mode import AutonomousMode
from llm import LLMError


class LifecycleTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        self.agent = SimpleNamespace(task=None,_goal_dispatch=None,goals=None,program_tasks=None,
            bridge=SimpleNamespace(binding=dict(generation=3)),_waiting_respawn=False,_brain_paused=False,
            _closing=False,max_model_calls=3,llm=SimpleNamespace(complete=AsyncMock()),receive=Mock())
        self.mode=AutonomousMode(self.agent,None)
        self.life=dict(ready=True,generation=3)
        async def control(command):
            if command in ('on','off'):
                self.mode.enabled = command=='on'
            return dict(enabled=self.mode.enabled,released=self.mode.released,version='v3' if command=='grant' else None)
        self.mode.control_request=AsyncMock(side_effect=control)
        self.mode.tools=Mock(return_value=[{'function':{'name':'navigate_to'}}])
        self.scene=dict(localScene={'actor':{'mapTile':{'x':1,'y':2}}},player={'life':60},
                        resources={'items':[],'gameUpdate':1},activeTeamBosses=[],nearbyBosses={},
                        binding=self.agent.bridge.binding)
        self.mode.observe=AsyncMock(return_value=self.scene)

    async def test_on_act_reobserve_and_rest(self):
        self.agent.llm.complete.side_effect=[
            dict(tool_calls=[dict(id='a',function=dict(name='navigate_to',arguments=json.dumps(dict(
                objective='visit opening',x=90,y=20))))]),dict(content='rest')]
        self.mode.act=AsyncMock(return_value=dict(status='completed',inputsReleased=True))
        await self.mode.command('on')
        for _ in range(3):
            self.mode.next_check=0
            await self.mode.pulse(self.life)
            await self.mode.worker
        self.assertEqual(self.agent.llm.complete.await_count,2)
        self.assertEqual(self.mode.observe.await_count,3)
        self.assertEqual(self.mode.progress['status'],'completed')
        self.agent.receive.assert_not_called()

    async def test_manual_waits_for_release_then_for_manual_completion(self):
        await self.mode.command('on')
        self.mode.released=False
        await self.mode.manual({'eventId':'manual'})
        await self.mode.pulse(self.life)
        self.agent.receive.assert_not_called()
        self.mode.released=True
        await self.mode.pulse(self.life)
        self.agent.receive.assert_called_once_with({'eventId':'manual'})
        self.agent.task=asyncio.create_task(asyncio.sleep(0))
        await self.mode.pulse(self.life)
        self.assertIsNone(self.mode.worker)
        await self.agent.task
        self.agent.llm.complete.return_value=dict(content='rest')
        await self.mode.pulse(self.life)
        await self.mode.worker
        self.mode.observe.assert_awaited_once()

    async def test_off_discards_late_model_response(self):
        started=asyncio.Event()
        async def late(*args):
            started.set()
            try:
                await asyncio.Event().wait()
            except asyncio.CancelledError:
                return dict(tool_calls=[dict(function=dict(name='navigate_to',arguments='{}'))])
        self.agent.llm.complete.side_effect=late
        self.mode.act=AsyncMock()
        await self.mode.command('on')
        await self.mode.pulse(self.life)
        await started.wait()
        await self.mode.command('off')
        self.mode.act.assert_not_awaited()

    async def test_respawn_team_boss_preempts_rest_and_uses_new_decision(self):
        mode=self.mode
        await mode.command('on')
        mode.progress=dict(actionId='old-boss',operation='boss_encounter',status='cancelled')
        mode.next_check=float('inf')
        mode.force_decision=False
        fresh=dict(entityRef='fresh-root',teammateRef='online-ally')
        self.scene['activeTeamBosses']=[fresh]
        mode.tools.return_value=[dict(function=dict(name='submit_task'))]
        call=dict(objective='assist my teammate',operation='boss_encounter',arguments=dict(entityRef='fresh-root'))
        self.agent.llm.complete.return_value=dict(tool_calls=[dict(function=dict(name='submit_task',arguments=json.dumps(call)))])
        mode.act=AsyncMock(return_value=dict(status='completed',inputsReleased=True,actionId='new-boss'))
        await mode.pulse(dict(self.life,teamBosses=[fresh]))
        await mode.worker
        observed=json.loads(self.agent.llm.complete.await_args.args[0][1]['content'])
        self.assertEqual(observed['activeTeamBosses'],[fresh])
        mode.act.assert_awaited_once_with('submit_task',dict(operation='boss_encounter',arguments=dict(entityRef='fresh-root')),'v3')
        self.assertEqual(mode.progress['actionId'],'new-boss')

    async def test_batched_reads_cannot_start_batched_actions(self):
        await self.mode.command('on')
        def call(name):
            return dict(id=name,function=dict(name=name,arguments='{}'))
        self.mode.tools.return_value=[dict(function=dict(name=n)) for n in ('get_player_state','navigate_to')]
        self.agent.llm.complete.side_effect=[dict(role='assistant',tool_calls=[call('get_player_state'),call('navigate_to')]),
                                             dict(content='rest')]
        self.mode.act=AsyncMock()
        with patch('autonomous_mode.atomic.dispatch',new_callable=AsyncMock,return_value={'valid':True}) as dispatch:
            await self.mode.pulse(self.life)
            await self.mode.worker
            dispatch.assert_awaited_once_with(self.agent.bridge,'get_player_state',{})
        self.mode.act.assert_not_awaited()
        self.assertEqual(len(self.agent.llm.complete.await_args.args[0]),5)

    def navigate(self, x):
        return dict(tool_calls=[dict(function=dict(name='navigate_to',arguments=json.dumps(
            dict(objective='explore',x=x,y=20))))])

    async def cycle(self):
        self.mode.next_check=0
        await self.mode.pulse(self.life)
        await self.mode.worker

    async def test_transient_model_failure_only_retries_decision(self):
        self.agent.llm.complete.side_effect=[LLMError('timeout',retryable=True),self.navigate(12)]
        self.mode.act=AsyncMock(return_value=dict(status='completed',completed=True,inputsReleased=True))
        await self.mode.command('on')
        with patch('autonomous_mode.MODEL_BACKOFF',(0,0)):
            await self.cycle()
        self.assertEqual(self.agent.llm.complete.await_count,2)
        self.mode.act.assert_awaited_once()

    async def test_exhausted_model_retries_keep_status_until_fresh_trigger(self):
        self.agent.llm.complete.side_effect=LLMError('provider unavailable',retryable=True)
        await self.mode.command('on')
        with patch('autonomous_mode.MODEL_BACKOFF',(0,0)):
            await self.cycle()
            await self.cycle()
        self.assertEqual(self.agent.llm.complete.await_count,3)
        self.assertIn('model_blocked',self.mode.status())
        self.assertIn('provider unavailable',self.mode.status())
        self.agent.llm.complete.side_effect=None
        self.agent.llm.complete.return_value=dict(content='rest')
        await self.mode.command('on')
        await self.cycle()
        self.assertIsNone(self.mode.error)

    async def test_successful_repeated_action_with_progress(self):
        self.agent.llm.complete.return_value=self.navigate(12)
        self.mode.act=AsyncMock(return_value=dict(status='completed',completed=True,inputsReleased=True))
        await self.mode.command('on')
        for x in range(5):
            self.scene['player']['position']=dict(x=x*16,y=10)
            await self.cycle()
        self.assertEqual(self.mode.act.await_count,5)

    async def test_unreachable_alternating_targets_stop(self):
        self.agent.llm.complete.side_effect=[self.navigate(x) for x in [12,22,12,22,12]]
        self.mode.act=AsyncMock(return_value=dict(status='failed',reason='unreachable',inputsReleased=True))
        await self.mode.command('on')
        for _ in range(7):
            await self.cycle()
        self.assertEqual(self.mode.act.await_count,4)
        self.assertEqual(self.mode.phase,'no_progress')
        self.assertIn('unreachable',self.mode.status())

    async def test_uncertain_or_cancelled_receipt_stops_scheduling(self):
        self.agent.llm.complete.return_value=self.navigate(12)
        await self.mode.command('on')
        for status,released in [('failed',False),('unknown',True),('cancelled',True)]:
            self.mode.hold=False
            self.mode.last_attempt=None
            self.mode.stalled.clear()
            self.mode.force_decision=True
            self.mode.act=AsyncMock(return_value=dict(status=status,inputsReleased=released))
            await self.cycle()
            await self.cycle()
            self.mode.act.assert_awaited_once()
            self.assertTrue(self.mode.hold)

    async def test_incomplete_baseline_waits_for_coverage_and_resumes(self):
        await self.mode.command('on')
        self.scene['resources']['complete']=False
        self.mode.tools.return_value=[dict(function=dict(name='explore'))]
        call=dict(tool_calls=[dict(function=dict(name='explore',arguments=json.dumps(dict(direction='right',objective='survey'))))])
        self.agent.llm.complete.side_effect=[call,dict(content='reassess')]
        result=dict(returnReason='observation_incomplete',findings=[],baseline=dict(complete=False))
        self.mode.act=AsyncMock(return_value=dict(status='failed',inputsReleased=True,explorationResult=result))
        for _ in range(4):
            await self.cycle()
        self.assertEqual(self.mode.phase,'observation_incomplete')
        self.assertFalse(self.mode.stalled)
        self.mode.act.assert_awaited_once()
        self.scene['resources']['complete']=True
        await self.cycle()
        self.assertEqual(self.agent.llm.complete.await_count,2)

    async def test_discovery_returns_to_main_model_and_survives_timeout_and_detour(self):
        await self.mode.command('on')
        self.mode.tools.return_value=[dict(function=dict(name=n)) for n in ('explore','navigate_to','mine_target')]
        calls = [('explore',dict(direction='right')), ('navigate_to',dict(x=50,y=32)),
                 ('mine_target',dict(x=10,y=2)), ('explore',dict(direction='left'))]
        replies = [dict(tool_calls=[dict(function=dict(name=n, arguments=json.dumps(dict(a,objective=n))))])
                   for n,a in calls]
        event = dict(items=[dict(anchor=dict(x=10,y=2))],gameUpdate=4)
        result = dict(direction='right',returnReason='discovery',findings=[event],progress=dict(completedSegments=1))
        self.mode.act=AsyncMock(side_effect=[dict(status='completed',completed=True,inputsReleased=True,
            explorationResult=result)] + [dict(status='completed',completed=True,inputsReleased=True)]*3)
        self.agent.llm.complete.side_effect=[replies[0],LLMError('timeout',retryable=True),*replies[1:]]
        with patch('autonomous_mode.MODEL_BACKOFF',(0,0)):
            for _ in range(4):
                await self.cycle()
        payloads=[json.loads(call.args[0][1]['content']) for call in self.agent.llm.complete.await_args_list]
        self.assertEqual(payloads[1],payloads[2])
        for payload in payloads[1:]:
            self.assertEqual(payload['explorationContext']['direction'],'right')
            self.assertEqual(payload['explorationContext']['investigating'],[event])
        self.assertEqual(payloads[-1]['explorationContext']['lastInvestigation']['operation'],'mine_target')
        self.assertEqual(len(self.mode.stalled),2)  # only the two mocked, effectless detour actions
        self.assertEqual(self.mode.act.await_count,4)
        for call in self.mode.act.await_args_list:
            if call.args[0]=='explore':
                self.assertIs(call.kwargs['baseline'], self.scene['resources'])

    async def test_action_transport_error_holds_without_model_or_body_replay(self):
        self.agent.llm.complete.return_value=self.navigate(12)
        self.mode.act=AsyncMock(side_effect=OSError('lost acknowledgement'))
        await self.mode.command('on')
        await self.cycle()
        await self.cycle()
        self.agent.llm.complete.assert_awaited_once()
        self.mode.act.assert_awaited_once()
        self.assertIn('lost acknowledgement',self.mode.status())
        self.assertTrue(self.mode.hold)
