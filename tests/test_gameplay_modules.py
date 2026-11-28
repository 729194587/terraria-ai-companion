"""Production Worker and host budget/terminal handling with deterministic API receipts."""
import json
from pathlib import Path
import re
import unittest
from unittest.mock import AsyncMock, Mock, patch

import test_server_ai_contract as contract_tests
from test_server_ai_contract import FULL, bridge
from api_result_contracts import schema_for
from lua_runtime import LuaRuntime, Limits
import atomic_operations as atomic
import lua_modules
from server_ai_contract import api_help, server_definitions
from lua_agent import server_contract


CAPS = FULL | {'server_ai_build_place_v1','server_ai_build_diff_v1','server_ai_housing_inspect_v1',
               'server_ai_containers_v1','server_ai_npc_housing_v1', 'server_ai_merchant_shop_v1', 'server_ai_merchant_purchase_v1',
               'server_ai_container_transfer_v1','server_ai_container_batch_v1','server_ai_container_amount_v1'}


def recipe(token, *, available=True, space=True):
    return dict(supported=True, available=available, hasOutputSpace=space, recipeId=7, recipeToken=token,
                output=dict(typeId=8,count=3), reason=None if available else 'missing_crafting_station',
                stations=[dict(adjacent=available)], ingredients=[dict(item=dict(typeId=9,count=1))])


def query(items, *, truncated=False):
    return dict(valid=True, available=True, data=dict(items=items,truncated=truncated))


def read_data(**data):
    return dict(valid=True, available=True, data=data)


def chest_view(stacks, token, *, x=10):
    slots = [dict(slot=i, typeId=8, stack=n, prefix=0, maxStack=99, slotToken=token*64)
             for i, n in enumerate(stacks)]
    return read_data(status='open', container=dict(x=x, y=20, typeId=21, chestIndex=7,
                     slots=slots, containerToken=token*64))


def chest_lookup():
    return [read_data(status='ok', typeId=8), read_data(
        cell=dict(knowledge='read', hasTile=True, tileType=21, frameX=18, frameY=18),
        preflight=dict(withinConservativeReach=True))]


class ModuleTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        self.bridge = contract_tests.ApiHelpTests.runtime_bridge(CAPS)
        self.bridge.close = Mock()
        self.runtime = LuaRuntime(self.bridge)

    async def run_lua(self, source, replies, **options):
        calls = []
        async def dispatch(bridge, name, args, *unused, **kw):
            calls.append((name,args))
            reply = replies[len(calls)-1]
            if reply.get('dispatchState') == 'not_admitted':
                kw['lifecycle']['dispatchState'] = 'not_admitted'
            return reply
        with patch('atomic_operations.dispatch', side_effect=dispatch):
            result = await self.runtime.execute(source, **options)
        return result,calls

    async def test_container_find_capability_is_independent_of_transfers(self):
        source = 'return {search=type(terraria.container.find), take=type(terraria.container.take)}'
        for caps, expected in [
            (CAPS, ('nil', 'function')),
            ({'server_ai_atomic_v1', 'server_ai_container_search_v1'}, ('function', 'nil')),
            (CAPS | {'server_ai_container_search_v1'}, ('function', 'function')),
        ]:
            b = contract_tests.ApiHelpTests.runtime_bridge(caps)
            result = await LuaRuntime(b).execute(source)
            self.assertEqual(dict(zip(('search', 'take'), expected)), result['value'])
            self.assertEqual(expected[0] == 'function', 'terraria.container.find(' in lua_modules.index(b))
            prompt, _ = server_contract(b, compact=True)
            help_text = api_help(b, {'module': 'container'})['reference']
            self.assertEqual(expected[0] == 'function', '按物品查附近箱子及各箱数量' in prompt)
            self.assertEqual(expected[1] == 'function', '按物品和数量存取指定箱子' in prompt)
            self.assertEqual(expected[0] == 'function', 'terraria.container.find(' in help_text)
            self.assertEqual(expected[1] == 'function', 'terraria.container.take(' in help_text)

    async def test_take_spans_stacks_with_one_transfer_and_closes(self):
        opened = dict(valid=True, status='completed', completed=True, evidence=dict(chestIndex=7))
        moved = dict(valid=True, completed=True, status='completed', reason='destination_full', effectVerified=True,
                     evidence=dict(movedCount=5, moves=[dict(chestIndex=7,typeId=8,count=n) for n in (2,3)]))
        replies = [*chest_lookup(), read_data(status='unavailable', reason='container_not_open'), opened,
                   chest_view([2,10], 'a'), moved, chest_view([0,7], 'b'),
                   dict(valid=True, status='completed', completed=True)]
        result, calls = await self.run_lua('return terraria.container.take({x=11,y=21},"Terraria/Torch",12)', replies)
        self.assertEqual('completed', result['status'], result)
        self.assertEqual(dict(x=10,y=20,requested=12,moved=5,remaining=7,status='partial',reason='destination_full'),
                         result['value'])
        self.assertEqual([('transfer_container_amount', dict(direction='take',typeId=8,count=12,containerToken='a'*64))],
                         [c for c in calls if c[0] == 'transfer_container_amount'])
        self.assertEqual(1, sum(op == 'inspect_inventory' for op,_ in calls))
        self.assertEqual(('close_container',{'containerToken':'b'*64}), calls[-1])

    async def test_container_does_not_mutate_another_open_chest(self):
        source = 'return terraria.container.put({x=11,y=21},"Terraria/Torch",12)'
        result, calls = await self.run_lua(source, [*chest_lookup(), chest_view([5], 'a', x=12)])
        self.assertEqual('different_container_open',result['value']['reason'])
        self.assertEqual(0, result['actions'])

    async def test_container_reserves_close_on_limited_budget(self):
        replies = [*chest_lookup(), read_data(status='unavailable', reason='container_not_open')]
        result, calls = await self.run_lua('return terraria.container.take({x=11,y=21},"Terraria/Torch",12)',
                                           replies, limits=Limits(actions=2))
        self.assertEqual(('insufficient_budget',0,12), tuple(result['value'][k] for k in ('reason','moved','remaining')))
        self.assertEqual('inspect_container', calls[-1][0])

    async def test_container_unknown_batch_is_not_replayed(self):
        unknown = dict(valid=False, completed=False, status='unknown',reason='container_batch_changed_before_confirmation')
        replies = [*chest_lookup(), chest_view([2,10], 'a'), unknown]
        result, calls = await self.run_lua('return terraria.container.take({x=11,y=21},"Terraria/Torch",12)', replies)
        self.assertEqual('action_unknown', result['status'], result)
        self.assertEqual(('unknown',0,12),tuple(result['moduleResult'][k] for k in ('status','moved','remaining')))
        self.assertEqual('transfer_container_amount', calls[-1][0])

    async def test_container_closes_after_confirmed_admission_refusal(self):
        opened = dict(valid=True, status='completed', completed=True, evidence=dict(chestIndex=7))
        refused = dict(valid=False, status='failed', completed=False, reason='action_busy', dispatchState='not_admitted')
        replies = [*chest_lookup(), read_data(status='unavailable', reason='container_not_open'), opened,
                   chest_view([2,10], 'a'), refused, chest_view([2,10], 'b'),
                   dict(valid=True, status='completed', completed=True)]
        result, calls = await self.run_lua('return terraria.container.take({x=11,y=21},"Terraria/Torch",12)', replies)
        self.assertEqual(('unavailable',0,'action_busy'), tuple(result['value'][k] for k in ('status','moved','reason')))
        self.assertEqual(('close_container',{'containerToken':'b'*64}), calls[-1])

    async def test_talk_to_does_not_follow_replacement_in_same_slot(self):
        caps = CAPS | {'server_ai_npc_dialogue_v1'}
        self.assertNotIn('npc', [m.name for m in lua_modules.available(bridge(caps))])
        self.bridge = contract_tests.ApiHelpTests.runtime_bridge(caps | {'server_ai_general_npc_dialogue_v1'})
        self.runtime = LuaRuntime(self.bridge)
        def npc(ref, distance):
            return dict(slot=2, typeId=18, distancePixels=distance, details=dict(
                entityRef=ref, contentKey='Terraria/Nurse', bottomWorldPixels=dict(x=500,y=320)))
        result, calls = await self.run_lua('return terraria.npc.talk_to("Terraria/Nurse")', [
            query([npc('first', 180)]), dict(valid=True, status='completed', completed=True),
            query([npc('replacement', 10)])])
        self.assertEqual('completed', result['status'], result)
        self.assertEqual('target_not_observed', result['value']['reason'])
        self.assertEqual('first', result['value']['entityRef'])
        self.assertEqual(['scan_entities', 'navigate_to', 'scan_entities'], [n for n, _ in calls])

    async def test_give_preserves_cargo_and_returns_only_verified_drop(self):
        caps = CAPS | {'server_ai_nearby_handoff_v1', 'server_ai_regroup_v1', 'server_ai_drop_item_v1'}
        self.runtime = LuaRuntime(contract_tests.ApiHelpTests.runtime_bridge(caps))
        target = 'a' * 32
        for key, near, has_potion, requested, expected in [('Terraria/Torch', True, True, 12, 'wormhole'),
                                    ('Terraria/Torch', True, False, 12, 'none'),
                                    ('Terraria/Torch', False, False, 12, 'walk'),
                                    ('Terraria/WormholePotion', False, True, 12, 'walk'),
                                    ('Terraria/WormholePotion', True, True, 2, 'wormhole'),
                                    ('Terraria/Torch', False, True, 12, 'wormhole')]:
            with self.subTest(key=key, near=near):
                item = dict(slot=4, typeId=8, stack=3, prefix=0, maxStack=9999, favorited=False, inventoryToken='b'*64)
                def data(**fields):
                    return dict(valid=True, available=True, data=fields)
                team = data(hasWormholePotion=has_potion, players=[dict(teammateRef=target, handoff=dict(x=200,y=128,inRange=near))])
                found = data(status='ok', contentKey=key, typeId=8, slots=[dict(slot=n) for n in (4,5,51)])
                replies = [team, found, data(inventory=[item, dict(item,slot=5,stack=80,favorited=True)])]
                if expected != 'none':
                    replies.append(dict(status='completed', valid=True, completed=True))
                spent_cargo = key == 'Terraria/WormholePotion' and expected == 'wormhole'
                if spent_cargo:
                    item = dict(item, stack=2, inventoryToken='c'*64)
                    replies.append(data(inventory=[item]))
                replies.append(dict(status='completed', valid=True, completed=True,
                               evidence=dict(spawnedCount=item['stack'], entityRef='drop-1')))
                result, calls = await self.run_lua(f'return terraria.inventory.give("{target}", "{key}", {requested})', replies)
                self.assertEqual('completed', result['status'], result)
                self.assertEqual(('completed' if spent_cargo else 'partial', expected, item['stack'], 'drop-1'), tuple(result['value'][k] for k in
                                 ('status','approach','dropped','entityRef')))
                self.assertEqual([4,5], calls[2][1]['slots'])
                self.assertEqual(expected == 'walk', any(op == 'navigate_to' for op,_ in calls))
                self.assertEqual(expected == 'wormhole', any(op == 'teleport_to_teammate' for op,_ in calls))
                self.assertEqual(('drop_inventory_item', dict(sourceSlot=4, sourceToken=item['inventoryToken'], count=item['stack'],
                                 teammateRef=target, nearbyOnly=True)), calls[-1])
                self.assertEqual(1, sum(op == 'list_teammates' for op,_ in calls))

    async def test_give_drops_from_original_slots_and_keeps_confirmed_count(self):
        caps = CAPS | {'server_ai_nearby_handoff_v1', 'server_ai_regroup_v1', 'server_ai_drop_item_v1'}
        self.bridge = contract_tests.ApiHelpTests.runtime_bridge(caps)
        self.bridge.close = Mock()
        self.runtime = LuaRuntime(self.bridge)
        target = 'a' * 32
        key = 'Terraria/Torch'
        first = dict(slot=4, stack=5, typeId=8, prefix=0, maxStack=9999, inventoryToken='b'*64)
        second = dict(first, slot=1, stack=9, inventoryToken='c'*64)
        slots = [dict(first, slot=0, prefix=1), second, first]
        team = read_data(hasWormholePotion=True, players=[dict(teammateRef=target, handoff=dict(inRange=True))])
        found = read_data(status='ok', contentKey=key, typeId=8, slots=slots)
        def thrown(n, ref):
            return dict(status='completed', valid=True, completed=True, evidence=dict(spawnedCount=n, entityRef=ref))
        for unknown in (False, True):
            last = dict(status='unknown', completed=False, valid=False, reason='drop_effect_unconfirmed') if unknown else thrown(7, 'two')
            result, calls = await self.run_lua(f'return terraria.inventory.give("{target}", "{key}", 12)', [
                team, found, read_data(selectedSlot=4, inventory=slots), dict(status='completed', completed=True, valid=True),
                thrown(5, 'one'), last])
            self.assertEqual(['list_teammates','inspect_inventory','inspect_inventory','teleport_to_teammate',
                              'drop_inventory_item','drop_inventory_item'], [op for op,_ in calls])
            self.assertEqual([(4, 5), (1, 7)], [(a['sourceSlot'], a['count']) for _,a in calls[-2:]])
            self.assertEqual('action_unknown' if unknown else 'completed', result['status'], result)
            report = result['moduleResult' if unknown else 'value']
            self.assertEqual(('unknown',5,'one') if unknown else ('completed',12,'two'),
                             (report['status'], report['dropped'], report['entityRef']))
            self.assertEqual([5] if unknown else [5,7], [d['spawnedCount'] for d in report['drops']])

    async def test_give_unavailable_teammate_and_older_server(self):
        caps = CAPS | {'server_ai_regroup_v1', 'server_ai_drop_item_v1'}
        old = contract_tests.ApiHelpTests.runtime_bridge(caps)
        self.assertNotIn('inventory', [m.name for m in lua_modules.available(old)])
        old_drop, = [d['function'] for d in server_definitions(old) if d['function']['name'] == 'drop_inventory_item']
        self.assertNotIn('nearbyOnly', old_drop['parameters']['properties'])
        self.runtime = LuaRuntime(contract_tests.ApiHelpTests.runtime_bridge(caps | {'server_ai_nearby_handoff_v1'}))
        target = 'a' * 32
        result, calls = await self.run_lua(f'return terraria.inventory.give("{target}", "Terraria/Torch", 1)', [
            dict(valid=True, available=True, data=dict(hasWormholePotion=True, players=[dict(teammateRef=target, handoff=None)]))])
        self.assertEqual('teammate_unavailable', result['value']['reason'], result)
        self.assertEqual(1, len(calls))

    async def test_equip_example_uses_fresh_source_and_head_tokens(self):
        example, = api_help(self.bridge, {'name':'equip_item'})['examples']
        item = dict(typeId=88, slot=5)
        head = dict(kind='head', enabled=True, slotId='vanilla:head', targetToken='a'*64)
        def bag(**data):
            return dict(valid=True, available=True, data=data)
        result, calls = await self.run_lua(example, [
            bag(inventory=[item], functionalEquipmentSlots=[head]),
            bag(inventory=[dict(item, inventoryToken='b'*64)]),
            dict(valid=True, status='completed', completed=True)])
        self.assertEqual('completed', result['status'], result)
        self.assertEqual(('equip_item', dict(sourceSlot=5, sourceToken='b'*64,
                         targetSlotId='vanilla:head', targetToken='a'*64)), calls[-1])

    async def test_full_help_teaches_reading_actual_result_fields(self):
        receipts = json.loads((Path(__file__).parent / 'fixtures/api_results/observations.json').read_text('utf8'))
        player = receipts['get_player_state']
        memory = receipts['query_map']['data']
        scan = receipts['scan_entities']
        empty = {**scan, 'data': {**scan['data'], 'status':'not_found', 'items':[],
                                  'matchedCount':0, 'returnedCount':0, 'truncated':False}}
        cases = [
            ('get_player_state', player, {'player':player['player'], 'motion':player['motion']}),
            ('query_map', receipts['query_map'], dict(row=memory['rows'][0], x=memory['originX'],
                 y=memory['originY'], cellSize=memory['cellSize'], knowledge=memory['knowledge'])),
            ('scan_entities', scan, dict(status=scan['data']['status'], first=scan['data']['items'][0],
                 matched=scan['data']['matchedCount'], truncated=scan['data']['truncated'])),
            ('scan_entities', empty, dict(status='not_found', matched=0, truncated=False)),
            ('scan_entities', {'valid':False, 'available':False}, {'valid':False, 'available':False}),
        ]
        for name, receipt, expected in cases:
            with self.subTest(name=name, status=expected.get('status')):
                source = f'return api("help",{{name="{name}"}}).examples[1]'
                help_run, calls = await self.run_lua(source, [])
                self.assertEqual([], calls)
                result, calls = await self.run_lua(help_run['value'], [receipt])
                self.assertEqual('completed', result['status'], result)
                self.assertEqual([name], [call[0] for call in calls])
                self.assertEqual(expected, result['value'])

    async def test_argument_error_supplies_executable_scoped_help(self):
        bad = 'api("observe_scene",{scale="local",width=45})'
        for source in ('return ' + bad, 'local ok,e=pcall(function() return ' + bad + ' end); return e'):
            run, calls = await self.run_lua(source, [])
            self.assertEqual([], calls)
            hint = run['recovery']
            if run['status'] == 'completed':
                self.assertEqual(hint['helpQuery'], run['value']['recovery']['helpQuery'])
            else:
                self.assertEqual('bad_argument', run['status'])
            self.assertIn('scale?:"local"', hint['signature'])
            self.assertNotIn('width', hint['signature'])
            result, calls = await self.run_lua(hint['helpQuery'], [])
            self.assertEqual([], calls)
            self.assertEqual('local', result['value']['mode'])
            self.assertNotIn('width', result['value']['parameters'])

    def test_conditional_read_parameters_match_admission(self):
        map_options = dict(centerX=0, centerY=0, width=4, height=4, cellSize=2)
        entity_options = dict(typeIds=[1], npcFilter='all')
        cases = [
            ('query_map', dict(mode='overview'), map_options, set()),
            ('query_map', dict(mode='region'), map_options, set(map_options)),
            ('scan_entities', dict(category='npc', radius=80), entity_options, set(entity_options)),
            ('scan_entities', dict(category='item', radius=80), entity_options, {'typeIds'}),
            ('scan_entities', dict(category='player', radius=80), entity_options, set()),
            ('scan_entities', dict(category='all', radius=80), entity_options, set()),
        ]
        for name, base, options, allowed in cases:
            summary = api_help(self.bridge, dict(name=name, forArgs=base))
            self.assertEqual(allowed, options.keys() & summary['parameters'].keys())
            for key, value in options.items():
                with self.subTest(name=name, base=base, key=key):
                    args = dict(base, **{key: value})
                    doc = api_help(self.bridge, dict(name=name, forArgs=args))
                    self.assertEqual(key in allowed, doc['valid'])
                    if key in allowed:
                        self.assertEqual(args, atomic.validate(name, args))
                    else:
                        with self.assertRaises(ValueError):
                            atomic.validate(name, args)

    def test_partial_read_help_preserves_missing_and_checks_supplied_values(self):
        args = dict(typeIds=[1])
        partial = api_help(self.bridge, dict(name='scan_entities', forArgs=args))
        self.assertEqual(['category', 'radius'], partial['missing'])
        self.assertEqual({'category': ['npc', 'item']}, partial['allowedWhen']['typeIds'])
        self.assertEqual([], partial['examples'])
        with self.assertRaises(ValueError):
            atomic.validate('scan_entities', args)
        for args in (dict(cellSize=3), dict(width=0), dict(mode=[]), dict(typo=True)):
            self.assertFalse(api_help(self.bridge, dict(name='query_map', forArgs=args))['valid'])

    async def test_read_help_examples_match_payload_fixtures(self):
        receipts = json.loads((Path(__file__).parent / 'fixtures/api_results/observations.json').read_text('utf8'))
        for name, args in [('query_map', dict(mode='overview')), ('query_map', dict(mode='region', width=4)),
                           ('scan_entities', dict(category='all', radius=80))]:
            with self.subTest(name=name, args=args):
                doc = api_help(self.bridge, dict(name=name, forArgs=args))
                receipt = receipts[name]
                data = receipt['data']
                self.assertLessEqual(set(doc['dataFields']['required']), data.keys())
                self.assertLessEqual(data.keys(), set(doc['dataFields']['required'] + doc['dataFields']['optional']))
                self.assertTrue(all(path.startswith('data.') for path in doc['paths']))
                result, calls = await self.run_lua(doc['example'], [receipt])
                self.assertEqual('completed', result['status'], result)
                self.assertEqual([(name, args)], calls)
                field = 'rows' if name == 'query_map' else 'status'
                self.assertIn('data.' + field, doc['paths'])
                self.assertEqual(data[field], result['value'][field])

    def test_inventory_mode_parameters_and_payload_branches(self):
        for mode in ('selected', 'hotbar', 'slots', 'item', 'full'):
            with self.subTest(mode=mode):
                doc = api_help(self.bridge, dict(name='inspect_inventory', forArgs=dict(mode=mode)))
                extra = [mode] if mode in ('slots', 'item') else []
                self.assertEqual(['mode', *extra], doc['required'])
                self.assertEqual(set(doc['required']), set(doc['parameters']))
                self.assertEqual(extra, doc['missing'])
                for key, value in dict(slots=[0], item='Terraria/Wood').items():
                    proposal = dict(mode=mode, **{key:value})
                    self.assertEqual(key == mode, api_help(self.bridge, dict(name='inspect_inventory', forArgs=proposal))['valid'])
                branches = doc['dataFields']
                if mode == 'item':
                    self.assertIn('count', branches['ok']['required'])
                    for status in ('ambiguous_item', 'unknown_item'):
                        self.assertIn('truncated', branches[status]['required'])
                        self.assertNotIn('count', branches[status]['required'])
                elif mode == 'full':
                    self.assertIn('functionalEquipmentSlots', branches['full']['required'])
                    self.assertNotIn('status', branches['full']['required'])
                else:
                    self.assertIn('inventory', branches['ok']['required'])
                    self.assertEqual(mode == 'slots', 'inventory_slot_out_of_range' in branches)

    async def test_inventory_help_counts_stacks_and_skips_empty_slots(self):
        bag = query([])
        bag['data']['inventory'] = [dict(empty=False, contentKey='Terraria/Wood', stack=3),
                                    dict(empty=True), dict(empty=False, contentKey='Terraria/Wood', stack=5)]
        help_args = dict(name='inspect_inventory', forArgs={'mode':'full'})
        example = api_help(self.bridge, help_args)['example']
        result, calls = await self.run_lua(example, [bag])
        self.assertEqual([('inspect_inventory', {'mode': 'full'})], calls)
        self.assertEqual({'Terraria/Wood': 8}, result['value'])

    async def test_usage_help_for_partial_item_and_non_ok_result(self):
        doc = api_help(self.bridge, dict(name='inspect_inventory', forArgs={'mode':'item'}))
        self.assertEqual(['item'], doc['missing'])
        self.assertEqual({'mode','item'}, set(doc['parameters']))
        self.assertIn('data.candidates', doc['paths'])
        receipt = dict(valid=True, available=True, data=dict(status='ambiguous_item', candidates=['a','b']))
        result, _ = await self.run_lua(doc['example'], [receipt])
        self.assertEqual(receipt['data'], result['value'])
        for args in ({'view':'full'}, {'mode':'full','slots':[0]}):
            self.assertFalse(api_help(self.bridge, dict(name='inspect_inventory', forArgs=args))['valid'])

    async def test_help_examples_forward_observed_fields(self):
        token, slot, chest = 'a'*64, 'b'*64, 'c'*64
        bag = query([])
        bag['data']['inventory'] = [dict(slot=0, stack=3, inventoryToken=token)]
        container = dict(valid=True, available=True, data=dict(status='open', container=dict(
            containerToken=chest, slots=[dict(slot=0, stack=2, slotToken=slot)])))
        npc = query([dict(typeId=17, details=dict(entityRef='merchant'))])
        price = dict(valid=True, available=True, data=dict(canPurchase=True, quoteToken=token))
        transfer = dict(inventorySlot=0, containerSlot=0, count=1, inventoryToken=token,
                        slotToken=slot, containerToken=chest)
        for name, reads, expected in (
                ('buy_npc_item', [npc,price], dict(entityRef='merchant',quoteToken=token)),
                ('deposit_container_item', [bag,container], transfer),
                ('withdraw_container_item', [bag,container], transfer),
                ('craft_recipe', [query([recipe(token)])], dict(recipeId=7,recipeToken=token,crafts=1))):
            with self.subTest(name=name):
                example, = api_help(self.bridge, dict(name=name))['examples']
                result,calls = await self.run_lua(example, [*reads, dict(valid=True,status='completed',completed=True)])
                self.assertEqual('completed', result['status'], result)
                self.assertEqual((name,expected), calls[-1])

    async def test_registry_matches_prompt_and_fresh_workers(self):
        for caps, expected in ((CAPS, ['container','crafting','building']),
                               (FULL,['crafting']), (set(),[])):
            with self.subTest(caps=caps):
                b = contract_tests.ApiHelpTests.runtime_bridge(caps)
                self.assertEqual(expected, [m.name for m in lua_modules.available(b)])
                prompt,_ = server_contract(b)
                for module in lua_modules.BUILTINS:
                    self.assertEqual(module.name in expected, 'terraria.'+module.name+'.' in prompt)
                result = await LuaRuntime(b).execute('local t={}; for k in pairs(terraria) do t[k]=true end; terraria.crafting=nil; return t')
                self.assertEqual(set(expected), set(result['value']))
        rebuilt,_ = await self.run_lua('return type(terraria.crafting.make)', [])
        self.assertEqual('function',rebuilt['value'])

    async def test_fresh_batches_and_candidates(self):
        def done(token, n):
            return dict(valid=True,completed=True,status='completed',evidence=dict(recipeId=7, recipeToken=token,
                        requestedCrafts=n, completedCrafts=n, outputTypeId=8,outputCountPerCraft=3,
                        inventoryVerified=True,producedCount=n*3))
        t1,t2 = 'a'*64,'b'*64
        result,calls = await self.run_lua('return terraria.crafting.make(8,61)', [
            query([recipe(t1,available=False)],truncated=True),query([recipe(t1)]),done(t1,20),
            query([recipe(t2)]),done(t2,1)])
        self.assertEqual('completed',result['status'],result)
        self.assertEqual((63,0),(result['value']['producedCount'],result['value']['remaining']))
        self.assertEqual([1,3], [i for i,(name,_) in enumerate(calls) if name=='query_recipes' and i])
        self.assertEqual([t1,t2],[a['recipeToken'] for op,a in calls if op=='craft_recipe'])
        self.assertEqual(1,calls[1][1]['offset'])

    async def test_missing_station_reports_requirements_across_pages_without_moving(self):
        row = recipe('a'*64, available=False)
        stations = [dict(typeId=18, contentKey='Terraria/WorkBenches', adjacent=False)]
        row['stations'] = stations
        # Even an older bridge's suggested landing must not trigger travel.
        row['stationTarget'] = {'x':264, 'y':320}
        result, calls = await self.run_lua('return terraria.crafting.make(8,3)',
                                           [query([row], truncated=True), query([])])
        self.assertEqual('completed', result['status'], result)
        report = result['value']
        self.assertEqual(('unavailable', 'missing_crafting_station', 3, stations),
                         (report['status'], report['reason'], report['remaining'], report['stations']))
        self.assertEqual(['query_recipes', 'query_recipes'], [op for op,_ in calls])
        self.assertEqual(0, result['actions'])

    async def test_station_loss_after_first_batch_preserves_remaining_quantity(self):
        token = 'a'*64
        batch = dict(valid=True, completed=True, status='completed', evidence=dict(
            recipeId=7, recipeToken=token, requestedCrafts=20, completedCrafts=20,
            outputTypeId=8, outputCountPerCraft=3, inventoryVerified=True, producedCount=60))
        result, calls = await self.run_lua('return terraria.crafting.make(8,61)', [
            query([recipe(token)]), batch, query([recipe(token, available=False)])])
        report = result['value']
        self.assertEqual(('partial', 'missing_crafting_station', 60, 1),
                         (report['status'], report['reason'], report['producedCount'], report['remaining']))
        self.assertEqual(['query_recipes', 'craft_recipe', 'query_recipes'], [op for op,_ in calls])

    async def test_base_chest_lookup_with_and_without_item(self):
        self.runtime = LuaRuntime(contract_tests.ApiHelpTests.runtime_bridge(CAPS | {'server_ai_base_containers_v1'}))
        rows = [dict(x=4,y=5,count=0,distanceTiles=2,isBase=True)]
        result, calls = await self.run_lua('return {all=terraria.container.base(), matches=terraria.container.base("Terraria/Torch",32)}',
            [read_data(status='ok',chests=rows), read_data(status='ok',chests=[])])
        self.assertEqual(rows, result['value']['all']['chests'])
        self.assertEqual([('list_base_chests', {}), ('list_base_chests', {'item':'Terraria/Torch','offset':32})], calls)

    async def test_unavailable_and_budget(self):
        q=query([recipe('a'*64,space=False),recipe('b'*64,available=False)])
        result,calls=await self.run_lua('return terraria.crafting.make(8,1)',[q])
        self.assertEqual('unavailable',result['value']['status'])
        self.assertEqual(q,result['value']['recipes'])
        self.assertEqual(1,len(calls))
        result,calls=await self.run_lua('return terraria.crafting.make(8,1)',[query([recipe('a'*64)])],
                                       limits=Limits(calls=1))
        self.assertEqual('call_limit',result['status'])
        self.assertEqual(1,len(calls))

    async def test_hard_failure_preserves_prior_batches_and_cannot_be_caught(self):
        t='a'*64
        def evidence(n, request):
            return dict(recipeId=7,recipeToken=t,outputTypeId=8,outputCountPerCraft=3,
                        requestedCrafts=request,completedCrafts=n,producedCount=3*n,inventoryVerified=n==request)
        q=query([recipe(t)])
        success=dict(valid=True,status='completed',completed=True,evidence=evidence(20,20))
        failure=dict(valid=True,status='failed',reason='insufficient_materials',completed=False,evidence=evidence(1,2))
        result,calls=await self.run_lua('pcall(terraria.crafting.make,8,65); return api("wait",{milliseconds=1})',
                                        [q,success,q,failure])
        self.assertEqual('failed',result['status'],result)
        self.assertEqual(4,len(calls))
        self.assertIsNone(result['value'])
        self.assertEqual((63,2),(result['moduleResult']['producedCount'],result['moduleResult']['remaining']))

    async def test_building_unknown_survey_does_not_submit(self):
        before=dict(valid=True,available=True,data=dict(entries=[],matched=0,pending=0))
        result,calls=await self.run_lua('return terraria.building.construct({origin={x=2,y=2},tiles={{x=0,y=0,itemType=9}}})',[before])
        self.assertEqual('unknown',result['value']['status'])
        self.assertEqual(0,result['actions'])
        self.assertEqual(1,len(calls))

    async def test_running_and_unknown_task_are_never_resubmitted(self):
        before=dict(valid=True,available=True,worldSessionId='w',actorSlot=254,actorGeneration=1,
                    binding=dict(world='w',slot=254,generation=1,actor='a'),
                    data=dict(entries=[dict(status='placeable')],matched=0,pending=1))
        for status in ('running','unknown'):
            with self.subTest(status=status):
                manager=Mock()
                manager.blocked.return_value=False
                manager.projection.return_value={}
                launch=dict(valid=True,status='running',operation='build_place',taskId='a'*32)
                manager.invoke=AsyncMock(side_effect=[launch,dict(launch,status=status,waitStatus='timeout')])
                with patch('program_tasks.coordinator',return_value=manager):
                    result,calls=await self.run_lua('return terraria.building.construct({origin={x=2,y=2},tiles={{x=0,y=0,itemType=9}}})',[before])
                report=result.get('moduleResult') or result['value']
                self.assertIsNotNone(report, result)
                self.assertEqual((status,'a'*32,None),(report['status'],report['taskId'],report['after']))
                self.assertEqual(['submit_task','await_task'],[c.args[0] for c in manager.invoke.await_args_list])
                self.assertEqual(1,len(calls))


class DocumentationTests(unittest.TestCase):
    def test_projected_contracts_and_example_dependencies(self):
        from lupa.lua54 import LuaRuntime as Lua
        parse=Lua().eval('function(s) return type(assert(load(s))) end')
        for caps in (FULL,CAPS):
            b=bridge(caps)
            defs={row['function']['name']:row['function'] for row in server_definitions(b)}
            for name, definition in defs.items():
                with self.subTest(name=name):
                    doc=api_help(b,dict(name=name))
                    self.assertEqual(definition['parameters'],doc['parameters'])
                    self.assertEqual(schema_for(name,capabilities=b.hello['capabilities']),doc['returnsSchema'])
                    self.assertTrue(set(doc['related'])<=defs.keys())
                    for code in doc['examples']:
                        self.assertEqual('function',parse(code))
                        self.assertTrue(set(re.findall(r'api\("([a-z_]+)"',code))<=defs.keys())
                    self.assertTrue(doc['examples'] or doc['prerequisites'])
