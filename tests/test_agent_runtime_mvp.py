"""Runtime contracts using model/game substitutes, plus real Lua workers."""
import asyncio
import copy
import io
import json
import multiprocessing as mp
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

import httpx

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'python'))
from agent import Agent
from agent_context import ContextManager, ContextBudgetExceeded, estimate_tokens
from agent_run import final_text, task_outcome
from llm import LLMClient, LLMError
from lua_agent import ProgramStore
from test_agent import Game, Model, event, final, tool
from test_lua_agent import program
from test_lua_runtime import RuntimeGame


class ContextTests(unittest.TestCase):
    def history(self):
        return [dict(role='system', content='system'),
                dict(role='user', content='old request '+'x'*9000), tool(),
                dict(role='tool', tool_call_id='call_1', content=json.dumps(
                    dict(status='completed', actionId='old-action', evidence={'path': 'archive'}))),
                final('old reply'), dict(role='user', content='recent request'), tool(),
                dict(role='tool', tool_call_id='call_1', content='{"valid":true}'),
                final('recent reply'), dict(role='user', content='current request')]

    def test_working_compaction_keeps_recent_pairs_and_canonical_unchanged(self):
        history = self.history(); before = copy.deepcopy(history)
        context = ContextManager(input_budget=1500, keep_recent_turns=1)
        projected = context.prepare(history, [], current_start=9)
        self.assertEqual(before, history)
        self.assertEqual(history[5:], projected[2:])
        self.assertLess(estimate_tokens(projected), estimate_tokens(history))
        summary = json.loads(projected[1]['content'])['historicalSummary']
        self.assertTrue(summary['lossy']); self.assertFalse(summary['currentState'])
        self.assertEqual('compacted', context.last_metrics['status'])

    def test_reactive_compacts_even_below_working_threshold(self):
        history = self.history()
        context = ContextManager(input_budget=10000, keep_recent_turns=1)
        self.assertEqual(history, context.prepare(history, [], current_start=9))
        result = context.prepare(history, [], current_start=9, reactive=True)
        self.assertLess(len(result), len(history))
        self.assertEqual('reactive_overflow', context.last_metrics['reason'])

    def test_summary_failure_and_current_turn_overflow_never_mutate_history(self):
        history = self.history(); before = copy.deepcopy(history)
        context = ContextManager(input_budget=256)
        with patch.object(context, 'summarize', side_effect=ValueError('summary unavailable')):
            with self.assertRaises(ValueError):
                context.prepare(history, [], current_start=9)
        self.assertEqual(before, history)
        with self.assertRaises(ContextBudgetExceeded):
            context.prepare([history[0], dict(role='user', content='x'*10000)], [], current_start=1)
        self.assertEqual(before, history)

    def test_tool_schema_size_is_part_of_budget(self):
        with self.assertRaises(ContextBudgetExceeded):
            ContextManager(256).prepare([dict(role='system', content='small')],
                [dict(schema='x'*3000)], current_start=1)

    def test_unqualified_success_removed_but_explanation_retained(self):
        result = final_text('任务已完成。目标被阻挡，建议先确认位置。', '行动未完成。')
        self.assertNotIn('任务已完成', result)
        self.assertIn('目标被阻挡，建议先确认位置。', result)
        self.assertEqual('unknown', task_outcome([dict(valid=True, status='completed', operation='use_item')]))

    def test_verified_task_partial_and_unknown_are_separate_from_script_success(self):
        from lua_agent import summarize
        task = dict(valid=True, completed=True, status='completed', operation='mine_target',
                    actionId='task-1', actionGeneration=2, targetTask=dict(x=10, y=20),
                    endEvidenceStatus='known', endEvidence=dict(inputsReleased=True, effectVerified=True,
                        worldSessionId='world', actorSlot=254),
                    evidence=dict(task=dict(taskId='task-1', taskName='mine_target', status='completed',
                        phase='terminal', worldSessionId='world', actorGeneration=2, actorSlot=254,
                        lastResult=dict(status='completed', inputsReleased=True, effectVerified=True,
                            evidence=dict(X=10, Y=20, targetAbsent=True, targetChanged=False, destroyedByActor=True)))))
        receipt = dict(status='completed', actionState='released', lastAction=task)
        result = summarize(receipt, [task])
        self.assertEqual('completed', result['taskOutcome']); self.assertEqual('unknown', result['goalOutcome'])
        failure = dict(valid=True, status='failed', operation='mine_target')
        result = summarize(dict(status='failed', actionState='released', lastAction=failure), [task, failure])
        self.assertEqual('partial', result['taskOutcome']); self.assertEqual('unknown', result['goalOutcome'])
        from agent_run import goal_outcome
        self.assertEqual('partial', goal_outcome([task, failure], False, goal_contract={
            'operation': 'mine_target', 'targetTask': {'x': 10, 'y': 20}}))
        task['evidence']['task']['lastResult']['evidence']['targetAbsent'] = False
        self.assertEqual('unknown', task_outcome([task]))
        result = summarize(dict(status='completed', actionState='idle', value={'taskOutcome': 'completed'}), [])
        self.assertEqual('unknown', result['taskOutcome']); self.assertEqual('unknown', result['goalOutcome'])
        result = summarize(dict(status='host_error', actionState='unknown'), [])
        self.assertEqual('unknown', result['taskOutcome']); self.assertEqual('unknown', result['goalOutcome'])


class RuntimeTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.store = ProgramStore(Path(self.directory.name)/'programs')
        self.stderr = patch('sys.stderr', new_callable=io.StringIO); self.stderr.start()

    async def asyncTearDown(self):
        self.stderr.stop(); self.directory.cleanup()
        self.assertFalse(mp.active_children())

    def agent(self, game, model, **options):
        return Agent(game, model, lua_store=self.store, **options)

    async def test_transient_model_retry_after_action_does_not_reexecute_program(self):
        model = Model(program("return api('select_hotbar_slot',{slot=2})"),
                      LLMError('temporary', retryable=True), final('选栏已核验。'))
        game = RuntimeGame(); agent = self.agent(game, model)
        agent.receive(event(1)); await agent.task
        self.assertEqual(3, len(model.requests))
        self.assertEqual(model.requests[1], model.requests[2])
        self.assertEqual(1, sum(c == 'start_atomic' for c, _ in game.sent))
        self.assertEqual('completed', agent.runs[-1].outcome)
        self.assertEqual(3, agent.runs[-1].metrics['modelCalls'])

    async def test_transient_retry_is_bounded_and_counted_against_run_budget(self):
        model = Model(LLMError('first', retryable=True), LLMError('second', retryable=True), final('unused'))
        agent = self.agent(Game(), model)
        agent.receive(event(1)); await agent.task
        self.assertEqual(2, len(model.requests)); self.assertEqual('failed', agent.runs[-1].outcome)
        model = Model(LLMError('first', retryable=True), final('unused'))
        agent = self.agent(Game(), model, max_model_calls=1)
        agent.receive(event(1)); await agent.task
        self.assertEqual(1, len(model.requests)); self.assertEqual('budget_exhausted', agent.runs[-1].outcome)

    async def test_reactive_overflow_retries_projection_once(self):
        context = ContextManager(100000, keep_recent_turns=0)
        model = Model(LLMError('overflow', context_overflow=True), final('ok'))
        agent = self.agent(Game(), model, context_manager=context)
        agent.history.extend([dict(role='user', content='old '+'x'*9000), final()])
        original = copy.deepcopy(agent.history)
        agent.receive(event(1)); await agent.task
        self.assertEqual(original, agent.history[:len(original)])
        self.assertLess(estimate_tokens(model.requests[1]), estimate_tokens(model.requests[0]))
        self.assertEqual('completed', agent.runs[-1].outcome)
        model = Model(LLMError('overflow', context_overflow=True), LLMError('still over', context_overflow=True))
        agent = self.agent(Game(), model)
        agent.receive(event(1)); await agent.task
        self.assertEqual(2, len(model.requests)); self.assertEqual('failed', agent.runs[-1].outcome)

    async def test_failed_compaction_preserves_canonical_and_prevents_model_call(self):
        context = ContextManager(256)
        model = Model(final('unused')); agent = self.agent(Game(), model, context_manager=context)
        agent.receive(event(1)); before = copy.deepcopy(agent.history)
        await agent.task
        self.assertEqual(before, agent.history[:len(before)])
        self.assertEqual([], model.requests); self.assertEqual('budget_exhausted', agent.runs[-1].outcome)

    async def test_cancelled_program_progress_handoff_is_available_after_cleanup(self):
        game = RuntimeGame(); original = game.command; active = asyncio.Event()
        loop = asyncio.get_running_loop()
        def command(cmd, **args):
            if cmd == 'start_atomic' and args['operation'] == 'use_item':
                game.pending = True
            reply = original(cmd, **args)
            if cmd == 'get_movement' and game.action:
                if game.action.get('endEvidence'):
                    reply['movement']['action']['endEvidence']['inputsReleased'] = True
                if game.action['status'] == 'running' and game.action['operation'] == 'use_item':
                    loop.call_soon_threadsafe(active.set)
            if cmd == 'cancel_move':
                reply['movement']['action']['endEvidence']['inputsReleased'] = True
            return reply
        game.command = command
        model = Model(program("api('select_hotbar_slot',{slot=2}); api('use_item')"), final('已读取上轮事实。'))
        agent = self.agent(game, model)
        agent.receive(event(1)); old = agent.task
        await asyncio.wait_for(active.wait(), 10)
        agent.receive(event(2)); await agent.task
        with self.assertRaises(asyncio.CancelledError): await old
        runtime = json.loads(model.requests[-1][-1]['content'])
        handoff = runtime['interruptedRuns'][0]
        self.assertEqual('superseded', handoff['reason'])
        self.assertTrue(handoff['inputReleased'])
        self.assertEqual('completed', handoff['actionResults']['items'][0]['result']['status'])
        self.assertEqual('cancelled', handoff['activeAction']['status'])
        self.assertFalse(handoff['retryAllowed'])
        self.assertEqual(['superseded', 'completed'], [r.outcome for r in agent.runs])
        self.assertEqual(1, sum(c == 'send_chat' for c, _ in game.sent))
        self.assertFalse(any(m['role'] == 'tool' for m in agent.history))

    async def test_stop_request_cancels_action_without_calling_model_again(self):
        game = RuntimeGame(pending=True)
        model = Model(program("api('use_item')")); agent = self.agent(game, model)
        agent.receive(event(1)); old = agent.task
        async def started():
            while game.action is None: await asyncio.sleep(.01)
        await asyncio.wait_for(started(), 10)
        agent.receive(event(2, text='停止')); await agent.task
        with self.assertRaises(asyncio.CancelledError): await old
        self.assertEqual('cancelled', game.action['status'])
        self.assertEqual(1, len(model.requests))
        self.assertEqual(['cancelled', 'cancelled'], [r.outcome for r in agent.runs])

    async def test_preempted_before_coroutine_start_has_terminal_run(self):
        agent = self.agent(Game(), Model(final('new')))
        agent.receive(event(1)); old = agent.task
        agent.receive(event(2)); await agent.task
        with self.assertRaises(asyncio.CancelledError): await old
        self.assertEqual('superseded', agent.runs[0].outcome)
        self.assertEqual('ended', agent.runs[0].phase)

    async def test_program_failure_is_run_failure_and_sent_text_is_history(self):
        game = RuntimeGame('failed')
        agent = self.agent(game, Model(program("api('mine_tile',{x=1,y=1})"),
            final('任务已完成。目标被阻挡，建议先确认位置。')))
        agent.receive(event(1)); await agent.task
        sent = next(p['text'] for c, p in game.sent if c == 'send_chat')
        self.assertEqual(sent, agent.history[-1]['content'])
        self.assertNotIn('任务已完成', sent); self.assertIn('verified_effect', sent)
        self.assertIn('Lua 程序未完成', sent)
        self.assertEqual('failed', agent.runs[-1].outcome)
        self.assertEqual('unknown', agent.runs[-1].goal_outcome)

    async def test_script_return_cannot_certify_goal_completion(self):
        game = RuntimeGame()
        agent = self.agent(game, Model(program('return {success=true}'), final('任务已完成。')))
        agent.receive(event(1)); await agent.task
        sent = next(p['text'] for c, p in game.sent if c == 'send_chat')
        self.assertNotIn('任务已完成', sent); self.assertIn('尚未确认', sent)
        self.assertEqual('unknown', agent.runs[-1].goal_outcome)

    async def test_unknown_without_action_receipt_remains_unknown_at_run_end(self):
        game = RuntimeGame(); game._lua_poisoned = True
        records = []
        agent = self.agent(game, Model(program('return 1'), final('任务已完成。')))
        with patch('agent.diagnostics.emit', side_effect=lambda kind, **data: records.append((kind, data))):
            agent.receive(event(1)); await agent.task
        end = next(data for kind, data in records if kind == 'agent_end')
        self.assertEqual('unknown', end['task_outcome'])
        self.assertEqual('unknown', end['goal_outcome'])
        self.assertFalse(any(c == 'start_atomic' for c, _ in game.sent))

    async def test_http_errors_classified_without_exposing_provider_text(self):
        for status, detail, retryable, overflow in (
                (400, dict(code='context_length_exceeded', message='private-provider-text'), False, True),
                (429, {}, True, False), (503, {}, True, False), (401, {}, False, False)):
            client = LLMClient('https://model.test/v1', 'fixture', 'fixture',
                transport=httpx.MockTransport(lambda r: httpx.Response(status, json={'error': detail})))
            try:
                with self.assertRaises(LLMError) as raised: await client.complete([], [])
                self.assertEqual((retryable, overflow), (raised.exception.retryable, raised.exception.context_overflow))
                self.assertNotIn('private-provider-text', str(raised.exception))
            finally:
                await client.close()


if __name__ == '__main__':
    unittest.main()
