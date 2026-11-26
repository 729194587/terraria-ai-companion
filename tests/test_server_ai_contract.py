"""Handshake projections and model reference checked against actual Worker permissions."""
import copy
import re
import itertools
import json
from pathlib import Path
import sys
from types import SimpleNamespace
import unittest
from unittest.mock import Mock, patch
from jsonschema import Draft202012Validator

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'python'))
import atomic_operations as atomic
import program_tasks
from api_result_contracts import DRAFT, missing_for, registered_names, schema_for
from lua_agent import server_contract
from lua_runtime import Limits, LuaRuntime
from server_ai import operations_for_capabilities
from server_ai_contract import (api_help, capability_manifest, exposed_operations, BASE_GLOBALS, LIBRARIES,
                                runtime_reference, helper_reference, server_definitions)
from test_lua_runtime import RuntimeGame

BASE = {'server_ai_atomic_v1', 'atomic_operations_v1', 'hotbar_keys_v1'}
OPTIONAL = ('server_ai_reads_v1', 'server_ai_perception_v1', 'server_ai_map_v1',
            'server_ai_crafting_v1', 'server_ai_scene_v1', 'server_ai_actions_v1',
            'server_ai_direct_input_v1', 'server_ai_place_object_workbench_v1',
            'server_ai_place_tile_v1', 'landing_region_navigation_v1', 'server_ai_inventory_v1', 'server_ai_tasks_v1')
FULL = BASE | set(OPTIONAL) | {
    'server_ai_scene_region_v1', 'server_ai_program_tasks_v1', 'server_ai_navigation_v1',
    'server_ai_equipment_v1', 'server_ai_place_rope_v1', 'server_ai_move_on_rope_v1',
}
FULL_ACTIONS = FULL | {'server_ai_containers_v1', 'server_ai_container_transfer_v1',
                       'server_ai_container_batch_v1', 'server_ai_sorting_v1'} | {
            'server_ai_combat_v1', 'server_ai_weapon_button_v1', 'server_ai_combat_primary_v1',
            'server_ai_boss_encounter_v1', 'server_ai_boss_strategy_v1', 'server_ai_open_item_v1', 'server_ai_drop_item_v1', 'server_ai_inventory_favorite_v1', 'server_ai_personal_inventory_v1',
            'server_ai_pylons_v1', 'server_ai_regroup_v1', 'server_ai_dismiss_summon_v1', 'server_ai_doors_v1', 'server_ai_interact_tile_v1', 'server_ai_hammer_v1',
            'server_ai_collect_drops_v1', 'server_ai_place_item_v1', 'server_ai_build_place_v1',
            'server_ai_material_plan_v1', 'server_ai_item_sources_v1', 'server_ai_item_info_v1', 'server_ai_build_diff_v1', 'server_ai_merchant_shop_v1', 'server_ai_merchant_purchase_v1',
            'server_ai_merchant_sale_v1', 'server_ai_npc_dialogue_v1', 'server_ai_merchant_bulk_purchase_v1',
            'server_ai_nurse_v1', 'server_ai_reforge_v1',
            'server_ai_npc_housing_v1', 'server_ai_housing_assignment_v1', 'server_ai_housing_inspect_v1'}


def bridge(caps):
    b = SimpleNamespace(hello={'capabilities': sorted(caps)},
                        supported_atomic_operations=operations_for_capabilities(caps))
    if 'server_ai_program_tasks_v1' in caps:
        b.task_request = Mock(side_effect=AssertionError('contract inspection must not dispatch a task'))
    return b


def contract_parts(prompt):
    manifest, rest = prompt.split('\nCapability Manifest\n', 1)[1].split('\nAPI Contract\n', 1)
    api, reference = rest.split('\nLua Runtime Reference\n', 1)
    return manifest, json.loads(api.split('\n', 1)[1]), reference


