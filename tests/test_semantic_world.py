"""Real Lua helper with synthetic observations; no native geometry claims."""
import copy
from pathlib import Path
import sys
import unittest
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'python'))
from lupa.lua54 import LuaRuntime
from lua_worker import BOOTSTRAP, EXPLORATION_SOURCE, SEMANTIC_SOURCE

class SemanticTests(unittest.TestCase):
    def setUp(self):
        self.tiles=[]; self.overrides={}; self.changed=False; self.mismatch=None; self.calls=[]
        self.drops=[]
    def tile(self,x,y,t=7):
        self.tiles.append(dict(x=x,y=y,typeId=t,contentKey='Terraria/Trees' if t==5 else 'Terraria/Copper',distancePixels=x+y))
    def run_helper(self, options='{}'):
        lua=LuaRuntime(unpack_returned_tuples=True)
        run,mark,kind,null,line=lua.execute(BOOTSTRAP)
        def convert(v):
            if isinstance(v,dict): return lua.table_from({k:convert(x) for k,x in v.items()})
            if isinstance(v,list): return lua.table_from([convert(x) for x in v])
            return v
        def plain(v):
            if hasattr(v,'items'):
                if kind(v)=='array': return [plain(v[i]) for i in range(1,len(v)+1)]
                return {k:plain(x) for k,x in v.items()}
            return v
        def api(name,args,line):
            a=plain(args); self.calls.append(name)
            b=dict(minX=0,minY=0,maxX=20,maxY=20)
            if name=='observe_terrain': d=dict(bounds=b,complete=True)
            elif name=='resolve_content':
                t={'Terraria/Trees':5,'Terraria/Copper':7}.get(a['query'])
                d=dict(status='ok' if t else 'not_found',truncated=False,candidates=[dict(typeId=t)] if t else [])
            elif name=='scan_tiles': d=dict(items=copy.deepcopy(self.tiles),bounds=b,complete=True,truncated=False,unknownCount=0,**{}) | self.overrides
            elif name=='inspect_tile':
                t=next(t for t in self.tiles if t['x']==a['x'] and t['y']==a['y'])
                d=dict(cell=dict(knowledge='read',hasTile=not self.changed,tileType=t['typeId']),preflight=dict(withinConservativeReach=False))
            else: d=dict(items=copy.deepcopy(self.drops),complete=True,truncated=False,radiusPixels=a['radius'])
            r=dict(valid=True,available=True,worldSessionId='w',actorGeneration=1,actorSlot=254,binding=dict(actor='a',world='w',generation=1,slot=254),gameUpdate=len(self.calls),observedAtUnixMs=1000+len(self.calls),clock='server_utc_at_projection',data=d)
            if name=='inspect_tile' and self.mismatch:
                if self.mismatch=='actor': r['binding']['actor']='other'
                else: r[self.mismatch]='other'
            return convert(r)
        return plain(run('return sense_world('+options+')',api,lambda *a:None,1000000,EXPLORATION_SOURCE,SEMANTIC_SOURCE))
    def test_vertical_tree_range_and_bottom_candidate(self):
        for y in (2,3,4): self.tile(3,y,5)
        o=self.run_helper()['objects'][0]
        self.assertEqual(3,o['observedTileCount']); self.assertEqual(4,o['candidate']['y'])
        self.assertNotIn('tiles',o); self.assertFalse(o['complete'])
        self.assertEqual('observed_present',o['candidate']['status'])
    def test_adjacent_tree_columns_remain_ambiguous_not_merged(self):
        for x in (2,3):
            for y in (2,3): self.tile(x,y,5)
        objects=self.run_helper()['objects']
        self.assertEqual(2,len(objects))
        self.assertTrue(all(o['ownership']=='ambiguous_lateral_tiles' for o in objects))
    def test_ore_four_neighbours_and_type_separation(self):
        for x,y,t in ((2,2,7),(3,2,7),(4,3,7),(3,3,8)): self.tile(x,y,t)
        objects=self.run_helper()['objects']
        self.assertEqual([2,1,1],[o['observedTileCount'] for o in objects])
        self.assertTrue(all(o['complete'] for o in objects))
        self.assertEqual({1:2,2:2},objects[0]['tiles'][0])
    def test_truncated_unknown_and_edge_never_complete(self):
        for overrides in ({'truncated':True},{'unknownCount':1},{'complete':False}):
            self.tiles=[]; self.tile(2,2); self.overrides=overrides
            self.assertFalse(self.run_helper()['objects'][0]['complete'])
        self.overrides={}; self.tiles=[]; self.tile(0,2)
        self.assertTrue(self.run_helper()['objects'][0]['boundaryTouched'])
    def test_changed_target_and_fresh_call(self):
        self.tile(2,2); self.changed=True
        self.assertEqual('changed_or_unknown',self.run_helper()['objects'][0]['candidate']['status'])
        self.tiles=[]
        self.assertEqual([],self.run_helper()['objects'])
    def test_identity_changes_abort_without_returning_targets(self):
        self.tile(2,2)
        for field in ('actorGeneration','actorSlot','worldSessionId','actor'):
            self.mismatch=field
            with self.assertRaisesRegex(Exception,'identity|binding'): self.run_helper()
    def test_omission_count_and_budget(self):
        for x in (2,4,6): self.tile(x,2)
        r=self.run_helper('{maxObjects=1}')
        self.assertEqual(2,r['omittedObjects']); self.assertEqual(1,self.calls.count('inspect_tile'))
    def test_drop_center_velocity_inventory_and_sample_scope(self):
        self.drops=[dict(slot=9,typeId=9,details=dict(contentKey='Terraria/Wood',stack=3),centerWorldPixels=dict(x=40,y=80),velocityPixelsPerTick=dict(x=1,y=2),distancePixels=30)]
        r=self.run_helper(); d=r['drops'][0]
        self.assertEqual(40,d['navigationHint']['targetCenterX']); self.assertEqual(3,d['stack'])
        self.assertIn('sample only',d['identity']); self.assertIn('inventory',d['verification'])
        self.assertNotEqual(r['sources'][0]['gameUpdate'],r['sources'][-1]['gameUpdate'])
    def test_gap_does_not_invent_tree_cells(self):
        self.tile(3,2,5); self.tile(3,4,5)
        objects=self.run_helper()['objects']
        self.assertEqual(2,len(objects))
        self.assertTrue(all(o['observedTileCount']==1 and not o['complete'] for o in objects))
    def test_drop_slot_reuse_is_a_fresh_observation(self):
        self.drops=[dict(slot=9,typeId=9,details=dict(contentKey='Terraria/Wood',stack=3),centerWorldPixels=dict(x=40,y=80),velocityPixelsPerTick=dict(x=0,y=0),distancePixels=30)]
        before=self.run_helper()['drops'][0]
        self.drops[0].update(typeId=3,details=dict(contentKey='Terraria/StoneBlock',stack=1))
        after=self.run_helper()['drops'][0]
        self.assertEqual(before['observationSlot'],after['observationSlot'])
        self.assertNotEqual(before['typeId'],after['typeId'])
        self.assertEqual(1,after['stack'])
    def test_model_contract_requires_all_five_perception_apis(self):
        from types import SimpleNamespace
        from lua_agent import server_contract
        names={'observe_terrain','resolve_content','scan_tiles','scan_entities','inspect_tile'}
        self.assertIn('sense_world',server_contract(SimpleNamespace(supported_atomic_operations=names))[0])
        for missing in names:
            self.assertNotIn('sense_world',server_contract(SimpleNamespace(supported_atomic_operations=names-{missing}))[0])
    def test_invalid_options_fail_before_reads(self):
        for options in ('{maxObjects=17}','{radius=0}','{foo=1}'):
            with self.assertRaises(Exception): self.run_helper(options)
        self.assertEqual([],self.calls)

if __name__=='__main__': unittest.main()
