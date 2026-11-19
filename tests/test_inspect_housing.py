"""Read-only server housing query, discovery and native result shape."""
import asyncio
import threading
import unittest
from unittest.mock import Mock

from jsonschema import Draft202012Validator
from test_server_ai_contract import BASE, bridge
import atomic_operations as atomic
from control import BridgeError
from server_ai import ServerAiBridge
from server_ai_contract import api_help


NAME = 'inspect_housing'
CAP = 'server_ai_housing_inspect_v1'
ARGS = {'x': 123, 'y': 45}


class HousingTests(unittest.TestCase):
    def test_parameters_and_discovery(self):
        atomic.validate(NAME, ARGS)
        for args in ({}, {'x': 123}, dict(ARGS, npcType=22), dict(ARGS, x=True), dict(ARGS, y=-1)):
            with self.subTest(args=args), self.assertRaises(ValueError):
                atomic.validate(NAME, args)
        backend = bridge(BASE | {'server_ai_perception_v1'})
        self.assertEqual(api_help(backend, {'name': NAME})['status'], 'not_exposed')
        with self.assertRaises(atomic.PreDispatchRejected):
            asyncio.run(atomic.dispatch(backend, NAME, ARGS))
        self.assertIn(NAME, atomic.READS)
        self.assertNotIn(NAME, atomic.ACTIONS)

    def test_query_and_stage_results(self):
        backend = object.__new__(ServerAiBridge)
        projected = bridge(BASE | {CAP})
        backend.hello = projected.hello
        backend.supported_atomic_operations = projected.supported_atomic_operations
        backend._lock = threading.RLock()
        backend.binding = dict(world='w', actor='a', generation=1, slot=254)
        backend._request = Mock()
        validator = Draft202012Validator(api_help(backend, {'name': NAME})['returnsSchema'])
        data = dict(queryTile=ARGS, stinkbug=None, echoStinkbug=None, source='server_ai_player', customState='unknown', inventoryTokenStatus='unused',
                    npcType=22, status='invalid', failureStage='structure', reason='solid_query_tile',
                    nativeReason='RoomCheckStartedInASolidTile', structureValid=False, bounds=None,
                    furnitureValid=None, furniture=None, occupied=None, evil=None, score=None,
                    homeTile=None, checkVersion=CAP)
        stages = [
            {},
            dict(reason='unclassified_structure_failure', status='unknown'),
            dict(status='invalid', structureValid=True, failureStage='furniture', reason='room_needs_failed',
                 nativeReason='None', stinkbug=False, echoStinkbug=False, bounds=dict(minX=120, maxX=127, minY=40, maxY=47),
                 furnitureValid=False, furniture=dict(door=True, light=False, table=True, chair=True)),
            dict(status='valid', failureStage=None, reason=None, furnitureValid=True,
                 furniture=dict(door=True, light=True, table=True, chair=True), occupied=False,
                 evil=False, score=1, homeTile=dict(x=122, y=46)),
        ]
        for changes in stages:
            data.update(changes)
            envelope = dict(available=True, worldSessionId='w', actorSlot=254, actorGeneration=1,
                            gameUpdate=10, observedAtUnixMs=1000, clock='server_utc_at_projection', data=data)
            backend._request.return_value = {'data': envelope}
            result = asyncio.run(atomic.dispatch(backend, NAME, ARGS))
            backend._request.assert_called_with('query', operation=NAME, arguments=ARGS)
            self.assertTrue(result['valid'])
            self.assertEqual(result['data'], data)
            validator.validate(result)
        self.assertFalse(validator.is_valid(dict(result, data=dict(data, status='completed'))))
        with self.assertRaises(BridgeError):
            backend.command('start_atomic', operation=NAME, arguments=ARGS)
        self.assertEqual(backend._request.call_count, len(stages))