class ApiHelpTests(unittest.IsolatedAsyncioTestCase):
    @staticmethod
    def runtime_bridge(capabilities):
        b = bridge(capabilities)

        def idle_movement(command):
            if command != 'get_movement':
                raise AssertionError(f'Unexpected game dispatch: {command}')
            return {'movement': {'action': None}}

        b.command = Mock(side_effect=idle_movement)
        return b

    async def test_worker_returns_current_index_and_exact_projected_schema_without_dispatch(self):
        b = self.runtime_bridge(FULL)
        b.task_request = Mock(side_effect=AssertionError('help must not send a task request'))
        result = await LuaRuntime(b).execute("""
            local index = api('help')
            local details = {}
            for _, category in ipairs(index.categories) do
                for _, name in ipairs(category.apis) do
                    local detail = api('help',{name=name})
                    detail.draft = detail.returnsSchema["$schema"]
                    detail.returnsSchema = nil
                    details[name] = detail
                end
            end
            return {index=index,details=details}
        """, allow_actions=False)
        self.assertEqual('completed', result['status'], result)
        self.assertEqual(0, result['actions'])
        self.assertIsNone(result['lastObservation'])
        expected = {item['function']['name']: item['function'] for item in server_definitions(b)}
        self.assertEqual(35, len(expected))
        self.assertEqual(len(expected) + 1, result['calls'])
        self.assertEqual(set(expected), set(result['value']['details']))
        for name, detail in result['value']['details'].items():
            self.assertTrue(detail['valid'])
            self.assertEqual(DRAFT, detail['draft'])
            self.assertEqual(expected[name]['parameters'], detail['parameters'])
            self.assertEqual(expected[name]['description'], detail['description'])
            for field in ('category', 'returns', 'statusSemantics', 'limits', 'example'):
                self.assertTrue(detail[field], (name, field))
        self.assertIn('player,motion', result['value']['details']['get_player_state']['returns'])
        self.assertIn('waitStatus=timeout', result['value']['details']['await_task']['statusSemantics'])
        self.assertEqual({}, result['value']['details']['use_item']['parameters']['properties'])
        self.assertIn('scale', result['value']['details']['observe_scene']['parameters']['properties'])
        b.command.assert_called_once_with('get_movement')  # Normal Worker admission check.
        b.task_request.assert_not_called()

    async def test_worker_reports_missing_help_without_disclosing_unavailable_schema(self):
        b = self.runtime_bridge(BASE | {'server_ai_scene_v1'})
        result = await LuaRuntime(b).execute("""
            return {api('help',{name='mine_target'}), api('help',{name='missing'}),
                    api('help',{name=17}), api('help',{name='observe_scene'})}
        """)
        self.assertEqual('completed', result['status'])
        unavailable, missing, bad_arguments, scene = result['value']
        self.assertEqual({'valid': False, 'status': 'not_exposed'}, unavailable)
        self.assertEqual({'valid': False, 'status': 'unknown_api'}, missing)
        self.assertEqual('bad_argument', bad_arguments['status'])
        self.assertNotIn('scale', scene['parameters']['properties'])
        self.assertNotIn('SceneRegional', scene['returnsSchema']['$defs'])

    async def test_worker_round_trips_largest_single_help_schema(self):
        result = await LuaRuntime(self.runtime_bridge(FULL_ACTIONS)).execute(
            "return api('help',{name='stop_action'})", allow_actions=False)
        self.assertEqual('completed', result['status'], result)
        self.assertEqual(schema_for('stop_action', capabilities=FULL_ACTIONS), result['value']['returnsSchema'])

    async def test_help_uses_existing_call_budget_and_is_server_only(self):
        result = await LuaRuntime(self.runtime_bridge(BASE)).execute(
            "api('help'); return api('help')", limits=Limits(calls=1))
        self.assertEqual('call_limit', result['status'])
        self.assertEqual(1, result['calls'])
        self.assertEqual(0, result['actions'])
        client = await LuaRuntime(self.runtime_bridge(set())).execute("return api('help')")
        self.assertEqual('not_exposed', client['value']['status'])


