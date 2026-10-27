"""Regression for G8.5 tree scanning from real floating-point player positions."""
import copy
from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'python'))
import atomic_operations as atomic
from lua_runtime import LuaRuntime, Limits
from test_lua_runtime import RuntimeGame


class CoordinateValidationTests(unittest.TestCase):
    def test_normalizes_only_integer_schema_fields_without_mutating_input(self):
        args = dict(minX=2053.0, minY=165.0, maxX=2097.0, maxY=193.0, tileTypes=[5.0], maxResults=20.0)
        original = copy.deepcopy(args)
        normalized = atomic.validate('scan_tiles', args)
        self.assertEqual(original, args)
        self.assertIs(type(args['minX']), float)
        for name in ('minX', 'minY', 'maxX', 'maxY', 'maxResults'):
            self.assertIs(type(normalized[name]), int)
        self.assertIs(type(normalized['tileTypes'][0]), int)
        target = atomic.validate('use_item', {'target': {'x': 12.5, 'y': 20.0}})
        self.assertEqual({'target': {'x': 12.5, 'y': 20.0}}, target)
        self.assertIs(type(target['target']['y']), float)
        self.assertEqual({'slots': [9, 58], 'mode': 'slots'},
                         atomic.validate('inspect_inventory', {'slots': [9.0, 58.0], 'mode': 'slots'}))

    def test_rejects_fractional_nonfinite_boolean_string_and_out_of_range(self):
        for value in (2113.5, True, '2113', float('nan'), float('inf'), -1.0, 100001.0):
            with self.subTest(value=value), self.assertRaisesRegex(ValueError, r'inspect_tile.x: expected finite integer'):
                atomic.validate('inspect_tile', {'x': value, 'y': 2})
        with self.assertRaisesRegex(ValueError, r'tileTypes\[2\]'):
            atomic.validate('scan_tiles', dict(minX=0, minY=0, maxX=1, maxY=1, tileTypes=[5, 1.5]))
        with self.assertRaisesRegex(ValueError, 'duplicate'):
            atomic.validate('scan_tiles', dict(minX=0, minY=0, maxX=1, maxY=1, tileTypes=[5, 5.0]))

    def test_recorded_oversized_bounds_report_area_and_keep_limit(self):
        with self.assertRaisesRegex(ValueError, r'121 x 51 = 6171.*<=4096'):
            atomic.validate('scan_tiles', dict(minX=2053.0, maxX=2173.0, minY=165, maxY=215, tileTypes=[5]))
        atomic.validate('scan_tiles', dict(minX=0.0, minY=0.0, maxX=63.0, maxY=63.0, tileTypes=[5]))
        with self.assertRaisesRegex(ValueError, '4097'):
            atomic.validate('scan_tiles', dict(minX=0.0, minY=0.0, maxX=4096.0, maxY=0.0, tileTypes=[5]))


class LuaCoordinateTests(unittest.IsolatedAsyncioTestCase):
    async def test_float_player_position_floor_and_division_reach_integer_query(self):
        game = RuntimeGame()
        original = game.get_terrain
        def terrain():
            result = original()
            result['state']['position'] = dict(x=33823.027, y=3286.0)
            return result
        game.get_terrain = terrain
        source = """
            local s=api('get_player_state',{})
            local p=s.player.position
            local cx=(p.x+10)//16
            local cy=math.floor((p.y+20)/16)
            assert(math.tointeger(cx)==2114 and math.tointeger(1.5)==nil)
            assert(math.floor(-1.5)==-2 and math.random==nil and math.huge==nil)
            return api('scan_tiles',{minX=cx-22,minY=cy-14,maxX=cx+22,maxY=cy+14,
                tileTypes={5.0},maxResults=20.0})
        """
        result = await LuaRuntime(game).execute(source, limits=Limits(seconds=8, actions=0))
        self.assertEqual('completed', result['status'], result)
        self.assertEqual(0, result['actions'])
        query = next(p for c, p in game.sent if c == 'query_atomic')
        self.assertEqual(dict(minX=2092, minY=192, maxX=2136, maxY=220, tileTypes=[5], maxResults=20), query['arguments'])
        self.assertIs(type(query['arguments']['minX']), int)
        self.assertIs(type(query['arguments']['tileTypes'][0]), int)

    async def test_recorded_large_scan_fails_before_rpc_with_specific_reason(self):
        game = RuntimeGame()
        result = await LuaRuntime(game).execute("""
            return api('scan_tiles',{minX=2053.0,maxX=2173.0,minY=165,maxY=215,tileTypes={5}})
        """, limits=Limits(seconds=8, actions=0))
        self.assertEqual('bad_argument', result['status'], result)
        self.assertIn('121 x 51 = 6171', result['failure']['message'])
        self.assertFalse(any(c in ("query_atomic", "start_atomic") for c, _ in game.sent))
        self.assertEqual(0, result['actions'])
