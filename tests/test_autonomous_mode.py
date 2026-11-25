import asyncio
import json
import sys
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import AsyncMock, Mock

sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'python'))
from autonomous_mode import AutonomousMode


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
