"""Crafting contract/real Worker/loopback tests. Native recipes tested separately."""
import unittest
import test_server_ai_backend as backend
import atomic_operations as atomic
import agent
import lua_agent
from lua_runtime import LuaRuntime, Limits
from server_ai import ServerAiBridge
from server_ai_contract import api_help

GOAL=dict(recipeId=3,recipeToken='a'*64,crafts=2)


class CraftTests(unittest.IsolatedAsyncioTestCase):
    setUp=backend.BackendTests.setUp
    tearDown=backend.BackendTests.tearDown

    def enable(self):
        self.bridge.close();self.host.crafting=True;self.host.actions=True;self.host.perception=True
        original=self.host.query
        def query(op,args):
            r=original('get_player_state' if op=='query_recipes' else op,args)
            r.update(observedAtUnixMs=1793232000000,clock='server_utc_at_projection')
            if op=='query_recipes':r['data']=dict(items=[dict(recipeId=3,recipeToken='a'*64,available=True,supported=True,output=dict(typeId=36,count=1))],truncated=True)
            return r
        self.host.query=query;self.bridge=ServerAiBridge(self.host.server_address[1],timeout=1)

    async def test_capability_and_client_do_not_advertise_unsupported_crafting(self):
        for name in ('query_recipes','craft_recipe'):
            self.assertNotIn(name,self.bridge.supported_atomic_operations)
            self.assertNotIn(name,[t['function']['name'] for t in agent.TOOLS])
            with self.assertRaises(atomic.PreDispatchRejected):await atomic.dispatch(self.bridge,name,{} if name=='query_recipes' else GOAL)
        self.assertFalse(any(q['op']=='start' or q['op']=='query' for q in self.host.sent))

    async def test_query_has_no_action_budget_and_preserves_truncation(self):
        self.enable()
        r=await LuaRuntime(self.bridge,evidence_root=self.directory.name).execute("return api('query_recipes',{})",limits=Limits(actions=0,seconds=8))
        self.assertEqual('completed',r['status'],r);self.assertEqual(0,r['actions']);self.assertTrue(r['value']['data']['truncated'])

    async def test_ingredient_filter_follows_handshake_and_uses_read_only_rpc(self):
        self.enable()
        def help_row():
            return api_help(self.bridge, {'name': 'query_recipes'})
        self.assertNotIn('ingredientType', help_row()['parameters']['properties'])
        with self.assertRaises(atomic.PreDispatchRejected):
            await atomic.dispatch(self.bridge, 'query_recipes', {'ingredientType':23})
        self.assertFalse(any(q['op'] == 'query' for q in self.host.sent))
        self.host.extra_capabilities = ['server_ai_recipe_uses_v1']
        self.bridge.close()
        self.bridge = ServerAiBridge(self.host.server_address[1], timeout=1)
        self.assertIn('ingredientType', help_row()['parameters']['properties'])
        result = await LuaRuntime(self.bridge, evidence_root=self.directory.name).execute(
            "return api('query_recipes',{ingredientType=23,limit=1}).data.truncated", limits=Limits(actions=0))
        self.assertEqual(('completed', 0, True), (result['status'], result['actions'], result['value']))
        query, = [q for q in self.host.sent if q['op'] == 'query']
        self.assertEqual({'ingredientType':23, 'limit':1}, query['arguments'])

    async def test_worker_selects_queried_recipe_and_passes_batch_count(self):
        self.enable()
        r=await LuaRuntime(self.bridge,evidence_root=self.directory.name).execute("local q=api('query_recipes',{}); local r=q.data.items[1]; return api('craft_recipe',{recipeId=r.recipeId,recipeToken=r.recipeToken,crafts=2})",limits=Limits(actions=1,seconds=8))
        self.assertTrue(r['value']['completed'],r);self.assertEqual(2,r['value']['evidence']['completedCrafts'])
        self.assertEqual(GOAL,next(q['arguments'] for q in self.host.sent if q['op']=='start'))

    async def test_missing_or_partial_effect_never_completed(self):
        self.enable()
        for override in ({'inventoryVerified':False},{'completedCrafts':1},{'recipeToken':'b'*64},{'batches':[]},{'mutationAttempted':False}):
            with self.subTest(override=override):
                self.host.crafting_override=override
                r=await atomic.dispatch(self.bridge,'craft_recipe',GOAL)
                self.assertEqual('unknown',r['status'],r);self.assertFalse(r['completed'])

    async def test_recipe_receipt_change_is_unknown(self):
        self.enable();self.host.mode='changed_recipe'
        r=await atomic.dispatch(self.bridge,'craft_recipe',GOAL)
        self.assertEqual('unknown',r['status'],r)

    async def test_failed_craft_terminates_before_query_or_second_action(self):
        self.enable();self.host.mode='failed'
        r=await LuaRuntime(self.bridge,evidence_root=self.directory.name).execute("local r=api('craft_recipe',{recipeId=3,recipeToken='"+'a'*64+"',crafts=2}); local q=api('query_recipes',{}); api('craft_recipe',{recipeId=3,recipeToken='"+'a'*64+"',crafts=1}); return q",limits=Limits(actions=2,seconds=8))
        self.assertEqual(1,sum(q['op']=='start' for q in self.host.sent));self.assertFalse(any(q['op']=='query' for q in self.host.sent));self.assertEqual(1,r['actions'])
        self.assertEqual('failed',r['status'],r);self.assertIsNone(r['value'])
        self.assertEqual('known',r['lastAction']['endEvidenceStatus'])

    async def test_invalid_options_reject_before_rpc_and_contract_is_explicit(self):
        self.enable()
        for name,args in [('craft_recipe',{**GOAL,'crafts':0}),('craft_recipe',{**GOAL,'crafts':21}),('craft_recipe',{**GOAL,'recipeToken':'x'*64}),('craft_recipe',{**GOAL,'crafts':True}),('query_recipes',{'limit':25}),('query_recipes',{'availableOnly':1})]:
            with self.subTest(args=args),self.assertRaises(ValueError):await atomic.dispatch(self.bridge,name,args)
        self.assertFalse(any(q['op'] in ('start','query') for q in self.host.sent))
        instructions,_=lua_agent.server_contract(self.bridge)
        self.assertIn('crafts是配方执行次数',instructions);self.assertIn('不回滚',instructions)


if __name__=='__main__':unittest.main()
