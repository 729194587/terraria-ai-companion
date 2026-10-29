"""Return-value codec: real Lua tables, bounded JSON and independent decoding."""
from collections import namedtuple
from types import SimpleNamespace
import unittest

from test_lua_runtime import RuntimeGame
from lua_runtime import LuaRuntime, Limits
import lua_agent

Mixed = namedtuple('Mixed', 'array fields')


def unpack(value):
    """Independent reader of the documented v1 grammar (not the wire decoder)."""
    if isinstance(value, list):
        return [unpack(v) for v in value]
    if not isinstance(value, dict):
        return value
    if set(value) == {'$luaTable'}:
        body = value['$luaTable']
        assert body['version'] == 1
        fields = {k: unpack(v) for k, v in body['fields'].items()}
        if body['kind'] == 'mixed':
            return Mixed(unpack(body['array']), fields)
        assert body['kind'] == 'object'
        return fields
    return {k: unpack(v) for k, v in value.items()}


class LuaTableCodec(unittest.IsolatedAsyncioTestCase):
    def test_model_contracts_include_encoding_and_minimal_example(self):
        server, _ = lua_agent.server_contract(SimpleNamespace(supported_atomic_operations=frozenset()))
        for contract in (lua_agent.INSTRUCTIONS, server):
            for fragment in ('return {10,20,30,summary="collected"}', '$luaTable', 'serialization_error', 'fields'):
                self.assertIn(fragment, contract)

    async def test_private_identity_check_does_not_expand_sandbox(self):
        r = await self.run_value('return {type(rawequal),type(pcall),type(xpcall),type(print),type(debug),type(load)}')
        self.assertEqual('completed',r['status'],r)
        self.assertEqual(['nil']*6,r['value'])

    async def run_value(self, source):
        game = RuntimeGame()
        r = await LuaRuntime(game).execute(source, limits=Limits(seconds=8))
        self.assertFalse(any(cmd == 'start_atomic' for cmd, _ in game.sent))
        return r

    async def test_pure_arrays_objects_empty_and_json_types_are_unchanged(self):
        for source, expected in (
            ('return {10,20,30}', [10,20,30]),
            ('return {summary="collected", ok=true, n=1.5, none=json.null}', dict(summary='collected',ok=True,n=1.5,none=None)),
            ('return {}', {}), ('return json.object()', {}), ('return json.array()', []),
            ('return {10,json.null,30}', [10,None,30])):
            with self.subTest(source=source):
                r = await self.run_value(source)
                self.assertEqual('completed', r['status'], r)
                self.assertEqual(expected, r['value'])

    async def test_natural_mixed_table_uses_versioned_parts_without_loss(self):
        r = await self.run_value('return {10,20,30, summary="collected"}')
        self.assertEqual('completed', r['status'], r)
        self.assertEqual({'$luaTable': dict(version=1,kind='mixed',array=[10,20,30],fields=dict(summary='collected'))}, r['value'])
        self.assertEqual(Mixed([10,20,30],dict(summary='collected')), unpack(r['value']))

    async def test_nested_mixed_tables_and_shared_noncyclic_references(self):
        r = await self.run_value('local t={1,note="same"}; return {child={t,t,key={2,ready=false}},empty={}}')
        self.assertEqual('completed', r['status'], r)
        t = Mixed([1],dict(note='same'))
        self.assertEqual(dict(child=Mixed([t,t],dict(key=Mixed([2],dict(ready=False)))),empty={}),unpack(r['value']))

    async def test_reserved_encoding_keys_and_numeric_string_keys_cannot_collide(self):
        r = await self.run_value('return {10,["1"]="field",[""]="empty",["$luaTable"]={version=1,kind="mixed",array={99},fields={x=true}}}')
        self.assertEqual('completed', r['status'], r)
        literal = dict(version=1,kind='mixed',array=[99],fields=dict(x=True))
        self.assertEqual(Mixed([10],{'1':'field','':'empty','$luaTable':literal}),unpack(r['value']))
        r = await self.run_value('return {["$luaTable"]={version=1,kind="mixed",array={99},fields={x=true}}, nested={["$luaTable"]=json.null}}')
        self.assertEqual('object',r['value']['$luaTable']['kind'])
        self.assertEqual({'$luaTable':literal,'nested':{'$luaTable':None}},unpack(r['value']))

    async def test_sparse_special_keys_cycles_and_explicit_tag_conflicts_are_serialization_errors(self):
        sources = ('return {[1]=10,[3]=30}', 'return {[0]=10}', 'return {[-1]=10}',
                   'return {[true]=10}', 'return {[1.5]=10}', 'return {[{}]=10}',
                   'local t={};t.self=t;return t', 'local a={};local b={a};a.b=b;return a',
                   'return json.array({1,summary="wrong tag"})', 'return json.object({1})',
                   'return function() end', 'return 0/0', r'return "\255"', r'return {["\255"]=1}')
        for source in sources:
            with self.subTest(source=source):
                r = await self.run_value(source)
                self.assertEqual('serialization_error', r['status'], r)
                self.assertIsNone(r['value'])
                self.assertTrue(r['error'])
                self.assertEqual('serialization_error',r['failure']['kind'])

    async def test_real_encoded_size_limits_remain_value_limit(self):
        for n, expected in ((17,'completed'),(18,'value_limit')):
            r = await self.run_value('local s="x"; for i=1,'+str(n)+' do s=s..s end; return {1,label=s}')
            self.assertEqual(expected, r['status'], r)

    async def test_mixed_api_arguments_never_reach_game_and_prior_observation_survives(self):
        game = RuntimeGame()
        r = await LuaRuntime(game).execute("api('get_player_state',{}); return api('scan_tiles',{1,minX=0,maxX=1,minY=0,maxY=1,tileTypes={1}})", limits=Limits(seconds=8))
        self.assertEqual('serialization_error',r['status'],r)
        self.assertEqual('get_player_state',r['lastObservation']['operation'])
        self.assertFalse(any(cmd in ('query_atomic','start_atomic') for cmd,_ in game.sent))


if __name__ == '__main__':
    unittest.main()
