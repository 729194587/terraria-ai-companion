"""Handshake projections and model reference checked against actual Worker permissions."""
import copy
import itertools
import json
from pathlib import Path
import sys
from types import SimpleNamespace
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'python'))
import atomic_operations as atomic
from lua_agent import server_contract
from lua_runtime import LuaRuntime
from server_ai import operations_for_capabilities
from server_ai_contract import (capability_manifest, exposed_operations, BASE_GLOBALS, LIBRARIES,
                                runtime_reference, helper_reference)
from test_lua_runtime import RuntimeGame

BASE = {'server_ai_atomic_v1', 'atomic_operations_v1', 'hotbar_keys_v1'}
OPTIONAL = ('server_ai_reads_v1', 'server_ai_perception_v1', 'server_ai_map_v1',
            'server_ai_crafting_v1', 'server_ai_scene_v1', 'server_ai_actions_v1',
            'server_ai_direct_input_v1', 'server_ai_place_object_workbench_v1',
            'server_ai_place_tile_v1', 'landing_region_navigation_v1')


def bridge(caps):
    return SimpleNamespace(hello={'capabilities': sorted(caps)},
                           supported_atomic_operations=operations_for_capabilities(caps))


def contract_parts(prompt):
    manifest, rest = prompt.split('\nCapability Manifest\n', 1)[1].split('\nAPI Contract\n', 1)
    api, reference = rest.split('\nLua Runtime Reference\n', 1)
    return manifest, json.loads(api.split('\n', 1)[1]), reference


class ManifestTests(unittest.TestCase):
    def test_all_handshake_combinations_match_manifest_and_contract(self):
        original = copy.deepcopy(atomic.TOOLS)
        for enabled in itertools.product((False, True), repeat=len(OPTIONAL)):
            caps = BASE | {cap for cap, yes in zip(OPTIONAL, enabled) if yes}
            b = bridge(caps)
            expected = {'select_hotbar_slot', 'wait', 'stop_action'}
            for cap, ops in (
                ('server_ai_reads_v1', {'get_player_state', 'inspect_inventory'}),
                ('server_ai_perception_v1', {'observe_terrain', 'scan_tiles', 'scan_entities', 'inspect_tile', 'resolve_content'}),
                ('server_ai_map_v1', {'query_map'}), ('server_ai_crafting_v1', {'query_recipes'}),
                ('server_ai_scene_v1', {'observe_scene'}),
            ):
                if cap in caps: expected |= ops
            if 'server_ai_actions_v1' in caps:
                expected |= {'navigate_to_x', 'use_item', 'mine_tile'}
                for cap, op in (('server_ai_direct_input_v1', 'move_input'),
                                ('server_ai_crafting_v1', 'craft_recipe'),
                                ('server_ai_place_tile_v1', 'place_tile'),
                                ('server_ai_place_object_workbench_v1', 'place_object'),
                                ('landing_region_navigation_v1', 'navigate_to_landing_region')):
                    if cap in caps: expected.add(op)
            self.assertEqual(expected, b.supported_atomic_operations)
            prompt, tools = server_contract(b)
            manifest_text, definitions, reference = contract_parts(prompt)
            manifest = capability_manifest(b)
            self.assertEqual(expected, {d['function']['name'] for d in definitions})
            self.assertEqual(expected, {op for c in manifest['categories'] for op in c['callableApis']})
            self.assertEqual(['execute_lua'], [t['function']['name'] for t in tools])
            for c in manifest['categories']:
                self.assertIn(' | '.join((c['category'], c['status'], ','.join(c['callableApis']) or '—', ','.join(c['notExposed']) or '—')), manifest_text)
                self.assertFalse(set(c['notExposed']) & expected)
                self.assertLessEqual(set(c.get('alternatives', ())), expected)
                if not c['callableApis']: self.assertEqual('Not exposed', c['status'])
            # Only examples for exposed APIs are supplied, even on partial backends.
            examples = reference.split('\n示例', 1)[1]
            for absent in {'move_input', 'get_player_state', 'select_hotbar_slot'} - expected:
                self.assertNotIn('api("'+absent+'"', examples)
        self.assertEqual(original, atomic.TOOLS)

    def test_unknown_flags_and_stale_whitelist_fail_closed(self):
        self.assertEqual(frozenset(), operations_for_capabilities({'server_ai_direct_input_v1'}))
        self.assertEqual(operations_for_capabilities(BASE), operations_for_capabilities(BASE | {'future_all_apis_v9'}))
        b = bridge(BASE)
        b.supported_atomic_operations = set(atomic.SCHEMAS)
        self.assertEqual({'select_hotbar_slot', 'wait', 'stop_action'}, exposed_operations(b))
        manifest, definitions, _ = contract_parts(server_contract(b)[0])
        self.assertNotIn('move_input', {d['function']['name'] for d in definitions})
        self.assertEqual('Not exposed', next(c for c in capability_manifest(b)['categories'] if c['category']=='直接身体输入')['status'])

    def test_limited_boundaries_and_shared_schemas(self):
        b = bridge(BASE | set(OPTIONAL))
        manifest, definitions, _ = contract_parts(server_contract(b)[0])
        defs = {t['function']['name']: t['function'] for t in definitions}
        for name, f in defs.items():
            if name == 'use_item':
                self.assertEqual({}, f['parameters']['properties'])
            else:
                self.assertEqual(atomic.SCHEMAS[name], f['parameters'])
        for name, facts in {
            'move_input': ('jumpHoldUpdates', 'X/Y', '不要求导航profile', 'unknown'),
            'use_item': ('木剑', '铜镐', '铜斧', '苹果', '不证明命中'),
            'place_tile': ('泥土', '石块', '木材', '火把', '不支持家具'),
            'place_object': ('Work Bench', '2×1', '消耗1件'),
            'craft_recipe': ('0..57', '特殊回调', '箱子'),
            'navigate_to_landing_region': ('64px', '斜平台', '0.5px'),
        }.items():
            for fact in facts:self.assertIn(fact, defs[name]['description'])
        for category in ('直接身体输入', '目标导航', '物品与战斗', '建造', '制作'):
            self.assertEqual('Limited', next(c for c in capability_manifest(b)['categories'] if c['category']==category)['status'])
        before = server_contract(b)
        before[1][0]['function']['parameters']['properties']['source']['type']='number'
        self.assertEqual('string', server_contract(b)[1][0]['function']['parameters']['properties']['source']['type'])

    def test_helper_dependencies_and_runtime_limits(self):
        minimal = helper_reference(exposed_operations(bridge(BASE)))
        self.assertNotIn('sense_world(options)', minimal)
        self.assertNotIn('explore(options)', minimal)
        full = helper_reference(exposed_operations(bridge(BASE | set(OPTIONAL))))
        self.assertIn('oreNames', full)
        self.assertIn('dropTypes', full)
        self.assertIn('不是默认或必经方案', full)
        with patch('server_ai_contract.SCENE_HELPER_AVAILABLE', False):
            legacy = helper_reference(exposed_operations(bridge(BASE | set(OPTIONAL))))
            self.assertIn('水平局部候选', legacy)
            self.assertNotIn('scene_landing_regions', legacy)
            scene_only = {'get_player_state', 'observe_scene', 'scan_entities', 'navigate_to_landing_region'}
            self.assertNotIn('explore(options)', helper_reference(scene_only))
        for fact in ('64次', '8次', '60秒', 'runtimeLua', '无跨程序可写持久化', 'continuationAllowed=true', 'unknown保持unknown'):
            self.assertIn(fact, runtime_reference(set()))


