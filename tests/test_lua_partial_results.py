"""Partial host evidence through real Worker/Agent and simulated game/model."""
import copy
import io
import json
import multiprocessing as mp
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'python'))
from agent import Agent
from lua_agent import ProgramStore, summarize
from lua_action_results import ACTION_RESULTS_BYTES, action_results
from test_agent import Model, final
from test_lua_agent import LocalGame, lua_results, program


class HistoryProjectionTests(unittest.TestCase):
    def test_full_receipts_are_ordered_detached_and_not_value(self):
        actions = [dict(operation='select_hotbar_slot', actionId=1, valid=True, status='completed',
                        completed=True, evidence={'selectedSlot': 2}),
                   dict(operation='use_item', actionId=2, valid=True, status='failed', completed=False,
                        reason='blocked', reasonDetail='actual target blocked', endEvidenceStatus='known',
                        endEvidence={'inputsReleased': True})]
        receipt = dict(status='failed', actionState='released', value=None, lastAction=actions[-1],
                       lastObservation={'sample': 'unchanged'})
        original = copy.deepcopy((receipt, actions))
        result = summarize(receipt, actions)
        history = result['actionResults']
        self.assertEqual(actions, [item['result'] for item in history['items']])
        self.assertEqual([1, 2], [item['sequence'] for item in history['items']])
        self.assertFalse(history['truncated'])
        self.assertEqual({'completed': 1, 'failed': 1}, history['statusCounts'])
        self.assertIsNone(result['value'])
        self.assertFalse(result['programCompleted']); self.assertFalse(result['actionSuccess'])
        self.assertEqual('not_completed', result['taskOutcome'])
        self.assertEqual(original, (receipt, actions))
        history['items'][0]['result']['evidence']['selectedSlot'] = 999
        self.assertEqual(2, actions[0]['evidence']['selectedSlot'])

    def test_large_receipts_become_explicit_summaries_with_real_unknown_and_release(self):
        actions = [dict(operation='move_input', actionId=i, status=status, valid=True,
                        completed=status == 'completed', reason='actual reason', evidence={'huge': '木' * 30000},
                        endEvidenceStatus='known', endEvidence={'inputsReleased': True})
                   for i, status in enumerate(('completed', 'unknown'))]
        history = action_results(actions, actions[-1])
        self.assertLessEqual(len(json.dumps(history, ensure_ascii=False).encode()), ACTION_RESULTS_BYTES)
        self.assertEqual((2, 2, 0, 2), tuple(history[k] for k in
                         ('totalCount', 'returnedCount', 'omittedCount', 'summaryOnlyCount')))
        self.assertTrue(history['truncated'])
        self.assertEqual(['completed', 'unknown'], [i['result']['status'] for i in history['items']])
        self.assertTrue(history['items'][-1]['result']['endEvidence']['inputsReleased'])
        self.assertEqual('known', history['items'][-1]['result']['endEvidenceStatus'])

    def test_many_summaries_report_omissions_and_keep_early_progress_and_final_failure(self):
        actions = [dict(actionId=i, status='unknown' if i == 255 else 'completed', valid=True,
                        reason='木' * 1000, reasonDetail='水' * 1000, evidence={'payload': 'x' * 40000})
                   for i in range(256)]
        history = action_results(actions)
        self.assertLessEqual(len(json.dumps(history, ensure_ascii=False).encode()), ACTION_RESULTS_BYTES)
        self.assertGreater(history['omittedCount'], 0)
        self.assertEqual(256, history['returnedCount'] + history['omittedCount'])
        self.assertEqual({'completed': 255, 'unknown': 1}, history['statusCounts'])
        self.assertEqual((1, 256), (history['items'][0]['sequence'], history['items'][-1]['sequence']))
        self.assertEqual('unknown', history['items'][-1]['result']['status'])

    def test_cleanup_is_visible_and_bounded_last_action_is_not_a_duplicate(self):
        completed = dict(actionId=1, status='completed', valid=True, evidence={'actual': 7})
        bounded = dict(actionId=1, status='completed', valid=True, payloadOmitted=True)
        cleanup = dict(actionId=2, status='unknown', reason='cleanup_unconfirmed', valid=False)
        history = action_results([completed], bounded, cleanup)
        self.assertEqual(2, history['totalCount'])
        self.assertEqual(['api', 'cleanup'], [item['phase'] for item in history['items']])
        self.assertEqual(cleanup, history['items'][-1]['result'])


