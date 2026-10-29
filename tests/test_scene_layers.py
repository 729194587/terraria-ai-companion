"""Python schema and real Worker forwarding; actual projection is tested in C#."""
import multiprocessing as mp
import unittest
import test_server_ai_scene as scene
import atomic_operations as atomic
import lua_agent
from lua_runtime import LuaRuntime, Limits


class LayerArguments(unittest.TestCase):
    def test_schema_validates_all_single_layers_combinations_and_default(self):
        self.assertEqual({},atomic.validate('observe_scene',{}))
        for layers in ([name] for name in atomic.SCENE_LAYERS):
            self.assertEqual(layers,atomic.validate('observe_scene',dict(layers=layers))['layers'])
        args=dict(layers=['geometry','landingRegions','hazards'],maxObjects=1,maxPlacements=2)
        self.assertEqual(args,atomic.validate('observe_scene',args))

    def test_illegal_layers_are_diagnosed_without_fallback(self):
        for value in ([],['bad'],['map','map'],'map',{},None,[1],[True],['Map'],[''],['map']*8):
            with self.subTest(layers=value),self.assertRaises(atomic.ArgumentValidationError):
                atomic.validate('observe_scene',dict(layers=value))
        with self.assertRaises(atomic.ArgumentValidationError):
            atomic.validate('observe_scene',dict(layers=['map'],maxObjects=0))


class LayerRuntime(unittest.IsolatedAsyncioTestCase):
    setUp=scene.SceneTests.setUp
    tearDown=scene.SceneTests.tearDown
    enable=scene.SceneTests.enable

    async def test_real_worker_forwards_selected_layers_without_extra_calls_or_actions(self):
        self.enable()
        r=await LuaRuntime(self.bridge,evidence_root=self.directory.name).execute("return api('observe_scene',{layers={'objectPlacementCandidates'},maxPlacements=1})",limits=Limits(seconds=8,calls=1,actions=0))
        self.assertEqual('completed',r['status'],r)
        self.assertEqual((1,0),(r['calls'],r['actions']))
        queries=[q for q in self.host.sent if q['op']=='query']
        self.assertEqual([dict(layers=['objectPlacementCandidates'],maxPlacements=1)],[q['arguments'] for q in queries])
        self.assertFalse(any(q['op']=='start' for q in self.host.sent))
        self.assertFalse(mp.active_children())

    async def test_invalid_layers_never_dispatch_and_local_read_refusal_remains_catchable(self):
        self.enable()
        runtime=LuaRuntime(self.bridge,evidence_root=self.directory.name)
        for value in ('json.array()',"{'map','map'}","{'unknown'}","'map'",'42','{true}'):
            r=await runtime.execute("return api('observe_scene',{layers="+value+'})',limits=Limits(seconds=8,actions=0))
            self.assertEqual('bad_argument',r['status'],r)
            self.assertTrue(r['diagnostics'])
            self.assertFalse(any(q['op']=='query' for q in self.host.sent))
        r=await runtime.execute("local ok,e=pcall(function() api('observe_scene',{layers={'wrong'}}) end); assert(not ok and e.status=='bad_argument'); return api('observe_scene',{layers={'geometry','landingRegions'}})",limits=Limits(seconds=8,calls=2,actions=0))
        self.assertEqual('completed',r['status'],r)
        self.assertEqual(2,r['calls'])
        self.assertEqual([['geometry','landingRegions']],[q['arguments']['layers'] for q in self.host.sent if q['op']=='query'])
        self.assertFalse(mp.active_children())

    async def test_model_contract_exposes_layers_and_omission_semantics(self):
        self.enable()
        instructions,tools=lua_agent.server_contract(self.bridge)
        for term in ('includedLayers','objectCounts','placementMaterial','objectPlacementMaterial','未请求字段直接省略'):
            self.assertIn(term,instructions)
        self.assertEqual(['execute_lua'],[t['function']['name'] for t in tools])


if __name__=='__main__':unittest.main()
