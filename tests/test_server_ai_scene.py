"""Real Worker/loopback/dispatcher; semantic/native geometry is covered separately."""
import copy
import unittest
import test_server_ai_backend as backend
import atomic_operations as atomic
import lua_agent
import agent
from control import BridgeError
from lua_runtime import LuaRuntime, Limits
from server_ai import ServerAiBridge

GOAL=dict(minPlayerLeftX=164,maxPlayerLeftX=180,surfaceY=192)


class SceneTests(unittest.IsolatedAsyncioTestCase):
    setUp=backend.BackendTests.setUp
    tearDown=backend.BackendTests.tearDown

    def enable(self):
        self.bridge.close();self.host.scene=True;self.host.perception=True;self.host.actions=True;self.host.landing=True
        original=self.host.query
        def query(operation,args):
            r=original('get_player_state' if operation=='observe_scene' else operation,args)
            r.update(observedAtUnixMs=1793232000000,clock='server_utc_at_projection')
            if operation=='observe_scene':
                r['data']=dict(sceneId='scene-1',gameUpdate=33,landingRegions=dict(items=[dict(id='l1',target=copy.deepcopy(GOAL),feetDeltaY=-50)],truncated=False),
                    objectPlacementCandidates=dict(items=[],matchedCount=0,returnedCount=0,truncated=False,omittedCount=0,
                        diagnostics=dict(anchorCount=1305,inBoundsAnchorCount=1232,checkedCount=1231,rejectedCount=1231,
                            unknownCount=1,notCheckedCount=73,coveredScopeComplete=False,
                            rejectedByReason=dict(footprint_occupied=1200,liquid_present=0,missing_support=31,out_of_reach=0,player_collision=0),
                            notCheckedByReason=dict(outside_observation=73,definition_unavailable=0),firstExamples={})),
                    objects=dict(items=[dict(kind='tree_component',complete=False)],truncated=True,matchedCount=3,returnedCount=1,omittedCount=2),
                    coverage=dict(geometryComplete=False,unknownCells=2),hazards=dict(tiles=dict(items=[dict(sources=['lava','touch_hot'])],truncated=False)))
            return r
        self.host.query=query
        self.bridge=ServerAiBridge(self.host.server_address[1],timeout=1)

    async def test_old_backend_and_client_do_not_advertise_scene(self):
        self.assertNotIn('observe_scene',self.bridge.supported_atomic_operations)
        with self.assertRaises(BridgeError):await atomic.dispatch(self.bridge,'observe_scene',{})
        self.assertFalse(any(r['op']=='query' for r in self.host.sent))
        self.assertNotIn('observe_scene',[t['function']['name'] for t in agent.TOOLS])
        self.assertNotIn('observe_scene',lua_agent.TOOL['function']['description'])

    async def test_single_read_zero_action_budget_retains_unknown_and_omissions(self):
        self.enable()
        r=await LuaRuntime(self.bridge,evidence_root=self.directory.name).execute("return api('observe_scene',{})",limits=Limits(seconds=8,actions=0))
        self.assertEqual('completed',r['status'],r);self.assertEqual(1,r['calls']);self.assertEqual(0,r['actions'])
        d=r['value']['data'];self.assertFalse(d['coverage']['geometryComplete']);self.assertEqual(2,d['objects']['omittedCount'])
        self.assertEqual(['lava','touch_hot'],d['hazards']['tiles']['items'][0]['sources'])
        self.assertFalse(any(q['op']=='start' for q in self.host.sent))

    async def test_lua_passes_structured_region_without_coordinate_projection(self):
        self.enable()
        r=await LuaRuntime(self.bridge,evidence_root=self.directory.name).execute("""
local s=api('observe_scene',{})
local candidate=s.data.landingRegions.items[1]
return api('navigate_to_landing_region',candidate.target)
""",limits=Limits(seconds=8,actions=1))
        self.assertTrue(r['value']['completed'],r)
        self.assertEqual(GOAL,next(q['arguments'] for q in self.host.sent if q['op']=='start'))

    async def test_scene_contract_preserves_candidate_and_tree_limitations(self):
        self.enable();instructions,tools=lua_agent.server_contract(self.bridge)
        self.assertIn('observe_scene',instructions);self.assertIn('candidate.target',instructions)
        self.assertIn('不保证路径/可达',instructions);self.assertIn('不是整树计数',instructions)
        self.assertEqual(['execute_lua','read_skill'],[t['function']['name'] for t in tools])
        self.assertIn('coveredScopeComplete',instructions);self.assertIn('rejectedCount',instructions)

    async def test_object_diagnostics_pass_through_and_remain_observation_only(self):
        self.enable()
        r=await LuaRuntime(self.bridge,evidence_root=self.directory.name).execute("return api('observe_scene',{})",limits=Limits(seconds=8,calls=1,actions=0))
        self.assertEqual('completed',r['status'],r)
        candidates=r['value']['data']['objectPlacementCandidates'];d=candidates['diagnostics']
        self.assertFalse(candidates['truncated']);self.assertEqual(0,candidates['omittedCount'])
        self.assertEqual((1231,1,73),(d['rejectedCount'],d['unknownCount'],d['notCheckedCount']))
        self.assertFalse(d['coveredScopeComplete'])
        self.assertEqual(candidates,r['lastObservation']['result']['data']['objectPlacementCandidates'])
        self.assertEqual(['observe_scene'],[q['operation'] for q in self.host.sent if q['op']=='query'])
        self.assertFalse(any(q['op']=='start' for q in self.host.sent))

    async def test_invalid_scene_options_reject_before_rpc(self):
        self.enable()
        for args in ({'maxObjects':0},{'maxRegions':25},{'maxHazards':65},{'maxPlacements':True},{'maxObjects':1.5},{'radius':100}):
            with self.subTest(args=args),self.assertRaises(ValueError):await atomic.dispatch(self.bridge,'observe_scene',args)
        self.assertFalse(any(q['op']=='query' for q in self.host.sent))

    async def test_identity_mismatch_never_returns_invented_map(self):
        self.enable();self.host.mode='bad_query_identity'
        with self.assertRaises(BridgeError):await atomic.dispatch(self.bridge,'observe_scene',{})
        self.assertTrue(self.bridge._closed)

    async def test_failed_program_stops_then_separate_scene_read_never_retries(self):
        self.enable();self.host.mode='failed'
        runtime=LuaRuntime(self.bridge,evidence_root=self.directory.name)
        r=await runtime.execute("""
local a=api('navigate_to_x',{targetX=164})
local scene=api('observe_scene',{})
return {action=a,scene=scene}
""",limits=Limits(seconds=8,actions=1))
        self.assertEqual('failed',r['status']);self.assertIsNone(r['value'])
        self.assertFalse(any(q['op']=='query' for q in self.host.sent))
        observed=await runtime.execute("return api('observe_scene',{})",limits=Limits(seconds=8,actions=0))
        self.assertEqual('completed',observed['status']);self.assertTrue(observed['value']['valid'])
        self.assertEqual(1,sum(q['op']=='start' for q in self.host.sent))


if __name__=='__main__':unittest.main()
