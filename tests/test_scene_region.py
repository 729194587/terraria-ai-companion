"""Regional schema/capability boundary and real Lua Worker transport, not engine geometry."""
import unittest
import atomic_operations as atomic
import test_server_ai_scene as scene
from lua_runtime import LuaRuntime, Limits
import lua_agent
from server_ai_contract import api_help


class RegionArguments(unittest.TestCase):
    def test_limits_and_action_layers(self):
        for args in (dict(scale='region'),dict(scale='region',layers=['resources'],width=120,height=68),dict(scale='region',width=128,height=64),dict(scale='region',width=85,height=96,layers=['map'])):
            self.assertEqual(args,atomic.validate('observe_scene',args))
        for args in (dict(width=120), dict(height=40), dict(width=100,height=40,layers=['objects'],maxObjects=32)):
            self.assertEqual(dict(args, scale='region'), atomic.validate('observe_scene', args))
            self.assertNotIn('scale', args)
        for args in (dict(width=100,layers=['landingRegions']),dict(scale='local',height=29),dict(scale='region',width=128,height=96),
                     dict(scale='region',layers=['landingRegions']),dict(scale='region',maxRegions=1),
                     dict(scale='region',maxPlacements=1),dict(scale='region',width=True),dict(scale='region',height=28)):
            with self.subTest(args=args),self.assertRaises(ValueError):atomic.validate('observe_scene',args)


