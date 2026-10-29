"""Schema diagnostics and real Worker rejection before any game RPC."""
import json
import unittest

from test_lua_runtime import RuntimeGame
import atomic_operations as atomic
from lua_runtime import LuaRuntime, Limits


class ParameterDiagnostics(unittest.TestCase):
    def rejected(self, name, arguments):
        with self.assertRaises(atomic.ArgumentValidationError) as caught:
            atomic.validate(name, arguments)
        return caught.exception

    def test_scan_missing_fields_reports_real_required_fields_and_types(self):
        error = self.rejected('scan_tiles', {})
        self.assertEqual({'minX', 'maxX', 'minY', 'maxY', 'tileTypes'},
                         {i['path'].split('.')[-1] for i in error.diagnostics})
        self.assertEqual('array of integers', next(i['expected'] for i in error.diagnostics if i['path'].endswith('tileTypes')))

    def test_types_unknown_fields_and_resolve_id_are_aggregated(self):
        error = self.rejected('scan_tiles', dict(minX='0', maxX=2, minY=0, maxY=2, tileTypes=7, width=3))
        self.assertEqual(3, len(error.diagnostics))
        self.assertIn("unknown field 'width'", str(error))
        self.assertEqual('integer', next(i['actual'] for i in error.diagnostics if i['path'].endswith('tileTypes')))
        error = self.rejected('resolve_content', dict(kind='enemy', id=123))
        self.assertEqual({'missing_required', 'unknown_field', 'unsupported_value'}, {i['code'] for i in error.diagnostics})
        self.assertIn("missing required field 'query'", str(error))
        self.assertIn("unknown field 'id'", str(error))

    def test_bounded_diagnostics_do_not_echo_values_and_normalization_stays(self):
        secret = 'untrusted-value-' * 10000
        error = self.rejected('resolve_content', dict(kind=secret, query=secret, **{f'extra{i}': secret for i in range(20)}))
        self.assertLessEqual(len(error.diagnostics), 8)
        self.assertTrue(error.diagnostics_truncated)
        self.assertNotIn('untrusted-value', json.dumps(error.diagnostics))
        args = atomic.validate('scan_tiles', dict(minX=0.0, maxX=1.0, minY=0, maxY=1, tileTypes=[1.0]))
        self.assertIs(type(args['minX']), int)
        self.assertEqual([1], args['tileTypes'])


class RuntimeArgumentDiagnostics(unittest.IsolatedAsyncioTestCase):
    async def test_invalid_arguments_keep_bad_argument_and_never_dispatch(self):
        for source in ("return api('scan_tiles',{width=3,tileTypes=7})",
                       "return api('resolve_content',{kind='enemy',id=73})"):
            with self.subTest(source=source):
                game = RuntimeGame()
                r = await LuaRuntime(game).execute(source, limits=Limits(seconds=8))
                self.assertEqual('bad_argument', r['status'], r)
                self.assertEqual('idle', r['actionState'])
                self.assertEqual('bad_argument', r['failure']['kind'])
                self.assertTrue(r['diagnostics'])
                self.assertEqual(1, r['failure']['call']['id'])
                self.assertFalse(any(cmd in ('query_atomic', 'start_atomic') for cmd, _ in game.sent))

    async def test_valid_schema_calls_still_reach_existing_dispatcher(self):
        game = RuntimeGame()
        game.hello['capabilities'].append('atomic_foundations_v1')
        r = await LuaRuntime(game).execute("api('scan_tiles',{minX=0,maxX=1,minY=0,maxY=1,tileTypes={1}}); return api('resolve_content',{kind='tile',query='Terraria/Stone'})", limits=Limits(seconds=8))
        self.assertEqual('completed', r['status'], r)
        self.assertEqual(['scan_tiles', 'resolve_content'], [p['operation'] for cmd, p in game.sent if cmd == 'query_atomic'])