class ResultRegistryTests(unittest.TestCase):
    def test_inventory_favorite_requires_requested_state(self):
        self.assertIn('set_inventory_favorite', operations_for_capabilities(FULL_ACTIONS))
        self.assertNotIn('set_inventory_favorite', operations_for_capabilities(
            FULL_ACTIONS - {'server_ai_inventory_favorite_v1'}))
        for desired in (True, False):
            goal = dict(sourceSlot=12, favorited=desired)
            evidence = dict(sourceSlot=12, typeId=8, stack=10, prefix=0,
                            beforeFavorite=not desired, afterFavorite=desired)
            self.assertTrue(atomic.verified_favorite(goal, evidence))
            self.assertTrue(atomic.verified_favorite(goal, evidence | dict(beforeFavorite=desired)))
            for change in (dict(afterFavorite=not desired), dict(afterFavorite=int(desired)),
                           dict(sourceSlot=13), dict(stack=0)):
                self.assertFalse(atomic.verified_favorite(goal, evidence | change))

    def test_drop_inventory_item_requires_capability_and_matching_ground_evidence(self):
        cap = 'server_ai_drop_item_v1'
        self.assertIn('drop_inventory_item', operations_for_capabilities(FULL_ACTIONS))
        self.assertNotIn('drop_inventory_item', operations_for_capabilities(FULL_ACTIONS - {cap}))
        goal = dict(sourceSlot=20, count=3, teammateRef='a' * 32)
        evidence = dict(sourceSlot=20, typeId=8, prefix=0, beforeStack=10, afterStack=7,
                        spawnedCount=3, entityRef='world-drop', teammateRef='a' * 32,
                        receiptStatus='unconfirmed')
        self.assertTrue(atomic.verified_drop(goal, evidence))
        for change in (dict(afterStack=8), dict(spawnedCount=2), dict(entityRef=''),
                       dict(teammateRef='b' * 32), dict(receiptStatus='received'), dict(sourceSlot=19)):
            with self.subTest(change=change):
                self.assertFalse(atomic.verified_drop(goal, evidence | change))

    def test_registry_index_and_full_help(self):
        # Shared/client names intentionally unavailable to Server AI, even with all capabilities.
        unavailable = {'attack_entity', 'drop_through_platform', 'get_player_status', 'get_world_state',
                       'observe_exploration', 'place_wall'}
        task_only = {'combat_attack', 'boss_encounter', 'collect_drops', 'place_item', 'build_place'}
        scoped = {'inspect_inventory', 'observe_scene', 'query_map', 'scan_entities'}
        registered = [entry['function']['name'] for entry in atomic.TOOLS] + sorted(program_tasks.NAMES)
        self.assertCountEqual(registered, set(registered))
        for caps in (BASE, FULL_ACTIONS):
            b = bridge(caps)
            definitions = {entry['function']['name']: entry['function'] for entry in server_definitions(b)}
            expected = (set(registered) - unavailable - task_only if caps == FULL_ACTIONS
                        else {'wait', 'stop_action', 'select_hotbar_slot'})
            self.assertEqual(expected, definitions.keys())
            rows = api_help(b, {})['categories']
            self.assertCountEqual(expected, [name for row in rows for name in row['apis']])
            for row in rows:
                self.assertEqual(len(row['apis']), len(row['signatures']))
            categories = {name: row['category'] for row in rows for name in row['apis']}
            for name in registered:
                with self.subTest(name=name, caps='extended' if caps == FULL_ACTIONS else 'base'):
                    doc = api_help(b, {'name': name})
                    partial = api_help(b, {'name': name, 'forArgs': {}})
                    if name not in expected:
                        self.assertEqual('not_exposed', doc['status'])
                        self.assertEqual('not_exposed', partial['status'])
                        continue
                    self.assertEqual('ok', doc['status'])
                    self.assertEqual(categories[name], doc['category'])
                    self.assertEqual(definitions[name]['description'], doc['description'])
                    self.assertTrue(doc['description'].strip())
                    self.assertEqual(definitions[name]['parameters'], doc['parameters'])
                    self.assertEqual(schema_for(name, capabilities=caps), doc['returnsSchema'])
                    self.assertTrue(doc['returns'] and doc['statusSemantics'])
                    self.assertTrue(doc['examples'] or doc['prerequisites'])
                    self.assertLessEqual(set(doc['related']), expected)
                    for example in doc['examples']:
                        refs = re.findall(r"""api\(\s*["'](\w+)["']""", example)
                        self.assertLessEqual(set(refs), expected)
                    self.assertEqual('ok' if name in scoped else 'bad_argument', partial['status'])
            if caps == FULL_ACTIONS:
                submit = api_help(b, {'name': 'submit_task'})['parameters']['properties']
                self.assertEqual(task_only | {'mine_target'}, set(submit['operation']['enum']))

    def test_guide_only_advertises_available_argument_help(self):
        for caps, names in [(BASE, set()), (BASE | {'server_ai_map_v1'}, {'query_map'}),
                            (FULL_ACTIONS, {'query_map', 'observe_scene', 'inspect_inventory', 'scan_entities'})]:
            guide, _ = server_contract(bridge(caps), compact=True)
            if names:
                self.assertIn('、'.join(sorted(names)) + ' 可用 forArgs', guide)
            else:
                self.assertNotIn('forArgs', guide)

    def test_completed_drop_batch_can_report_more_targets(self):
        from api_result_contracts import TASK_COMPLETION_PROOF

        check = Draft202012Validator(TASK_COMPLETION_PROOF['collect_drops'])
        evidence = dict(remaining=[], unavailable=[], pickupUnconfirmed=False,
                        truncated=True, outcome='partial', canContinue=True)
        task = dict(taskName='collect_drops', status='completed', phase='terminal',
                    result=dict(inputsReleased=True, effectVerified=True, evidence=evidence))
        self.assertTrue(check.is_valid(task))
        evidence['remaining'] = [{'reason': 'inventory_full'}]
        self.assertFalse(check.is_valid(task))
        evidence.update(remaining=[], canContinue=False)
        self.assertFalse(check.is_valid(task))

    def test_place_item_completion_requires_chest_storage_and_single_consumption(self):
        from api_result_contracts import TASK_COMPLETION_PROOF

        check = Draft202012Validator(TASK_COMPLETION_PROOF['place_item'])
        proof = dict(taskName='place_item', status='completed', phase='terminal', result=dict(
            inputsReleased=True, effectVerified=True, evidence=dict(kind='chest', chestVerified=True,
                placedByActor=True, targetPresent=True, targetChanged=False, consumedCount=1,
                nativePlacementEvents=1, uncertainNativeEffect=False)))
        self.assertTrue(check.is_valid(proof))
        proof['result']['evidence']['chestVerified'] = False
        self.assertFalse(check.is_valid(proof))
        proof['result']['evidence'].update(kind='wall', consumedCount=0)
        self.assertFalse(check.is_valid(proof))
        proof['result']['evidence']['consumedCount'] = 1
        self.assertTrue(check.is_valid(proof))

    def test_open_capabilities_and_self_contained_schemas(self):
        for caps in (BASE, BASE | {'server_ai_scene_v1'}, FULL - {'server_ai_program_tasks_v1'}, FULL):
            b = bridge(caps)
            definitions = {row['function']['name']: row['function'] for row in server_definitions(b)}
            self.assertEqual(set(), missing_for(definitions))
            self.assertNotIn('returnsSchema', json.dumps(api_help(b, {})))
            for name, definition in definitions.items():
                with self.subTest(name=name, capabilities=caps):
                    detail = api_help(b, {'name': name})
                    schema = detail['returnsSchema']
                    self.assertEqual(definition['parameters'], detail['parameters'])
                    self.assertEqual(schema_for(name, capabilities=caps), schema)
                    Draft202012Validator.check_schema(schema)
                    reachable = set()

                    def walk(node):
                        if isinstance(node, dict):
                            if '$ref' in node:
                                prefix = '#/$defs/'
                                self.assertTrue(node['$ref'].startswith(prefix))
                                target = node['$ref'].removeprefix(prefix)
                                if target not in reachable:
                                    reachable.add(target)
                                    walk(schema['$defs'][target])
                            for key, value in node.items():
                                if key != '$defs':
                                    walk(value)
                        elif isinstance(node, list):
                            for value in node:
                                walk(value)

                    walk(schema)
                    self.assertEqual(set(schema.get('$defs', {})), reachable)
        containers = atomic.CONTAINER_OPERATIONS | {'inspect_container'}
        self.assertTrue(containers.isdisjoint(definitions))
        p0 = bridge(FULL | {'server_ai_containers_v1'})
        enabled = bridge(FULL | {'server_ai_containers_v1', 'server_ai_container_transfer_v1', 'server_ai_container_batch_v1', 'server_ai_sorting_v1'})
        self.assertTrue((atomic.CONTAINER_TRANSFERS | atomic.CONTAINER_BATCHES | atomic.SORT_OPERATIONS).isdisjoint(p0.supported_atomic_operations))
        for name in containers:
            self.assertEqual('not_exposed', api_help(b, {'name': name})['status'])
            help_result = api_help(enabled, {'name': name})
            self.assertEqual('ok', help_result['status'])
            Draft202012Validator.check_schema(help_result['returnsSchema'])
        combat = bridge(FULL_ACTIONS)
        self.assertEqual(set(definitions) | set(combat.supported_atomic_operations), registered_names())
        self.assertEqual({'future_api'}, missing_for(set(definitions) | {'future_api'}))
        self.assertNotIn('Action_mine_tile', schema_for('stop_action', capabilities=BASE)['$defs'])
        copy = schema_for('observe_scene', capabilities=FULL)
        copy['$defs']['SceneLocal']['required'].clear()
        self.assertTrue(schema_for('observe_scene', capabilities=FULL)['$defs']['SceneLocal']['required'])
        self.assertIn('return api("help")', server_contract(b)[0])
        self.assertNotIn('"$schema"', server_contract(b)[0])

    def test_open_operation_with_missing_contract_has_explicit_error(self):
        with patch('api_result_contracts.schema_for', side_effect=KeyError):
            self.assertEqual({'valid': False, 'status': 'contract_missing', 'name': 'wait'},
                             api_help(bridge(BASE), {'name': 'wait'}))


