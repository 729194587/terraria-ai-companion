"""Regional schema/capability boundary and real Lua Worker transport, not engine geometry."""
import unittest
import atomic_operations as atomic
import test_server_ai_scene as scene
from lua_runtime import LuaRuntime, Limits
import lua_agent


class RegionArguments(unittest.TestCase):
    def test_limits_and_action_layers(self):
        for args in (dict(scale='region'),dict(scale='region',width=128,height=64),dict(scale='region',width=85,height=96,layers=['map'])):
            self.assertEqual(args,atomic.validate('observe_scene',args))
        for args in (dict(width=120),dict(scale='local',height=29),dict(scale='region',width=128,height=96),
                     dict(scale='region',layers=['landingRegions']),dict(scale='region',maxRegions=1),
                     dict(scale='region',maxPlacements=1),dict(scale='region',width=True),dict(scale='region',height=28)):
            with self.subTest(args=args),self.assertRaises(ValueError):atomic.validate('observe_scene',args)


class RegionRuntime(unittest.IsolatedAsyncioTestCase):
    setUp=scene.SceneTests.setUp
    tearDown=scene.SceneTests.tearDown
    enable=scene.SceneTests.enable

    async def test_old_server_hides_options_and_refuses_before_rpc(self):
        self.enable()
        with self.assertRaises(atomic.PreDispatchRejected):
            await atomic.dispatch(self.bridge,'observe_scene',dict(scale='region'))
        self.assertFalse(any(q['op']=='query' for q in self.host.sent))
        text,_=lua_agent.server_contract(self.bridge)
        self.assertNotIn('server_ai_scene_region_v1',text)
        definition=next(d for d in atomic.server_tools(self.bridge.supported_atomic_operations) if d['function']['name']=='observe_scene')
        self.assertNotIn('scale',definition['function']['parameters']['properties'])
        await atomic.dispatch(self.bridge,'observe_scene',{})
        self.assertEqual(1,sum(q['op']=='query' for q in self.host.sent))

    async def test_worker_transports_region_once_with_unknown_and_omissions(self):
        self.enable();self.bridge.hello['capabilities'].append('server_ai_scene_region_v1')
        original=self.host.query
        def query(op,args):
            r=original(op,args)
            if op=='observe_scene' and args.get('scale')=='region':
                r['data']=dict(schemaVersion=3,scale='region',includedLayers=['geometry','hazards'],
                    coverage=dict(samplingTruncated=True,unknownByReason=dict(not_sampled=128),geometryComplete=False),
                    geometry=dict(grid=dict(palette=['.','?'],rows=[dict(encoding='rle',data=[1,0,1,1])])),
                    hazards=dict(tiles=dict(items=[],truncated=True,omittedCount=10),tileMask=dict(palette=[0,66],rows=[dict(encoding='indices',data=[0,1])])),
                    sample=dict(source='server_world_tile_current'))
            return r
        self.host.query=query
        r=await LuaRuntime(self.bridge,evidence_root=self.directory.name).execute(
            "return api('observe_scene',{scale='region',width=120,height=68,layers={'geometry','hazards'}})",
            limits=Limits(seconds=8,calls=1,actions=0))
        self.assertEqual('completed',r['status'],r)
        self.assertEqual((1,0),(r['calls'],r['actions']))
        d=r['value']['data'];self.assertTrue(d['coverage']['samplingTruncated'])
        self.assertEqual(128,d['coverage']['unknownByReason']['not_sampled'])
        self.assertTrue(d['hazards']['tiles']['truncated']);self.assertEqual([0,66],d['hazards']['tileMask']['palette'])
        self.assertNotIn('landingRegions',d)
        self.assertEqual(d,r['lastObservation']['result']['data'])
        sent=[q for q in self.host.sent if q['op']=='query']
        self.assertEqual([dict(scale='region',width=120,height=68,layers=['geometry','hazards'])],[q['arguments'] for q in sent])
        text,_=lua_agent.server_contract(self.bridge)
        self.assertIn('server_ai_scene_region_v1',text)
        self.assertIn('not_sampled',text)
        definition=next(d for d in atomic.server_tools(self.bridge.supported_atomic_operations,scene_region=True) if d['function']['name']=='observe_scene')
        self.assertIn(atomic.SCENE_REGION_CONTRACT,definition['function']['description'])
        self.assertFalse(any(q['op']=='start' for q in self.host.sent))

    async def test_dense_packed_rows_fit_unchanged_worker_limits_and_lua_decodes(self):
        self.enable();self.bridge.hello['capabilities'].append('server_ai_scene_region_v1')
        original=self.host.query
        packed=dict(encodingVersion=2,palette=['.','#'],rows=[dict(encoding='hex_indices',indexDigits=1,data='01'*64) for _ in range(64)])
        def query(op,args):
            r=original(op,args)
            if op=='observe_scene':
                r['data']=dict(schemaVersion=3,scale='region',geometry=dict(grid=packed),map=dict(terrain=packed),
                    coverage=dict(knowledge=packed,samplingTruncated=False),hazards=dict(tileMask=packed))
            return r
        self.host.query=query
        r=await LuaRuntime(self.bridge,evidence_root=self.directory.name).execute("""
local r=api('observe_scene',{scale='region',width=128,height=64,layers={'map','geometry','hazards'}})
local g=r.data.geometry.grid
local row=g.rows[64]
local i=tonumber(string.sub(row.data,(128-1)*row.indexDigits+1,128*row.indexDigits),16)
return {last=g.palette[i+1],rows=#g.rows}
""",limits=Limits(seconds=8,calls=1,actions=0))
        self.assertEqual('completed',r['status'],r)
        self.assertEqual(dict(last='#',rows=64),r['value'])
        self.assertEqual((1,0),(r['calls'],r['actions']))


if __name__=='__main__':unittest.main()
