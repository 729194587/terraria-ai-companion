"""Real Worker/Dispatcher/loopback recovery, with native game evidence substitutes."""
import copy
import json
import unittest

import test_server_ai_backend as backend
import atomic_operations as atomic
import lua_agent
from lua_runtime import LuaRuntime, Limits
from server_ai import ServerAiBridge


class RecoveryTests(unittest.IsolatedAsyncioTestCase):
    setUp = backend.BackendTests.setUp
    tearDown = backend.BackendTests.tearDown

    def enable(self, operation='mine_tile', reason='invalid_tool', alter=None):
        self.bridge.close()
        self.host.sent.clear(); self.host.polls=0
        self.host.actions = self.host.perception = self.host.object_placement = True
        def transform(a):
            if a['operation'] != operation:
                return
            a.update(status='failed', reason=reason)
            if operation == 'mine_tile':
                a['evidence'] = dict(Ticks=0, Swings=0, Strikes=0, PeakDamage=0, Transforms=0,
                    targetChanged=False, targetUnchanged=True, nativeDestroyed=False, targetAbsent=False, destroyedByActor=False, effectStatus='not_started')
            else:
                a['evidence'] = dict(Ticks=0, Starts=0, Attempts=0, nativePlacementEvents=0, consumedCount=0,
                    targetChanged=False, uncertainNativeEffect=False, placedByActor=False, targetPresent=False,
                    fullFootprintVerified=False, objectPlacement=True, effectStatus='not_confirmed')
            a['endEvidence'].update(inputsAcquired=False, inputsReleased=True, effectVerified=False)
            if alter:
                alter(a)
        self.host.action_transform = transform
        self.bridge = ServerAiBridge(self.host.server_address[1], timeout=2)

    async def test_local_bad_argument_for_reads_and_actions_preserves_local_state(self):
        self.enable()
        r = await LuaRuntime(self.bridge, evidence_root=self.directory.name).execute("""
local n=17
for _,name in ipairs({'inspect_inventory','mine_tile'}) do
  local ok,e=pcall(function() return api(name,{}) end)
  assert(not ok and e.status=='bad_argument' and e.argumentOperation==name and e.diagnostics)
  n=n+1
end
local read=api('inspect_inventory',{mode='selected'})
return {n=n,read=read.valid,selected=api('select_hotbar_slot',{slot=2}).completed}
""", limits=Limits(seconds=8, calls=4, actions=1))
        self.assertEqual('completed', r['status'], r)
        self.assertEqual({'n':19,'read':True,'selected':True}, r['value'])
        self.assertEqual((4,1), (r['calls'],r['actions']))
        self.assertEqual(1, sum(q['op']=='start' for q in self.host.sent))

    async def test_mining_zero_effect_and_object_zero_effect_resume_original_worker(self):
        for operation, reason in (('mine_tile','invalid_tool'),('mine_tile','out_of_range'),
                                  ('mine_tile','arbitrary_reason'),('place_object','native_refused')):
            with self.subTest(operation=operation,reason=reason):
                self.enable(operation,reason); callbacks=[]
                r = await LuaRuntime(self.bridge,evidence_root=self.directory.name).execute("""
local n=17
local ok,a=pcall(function() return api('"""+operation+"""',{x=10,y=20}) end)
assert(ok and a.status=='failed' and a.continuationAllowed and n==17)
local read=api('inspect_inventory',{mode='selected'})
local next=api('select_hotbar_slot',{slot=2})
return {n=n,failed=a,read=read.valid,selected=next.completed}
""", limits=Limits(seconds=8,calls=3,actions=2),
                    on_result=lambda op,args,result: callbacks.append(result) if op in atomic.ACTIONS else None)
                self.assertEqual('completed',r['status'],r)
                self.assertEqual(17,r['value']['n']);self.assertTrue(r['value']['selected'])
                self.assertEqual(reason,r['value']['failed']['reason'])
                self.assertEqual('inspect_inventory',r['lastObservation']['operation'])
                self.assertEqual((3,2),(r['calls'],r['actions']))
                summary=lua_agent.summarize(r,callbacks)
                self.assertEqual(['failed','completed'],[i['result']['status'] for i in summary['actionResults']['items']])
                self.assertFalse(summary['actionSuccess'])
                starts=[q['operation'] for q in self.host.sent if q['op']=='start']
                self.assertEqual([operation,'select_hotbar_slot'],starts)

    async def test_side_effects_uncertainty_and_identity_never_resume(self):
        cases={
            **{key:lambda a,key=key:a['evidence'].update({key:1}) for key in ('Ticks','Swings','Strikes','PeakDamage','Transforms')},
            'changed':lambda a:a['evidence'].update(targetChanged=True),
            'effect':lambda a:a['evidence'].update(destroyedByActor=True),
            'missing':lambda a:a['evidence'].pop('PeakDamage'),
            'missing_target_proof':lambda a:a['evidence'].pop('targetUnchanged'),
            'boolean_zero':lambda a:a['evidence'].update(Ticks=False),
            'release':lambda a:a['endEvidence'].update(inputsReleased=False),
            'no_end':lambda a:a.update(endEvidenceStatus='unknown'),
            'identity':lambda a:a['endEvidence'].update(actorGeneration=999),
            'world':lambda a:a['endEvidence'].update(worldSessionId='other'),
            **{status:lambda a,status=status:a.update(status=status) for status in ('unknown','cancelled','timeout')},
        }
        for name,alter in cases.items():
            with self.subTest(case=name):
                self.enable(alter=alter)
                r=await LuaRuntime(self.bridge,evidence_root=self.directory.name).execute("pcall(function() api('mine_tile',{x=10,y=20}) end); return api('select_hotbar_slot',{slot=2})",limits=Limits(seconds=8))
                self.assertNotEqual('completed',r['status'],r);self.assertIsNone(r['value'])
                self.assertEqual(1,sum(q['op']=='start' for q in self.host.sent))

    async def test_object_missing_or_partial_evidence_remains_fatal(self):
        for key,value in (('nativePlacementEvents',1),('consumedCount',1),('uncertainNativeEffect',True),
                          ('targetChanged',True),('placedByActor',True),('targetPresent',True),('consumedCount',None)):
            with self.subTest(key=key,value=value):
                self.enable('place_object',alter=lambda a:a['evidence'].update({key:value}))
                r=await LuaRuntime(self.bridge,evidence_root=self.directory.name).execute("api('place_object',{x=10,y=20}); return api('select_hotbar_slot',{slot=2})",limits=Limits(seconds=8))
                self.assertNotEqual('completed',r['status'],r)
                self.assertEqual(1,sum(q['op']=='start' for q in self.host.sent))

    async def test_object_attempt_and_released_inputs_do_not_imply_game_effect(self):
        def attempted(a):
            a['evidence'].update(Ticks=1,Starts=1,Attempts=1)
            a['endEvidence']['inputsAcquired']=True
        self.enable('place_object',alter=attempted)
        r=await LuaRuntime(self.bridge,evidence_root=self.directory.name).execute("local a=api('place_object',{x=10,y=20}); assert(a.continuationAllowed); return api('select_hotbar_slot',{slot=2})",limits=Limits(seconds=8))
        self.assertEqual('completed',r['status'],r)

    async def test_recovery_does_not_reset_action_or_api_budget(self):
        for limits in (Limits(seconds=8,actions=1),Limits(seconds=8,calls=1)):
            self.enable()
            r=await LuaRuntime(self.bridge,evidence_root=self.directory.name).execute("api('mine_tile',{x=10,y=20}); return api('select_hotbar_slot',{slot=2})",limits=limits)
            self.assertIn(r['status'],('action_limit','call_limit'),r)
            self.assertEqual('failed',r['lastAction']['status'])
            self.assertEqual(1,sum(q['op']=='start' for q in self.host.sent))


class InventoryContractTests(unittest.TestCase):
    def test_mode_required_and_descriptions_explain_actual_scope(self):
        with self.assertRaises(atomic.ArgumentValidationError):atomic.validate('inspect_inventory',{})
        tool=next(t for t in atomic.TOOLS if t['function']['name']=='inspect_inventory')['function']
        self.assertIn('mode',tool['parameters']['required'])
        for text in ('inventoryScope','inspectedSlots','全部player.inventory','Buff','银行','Mod自定义'):
            self.assertIn(text,tool['description'])
        for mode in ('selected','hotbar','full'):
            self.assertEqual({'mode':mode},atomic.validate('inspect_inventory',{'mode':mode}))
