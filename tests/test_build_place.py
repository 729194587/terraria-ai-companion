"""Offline coverage for the Blueprint task API and its terminal evidence."""
import asyncio
import copy
import unittest
from unittest.mock import Mock

from test_build_diff import atomic, ServerAiBridge, operations_for_capabilities, api_help
import program_tasks


class BuildPlaceTests(unittest.TestCase):
    def setUp(self):
        self.args = {'blueprint': {'origin': {'x': 20, 'y': 30}, 'tiles': [
            {'x': -1, 'y': 0, 'itemType': 9}, {'x': 0, 'y': 0, 'itemType': 9}]}}
        self.bridge = object.__new__(ServerAiBridge)
        self.caps = ['server_ai_atomic_v1', 'server_ai_actions_v1', 'server_ai_tasks_v1',
                     'server_ai_program_tasks_v1']
        self.bridge.hello = {'capabilities': self.caps}

    def test_only_advertised_as_task_with_capability(self):
        for enabled in (False, True):
            if enabled:
                self.caps.append('server_ai_build_place_v1')
            self.bridge.supported_atomic_operations = operations_for_capabilities(self.caps)
            params = api_help(self.bridge, {'name': 'submit_task'})['parameters']
            self.assertEqual(enabled, 'build_place' in params['properties']['operation']['enum'])
            self.assertEqual('not_exposed', api_help(self.bridge, {'name': 'build_place'})['status'])
            with self.assertRaises(atomic.PreDispatchRejected):
                asyncio.run(atomic.dispatch(self.bridge, 'build_place', self.args))

    def test_validation_reuses_full_blueprint_rules(self):
        submit = dict(operation='build_place', arguments=self.args)
        self.assertEqual(submit, program_tasks.validate('submit_task', submit))
        for invalid in (dict(self.args, navigate=True), {'blueprint': {}},
                        {'blueprint': dict(self.args['blueprint'], tiles=self.args['blueprint']['tiles'] * 2)}):
            with self.assertRaises(ValueError):
                program_tasks.validate('submit_task', dict(submit, arguments=invalid))

    def test_submission_preserves_arguments_and_identity(self):
        self.caps.append('server_ai_build_place_v1')
        self.bridge.supported_atomic_operations = operations_for_capabilities(self.caps)
        self.bridge.binding = dict(world='w', generation=1, slot=254, actor='a')
        action = dict(actionId='id', worldSessionId='w', generation=1, actorSlot=254, actorId='a')
        self.bridge._request = Mock(return_value={'data': {'action': action}})
        self.bridge.task_request('start', 'id', operation='build_place', arguments=self.args)
        self.bridge._request.assert_called_once_with('start', actionId='id', operation='build_place', arguments=self.args)

    def test_partial_and_duplicate_entries_never_prove_completion(self):
        proof = dict(requested=dict(x=20,y=30), kind='tile', placedByActor=True, targetPresent=True,
                     targetChanged=False, uncertainNativeEffect=False, nativePlacementEvents=1, consumedCount=1, itemType=9)
        rows = [dict(layer='tiles', x=19, y=30, itemType=9, status='matched', nativeEvidence=None),
                dict(layer='tiles', x=20, y=30, itemType=9, status='placed', nativeEvidence=proof)]
        evidence = dict(outcome='completed', entries=rows, completedByLayer=dict(tiles=2, walls=0, objects=0))
        self.assertTrue(atomic.verified_build_entries(self.args, evidence))
        for updates in (dict(outcome='partial'), dict(entries=rows[:1]), dict(entries=rows[:1]*2),
                        dict(entries=[dict(rows[0], nativeEvidence=proof), rows[1]])):
            self.assertFalse(atomic.verified_build_entries(self.args, dict(evidence, **updates)))
        resumed = copy.deepcopy(evidence)
        resumed['entries'][1].update(status='matched', nativeEvidence=None)
        self.assertTrue(atomic.verified_build_entries(self.args, resumed))