class PartialAgentTests(unittest.IsolatedAsyncioTestCase):
    async def run_program(self, source, status='failed', followup=True):
        game = LocalGame(); original = game.command
        starts = 0
        def command(cmd, **params):
            nonlocal starts
            if cmd == 'start_atomic':
                starts += 1
                game.outcome = 'completed' if starts == 1 else status
            reply = original(cmd, **params)
            if cmd == 'get_movement' and game.action and game.action['status'] != 'running':
                action = reply['movement']['action']
                action['reasonDetail'] = 'native target blocked' if starts > 1 else 'selected native slot'
                action['endEvidence']['inputsReleased'] = True
                action['evidence']['actualProgress'] = starts
            return reply
        game.command = command
        explanation = '已完成选栏；后续动作未完成，停止。'
        answers = [program(source)]
        if followup:
            answers.append(program("return api('use_item')"))
        answers.append(final(explanation))
        model = Model(*answers)
        with tempfile.TemporaryDirectory() as directory, patch('sys.stderr', new_callable=io.StringIO), patch('sys.stdout', new_callable=io.StringIO) as output:
            agent = Agent(game, model, local_input=True, lua_store=ProgramStore(Path(directory)))
            await agent.receive_local('按授权执行测试任务')
            await agent.task
            sent = output.getvalue()
        self.assertFalse(mp.active_children())
        return model, game, sent, explanation

    async def test_success_then_failure_is_model_visible_and_explanation_survives(self):
        model, game, sent, explanation = await self.run_program("api('select_hotbar_slot',{slot=2}); api('use_item'); api('select_hotbar_slot',{slot=3})")
        receipt = lua_results(model)[0]
        history = receipt['actionResults']['items']
        self.assertEqual(['completed', 'failed'], [item['result']['status'] for item in history])
        self.assertEqual('native target blocked', history[-1]['result']['reasonDetail'])
        self.assertEqual(1, history[0]['result']['evidence']['actualProgress'])
        self.assertIsNone(receipt['value'])
        self.assertEqual(2, sum(cmd == 'start_atomic' for cmd, _ in game.sent))
        self.assertIn(explanation, sent); self.assertIn('行动未完成', sent)
        self.assertFalse(receipt['actionSuccess'])

    async def test_success_then_unknown_keeps_success_and_quarantine(self):
        model, game, sent, explanation = await self.run_program("api('select_hotbar_slot',{slot=2}); api('use_item')", 'unknown')
        receipt = lua_results(model)[0]
        results = [item['result'] for item in receipt['actionResults']['items']]
        self.assertEqual(['completed', 'unknown'], [r['status'] for r in results])
        self.assertTrue(results[-1]['endEvidence']['inputsReleased'])
        self.assertEqual('known', results[-1]['endEvidenceStatus'])
        self.assertEqual('unknown', receipt['actionState'])
        self.assertTrue(game.closed); self.assertTrue(game._lua_poisoned)
        self.assertEqual(2, sum(cmd == 'start_atomic' for cmd, _ in game.sent))
        self.assertIn('unknown', sent); self.assertIn(explanation, sent)

    async def test_normal_return_and_pure_lua_error_keep_original_semantics(self):
        for source, completed, value in (("api('select_hotbar_slot',{slot=2}); return {actual=7}", True, {'actual': 7}),
                                         ("error('pure Lua failure')", False, None)):
            with self.subTest(completed=completed):
                model, _, sent, _ = await self.run_program(source, followup=False)
                receipt = lua_results(model)[0]
                self.assertEqual(completed, receipt['programCompleted'])
                self.assertEqual(value, receipt['value'])
                self.assertEqual(1 if completed else 0, receipt['actionResults']['totalCount'])
                self.assertEqual(True if completed else None, receipt['actionSuccess'])
                self.assertEqual('unknown' if completed else 'not_completed', receipt['taskOutcome'])
                if not completed:
                    self.assertIn('Lua 程序未完成', sent)
