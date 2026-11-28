"""Durable survey goals for a single world and character. Only the host owns task handles."""
from contextlib import closing
import json
from pathlib import Path
import sqlite3
import time

from agent_memory import GUID
from exploration_frontiers import SPAN

DEFAULT_ROOT = Path(__file__).resolve().parents[1]/'.local'/'agent-memory'
COOLDOWN = 90
MAX_ATTEMPTS = 3


class ExplorationSession:
    def __init__(self, identity, root=DEFAULT_ROOT):
        if not all(isinstance(value,str) and GUID.fullmatch(value) for value in identity):
            raise ValueError('Invalid exploration map identity')
        folder = Path(root).joinpath(*identity)
        folder.mkdir(parents=True,exist_ok=True)
        self.path = folder/'exploration.sqlite3'
        with closing(self.connect()) as db:
            db.executescript("""
                CREATE TABLE IF NOT EXISTS goals (
                    id INTEGER PRIMARY KEY, kind TEXT, y INTEGER, bucket INTEGER,
                    lo INTEGER, hi INTEGER, state TEXT, data TEXT);
                CREATE INDEX IF NOT EXISTS near_goal ON goals(y,bucket);
                CREATE INDEX IF NOT EXISTS goal_state ON goals(state);
                CREATE TABLE IF NOT EXISTS discoveries (key TEXT PRIMARY KEY, data TEXT);
            """)

    def connect(self):
        db = sqlite3.connect(self.path,timeout=3)
        db.row_factory = sqlite3.Row
        return db

    @staticmethod
    def decode(row):
        return dict(json.loads(row['data']),id=row['id'],state=row['state'])

    @staticmethod
    def evidence(goal, window):
        if goal['kind']=='map':
            return window.revealed(goal['witness'])
        return any(window.visited(x,goal['y']) for x in range(goal['lo'],goal['hi']+1))

    @staticmethod
    def signature(goal, window):
        x, y = goal['target']['x']//16,goal['y']
        if not window.contains(x-2,y-4) or not window.contains(x+2,y+1):
            return None
        return ''.join(window.cell(i,j) for j in range(y-4,y+2) for i in range(x-2,x+3))

    def refresh(self, goals, window):
        """Merge only overlapping runs on the same surface inside a world-aligned band."""
        observed = {}
        with closing(self.connect()) as db, db:
            db.execute('BEGIN IMMEDIATE')
            rows = db.execute('SELECT * FROM goals WHERE y BETWEEN ? AND ? AND bucket BETWEEN ? AND ?',
                              (window.top,window.top+window.height-1,window.left//SPAN,
                               (window.left+window.width-1)//SPAN)).fetchall()
            by_area = {}
            for row in rows:
                goal = self.decode(row)
                if self.evidence(goal,window) and goal['state']!='surveyed':
                    db.execute("UPDATE goals SET state='surveyed' WHERE id=?",(goal['id'],))
                    goal['state']='surveyed'
                by_area.setdefault((row['kind'],row['y'],row['bucket']),[]).append(goal)
            for fresh in goals:
                area = fresh['kind'],fresh['y'],fresh['lo']//SPAN
                prior = next((old for old in by_area.get(area,())
                              if old['id'] not in observed and old['lo']<=fresh['hi'] and fresh['lo']<=old['hi']
                              and (area[0]=='visit' or old['witness']==fresh['witness']
                                   or old['state']!='surveyed' and not window.revealed(old['witness']))),None)
                if prior:
                    # Keep the original investigation point for an unrevealed frontier.
                    data = dict(fresh,history=prior['history'],lastResult=prior.get('lastResult'),
                                knownAccess=prior.get('knownAccess',False) or fresh['natural'])
                    if area[0]=='map':
                        data['witness']=prior['witness']
                        if data['witness']!=fresh['witness']:
                            data['target']=prior['target']
                    state = prior['state']
                    if state=='surveyed' and area[0]=='visit':
                        if self.evidence(prior,window):
                            continue
                        state = 'pending'
                    key = prior['id']
                    db.execute('UPDATE goals SET lo=?,hi=?,data=?,state=? WHERE id=?',
                               (data['lo'],data['hi'],json.dumps(data),state,key))
                else:
                    data = dict(fresh,history=[],knownAccess=fresh['natural'])
                    state = 'pending'
                    key = db.execute('INSERT INTO goals(kind,y,bucket,lo,hi,state,data) VALUES(?,?,?,?,?,?,?)',
                                     (*area,data['lo'],data['hi'],state,json.dumps(data))).lastrowid
                if state!='surveyed':
                    observed[key]=dict(data,id=key,state=state)
        return list(observed.values())

    def saved(self, here, limit=24):
        with closing(self.connect()) as db:
            rows = db.execute("""SELECT * FROM goals WHERE state IN ('active','pending','deferred')
                ORDER BY (state='active') DESC,(state='deferred'), abs(y-?)+abs(bucket*?-?),id DESC LIMIT ?""",
                (here[1],SPAN,here[0],limit))
            return [self.decode(r) for r in rows]

    def choose(self, options, here, window, preference=None, now=None):
        now = time.time() if now is None else now
        def priority(g):
            history = g['history']
            x,y = g['target']['x']//16,g['y']
            dx,dy = x-here[0],y-here[1]
            alignment = {'left':-dx,'right':dx,'up':-dy,'down':dy}.get(preference,0)
            return (g['state']!='active', bool(history), not g['natural'],
                    g['source']=='map', not bool(g['hint']), g['kind']!='visit',
                    alignment < 0, abs(dx)+abs(dy), -g['id'])
        eligible = []
        for g in options:
            history = g['history']
            if history:
                last = history[-1]
                sig = self.signature(g,window)
                changed = sig is not None and last['terrain'] is not None and last['terrain']!=sig
                different_side = (here[0]-g['target']['x']//16)*(last['from'][0]-g['target']['x']//16)<0
                approach = different_side and abs(here[0]-last['from'][0])>=12
                if not changed and (len(history)>=MAX_ATTEMPTS or
                                    not approach and now-last['time']<COOLDOWN):
                    continue
            eligible.append(g)
        return min(eligible,key=priority,default=None)

    def activate(self, goal):
        with closing(self.connect()) as db, db:
            db.execute("UPDATE goals SET state='pending' WHERE state='active' AND id!=?",(goal['id'],))
            db.execute("UPDATE goals SET state='active' WHERE id=?",(goal['id'],))

    def finish(self, goal, window, here, reason):
        surveyed = self.evidence(goal,window)
        data = {k:v for k,v in goal.items() if k not in ('id','state')}
        data['lastResult'] = dict(surveyed=surveyed,reason=reason,time=time.time())
        if not surveyed:
            data['history'] = (data['history']+[dict(time=time.time(),terrain=self.signature(goal,window),
                                                   reason=reason,**{'from':here})])[-MAX_ATTEMPTS:]
        with closing(self.connect()) as db, db:
            db.execute('UPDATE goals SET state=?,data=? WHERE id=?',
                       ('surveyed' if surveyed else 'deferred',json.dumps(data),goal['id']))
        return surveyed

    def discovery(self, key, item):
        with closing(self.connect()) as db, db:
            return db.execute('INSERT OR IGNORE INTO discoveries VALUES(?,?)',
                              (json.dumps(key,sort_keys=True),json.dumps(item))).rowcount==1
