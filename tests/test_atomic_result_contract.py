"""Regression coverage for action receipts, task outcomes and JSONL accounting."""
import asyncio
import copy
import io
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'python'))
import agent_trace as tracing
import atomic_operations as atomic
from agent import Agent
from control import BridgeError
from test_agent import Model, event, final, tool
from test_atomic_operations import Game


class ReasonProjectionTests(unittest.TestCase):
    def test_reason_and_diagnostic_detail_survive_python_and_json_projection(self):
        for status, reason, detail in (
            ('completed', 'native_tile_destroyed', None),
            ('failed', 'native_tool_damage_refused', '保护判定后的伤害为零：目标 (12,34)。'),
            ('cancelled', 'explicit_stop', None),
            ('unknown', 'native_effect_unconfirmed', '观察仍不足'),
        ):
            with self.subTest(status=status):
                receipt = atomic.result(dict(status=status, reason=reason, reasonDetail=detail, actionId='id'), 1)
                restored = json.loads(json.dumps(receipt, ensure_ascii=False))
                self.assertEqual(reason, restored['reason'])
                self.assertEqual(detail, restored['reasonDetail'])
                self.assertEqual(status == 'completed', restored['completed'])
                self.assertEqual(status == 'cancelled', restored['cancelled'])


class CombatReceiptTests(unittest.TestCase):
    def test_defense_resolves_separate_spawn_evidence_and_external_removal(self):
        a = {'Slot': 0, 'TypeId': 1, 'EntityRef': 'spawn-a'}
        b = {'Slot': 0, 'TypeId': 1, 'EntityRef': 'spawn-b'}
        strike = {'LifeBefore': 8, 'LifeAfter': -1, 'DamageDone': 9, 'Defeated': True, 'GameUpdate': 10}
        proof = dict(source='reflex', threat='native', target=b, lastHit=None, encounters=[
            dict(Target=a, Reason='combat_target_defeated', LastHit=strike),
            dict(Target=b, Reason='combat_target_invalidated', LastHit=None),
        ])
        result = dict(reason='combat_threats_cleared', inputsReleased=True, effectVerified=True)
        task = dict(taskName='combat_attack', taskId='defense', status='completed')
        action = dict(actionId='defense', task=task, targetTask=dict(entityRef='spawn-a', threat='native'))
        self.assertTrue(atomic.verified_server_combat(action, task, result, proof))
        proof['encounters'][-1].update(Reason='combat_target_defeated', LastHit=strike)
        proof['lastHit'] = strike
        self.assertFalse(atomic.verified_server_combat(action, task, result, proof))