class ManifestTests(unittest.TestCase):
    def test_compact_guide_and_help_share_projected_signatures(self):
        for caps in (FULL_ACTIONS, FULL_ACTIONS - {'server_ai_scene_region_v1'}):
            backend = bridge(caps)
            guide, _ = server_contract(backend, compact=True)
            self.assertIn('没看到所需参数或返回字段时，先查该函数的 help，不要猜', guide)
            signatures = {}
            for row in api_help(backend, {})['categories']:
                self.assertEqual(len(row['apis']), len(row['signatures']))
                for name, signature in zip(row['apis'], row['signatures']):
                    self.assertTrue(signature.startswith(name + '({'))
                    self.assertIn(signature, guide)
                    signatures[name] = signature
            self.assertIn('mode:"selected"|"hotbar"|"item"|"full"|"slots"', signatures['inspect_inventory'])
            self.assertIn('slots?:integer[]', signatures['inspect_inventory'])
            self.assertEqual('server_ai_scene_region_v1' in caps, 'width?:integer' in signatures['observe_scene'])

    def test_opening_requires_capability_and_fresh_slot(self):
        old = bridge(FULL - {'server_ai_open_item_v1'})
        self.assertEqual('not_exposed', api_help(old, {'name':'open_inventory_item'})['status'])
        modern = bridge(set(old.hello['capabilities']) | {'server_ai_open_item_v1'})
        help_row = api_help(modern, {'name':'open_inventory_item'})
        self.assertEqual('ok', help_row['status'])
        self.assertEqual({'sourceSlot','sourceToken'}, set(help_row['parameters']['required']))

    def test_hammer_requires_handshake_and_native_effect(self):
        base = bridge(FULL)
        modern = bridge(FULL | {'server_ai_hammer_v1'})
        for name in ('mine_wall','hammer_tile'):
            self.assertEqual('not_exposed',api_help(base,{'name':name})['status'])
            row = api_help(modern,{'name':name})
            self.assertEqual('ok',row['status'])
            self.assertEqual({'x','y'},set(row['parameters']['required']))
            self.assertIn('HammerEvidence',row['returnsSchema']['$defs'])
            action = dict(operation=name,targetTile=dict(x=12,y=15))
            wall = name == 'mine_wall'
            cell = dict(hasTile=not wall,tileType=0 if wall else 1,wallType=4 if wall else 0,
                        halfBlock=False,slope=0,actuated=False)
            proof = dict(x=12,y=15,before=cell,after={**cell,'wallType':0,'halfBlock':not wall},
                         toolType=196,hammerPower=25,strikes=2,effectObserved=True)
            self.assertTrue(atomic.verified_hammer(action,proof))
            for mismatch in (dict(x=13),dict(after=cell),dict(effectObserved=False),
                             dict(after={**proof['after'],'tileType':22}),dict(strikes=0)):
                self.assertFalse(atomic.verified_hammer(action,{**proof,**mismatch}))

    def test_bed_interaction_requires_matching_personal_spawn(self):
        goal = {'target': dict(typeId=79, x=20, y=30)}
        setting = dict(kind='bed', x=20, y=30, bedSpawnX=21, bedSpawnY=31,
                       beforeActive=False, afterActive=True, beforeSpawnX=-1, beforeSpawnY=-1,
                       afterSpawnX=21, afterSpawnY=31)
        self.assertTrue(atomic.verified_interaction(goal, setting))
        clearing = setting | dict(beforeActive=True, afterActive=False, beforeSpawnX=21, beforeSpawnY=31,
                                  afterSpawnX=-1, afterSpawnY=-1)
        self.assertTrue(atomic.verified_interaction(goal, clearing))
        self.assertFalse(atomic.verified_interaction(goal, setting | dict(afterSpawnY=30)))
        self.assertFalse(atomic.verified_interaction(goal, clearing | dict(beforeSpawnX=22)))

    def test_interact_tile_has_backend_specific_inputs(self):
        old = bridge(FULL)
        modern = bridge(FULL | {'server_ai_interact_tile_v1'})
        self.assertEqual('not_exposed', api_help(old, {'name':'interact_tile'})['status'])
        row = api_help(modern, {'name':'interact_tile'})
        self.assertEqual(['target'], row['parameters']['required'])
        self.assertIn('TileInteractionEvidence', row['returnsSchema']['$defs'])
        self.assertEqual({'x':1,'y':2}, atomic.validate('interact_tile', {'x':1,'y':2}))
        with self.assertRaises(ValueError):
            atomic.validate('interact_tile', {'x':1,'y':2}, server=True)
        goal = {'target': {'x':21,'y':10,'typeId':132}}
        proof = dict(x=21,y=10,kind='lever',beforeActive=False,afterActive=True)
        self.assertTrue(atomic.verified_interaction(goal, proof))
        for mismatch in (dict(x=20), dict(kind='switch'), dict(afterActive=False)):
            self.assertFalse(atomic.verified_interaction(goal, {**proof, **mismatch}))

    def test_doors_require_handshake(self):
        old = bridge(FULL - {'server_ai_doors_v1'})
        modern = bridge(set(old.hello['capabilities']) | {'server_ai_doors_v1'})
        for name in ('open_door', 'close_door'):
            self.assertEqual('not_exposed', api_help(old, {'name': name})['status'])
            help_row = api_help(modern, {'name': name})
            self.assertEqual('ok', help_row['status'])
            self.assertEqual({'x', 'y'}, set(help_row['parameters']['required']))

    def test_material_plan_help_and_capability(self):
        name = 'plan_materials'
        self.assertIn(name, atomic.READS)
        self.assertEqual('not_exposed', api_help(bridge(FULL), {'name': name})['status'])
        help_doc = api_help(bridge(FULL | {'server_ai_material_plan_v1'}), {'name': name})
        validator = Draft202012Validator(help_doc['parameters'])
        self.assertTrue(validator.is_valid({'typeId': 4956, 'count': 2}))
        for invalid in ({'count': 2}, {'typeId': 1}, {'typeId': 0, 'count': 1}, {'typeId': 1, 'count': 0}):
            self.assertFalse(validator.is_valid(invalid))

    def test_item_queries_are_capability_gated_reads(self):
        for query in ('item_info', 'item_sources'):
            with self.subTest(query=query):
                name = 'query_' + query
                self.assertIn(name, atomic.READS)
                self.assertEqual('not_exposed', api_help(bridge(FULL), {'name': name})['status'])
                help_doc = api_help(bridge(FULL | {'server_ai_' + query + '_v1'}), {'name': name})
                validator = Draft202012Validator(help_doc['parameters'])
                self.assertTrue(validator.is_valid({'typeId': 193}))
                for invalid in ({}, {'typeId': 0}, {'typeId': '193'}, {'typeId': 2**31}, {'typeId': 1, 'x': 0}):
                    self.assertFalse(validator.is_valid(invalid))
                Draft202012Validator.check_schema(help_doc['returnsSchema'])

    def test_pylon_capability_distinguishes_queries_and_actions(self):
        for caps, expected in ((BASE,set()), (BASE | {'server_ai_pylons_v1'},{'list_pylons'}),
                               (FULL_ACTIONS,{'list_pylons','teleport_to_pylon'})):
            self.assertEqual(exposed_operations(bridge(caps)) & {'list_pylons','teleport_to_pylon'}, expected)
            for name in expected:
                Draft202012Validator.check_schema(schema_for(name))

    def test_regroup_capability_controls_atoms_and_boss_argument(self):
        old = bridge(FULL | {'server_ai_boss_encounter_v1'})
        modern = bridge(set(old.hello['capabilities']) | {'server_ai_regroup_v1'})
        for name in ('list_teammates', 'set_team', 'teleport_to_teammate'):
            self.assertEqual('not_exposed', api_help(old, {'name':name})['status'])
            self.assertEqual('ok', api_help(modern, {'name':name})['status'])
        for backend, allowed in ((old,False),(modern,True)):
            parameters=api_help(backend, {'name':'submit_task'})['parameters']
            self.assertEqual(allowed, 'regroupTeammateRef' in json.dumps(parameters))

    def test_boss_strategy_requires_new_handshake(self):
        import program_tasks
        old=bridge(FULL | {'server_ai_boss_encounter_v1','server_ai_boss_members_v1'})
        modern=bridge(set(old.hello['capabilities']) | {'server_ai_boss_strategy_v1'})
        self.assertEqual('not_exposed',api_help(old,{'name':'discover_bosses'})['status'])
        self.assertNotIn('retreatTo',api_help(old,{'name':'cancel_task'})['parameters']['properties'])
        for compact in (False,True):
            prompt,_=server_contract(old,compact=compact)
            self.assertNotIn('discover_bosses',prompt)
            self.assertNotIn('retreatTo',prompt)
        self.assertIn('retreatTo',api_help(modern,{'name':'cancel_task'})['parameters']['properties'])
        self.assertEqual('ok',api_help(modern,{'name':'discover_bosses'})['status'])
        self.assertIn('discover_bosses',api_help(modern,{'name':'submit_task'})['description'])
        with self.assertRaises(ValueError):
            program_tasks.validate('cancel_task',{'taskId':'a'*32,'retreatTo':{'feetX':True,'feetY':35}})

    def test_boss_members_help_and_terminal_evidence(self):
        caps=FULL | {'server_ai_boss_encounter_v1'}
        self.assertNotIn('毁灭者头',api_help(bridge(caps),{'name':'submit_task'})['description'])
        self.assertIn('毁灭者头',api_help(bridge(caps | {'server_ai_boss_members_v1'}),{'name':'submit_task'})['description'])
        red={'TypeId':125,'EntityRef':'red'}
        green={'TypeId':126,'EntityRef':'green'}
        proof={'seed':red,'startedUpdate':2,'rootDeathUpdate':15,'deathProof':[{'member':red,'update':12}]}
        self.assertFalse(atomic.verified_boss_deaths(proof))
        proof['deathProof'].append({'member':green,'update':15})
        self.assertTrue(atomic.verified_boss_deaths(proof))
        proof['deathProof'][1]['member']=red
        self.assertFalse(atomic.verified_boss_deaths(proof))
        self.assertFalse(atomic.verified_boss_deaths({},members_required=True))

    def test_boss_encounter_requires_capability_and_task_entry(self):
        import program_tasks
        for enabled in (False, True):
            caps = FULL | ({'server_ai_boss_encounter_v1'} if enabled else set())
            definitions = {d['function']['name']: d['function'] for d in server_definitions(bridge(caps))}
            self.assertNotIn('boss_encounter', definitions)
            operations = definitions['submit_task']['parameters']['properties']['operation']['enum']
            self.assertEqual(enabled, 'boss_encounter' in operations)
        with self.assertRaises(ValueError):
            program_tasks.validate('submit_task', {'operation': 'boss_encounter',
                                   'arguments': {'entityRef': 'root', 'durationSeconds': 601}})

    def test_combat_configuration_in_prompt_and_help(self):
        legacy = bridge(FULL | {'server_ai_combat_v1','server_ai_combat_primary_v1'})
        modern = bridge(set(legacy.hello['capabilities']) | {'server_ai_combat_secondary_v1',
                        'server_ai_combat_summon_loadout_v1','server_ai_combat_inventory_v1'})
        with_button=bridge(set(modern.hello['capabilities']) | {'server_ai_weapon_button_v1'})
        for current,expected in ((modern,False),(with_button,True)):
            variants=api_help(current,{'name':'submit_task'})['parameters']['properties']['arguments']['anyOf']
            self.assertEqual(any('button' in v['properties'] for v in variants),expected)
        for compact in (False,True):
            guide,_ = server_contract(modern,compact=compact)
            for term in ('combatCandidates','task_interrupt_combat','configure_combat_loadout','整份替换'):
                self.assertIn(term,guide)
        info = api_help(modern,{'name':'configure_combat_loadout'})
        self.assertEqual(set(info['parameters']['properties']),{'primary','secondary','summon'})
        self.assertIn('expectedTypeId=row.typeId',info['example'])
        self.assertIn('combatCandidates',api_help(modern,{'name':'inspect_inventory'})['description'])
        previous = api_help(legacy,{'name':'configure_combat_loadout'})
        self.assertNotIn('combatCandidates',previous['example'])
        self.assertNotEqual(previous['example'],'return api("configure_combat_loadout",{})')

    def test_persistent_navigation_follows_navigation_capability(self):
        for enabled in (False, True):
            caps = FULL - {'server_ai_navigation_v1'}
            if enabled:
                caps.add('server_ai_navigation_v1')
            tools = {row['function']['name']: row['function'] for row in server_definitions(bridge(caps))}
            operations = tools['submit_task']['parameters']['properties']['operation']['enum']
            self.assertEqual(enabled, 'navigate_to' in operations)
        args = dict(operation='navigate_to', arguments=dict(x=100000, y=3200))
        self.assertEqual(program_tasks.validate('submit_task', args), args)

    def test_place_item_is_capability_gated_and_only_callable_as_task(self):
        import program_tasks

        def definitions(caps):
            return {row['function']['name']: row['function'] for row in server_definitions(bridge(caps))}

        disabled = definitions(FULL)
        enabled = definitions(FULL | {'server_ai_place_item_v1'})
        self.assertNotIn('place_item', enabled)
        self.assertNotIn('place_item', disabled['submit_task']['parameters']['properties']['operation']['enum'])
        self.assertIn('place_item', enabled['submit_task']['parameters']['properties']['operation']['enum'])
        self.assertEqual(program_tasks.validate('submit_task', {
            'operation': 'place_item', 'arguments': {'x': 25, 'y': 40},
        })['arguments'], {'x': 25, 'y': 40})
        with self.assertRaises(ValueError):
            program_tasks.validate('submit_task', {'operation': 'place_item',
                'arguments': {'x': 25, 'y': 40, 'slot': 2}})

    def test_equipment_and_rope_contract_follows_capability_whitelist(self):
        extra = {'server_ai_equipment_v1', 'server_ai_place_rope_v1', 'server_ai_move_on_rope_v1'}
        operations = {'equip_item', 'unequip_item', 'place_rope', 'move_on_rope'}
        for caps in (BASE | extra, BASE | extra | {'server_ai_actions_v1'}):
            b = bridge(caps)
            manifest = capability_manifest(b)
            prompt, _ = server_contract(b)
            _, definitions, _ = contract_parts(prompt)
            expected = b.supported_atomic_operations
            self.assertEqual(expected, {d['function']['name'] for d in definitions})
            self.assertEqual(expected, {op for row in manifest['categories'] for op in row['callableApis']})
            gear = next(row for row in manifest['categories'] if row['category'] == '功能装备变更')
            self.assertEqual({'equip_item', 'unequip_item'} & expected, set(gear['callableApis']))
            self.assertEqual(operations if 'server_ai_actions_v1' in caps else set(), operations & expected)

    def test_autonomous_navigation_is_gated_and_takes_only_a_destination(self):
        capability = {'server_ai_navigation_v1'}
        self.assertNotIn('navigate_to', exposed_operations(bridge(BASE | capability)))
        self.assertNotIn('navigate_to', exposed_operations(bridge(BASE | {'server_ai_actions_v1'})))
        prompt, _ = server_contract(bridge(BASE | capability | {'server_ai_actions_v1'}))
        _, definitions, _ = contract_parts(prompt)
        schema = next(t['function']['parameters'] for t in definitions if t['function']['name'] == 'navigate_to')
        self.assertEqual({'x', 'y'}, set(schema['required']))
        self.assertEqual({'x', 'y'}, set(schema['properties']))

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
                                ('server_ai_tasks_v1', 'mine_target'),
                                ('landing_region_navigation_v1', 'navigate_to_landing_region')):
                    if cap in caps: expected.add(op)
                if 'server_ai_inventory_v1' in caps:
                    expected |= {'transfer_inventory_item', 'swap_inventory_slots', 'hold_inventory_item'}
            self.assertEqual(expected, b.supported_atomic_operations)
            prompt, tools = server_contract(b)
            manifest_text, definitions, reference = contract_parts(prompt)
            manifest = capability_manifest(b)
            self.assertEqual(expected, {d['function']['name'] for d in definitions})
            self.assertEqual(expected, {op for c in manifest['categories'] for op in c['callableApis']})
            self.assertEqual(['execute_lua', 'read_skill'], [t['function']['name'] for t in tools])
            for c in manifest['categories']:
                if c['category']=='背包变更' and c['callableApis']:
                    self.assertNotIn('不在服务器接口内',c['boundary'])
                self.assertIn(' | '.join((c['category'], c['status'], ','.join(c['callableApis']) or '—', ','.join(c['notExposed']) or '—')), manifest_text)
                self.assertFalse(set(c['notExposed']) & expected)
                reads = set(c.get('relatedReadOnlyApis', ()))
                actions = set(c.get('relatedActionApis', ()))
                self.assertLessEqual(reads, expected & atomic.READS)
                self.assertLessEqual(actions, expected - atomic.READS)
                self.assertNotIn('alternatives', c)
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
        b = bridge(BASE | set(OPTIONAL) | {'server_ai_scene_region_v1', 'server_ai_general_operations_v1'})
        manifest, definitions, _ = contract_parts(server_contract(b)[0])
        defs = {t['function']['name']: t['function'] for t in definitions}
        for name, f in defs.items():
            if name == 'use_item':
                self.assertEqual({}, f['parameters']['properties'])
            elif name == 'mine_target':
                expected = dict(atomic.SCHEMAS[name]['properties'])
                expected.pop('cluster')
                self.assertEqual(expected, f['parameters']['properties'])
            else:
                self.assertEqual(atomic.SCHEMAS[name], f['parameters'])
        for name, facts in {
            'move_input': ('jumpHoldUpdates', 'X/Y', '不要求导航profile', 'unknown'),
            'use_item': ('ItemCheck', 'ModItem', '具体治疗', '需另行观察'),
            'place_tile': ('createTile', 'placeStyle', '原生放置范围', 'place_object'),
            'place_object': ('TileObjectData', 'Anchor', '整组帧', '消耗'),
            'craft_recipe': ('0..57', '特殊回调', '箱子'),
            'navigate_to_landing_region': ('64px', '斜坡支撑', '0.5px', 'evidence.navigation.BlockingTiles', 'X/Y/ContentKey及Basis'),
        }.items():
            for fact in facts:self.assertIn(fact, defs[name]['description'])
        old = bridge(BASE | set(OPTIONAL))
        _, old_definitions, _ = contract_parts(server_contract(old)[0])
        old_defs = {t['function']['name']: t['function']['description'] for t in old_definitions}
        self.assertIn('默认样式的原版工作台', old_defs['place_object'])
        self.assertIn('物品使用范围受限', old_defs['use_item'])
        for category in ('直接身体输入', '目标导航', '物品与战斗', '建造', '制作'):
            self.assertEqual('Limited', next(c for c in capability_manifest(b)['categories'] if c['category']==category)['status'])
        before = server_contract(b)
        before[1][0]['function']['parameters']['properties']['source']['type']='number'
        self.assertEqual('string', server_contract(b)[1][0]['function']['parameters']['properties']['source']['type'])

    def test_related_observation_is_not_an_operation_alternative(self):
        for caps in (BASE, BASE | {'server_ai_reads_v1'}, BASE | (set(OPTIONAL) - {'server_ai_inventory_v1'})):
            with self.subTest(capabilities=caps):
                b = bridge(caps)
                manifest = capability_manifest(b)
                rows = {c['category']: c for c in manifest['categories']}
                inventory = rows['背包变更']
                interaction = rows['世界交互']
                if 'server_ai_reads_v1' in caps:
                    self.assertEqual(['inspect_inventory'], inventory['relatedReadOnlyApis'])
                    self.assertEqual(['select_hotbar_slot'], inventory['relatedActionApis'])
                else:
                    self.assertEqual([], inventory['relatedReadOnlyApis'])
                if 'server_ai_perception_v1' in caps:
                    self.assertIn('inspect_tile', interaction['relatedReadOnlyApis'])
                self.assertEqual([], interaction['relatedActionApis'])
                text, definitions, _ = contract_parts(server_contract(b)[0])
                self.assertNotIn('替代（非等价）', text)
                if 'server_ai_reads_v1' in caps:
                    self.assertIn('相关只读能力（不能替代操作）：inspect_inventory', text)
                self.assertEqual(set(exposed_operations(b)), {d['function']['name'] for d in definitions})

    def test_runtime_time_reference_is_accumulated_lua_only(self):
        text = runtime_reference(set())
        for fact in ('整轮累计Lua执行时间上限为60秒', '包含Lua期间的API等待',
                     '不包含LLM推理及模型请求耗时', '多份程序不重置'):
            self.assertIn(fact, text)
        self.assertNotIn('8次变更尝试、60秒', text)

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
