import base64
from pathlib import Path
import tempfile
import sys
import unittest

sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'python'))
from exploration_frontiers import MapWindow, candidates
from exploration_session import ExplorationSession, MAX_ATTEMPTS


def window(rows=None, feet=(), ox=0):
    rows = rows or [' '*24]*8+['#'*24]*2
    width, height = len(rows[0]),len(rows)
    bits = bytearray((width*height+7)//8)
    for x,y in feet:
        i = y*width+x-ox
        bits[i//8] |= 1 << (i%8)
    return MapWindow(dict(originX=ox,originY=0,width=width,height=height,rows=rows,cellSize=1,
                          visitedCount=len(feet),visitBits=base64.b64encode(bits),
                          worldUid='a'*32,characterId='b'*32))


def scene(ranges, connected=()):
    return dict(actor=dict(boundsWorldPixels=dict(x=0,width=20),feetY=128),
                passages=dict(connectedLandingIds=list(connected)),
                landingRegions=dict(items=[dict(id=name,target=dict(
                    minPlayerLeftX=a*16-10,maxPlayerLeftX=b*16-10,surfaceY=y*16))
                    for name,a,b,y in ranges],truncated=False))


class FrontierTests(unittest.TestCase):
    def test_separate_thin_wall_and_actual_visit_only(self):
        s = scene([('a',1,4,8),('b',6,10,8)],['a'])
        goals = candidates(s,window())
        self.assertEqual([(g['lo'],g['hi'],g['natural']) for g in goals],[(1,4,True),(6,10,False)])
        self.assertEqual([g['lo'] for g in candidates(s,window(feet=[(4,8)]))],[6])
        # Scene identifiers and a shifted map crop do not enter the target identity.
        self.assertEqual(goals,candidates(scene([('z',1,4,8),('x',6,10,8)],['z']),window(ox=1)))

    def test_unknown_witness_requires_air_and_real_map_coordinate(self):
        rows = ['#####?????']*5+['   # ?????']*3+['##########']
        s = scene([('ledge',0,2,8)])
        self.assertEqual([g['kind'] for g in candidates(s,window(rows))],['visit'])
        rows[6] = '     ?????'
        goal, = [g for g in candidates(s,window(rows)) if g['kind']=='map']
        self.assertEqual(goal['witness'],[5,6])
        self.assertEqual(goal['target']['y'],128)
        self.assertFalse(window(rows).revealed(goal['witness']))

    def test_remembered_stance_requires_support_and_headroom(self):
        s = scene([])
        self.assertEqual(candidates(s,window([' '*24]*9),regional=True),[])
        goals = candidates(s,window(),regional=True)
        self.assertEqual(len(goals),2)
        self.assertTrue(all(g['source']=='map' and not g['natural'] for g in goals))



class SessionTests(unittest.TestCase):
    def setUp(self):
        directory=tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.root=directory.name
        self.w=window()
        self.session=ExplorationSession(self.w.identity,self.root)
        self.scene=scene([('near',1,4,8),('other',6,10,8)],['near'])
        self.goals=candidates(self.scene,self.w)
        self.here=(3,8)

    def test_dead_end_retains_other_arm_across_restart(self):
        a,b=self.session.refresh(self.goals,self.w)
        self.assertEqual(self.session.choose([b,a],self.here,self.w)['id'],a['id'])
        self.session.activate(a)
        self.assertFalse(self.session.finish(a,self.w,self.here,'no_path'))
        restarted=ExplorationSession(self.w.identity,self.root)
        choices=restarted.refresh(self.goals,self.w)
        self.assertEqual([a['id'],b['id']],[g['id'] for g in choices])
        self.assertEqual(restarted.choose(choices,self.here,self.w)['id'],b['id'])
        self.assertTrue(restarted.finish(b,window(feet=[(7,8)]),self.here,'reached'))

    def test_only_relevant_evidence_and_bounded_retries(self):
        for attempt in range(MAX_ATTEMPTS):
            goal=self.session.refresh(self.goals,self.w)[0]
            self.assertIsNotNone(self.session.choose([goal],self.here,self.w,now=10**10))
            self.session.finish(goal,window(feet=[(18,8)]),self.here,'no new visit')
        blocked=self.session.saved(self.here)[-1]
        self.assertEqual(blocked['state'],'deferred')
        self.assertIsNone(self.session.choose([blocked],self.here,self.w,now=10**10))
        rows=[' '*24]*8+['#'*24]*2
        rows[5]='   #                    '
        self.assertIsNotNone(self.session.choose([blocked],self.here,window(rows)))

    def test_map_goal_requires_its_witness_and_visit_requires_its_floor(self):
        g=self.goals[0]
        self.assertFalse(self.session.evidence(g,window(feet=[(2,7)])))
        rows=[' '*24]*6+[' '*7+'?'+' '*16,' '*24,'#'*24]
        m=dict(g,kind='map',witness=[7,6])
        self.assertFalse(self.session.evidence(m,window(rows,feet=[(2,8)])))
        self.assertTrue(self.session.evidence(m,window()))

    def test_overlap_dedup_and_bending_back_against_preference(self):
        a,b=self.session.refresh(self.goals,self.w)
        narrowed=dict(self.goals[0],lo=2,hi=3)
        only,=self.session.refresh([narrowed],self.w)
        self.assertEqual(only['id'],a['id'])
        self.session.activate(only)
        options=self.session.refresh(self.goals,self.w)
        self.assertEqual(self.session.choose(options,(9,8),self.w,'right')['id'],a['id'])
        self.assertTrue(self.session.discovery(['chest',3,7],{}))
        self.assertFalse(ExplorationSession(self.w.identity,self.root).discovery(['chest',3,7],{}))

if __name__=='__main__':
    unittest.main()
