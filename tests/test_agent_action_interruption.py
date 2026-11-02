"""Direct action handoffs through real Agent/dispatch cancellation supervision."""
import asyncio
import io
import json
import multiprocessing as mp
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'python'))
import atomic_operations as atomic
import movement
from agent import Agent
from control import BridgeError
from lua_agent import ProgramStore
from test_agent import Model, event, final, tool
from test_lua_agent import program
from test_lua_runtime import RuntimeGame
from test_movement import MovementBridge


class AtomicGame(RuntimeGame):
    def __init__(self, cleanup='known'):
        super().__init__()
        self.cleanup_mode = cleanup

    def command(self, cmd, **params):
        if cmd == 'start_atomic':
            self.pending = params['operation'] == 'use_item'
        if cmd == 'cancel_move' and self.cleanup_mode == 'error':
            raise BridgeError('fixture cleanup disconnected')
        reply = super().command(cmd, **params)
        if cmd == 'cancel_move' and self.cleanup_mode == 'missing':
            reply['movement']['action'].pop('endEvidence', None)
            reply['movement']['action']['endEvidenceStatus'] = 'unknown'
        return reply


class MovingGame(MovementBridge):
    def __init__(self, cleanup='known'):
        super().__init__(complete_after=1000)
        self.cleanup_mode = cleanup

    def command(self, cmd, **params):
        if cmd == 'cancel_move' and self.cleanup_mode == 'error':
            raise BridgeError('fixture cleanup disconnected')
        reply = super().command(cmd, **params)
        if cmd == 'cancel_move' and self.cleanup_mode == 'missing':
            reply['movement']['action'].pop('endEvidence', None)
            reply['movement']['action']['endEvidenceStatus'] = 'unknown'
        return reply


class DirectInterruptionTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.store = ProgramStore(Path(self.directory.name)/'programs')
        self.stderr = patch('sys.stderr', new_callable=io.StringIO); self.stderr.start()

    async def asyncTearDown(self):
        self.stderr.stop(); self.directory.cleanup()
        self.assertFalse(mp.active_children())

    async def scenario(self, *, moving=False, timing='running', cleanup='known',
                       prior=None, stop=False, lost_ack=False, finish_before_cleanup=False):
        game = MovingGame(cleanup) if moving else AtomicGame(cleanup)
        module = movement if moving else atomic
        command_name = '_command' if moving else 'command'
        original = getattr(module, command_name)
        ack_entered, ack_release = asyncio.Event(), asyncio.Event()
        polled, cleanup_entered, cleanup_release = asyncio.Event(), asyncio.Event(), asyncio.Event()
        start_command = 'move_for' if moving else 'start_atomic'

        async def gated(bridge, cmd, **params):
            if cmd == 'cancel_move':
                cleanup_entered.set()
                await cleanup_release.wait()
            reply = await original(bridge, cmd, **params)
            target = moving or (game.action or {}).get('operation') == 'use_item'
            if cmd == start_command and target:
                ack_entered.set()
                if timing == 'ack':
                    await ack_release.wait()
                if lost_ack:
                    raise BridgeError('fixture lost start ACK')
            if cmd == 'get_movement' and target and (game.action or {}).get('status') == 'running':
                polled.set()
            return reply

        answers = []
        if prior == 'direct':
            answers.append(tool('select_hotbar_slot', '{"slot":2}'))
        elif prior == 'lua':
            answers.append(program("return api('select_hotbar_slot',{slot=2})"))
        if moving:
            answers.extend([tool('get_observation'), tool('move', '{"direction":"right","duration_ms":2000}')])
        else:
            answers.append(tool('use_item'))
        answers.append(final('已读取交接事实。'))
        model = Model(*answers)
        agent = Agent(game, model, lua_store=self.store)
        try:
            with patch.object(module, command_name, gated):
                agent.receive(event(1)); old = agent.task
                await asyncio.wait_for((ack_entered if timing == 'ack' else polled).wait(), 10)
                action_id = game.action['actionId']
                requests_before = len(model.requests)
                agent.receive(event(2, text='停止' if stop else '读取上一轮进度'))
                # Give cancellation a chance to wait for the still-pending ACK.
                await asyncio.sleep(.01)
                if timing == 'ack':
                    self.assertFalse(cleanup_entered.is_set())
                    self.assertEqual(requests_before, len(model.requests))
                    if finish_before_cleanup:
                        game.pending = False
                    ack_release.set()
                if not lost_ack and not finish_before_cleanup:
                    await asyncio.wait_for(cleanup_entered.wait(), 5)
                    self.assertEqual(requests_before, len(model.requests))
                cleanup_release.set()
                await asyncio.wait_for(agent.task, 10)
                with self.assertRaises(asyncio.CancelledError):
                    await old
                if stop:
                    self.assertEqual(requests_before, len(model.requests))
                    agent.receive(event(3, text='读取上一轮进度'))
                    await asyncio.wait_for(agent.task, 10)
            runtime = json.loads(model.requests[-1][-1]['content'])
            handoff = runtime['interruptedRuns'][0]
            self.assertEqual(1, len(runtime['interruptedRuns']))
            self.assertEqual('cancelled' if stop else 'superseded', handoff['reason'])
            self.assertFalse(handoff['retryAllowed'])
            self.assertIsNone(handoff['programStatus'])
            self.assertEqual('move' if moving else 'use_item', handoff['activeAction']['operation'])
            self.assertEqual(None if lost_ack else action_id, handoff['activeAction']['actionId'])
            self.assertTrue(handoff['evidence']['available'])
            self.assertEqual(2 if prior else 1, handoff['actionResults']['totalCount'])
            ids = [i['result'].get('actionId') for i in handoff['actionResults']['items']]
            self.assertEqual(len(ids), len(set(ids)))
            if prior:
                first = handoff['actionResults']['items'][0]['result']
                self.assertEqual(('select_hotbar_slot', 'completed'), (first['operation'], first['status']))
            if cleanup == 'known' and not lost_ack:
                self.assertEqual('released', handoff['actionState'])
                self.assertTrue(handoff['inputReleased'])
                self.assertEqual('completed' if finish_before_cleanup else 'cancelled', handoff['activeAction']['status'])
                self.assertEqual('world', handoff['activeAction']['endEvidence']['worldSessionId'])
            else:
                self.assertEqual('unknown', handoff['actionState'])
                self.assertIsNone(handoff['inputReleased'])
                self.assertEqual('unknown', handoff['activeAction']['status'])
            starts = [p for c, p in (game.calls if moving else game.sent) if c == start_command]
            self.assertEqual(1 if moving or not prior else 2, len(starts))
            return handoff
        finally:
            ack_release.set(); cleanup_release.set()
            await agent.close()

    async def test_atomic_cancel_while_start_ack_pending(self):
        for cleanup in ('known', 'missing', 'error'):
            with self.subTest(cleanup=cleanup):
                await self.scenario(timing='ack', cleanup=cleanup)

    async def test_atomic_running_cancel_preserves_prior_direct_result(self):
        for cleanup in ('known', 'missing', 'error'):
            with self.subTest(cleanup=cleanup):
                await self.scenario(prior='direct', cleanup=cleanup)

    async def test_atomic_lost_start_ack_keeps_unknown_identity(self):
        await self.scenario(timing='ack', lost_ack=True)

    async def test_completion_racing_cancel_is_recorded_once_as_completed(self):
        await self.scenario(timing='ack', prior='direct', finish_before_cleanup=True)

    async def test_move_running_cancel_handoffs_release_or_unknown(self):
        for cleanup in ('known', 'missing', 'error'):
            with self.subTest(cleanup=cleanup):
                await self.scenario(moving=True, cleanup=cleanup)

    async def test_move_cancel_while_start_ack_pending(self):
        await self.scenario(moving=True, timing='ack')

    async def test_explicit_stop_handoff_reaches_following_run(self):
        await self.scenario(stop=True)

    async def test_latest_direct_receipt_replaces_earlier_lua_receipt(self):
        await self.scenario(prior='lua')

    async def test_cancel_during_model_retains_settled_direct_result(self):
        for moving in (False, True):
            with self.subTest(moving=moving):
                waiting = asyncio.Event()
                class Waiting(Model):
                    async def complete(self, messages, tools):
                        if len(self.requests) == (2 if moving else 1):
                            self.requests.append(messages)
                            waiting.set()
                            await asyncio.Future()
                        return await super().complete(messages, tools)
                game = MovingGame() if moving else AtomicGame()
                if moving:
                    game.complete_after = 1
                answers = [tool('get_observation'), tool('move', '{"direction":"right","duration_ms":50}')] if moving else [
                    tool('select_hotbar_slot', '{"slot":2}')]
                model = Waiting(*answers, final('新轮'))
                agent = Agent(game, model, lua_store=self.store)
                try:
                    agent.receive(event(1)); old = agent.task
                    await asyncio.wait_for(waiting.wait(), 5)
                    agent.receive(event(2)); await asyncio.wait_for(agent.task, 5)
                    with self.assertRaises(asyncio.CancelledError): await old
                    handoff = json.loads(model.requests[-1][-1]['content'])['interruptedRuns'][0]
                    self.assertEqual(1, handoff['actionResults']['totalCount'])
                    self.assertEqual('completed', handoff['activeAction']['status'])
                    self.assertTrue(handoff['inputReleased'])
                    if moving:
                        self.assertEqual('known', handoff['actionResults']['items'][0]['result']['effects']['knowledge'])
                    self.assertFalse(any(c == 'cancel_move' for c, _ in (game.calls if moving else game.sent)))
                finally:
                    await agent.close()


if __name__ == '__main__':
    unittest.main()
