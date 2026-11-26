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
        linked = deepcopy(evidence)
        native, secondary, _ = linked['task']['result']['evidence']['targets']
        native['proof']['ActionId'] = 'pick-action'
        chain = dict(Before=dict(X=11, Y=20, Type=6, Revision=8), AfterRevision=9,
                     ActorSlot=254, ActionId='pick-action', SeedX=10, SeedY=20, Update=12)
        secondary.update(source='improvegame_chain', proof=None, chainProof=chain)
        self.assertTrue(atomic.verified_server_mine_task(action, linked))
        for wrong in (dict(ActorSlot=3), dict(ActionId='other'), dict(SeedX=12), dict(AfterRevision=8), dict(Update=1)):
            secondary['chainProof'] = {**chain, **wrong}
            self.assertFalse(atomic.verified_server_mine_task(action, linked))
        secondary.update(chainProof=chain, proof=targets[1]['proof'])
        self.assertFalse(atomic.verified_server_mine_task(action, linked))
        large = deepcopy(evidence)
        batch = large['task']['result']['evidence']
        batch['targets'] = [dict(x=x, y=20, typeId=6, status='verified',
                                 proof=dict(X=x, Y=20, Type=6, Update=12, Strikes=2))
                            for x in range(10, 31)]
        self.assertTrue(atomic.verified_server_mine_task(action, large))
        batch['discoveryComplete'] = False
        self.assertFalse(atomic.verified_server_mine_task(action, large))
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

    def test_verbose_navigation_retains_terminal_ore_proofs(self):
        from lua_runtime import bounded_action
        from lua_worker import VALUE_BYTES
        proof = dict(outcome='partial', targets=[dict(x=1, y=2, status='pending')])
        task = dict(taskName='mine_target', result=dict(evidence=proof),
                    progress=dict(settledChildren=1, children=['x' * VALUE_BYTES]))
        result = dict(task=task, evidence=dict(task=task), targetTask=dict(cluster=True),
                      inputsReleased=True, status='failed')
        compact = bounded_action(result)
        self.assertTrue(compact['inputsReleased'])
        self.assertTrue(compact['task']['historyOmitted'])
        self.assertEqual(compact['task']['result']['evidence'], proof)
        self.assertEqual(compact['task']['progress']['omittedChildren'], 1)
        self.assertEqual(len(task['progress']['children']), 1)
