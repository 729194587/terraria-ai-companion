"""Production Worker and host budget/terminal handling with deterministic API receipts."""
import re
import unittest
from unittest.mock import AsyncMock, Mock, patch

import test_server_ai_contract as contract_tests
from test_server_ai_contract import FULL, bridge
from api_result_contracts import schema_for
from lua_runtime import LuaRuntime, Limits
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