class StopContractTests(unittest.IsolatedAsyncioTestCase):
    async def test_supervisor_deadline_preserves_actual_stop_terminal(self):
        for status in ('completed','failed','timeout','cancelled','unknown'):
            with self.subTest(status=status):
                game=Game(pending=True)
                original=game.command
                def command(cmd,**params):
                    reply=original(cmd,**params)
                    if cmd=='cancel_move':
                        reply['movement']['action'].update(status=status,reason='native_race_terminal')
                    return reply
                game.command=command
                result=await atomic.dispatch(game,'use_item',{})
                self.assertEqual(status,result['status'])
                self.assertEqual(status=='completed',result['completed'])
                self.assertEqual('native_race_terminal',result['reason'])
                self.assertEqual({'status':'timeout','reason':'supervisor_deadline'},result['supervisorStopRequest'])
                self.assertEqual(1,sum(c=='start_atomic' for c,_ in game.sent))

    async def test_terminal_transition_between_poll_and_renew_requires_same_identity_and_release(self):
        for mode in ('completed', 'failed', 'missing_release', 'new_owner', 'still_running'):
            with self.subTest(mode=mode):
                game = Game(pending=True)
                original = game.command
                def command(cmd, **params):
                    if cmd == 'renew_move':
                        if mode != 'still_running':
                            game.pending = False
                            game.outcome = 'failed' if mode == 'failed' else 'completed'
                        if mode == 'new_owner':
                            game.action['session'] = 99
                        raise BridgeError('terminal renewal rejected', status='action_changed')
                    reply = original(cmd, **params)
                    if cmd == 'get_movement' and game.action and mode == 'missing_release':
                        reply['movement']['action']['endEvidenceStatus'] = 'unknown'
                    return reply
                game.command = command
                result = await atomic.dispatch(game, 'use_item', {})
                if mode in ('completed', 'failed'):
                    self.assertTrue(result['valid'], result)
                    self.assertEqual(mode, result['status'])
                    self.assertNotIn('supervisorError', result)
                    self.assertFalse(any(cmd == 'cancel_move' for cmd, _ in game.sent))
                elif mode == 'missing_release':
                    self.assertFalse(result['valid'])
                    self.assertEqual('unknown', result['status'])
                else:
                    self.assertFalse(result.get('completed', False), result)
                    self.assertIn('supervisorError', result)

    async def start(self):
        game = Game(pending=True)
        game.command('start_atomic', operation='mine_tile')
        game.action.update(session=1, generation=1)
        game.sent.clear()
        return game

    async def test_idle_without_id_is_noop_but_missing_owned_id_is_unknown(self):
        game = Game()
        self.assertEqual(await atomic.stop(game),
                         {'valid': True, 'status': 'completed', 'reason': 'no_agent_action', 'noOp': True})
        result = await atomic.stop(game, 42)
        self.assertFalse(result['valid'])
        self.assertEqual('unknown', result['status'])
        self.assertFalse(result['completed'])
        self.assertEqual('action_missing', result['reason'])
        self.assertFalse(any(c == 'cancel_move' for c, _ in game.sent))

    async def test_owner_and_identity_changes_do_not_cancel_new_action(self):
        for key, value in [('actionId', 99), ('session', 2), ('generation', 2), ('worldSessionId', 'new')]:
            with self.subTest(key=key):
                game = await self.start()
                expected = copy.deepcopy(game.action)
                game.action[key] = value
                result = await atomic.stop(game, expected['actionId'], expected)
                self.assertFalse(result['valid'])
                self.assertEqual('ownership_changed', result['reason'])
                self.assertFalse(any(c == 'cancel_move' for c, _ in game.sent))

    async def test_normal_cancel_and_missing_release_evidence(self):
        for missing in (False, True):
            game = await self.start()
            original = game.command
            def command(cmd, **params):
                reply = original(cmd, **params)
                if cmd == 'cancel_move' and missing:
                    reply['movement']['action']['endEvidenceStatus'] = 'unknown'
                return reply
            game.command = command
            result = await atomic.stop(game, game.action['actionId'])
            self.assertEqual('unknown' if missing else 'cancelled', result['status'])
            self.assertEqual(not missing, result['valid'])
            self.assertFalse(result['completed'])
            if missing:
                self.assertEqual('missing_game_thread_release_evidence', result['reason'])

    async def test_cancel_receipt_must_still_describe_owned_terminal_action(self):
        for change in ('missing', 'new_owner', 'running', 'session', 'generation'):
            with self.subTest(change=change):
                game = await self.start()
                original = game.command
                def command(cmd, **params):
                    reply = original(cmd, **params)
                    if cmd == 'cancel_move':
                        action = reply['movement']['action']
                        if change == 'missing': reply['movement']['action'] = None
                        elif change == 'new_owner': action['actionId'] += 1
                        elif change == 'running': action['status'] = 'running'
                        else: action[change] += 1
                    return reply
                game.command = command
                result = await atomic.stop(game, game.action['actionId'])
                self.assertFalse(result['valid'])
                self.assertEqual('unknown', result['status'])
                self.assertFalse(result['completed'])

    async def test_release_poll_rejects_identity_changes(self):
        game = await self.start()
        action = copy.deepcopy(game.action)
        action.update(status='cancelled', endEvidenceStatus='pending')
        game.action['session'] += 1
        result = await atomic.terminal(game, action, 1)
        self.assertFalse(result['valid'])
        self.assertEqual('unknown', result['status'])
        self.assertEqual('action_changed_before_release_evidence', result['reason'])

    async def test_deadline_cleanup_missing_or_replaced_action_is_not_success(self):
        for cleanup in ('missing', 'replaced', 'no_evidence', 'confirmed', 'error'):
            with self.subTest(cleanup=cleanup):
                game = Game(pending=True)
                original = game.command
                def command(cmd, **params):
                    if cmd == 'get_movement' and game.action:
                        if cleanup == 'missing': game.action = None
                        elif cleanup == 'replaced': game.action['actionId'] += 1
                        elif cleanup == 'error': raise BridgeError('cleanup unavailable')
                    reply = original(cmd, **params)
                    if cmd == 'start_atomic':
                        reply['movement']['action']['durationMs'] = -1000  # Immediate supervisor deadline.
                    if cmd == 'cancel_move' and cleanup == 'no_evidence':
                        reply['movement']['action']['endEvidenceStatus'] = 'unknown'
                    return reply
                game.command = command
                result = await atomic.dispatch(game, 'mine_tile', {'x': 1, 'y': 1})
                self.assertEqual('cancelled' if cleanup == 'confirmed' else 'unknown', result['status'])
                if cleanup == 'confirmed':
                    self.assertEqual({'status': 'timeout', 'reason': 'supervisor_deadline'},
                                     result['supervisorStopRequest'])
                self.assertFalse(result['completed'])
                if cleanup in ('missing', 'replaced', 'error'):
                    self.assertFalse(any(c == 'cancel_move' for c, _ in game.sent))

    async def test_task_cancellation_never_cancels_replacement_or_claims_cleanup_success(self):
        for cleanup in ('missing', 'replaced', 'error', 'no_evidence'):
            with self.subTest(cleanup=cleanup):
                game = Game(pending=True)
                task = asyncio.create_task(atomic.dispatch(game, 'use_item', {}))
                while game.action is None:
                    await asyncio.sleep(.001)
                original = game.command
                def command(cmd, **params):
                    if cmd == 'get_movement':
                        if cleanup == 'missing': game.action = None
                        elif cleanup == 'replaced': game.action['actionId'] += 1
                        elif cleanup == 'error': raise BridgeError('cleanup unavailable')
                    reply = original(cmd, **params)
                    if cmd == 'cancel_move':
                        reply['movement']['action']['endEvidenceStatus'] = 'unknown'
                    return reply
                game.command = command
                task.cancel()
                with self.assertRaises(asyncio.CancelledError):
                    await task
                cancels = [p for c, p in game.sent if c == 'cancel_move']
                self.assertEqual(1 if cleanup == 'no_evidence' else 0, len(cancels))


