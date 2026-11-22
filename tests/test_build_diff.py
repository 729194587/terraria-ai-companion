"""Blueprint input, discovery, and the server query wire without a game process."""
import asyncio
import json
import sys
import threading
import unittest
from pathlib import Path
from unittest.mock import Mock

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'python'))
import atomic_operations as atomic
from control import BridgeError
from server_ai import ServerAiBridge, operations_for_capabilities
from server_ai_contract import api_help


def validate(**layers):
    return atomic.validate('build_diff', {'blueprint': {'origin': {'x': 200, 'y': 300}, **layers}})


class BuildDiffTests(unittest.TestCase):
    def test_layers_and_span(self):
        first = dict(x=-2, y=0, itemType=9)
        last = dict(x=61, y=0, contentKey='Terraria/Wood')
        validate(tiles=[first, last], walls=[first])
        for layers in ({}, {'tiles': [first, first]}, {'tiles': [first], 'objects': [first]},
                       {'tiles': [first, dict(last, x=62)]}, {'objects': [dict(last, width=2)]},
                       {'walls': [dict(first, contentKey='wood')]}, {'tiles': [dict(x=0,y=0)]},
                       {'tiles': [dict(first, itemType=True)]}, {'tiles': [dict(first, x=-201)]},
                       {'walls': [first]*128, 'tiles': [first]*129}):
            with self.subTest(layers=list(layers)), self.assertRaises(ValueError):
                validate(**layers)

    def test_eight_by_eight_house_fits(self):
        frame = [dict(x=x, y=y, itemType=9) for x in range(8) for y in range(8)
                 if (x in (0, 7) or y in (0, 7)) and not (x == 0 and 4 <= y <= 6)]
        walls = [dict(x=x, y=y, itemType=93) for x in range(1, 7) for y in range(1, 7)]
        objects = [dict(x=x, y=y, itemType=item) for x, y, item in
                   [(0, 6, 25), (2, 6, 34), (4, 6, 36), (6, 2, 8)]]
        args = validate(tiles=frame, walls=walls, objects=objects)
        self.assertEqual(len(frame) + len(walls) + len(objects), 65)
        self.assertLess(len(json.dumps(args['blueprint'], separators=(',', ':')).encode()), 2000)

    def test_blueprint_utf8_limit(self):
        entry = dict(x=0, y=0, contentKey='木')
        args = validate(objects=[entry])
        size = len(json.dumps(args['blueprint'], ensure_ascii=False, separators=(',', ':')).encode())
        entry['contentKey'] += 'a' * (3072-size)
        validate(objects=[entry])
        entry['contentKey'] += 'a'
        with self.assertRaisesRegex(ValueError, '3072'):
            validate(objects=[entry])

    def test_capability_help_and_wire(self):
        bridge = object.__new__(ServerAiBridge)
        bridge.hello = {'capabilities': ['server_ai_atomic_v1', 'server_ai_perception_v1']}
        bridge.supported_atomic_operations = operations_for_capabilities(bridge.hello['capabilities'])
        args = validate(tiles=[dict(x=0, y=0, itemType=9)])
        self.assertEqual(api_help(bridge, {'name':'build_diff'})['status'], 'not_exposed')
        with self.assertRaises(atomic.PreDispatchRejected):
            asyncio.run(atomic.dispatch(bridge, 'build_diff', args))
        bridge.hello['capabilities'] = ['server_ai_atomic_v1', 'server_ai_build_diff_v1']
        bridge.supported_atomic_operations = operations_for_capabilities(bridge.hello['capabilities'])
        self.assertTrue(api_help(bridge, {'name':'build_diff'})['valid'])
        bridge._lock = threading.RLock()
        bridge._wire_lock = threading.Lock()
        bridge._pending_lock = threading.Lock()
        bridge._pending = {}
        bridge._request_id = 0
        bridge._retired_binding = bridge._closed = False
        bridge.binding = dict(world='w', generation=1, slot=254, actor='a')
        bridge.trace_connection_id = 'test'
        bridge.timeout = 1
        bridge.sock = Mock()

        def reply(wire):
            request = json.loads(wire)
            self.assertEqual(wire, (json.dumps(request, ensure_ascii=False, separators=(',', ':'))+'\n').encode())
            self.assertEqual(request['op'], 'query')
            self.assertEqual(request['arguments'], args)
            bridge._pending[request['id']].set_result(dict(ok=True, binding=bridge.binding, data=dict(available=False)))

        bridge.sock.sendall.side_effect = reply
        self.assertFalse(asyncio.run(atomic.dispatch(bridge, 'build_diff', args))['valid'])
        with self.assertRaises(BridgeError):
            bridge.command('start_atomic', operation='build_diff', arguments=args)
        bridge.binding['world'] = 'w'*4096
        with self.assertRaisesRegex(BridgeError, '4096'):
            asyncio.run(atomic.dispatch(bridge, 'build_diff', args))
        bridge.sock.sendall.assert_called_once()
