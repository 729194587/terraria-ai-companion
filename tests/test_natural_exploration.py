"""Replay a real generated-world rejection; dispatch assertions are not native physics."""
import copy
import json
from pathlib import Path
import sys
import unittest
sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'python'))
from lupa.lua54 import LuaRuntime
from lua_worker import BOOTSTRAP, EXPLORATION_SOURCE, SEMANTIC_SOURCE

class NaturalFilterTests(unittest.TestCase):
    def setUp(self):
        self.fixture=json.loads((Path(__file__).parent/'fixtures/natural-navigation-left-descent.json').read_text(encoding='utf-8'))
        self.targets=[]

    def run_filter(self):
        lua=LuaRuntime(unpack_returned_tuples=True)
        run,mark,kind,null,_=lua.execute(BOOTSTRAP)
        def convert(v):
            if v is None:return null
            if isinstance(v,dict):return lua.table_from({k:convert(x) for k,x in v.items()})
            if isinstance(v,list):return lua.table_from([convert(x) for x in v])
            return v
        def plain(v):
            if v==null:return None
            if hasattr(v,'items'):
                if kind(v)=='array':return [plain(v[i]) for i in range(1,len(v)+1)]
                return {k:plain(x) for k,x in v.items()}
            return v
        def api(name,args,line):
            if name=='navigate_to_x':
                self.targets.append(args['targetX'])
                # Stop after dispatch, without claiming simulated movement.
                return convert(dict(valid=True,completed=False,status='failed',endEvidenceStatus='known'))
            if name=='get_player_state':return convert(self.fixture['player'])
            if name=='observe_terrain':return convert(self.fixture['terrain'])
            return convert(self.fixture['queries'][name])
        return plain(run("return explore({direction='left',maxSteps=1,stepPixels=128})",api,lambda *a:None,1000000,EXPLORATION_SOURCE,SEMANTIC_SOURCE))

    def test_recorded_traversable_left_descent_is_not_rejected_before_native_planning(self):
        for name in ('natural-navigation-left-descent.json','natural-navigation-grass-descent.json'):
            with self.subTest(snapshot=name):
                self.fixture=json.loads((Path(__file__).parent/'fixtures'/name).read_text(encoding='utf-8'))
                self.targets=[]
                control=self.fixture['nativeControl']['action']
                self.assertTrue(control['completed'] and control['evidence']['settled'])
                self.assertTrue(control['endEvidence']['inputsReleased'])
                self.assertEqual('gap_without_near_landing',self.fixture['provenance']['originalDecision']['basis'])
                r=self.run_filter()
                self.assertEqual(1,len(self.targets))
                distance=self.fixture['player']['player']['position']['x']-self.targets[0]
                self.assertGreaterEqual(distance,64)
                self.assertLessEqual(distance,128)
                self.assertEqual('action_failed',r['reason'])
                self.assertEqual(distance,r['decisions'][0]['candidates'][-1]['distancePixels'])
                self.assertEqual('local_candidate',r['decisions'][0]['candidates'][-1]['reason'])

    def test_unknown_and_hazard_in_same_corridor_still_reject_all_candidates(self):
        original=copy.deepcopy(self.fixture)
        for cell in ('?','!'):
            self.fixture=copy.deepcopy(original);self.targets=[]
            t=self.fixture['terrain']['data']['terrain'];p=self.fixture['player']['player']['position']
            tx=int(p['x']//16);ty=int((p['y']+t['playerHeight'])//16)-1
            row=list(t['rows'][ty-t['originY']]);row[tx-t['originX']]=cell;t['rows'][ty-t['originY']]=''.join(row)
            r=self.run_filter()
            self.assertEqual([],self.targets)
            self.assertEqual('no_safe_direction',r['reason'])
            self.assertTrue(all(c['reason']=='unknown_or_hazard' for c in r['decisions'][0]['candidates']))

class NaturalReportTests(unittest.TestCase):
    def test_unknown_without_worker_value_keeps_known_prior_completions(self):
        from native_natural_navigation import completed_segments
        calls=[dict(operation='navigate_to_x',result=dict(completed=True)),
               dict(operation='navigate_to_x',result=dict(completed=False,status='unknown'))]
        self.assertEqual(1,completed_segments('explore',{},calls))

    def test_post_filter_control_does_not_inflate_explore_progress(self):
        from native_natural_navigation import completed_segments
        calls=[dict(operation='navigate_to_x',result=dict(completed=True)) for _ in range(3)]
        self.assertEqual(2,completed_segments('explore',dict(steps=[{},{}]),calls))

if __name__=='__main__':unittest.main()