class AgentContractTests(unittest.IsolatedAsyncioTestCase):
    async def run_agent(self, game, answers, **limits):
        with tempfile.TemporaryDirectory() as directory:
            trace = tracing.Trace(Path(directory) / 'trace.jsonl', enabled=True)
            with patch.object(tracing, 'trace', trace), patch('sys.stderr', new_callable=io.StringIO):
                agent = Agent(game, Model(*answers), **limits)
                agent.receive(event(1))
                await agent.task
            records = [json.loads(line) for line in trace.path.read_text(encoding='utf-8').splitlines()]
        return agent, records

    def end_and_cost(self, records, outcome, task_outcome, action_outcome):
        for kind in ('agent_end', 'chat_cost'):
            record = next(r for r in records if r['type'] == kind)
            self.assertEqual(outcome, record['outcome'])
            self.assertEqual(task_outcome, record['task_outcome'])
            self.assertEqual(action_outcome, record['action_outcome'])

    async def test_action_status_validity_feedback_and_accounting(self):
        for status in ('completed', 'failed', 'timeout', 'cancelled', 'unknown'):
            with self.subTest(status=status):
                game = Game(status)
                _, records = await self.run_agent(game, [tool('mine_tile', '{"x":1,"y":1}'), final('模型声称完成')])
                result = next(r for r in records if r['type'] == 'tool_result')
                self.assertTrue(result['result_valid'])
                self.assertEqual(status == 'completed', result['success'])
                self.assertEqual(status == 'completed', result['action_success'])
                self.assertEqual(status, result['result']['status'])
                self.end_and_cost(records, 'completed' if status == 'completed' else 'failed', 'unknown', status)
                text = next(p['text'] for c, p in game.sent if c == 'send_chat')
                self.assertNotIn('模型声称完成', text)
                self.assertIn('已核验动作：mine_tile' if status == 'completed' else 'verified_effect', text)
                self.assertIn('整体目标完成情况尚未确认', text)
                self.assertEqual(text, next(r['text'] for r in records if r['type'] == 'reply_prepared'))

    async def test_successful_reads_do_not_require_action_status(self):
        for name in ('inspect_tile', 'get_state'):
            with self.subTest(name=name):
                from test_agent import Game as ReadGame
                game = Game() if name == 'inspect_tile' else ReadGame()
                _, records = await self.run_agent(game, [tool(name, '{"x":1,"y":1}' if name == 'inspect_tile' else '{}'), final()])
                result = next(r for r in records if r['type'] == 'tool_result')
                self.assertTrue(result['result_valid'])
                self.assertTrue(result['success'])
                self.assertIsNone(result['action_success'])
                self.end_and_cost(records, 'completed', 'unknown', None)

    async def test_failed_mining_is_preserved_when_budget_blocks_next_action(self):
        game = Game('failed')
        agent, records = await self.run_agent(game, [tool('mine_tile', '{"x":1,"y":1}'), tool('place_tile', '{"x":2,"y":1}')], max_tool_calls=1)
        self.assertEqual(1, sum(c == 'start_atomic' for c, _ in game.sent))
        text = next(p['text'] for c, p in game.sent if c == 'send_chat')
        self.assertIn('verified_effect', text)
        self.assertIn('预算已用尽', text)
        self.assertNotIn('抵达', text)
        self.assertEqual(text, agent.history[-1]['content'])
        self.end_and_cost(records, 'budget_exhausted', 'unknown', 'failed')
        self.assertEqual('verified_effect', next(r for r in records if r['type'] == 'agent_end')['action_reason'])
        self.assertFalse(next(r for r in records if r['type'] == 'tool_result')['success'])

    async def test_blocked_action_does_not_replace_original_failure(self):
        game = Game('failed')
        _, records = await self.run_agent(game, [tool('mine_tile', '{"x":1,"y":1}'), tool('place_tile', '{"x":2,"y":1}'), final('完成')])
        self.assertEqual(1, sum(c == 'start_atomic' for c, _ in game.sent))
        results = [r for r in records if r['type'] == 'tool_result']
        self.assertTrue(results[1]['result']['blocked'])
        self.assertFalse(results[1]['result_valid'])
        self.assertFalse(results[1]['success'])
        text=next(p['text'] for c, p in game.sent if c == 'send_chat')
        self.assertIn('verified_effect',text)
        self.assertNotIn('已核验动作',text)
        self.end_and_cost(records, 'failed', 'unknown', 'failed')

    async def test_non_movement_budget_has_generic_feedback_and_no_fake_movement_terminal(self):
        game = Game()
        agent, records = await self.run_agent(game, [tool('inspect_tile', '{"x":1,"y":1}')], max_model_calls=1)
        self.assertEqual('本轮预算已用尽，未确认任务完成。', game.sent[-1][1]['text'])
        self.assertNotEqual('budget_exhausted', agent.last_movement_result['status'])
        self.end_and_cost(records, 'budget_exhausted', 'unknown', None)

    async def test_movement_budget_uses_movement_feedback(self):
        game = Game()
        # A move request activates the movement loop even when its observation guard rejects it.
        agent, records = await self.run_agent(game, [tool('move', '{"direction":"right","duration_ms":100}'), tool()], max_tool_calls=1)
        self.assertEqual('budget_exhausted', agent.last_movement_result['status'])
        self.assertIn('未确认抵达目标', game.sent[-1][1]['text'])
        self.end_and_cost(records, 'budget_exhausted', 'unknown', None)
