"""Two-dimensional tool and real Python Agent -> C# TCP -> planner integration."""
import asyncio
import copy
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'python'))
import atomic_operations as atomic
from agent import Agent
from control import Bridge, BridgeError
from test_agent import Model, event, tool, final
from test_atomic_operations import Game

GOAL = {'minPlayerLeftX': 1664, 'maxPlayerLeftX': 1744, 'surfaceY': 1552}
NAME = 'navigate_to_landing_region'


class LandingGame(Game):
    def __init__(self, pending=False):
        super().__init__(pending=pending)
        self.hello['capabilities'].append('landing_region_navigation_v1')
        self.target = None

    def command(self, cmd, **params):
        if cmd == 'start_atomic':
            self.target = copy.deepcopy(params['arguments'])
            super().command(cmd, **params)
            self.action.update(targetRegion=self.target, session=1, generation=self.generation)
            return self.reply('movement_started')
        return super().command(cmd, **params)


class LandingTests(unittest.IsolatedAsyncioTestCase):
    def test_invalid_parameters(self):
        for args in ({**GOAL, 'minPlayerLeftX': 1800}, {**GOAL, 'surfaceY': float('inf')},
                     {**GOAL, 'surfaceY': True}, {**GOAL, 'surfaceY': -1}, {**GOAL, 'targetX': 1},
                     {'minPlayerLeftX': 1, 'maxPlayerLeftX': 10}):
            with self.subTest(args=args), self.assertRaises(ValueError): atomic.validate(NAME, args)
        atomic.validate(NAME, GOAL)
        atomic.validate(NAME, {**GOAL, 'maxPlayerLeftX': GOAL['minPlayerLeftX']})

    async def test_capability_blocks_old_mod(self):
        game = Game()
        with self.assertRaisesRegex(BridgeError, 'landing_region_navigation_v1'):
            await atomic.dispatch(game, NAME, GOAL)
        self.assertEqual([], game.sent)

    async def test_region_sent_and_echoed(self):
        game = LandingGame()
        result = await atomic.dispatch(game, NAME, GOAL)
        self.assertEqual('completed', result['status'])
        self.assertEqual(GOAL, result['targetRegion'])
        self.assertEqual(GOAL, next(p for c, p in game.sent if c == 'start_atomic')['arguments'])

    async def test_renewal_uses_same_region_and_generation(self):
        game = LandingGame(pending=True)
        task = asyncio.create_task(atomic.dispatch(game, NAME, GOAL))
        try:
            for _ in range(100):
                if any(c == 'renew_move' for c, _ in game.sent): break
                await asyncio.sleep(.01)
            renewal = next(p for c, p in game.sent if c == 'renew_move')
            self.assertEqual(GOAL, renewal['targetRegion'])
            self.assertEqual(game.action['generation'], renewal['expectedGeneration'])
            self.assertEqual(game.action['actionId'], renewal['actionId'])
        finally:
            task.cancel()
            with self.assertRaises(asyncio.CancelledError): await task
        self.assertEqual('cancelled', game.action['status'])

    async def test_changed_target_never_reported_as_completed(self):
        game = LandingGame()
        original = game.command
        def changed(cmd, **params):
            reply = original(cmd, **params)
            if cmd == 'get_movement' and reply['movement']['action'] is not None:
                reply['movement']['action']['targetRegion'] = {**GOAL, 'surfaceY': 1600}
            return reply
        game.command = changed
        result = await atomic.dispatch(game, NAME, GOAL)
        self.assertFalse(result['completed'])
        self.assertEqual('unknown', result['status'])


@unittest.skipUnless(shutil.which('pwsh'), 'PowerShell 7 needed for compiled C# TCP integration')
class LandingProtocolTests(unittest.IsolatedAsyncioTestCase):
    async def test_agent_to_real_csharp_protocol_planner_and_landing(self):
        repo = Path(__file__).resolve().parents[1]
        process = await asyncio.create_subprocess_exec(
            shutil.which('pwsh'), '-NoProfile', '-File', str(repo / 'tests/run_csharp_tests.ps1'), '-LandingProtocolHost',
            stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.PIPE,
            creationflags=subprocess.CREATE_NO_WINDOW if os.name == 'nt' else 0)
        bridge = agent = None
        try:
            line = await asyncio.wait_for(process.stdout.readline(), 20)
            if not line.startswith(b'READY '):
                stderr = await asyncio.wait_for(process.stderr.read(), 5)
                self.fail(f'C# host did not become ready: {line!r} {stderr.decode(errors="replace")}')
            port = int(line.split()[1])
            bridge = await asyncio.to_thread(Bridge, port, 2)
            self.assertIn('landing_region_navigation_v1', bridge.hello['capabilities'])
            model = Model(tool(NAME, json.dumps(GOAL)), final('landing verified'))
            agent = Agent(bridge, model)
            agent.receive(event(1, eventId='landing-e2e'))
            await asyncio.wait_for(agent.task, 15)
            tools = [json.loads(m['content']) for m in model.requests[-1] if m.get('role') == 'tool']
            self.assertEqual(1, len(tools))
            result = tools[0]
            self.assertEqual('completed', result['status'], result)
            self.assertTrue(result['completed'])
            self.assertEqual(GOAL, result['targetRegion'])
            self.assertEqual(GOAL, result['evidence']['targetRegion'])
            self.assertEqual('arrived', result['evidence']['status'])
            self.assertGreater(result['evidence']['completedJumps'], 0)
            self.assertGreater(result['evidence']['planNumber'], 0)
            landing = result['evidence']['actualLanding']
            self.assertTrue(GOAL['minPlayerLeftX'] <= landing['x'] <= GOAL['maxPlayerLeftX'])
            self.assertAlmostEqual(GOAL['surfaceY'], landing['y'] + 42, delta=.5)
            self.assertTrue(result['evidence']['lastLanding']['settled'])
            self.assertEqual('PostUpdateRunSpeeds_before_bridge_input', result['endEvidence']['phase'])
        finally:
            if agent: await agent.close()
            if bridge: bridge.close()
            try: await asyncio.wait_for(process.wait(), 3)
            except asyncio.TimeoutError:
                process.terminate()
                await process.wait()
            if process.stdout: await process.stdout.read()
            if process.stderr: await process.stderr.read()