class WorkerReferenceTests(unittest.IsolatedAsyncioTestCase):
    async def test_documented_libraries_match_actual_worker_allowlist(self):
        pieces = []
        for library in LIBRARIES:
            pieces.append('local '+library+'Keys={}; for k in pairs('+library+') do table.insert('+library+'Keys,k) end; table.sort('+library+'Keys)')
        fields = ','.join(name+'='+name+'Keys' for name in LIBRARIES)
        base_types = ','.join('type('+name+')' for name in BASE_GLOBALS)
        r = await LuaRuntime(RuntimeGame()).execute('; '.join(pieces)+'; return {'+fields+',base={'+base_types+'}}')
        self.assertEqual('completed', r['status'], r)
        for library, members in LIBRARIES.items():
            self.assertEqual(sorted(members), r['value'][library])
        self.assertEqual(['function']*len(BASE_GLOBALS), r['value']['base'])
        r = await LuaRuntime(RuntimeGame()).execute('return {type(_G),type(os),type(require),type(print),type(json.encode),type(json.decode),type(string.rep),type(explore),type(sense_world),type(pcall)}')
        self.assertEqual(['nil']*7+['function']*3, r['value'])

    async def test_reference_json_rules_and_fresh_program_scope(self):
        runtime = LuaRuntime(RuntimeGame())
        r = await runtime.execute('saved=123; return {10,summary="ok",empty=json.array(),absent=json.null}')
        self.assertEqual({'$luaTable': {'version':1,'kind':'mixed','array':[10],
            'fields':{'summary':'ok','empty':[],'absent':None}}}, r['value'])
        r = await runtime.execute('return type(saved)')
        self.assertEqual('nil', r['value'])
        for source in ('return 0/0', 'return {[2]=1}', 'local t={}; t.self=t; return t'):
            r = await runtime.execute(source)
            self.assertEqual('serialization_error', r['status'], r)


if __name__ == '__main__':
    unittest.main()
