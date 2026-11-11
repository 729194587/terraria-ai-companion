import unittest

from action_recovery import Recovery, released_task_failure


class RecoveryPolicyTests(unittest.TestCase):
    def test_range_refusal_requires_changed_strategy_or_position(self):
        tracker = Recovery(None)
        target = {'x': 10, 'y': 20}
        failed = dict(status='failed', reason='out_of_range', continuationAllowed=True)
        self.assertEqual('out_of_range', tracker.observe('mine_tile', target, failed)['kind'])
        self.assertIsNotNone(tracker.blocked('mine_tile', target))
        self.assertIsNone(tracker.blocked('mine_tile', {'x': 11, 'y': 20}))
        tracker.observe('move_input', {}, dict(status='completed', valid=True, evidence={'noProgress': False}))
        self.assertIsNone(tracker.blocked('mine_tile', target))

    def test_repeated_no_progress_only_suppresses_same_move(self):
        tracker = Recovery(None)
        right = {'horizontal': 'right'}
        still = dict(status='completed', valid=True, evidence={'noProgress': True})
        self.assertEqual('no_progress', tracker.observe('move_input', right, still)['kind'])
        self.assertIsNone(tracker.blocked('move_input', right))
        tracker.observe('move_input', right, still)
        self.assertIsNotNone(tracker.blocked('move_input', right))
        self.assertIsNone(tracker.blocked('move_input', {'horizontal': 'left'}))

    def test_stance_failure_requires_terminal_task_and_matching_release(self):
        reply = dict(valid=True, status='failed', reason='no_safe_mining_stance_in_local_window',
                     operation='mine_target', dispatchState='admitted', endEvidenceStatus='known',
                     actionId='task', worldSessionId='world', actorId='actor', actionGeneration=3, actorSlot=9,
                     task=dict(taskId='task', phase='terminal', status='failed', currentChild=None),
                     endEvidence=dict(phase='ServerAI_game_thread_inputs_released', operation='mine_target',
                                      inputsReleased=True, effectVerified=False, worldSessionId='world',
                                      actorId='actor', actorGeneration=3, actorSlot=9))
        self.assertTrue(released_task_failure(reply))
        tracker = Recovery(None)
        target = {'target': {'x': 4, 'y': 7, 'token': 'old'}}
        self.assertEqual('no_stance', tracker.observe('mine_target', target,
                         {**reply, 'continuationAllowed': True})['kind'])
        self.assertIsNotNone(tracker.blocked('mine_target', {'target': {'x': 4, 'y': 7, 'token': 'new'}}))
        self.assertFalse(released_task_failure({**reply, 'status': 'unknown'}))
        self.assertFalse(released_task_failure({**reply, 'endEvidence': {**reply['endEvidence'], 'actorGeneration': 4}}))
        self.assertFalse(released_task_failure({**reply, 'task': {**reply['task'], 'currentChild': {'status': 'running'}}}))
