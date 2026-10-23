"""Real Lua -> dispatcher -> Bridge TCP -> actual C# sources, synthetic physics.

This is a protocol integration test, not a Terraria game acceptance test.
"""
import asyncio
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest
import json

import httpx

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'python'))
from control import Bridge
from lua_runtime import LuaRuntime
from lua_agent import ProgramStore
from agent import Agent
from llm import LLMClient
from test_agent import event, tool, final


@unittest.skipUnless(shutil.which('pwsh'), 'PowerShell 7 needed for compiled C# TCP integration')
class LuaProtocolTests(unittest.IsolatedAsyncioTestCase):
    async def test_model_http_agent_lua_to_actual_csharp_action(self):
        repo = Path(__file__).resolve().parents[1]
        process = await asyncio.create_subprocess_exec(
            shutil.which('pwsh'), '-NoProfile', '-File', str(repo / 'tests/run_csharp_tests.ps1'),
            '-LandingProtocolHost', stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.PIPE,
            creationflags=subprocess.CREATE_NO_WINDOW if os.name == 'nt' else 0)
        requests = []
        source = '''
            local r=api('navigate_to_landing_region',{
                minPlayerLeftX=1664,maxPlayerLeftX=1744,surfaceY=1552})
            if r.valid and r.completed then
                return {action=r,stop=api('stop_action')}
            end
            return {action=r}
        '''
        def handler(request):
            body = json.loads(request.content)
            requests.append(body)
            answer = tool('execute_lua', json.dumps({'source': source})) if len(requests) == 1 else final('模拟导航到达')
            return httpx.Response(200, json={'choices': [{'message': answer,
                'finish_reason': 'tool_calls' if len(requests) == 1 else 'stop'}]})
        client = LLMClient('https://model.test/v1', 'fixture', 'fixture', transport=httpx.MockTransport(handler))
        bridge = agent = None
        try:
            line = await asyncio.wait_for(process.stdout.readline(), 30)
            self.assertTrue(line.startswith(b'READY '), line)
            bridge = await asyncio.to_thread(Bridge, int(line.split()[1]), 2)
            with tempfile.TemporaryDirectory() as directory:
                agent = Agent(bridge, client, lua_store=ProgramStore(directory))
                agent.receive(event(1, eventId='landing-e2e'))
                await asyncio.wait_for(agent.task, 20)
            self.assertEqual(2, len(requests))
            result = json.loads(requests[1]['messages'][-2]['content'])
            self.assertTrue(result['programCompleted'], result)
            self.assertTrue(result['actionSuccess'], result)
            action = result['value']['action']
            self.assertEqual('arrived', action['evidence']['status'])
            self.assertGreater(action['evidence']['completedJumps'], 0)
            self.assertEqual('known', action['endEvidenceStatus'])
            self.assertEqual('released', result['actionState'])
        finally:
            if agent:
                await agent.close()
            if bridge:
                bridge.close()
            await client.close()
            try:
                await asyncio.wait_for(process.wait(), 3)
            except asyncio.TimeoutError:
                process.terminate()
                await process.wait()
            await process.stdout.read()
            errors = await process.stderr.read()
            self.assertEqual(b'', errors)

    async def test_lua_to_csharp_navigation_and_release_evidence(self):
        repo = Path(__file__).resolve().parents[1]
        process = await asyncio.create_subprocess_exec(
            shutil.which('pwsh'), '-NoProfile', '-File', str(repo / 'tests/run_csharp_tests.ps1'),
            '-LandingProtocolHost', stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.PIPE,
            creationflags=subprocess.CREATE_NO_WINDOW if os.name == 'nt' else 0)
        bridge = None
        try:
            line = await asyncio.wait_for(process.stdout.readline(), 30)
            self.assertTrue(line.startswith(b'READY '), line)
            bridge = await asyncio.to_thread(Bridge, int(line.split()[1]), 2)
            runtime = LuaRuntime(bridge)
            result = await runtime.execute('''
                local r = api('navigate_to_landing_region', {
                    minPlayerLeftX=1664,maxPlayerLeftX=1744,surfaceY=1552})
                assert(r.valid and r.completed)
                assert(r.endEvidenceStatus == 'known')
                if r.evidence.actualLanding.x >= 1664 then
                    return {action=r, stop=api('stop_action')}
                end
                error('wrong landing')
            ''', chat_id='landing-e2e')
            self.assertEqual('completed', result['status'], result)
            self.assertEqual('released', result['actionState'])
            action = result['value']['action']
            self.assertEqual('arrived', action['evidence']['status'])
            self.assertGreater(action['evidence']['completedJumps'], 0)
            self.assertEqual('PostUpdateRunSpeeds_before_bridge_input', action['endEvidence']['phase'])
            self.assertTrue(result['value']['stop']['valid'])
            self.assertEqual(2, result['calls'])
        finally:
            if bridge:
                bridge.close()
            try:
                await asyncio.wait_for(process.wait(), 3)
            except asyncio.TimeoutError:
                process.terminate()
                await process.wait()
            if process.stdout:
                await process.stdout.read()
            if process.stderr:
                errors = await process.stderr.read()
                self.assertEqual(b'', errors)


if __name__ == '__main__':
    unittest.main()