class RegionRuntime(unittest.IsolatedAsyncioTestCase):
    setUp=scene.SceneTests.setUp
    tearDown=scene.SceneTests.tearDown
    enable=scene.SceneTests.enable

    def test_mode_parameters_match_validation_and_default_layers(self):
        self.enable()
        self.bridge.hello['capabilities'].append('server_ai_scene_region_v1')
        samples = dict(width=45, height=29, maxObjects=1, maxHazards=1, maxRegions=1, maxPlacements=1)
        for mode in ('local', 'region'):
            args = dict(scale=mode)
            doc = api_help(self.bridge, dict(name='observe_scene', forArgs=args))
            for key, value in samples.items():
                proposal = dict(args, **{key: value})
                with self.subTest(mode=mode, key=key):
                    if key in doc['parameters']:
                        atomic.validate('observe_scene', proposal)
                    else:
                        with self.assertRaises(ValueError):
                            atomic.validate('observe_scene', proposal)
            layers = doc['parameters']['layers']['items']['enum']
            for layer in atomic.SCENE_LAYERS:
                proposal = dict(args, layers=[layer])
                self.assertEqual(layer in layers, api_help(self.bridge, dict(name='observe_scene', forArgs=proposal))['valid'])
            self.assertEqual(mode == 'local', 'map' in doc['dataFields'])
            self.assertIn('objectCounts', doc['dataFields'])
            self.assertEqual([doc['example']], doc['examples'])
        doc = api_help(self.bridge, dict(name='observe_scene', forArgs=dict(width=45,layers=['map'])))
        self.assertEqual('region', doc['mode'])
        self.assertIn('map', doc['dataFields'])
        self.assertNotIn('objectCounts', doc['dataFields'])
        self.assertFalse(api_help(self.bridge, dict(name='observe_scene', forArgs=dict(width=128,height=96)))['valid'])

    async def test_directional_landings_are_local_and_capability_gated(self):
        self.enable()
        args=dict(landingDirection='up',layers=['landingRegions'])
        self.assertEqual(atomic.validate('observe_scene',args),args)
        for invalid in (dict(args,scale='region'),dict(args,layers=['resources']),dict(args,landingDirection='auto')):
            with self.assertRaises(ValueError):
                atomic.validate('observe_scene',invalid)
        with self.assertRaises(atomic.PreDispatchRejected):
            await atomic.dispatch(self.bridge,'observe_scene',args)
        self.bridge.hello['capabilities'].append('server_ai_directional_landings_v1')
        self.assertTrue((await atomic.dispatch(self.bridge,'observe_scene',args))['valid'])

    async def test_regional_resource_capability_and_result_variant(self):
        self.enable()
        caps=self.bridge.hello['capabilities']
        caps.extend(['server_ai_resources_v1','server_ai_scene_region_v1'])
        args=dict(layers=['resources'],scale='region')
        help_args=dict(name='observe_scene',forArgs=args)
        self.assertFalse(api_help(self.bridge,help_args)['valid'])
        with self.assertRaises(atomic.PreDispatchRejected):
            await atomic.dispatch(self.bridge,'observe_scene',args)
        caps.append('server_ai_region_resources_v1')
        self.assertIn('resources',api_help(self.bridge,help_args)['dataFields'])
        import api_result_contracts as results
        schema=results.schema_for('observe_scene',capabilities=caps)
        self.assertEqual(schema['$defs']['SceneRegionalResources']['properties']['resources']
                         ['properties']['source']['const'],'region_resource_scan')

    async def test_map_help_selects_mode_and_decodes_first_row(self):
        self.enable()
        self.assertEqual({'local'}, set(api_help(self.bridge, {'name':'observe_scene'})['mapRows']))
        self.assertFalse(api_help(self.bridge, {'name':'observe_scene','forArgs':{'width':100}})['valid'])
        self.bridge.hello['capabilities'].append('server_ai_scene_region_v1')
        original = self.host.query
        def query(op, args):
            reply = original(op, args)
            if op == 'observe_scene':
                reply['data']['map'] = map_data
            return reply
        self.host.query = query
        runtime = LuaRuntime(self.bridge, evidence_root=self.directory.name)
        limits = Limits(seconds=8, calls=1, actions=0)
        palette = ['.','#'] + ['?'] * 14 + ['~']
        for mode, map_data, expected in (
            ('local', {'rows':['.#?']}, '.#?'),
            ('region', {'terrain':dict(encodingVersion=2, palette=palette,
                                       rows=[dict(encoding='rle',data=[2,1,1,0])])}, '##.'),
            ('region', {'terrain':dict(encodingVersion=2, palette=palette,
                                       rows=[dict(encoding='hex_indices',indexDigits=2,data='100001')])}, '~.#'),
        ):
            with self.subTest(mode=mode, expected=expected):
                help_result = await runtime.execute(
                    f'return api("help",{{name="observe_scene",forArgs={{scale="{mode}",layers={{"map"}}}}}})',
                    limits=limits)
                summary = help_result['value']
                full = api_help(self.bridge, {'name':'observe_scene'})['mapRows'][mode]
                self.assertEqual([full['path']], summary['paths'])
                self.assertIn(full['encoding'], summary['statusSemantics'])
                self.assertNotIn('returnsSchema', summary)
                result = await runtime.execute(summary['example'], limits=limits)
                self.assertEqual('completed', result['status'], result)
                self.assertEqual(expected, result['value'])

    async def test_scene_help_examples_match_backend_and_keep_coverage(self):
        self.enable()
        help_args = {'name': 'observe_scene'}
        local, = api_help(self.bridge, help_args)['examples']
        self.bridge.hello['capabilities'].append('server_ai_scene_region_v1')
        first, region = api_help(self.bridge, help_args)['examples']
        self.assertEqual(local, first)
        for example in (local, region):
            result = await LuaRuntime(self.bridge, evidence_root=self.directory.name).execute(
                example, limits=Limits(seconds=8, calls=1, actions=0))
            self.assertEqual('completed', result['status'], result)
            self.assertTrue(result['value']['objects']['truncated'])
            self.assertFalse(result['value']['coverage']['geometryComplete'])
        sent = [q['arguments'] for q in self.host.sent if q['op'] == 'query']
        self.assertEqual([dict(layers=['objects']),
                          dict(layers=['objects'],width=100,height=40,maxObjects=32,scale='region')], sent)

    async def test_old_server_hides_options_and_refuses_before_rpc(self):
        self.enable()
        with self.assertRaises(atomic.PreDispatchRejected):
            await atomic.dispatch(self.bridge,'observe_scene',dict(scale='region'))
        with self.assertRaises(atomic.PreDispatchRejected):
            await atomic.dispatch(self.bridge,'observe_scene',dict(width=100))
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
            "return api('observe_scene',{width=120,height=68,layers={'geometry','hazards'}})",
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
