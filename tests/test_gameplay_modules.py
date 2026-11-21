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
               'server_ai_container_transfer_v1'}


def recipe(token, *, available=True, space=True):
    return dict(supported=True, available=available, hasOutputSpace=space, recipeId=7, recipeToken=token,
                output=dict(typeId=8,count=3), reason=None if available else 'missing_crafting_station',
                stations=[dict(adjacent=available)], ingredients=[dict(item=dict(typeId=9,count=1))])


def query(items, *, truncated=False):
    return dict(valid=True, available=True, data=dict(items=items,truncated=truncated))


class ModuleTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        self.bridge = contract_tests.ApiHelpTests.runtime_bridge(CAPS)
        self.bridge.close = Mock()
        self.runtime = LuaRuntime(self.bridge)

    async def run_lua(self, source, replies, **options):
        calls = []
        async def dispatch(bridge, name, args, *unused, **kw):
            calls.append((name,args))
            return replies[len(calls)-1]
        with patch('atomic_operations.dispatch', side_effect=dispatch):
            result = await self.runtime.execute(source, **options)
        return result,calls

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
        for caps, expected in ((CAPS, ['crafting','building']), (FULL,['crafting']), (set(),[])):
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
