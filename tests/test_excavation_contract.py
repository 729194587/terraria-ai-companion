from copy import deepcopy
from types import SimpleNamespace
import unittest

import atomic_operations as atomic
import program_tasks


class ExcavationContractTests(unittest.TestCase):
    def test_opt_in_capability_and_arguments(self):
        args = {'x': 10, 'y': 20, 'cluster': True}
        self.assertEqual(atomic.validate('mine_target', args), args)
        with self.assertRaises(ValueError):
            atomic.validate('mine_target', {**args, 'cluster': 'true'})
        with self.assertRaises(atomic.PreDispatchRejected):
            atomic.validate_backend(SimpleNamespace(hello={}), 'mine_target', args)
        atomic.validate_backend(SimpleNamespace(hello={'capabilities': ['server_ai_excavation_v1']}), 'mine_target', args)
        for supported in (False, True):
            tool = atomic.server_tools({'mine_target'}, excavation=supported)[0]['function']
            self.assertEqual('cluster' in tool['parameters']['properties'], supported)
            submit = next(t['function'] for t in program_tasks.definitions(excavation=supported)
                          if t['function']['name'] == 'submit_task')
            self.assertEqual('cluster' in submit['parameters']['properties']['arguments']['properties'], supported)

    def test_completion_requires_each_distinct_connected_tile_and_release(self):
        action = dict(actionId='batch', generation=2, worldSessionId='world', actorSlot=254,
                      targetTask={'x': 10, 'y': 20, 'cluster': True})
        targets = [dict(x=x, y=20, typeId=6, status='verified',
                        proof=dict(X=x, Y=20, Type=6, Update=12+index*22, Strikes=2))
                   for index, x in enumerate((10, 11, 12))]
        result = dict(status='completed', inputsReleased=True, effectVerified=True,
                      evidence=dict(outcome='completed', seedX=10, seedY=20, typeId=6, targets=targets))
        evidence = dict(task=dict(taskId='batch', taskName='mine_target', status='completed', phase='terminal',
                                  currentChild=None, worldSessionId='world', actorGeneration=2, actorSlot=254,
                                  result=result))
        self.assertTrue(atomic.verified_server_mine_task(action, evidence))
        for mutation in (
            lambda task: task.update(actorGeneration=3),
            lambda task: task['result'].update(inputsReleased=False),
            lambda task: task['result']['evidence']['targets'][1].update(status='changed'),
            lambda task: task['result']['evidence']['targets'].append(deepcopy(targets[0])),
            lambda task: task['result']['evidence']['targets'].pop(1),
            lambda task: task['result']['evidence']['targets'][0]['proof'].update(Strikes=0),
        ):
            with self.subTest(mutation=mutation):
                altered = deepcopy(evidence)
                mutation(altered['task'])
                self.assertFalse(atomic.verified_server_mine_task(action, altered))
