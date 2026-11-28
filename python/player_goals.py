"""Durable, bounded player commissions. A native terminal receipt advances one step."""
from contextlib import closing
import json
import sqlite3
import time
import uuid

import atomic_operations as atomic
from agent_run import task_outcome


MAX_STEPS = 8
DISPATCH_WINDOW_SECONDS = 300
STEP_ARGUMENTS = {
    'mine_target': atomic.SCHEMAS['place_item'],  # Durable mining targets use absolute tile coordinates.
    'place_item': atomic.SCHEMAS['place_item'],
    'navigate_to': atomic.SCHEMAS['navigate_to'],
    'collect_drops': atomic.SCHEMAS['collect_drops'],
    # The full blueprint format is available on demand through build_diff / build_place help.
    'build_place': {'type':'object', 'properties':{'blueprint':{'type':'object',
                    'description':'使用 build_diff 已调查的 blueprint'}}, 'required':['blueprint'], 'additionalProperties':False},
}


def definition(operations):
    step = {'oneOf': [
        {'type': 'object', 'additionalProperties': False, 'required': ['operation', 'arguments'],
         'properties': {'operation': {'const': name}, 'arguments': schema}}
        for name, schema in STEP_ARGUMENTS.items() if name in operations]}
    return atomic.tool('schedule_player_goal',
        '登记玩家交代的有序步骤。导航用落脚点世界像素，挖格和放置用 Tile 坐标。'
        '各步等原生终态、核验效果并释放身体后继续。'
        '登记后只报告已排队。', {'steps': {'type':'array', 'minItems':2, 'maxItems':MAX_STEPS, 'items':step}}, ['steps'])


class GoalStore:
    """One database per stable world/character, one unfinished commission at a time."""
    def __init__(self, path):
        self.path = path
        with closing(self.connect()) as db:
            db.executescript("""
                CREATE TABLE IF NOT EXISTS goals(
                    id TEXT PRIMARY KEY, binding TEXT NOT NULL, chat TEXT NOT NULL, sender TEXT NOT NULL,
                    instruction TEXT NOT NULL, expires REAL NOT NULL, state TEXT NOT NULL,
                    progress INTEGER NOT NULL DEFAULT 0, total INTEGER NOT NULL, revoked INTEGER NOT NULL DEFAULT 0,
                    UNIQUE(binding,chat));
                CREATE UNIQUE INDEX IF NOT EXISTS one_active_goal ON goals((1))
                    WHERE state IN ('ready','running','reconcile');
                CREATE TABLE IF NOT EXISTS steps(
                    goal TEXT NOT NULL, ordinal INTEGER NOT NULL, task TEXT NOT NULL UNIQUE,
                    args TEXT NOT NULL, receipt TEXT, uncertainty TEXT, PRIMARY KEY(goal,ordinal));
                UPDATE goals SET state='cancelled', revoked=1
                    WHERE state IN ('ready','running','reconcile') AND EXISTS (
                        SELECT 1 FROM steps WHERE goal=goals.id AND ordinal=goals.progress
                        AND json_extract(args,'$.operation')='explore_frontier');
            """)

    def connect(self):
        db = sqlite3.connect(self.path, timeout=2)
        db.row_factory = sqlite3.Row
        return db

    def create(self, args, binding, chat, sender, instruction, operations, budget):
        if not isinstance(args, dict) or set(args) != {'steps'} or not isinstance(args['steps'], list):
            raise ValueError('Expected a list of goal steps')
        steps = args['steps']
        if not 2 <= len(steps) <= min(MAX_STEPS, budget):
            raise ValueError('Goal exceeds available step budget')
        for step in steps:
            if (not isinstance(step, dict) or set(step) != {'operation','arguments'}
                    or not isinstance(step['operation'], str) or step['operation'] not in STEP_ARGUMENTS
                    or step['operation'] not in operations or not isinstance(step['arguments'], dict)):
                raise ValueError('Expected an available commission step and its arguments')
            if step['operation'] == 'mine_target' and set(step['arguments']) != {'x','y'}:
                raise ValueError('A mining step requires absolute Tile x/y')
            atomic.validate(step['operation'], step['arguments'])
        goal = uuid.uuid4().hex
        with closing(self.connect()) as db, db:
            db.execute("INSERT INTO goals(id,binding,chat,sender,instruction,expires,total,state) VALUES(?,?,?,?,?,?,?,'ready')",
                       (goal, binding, chat, str(sender), instruction, time.time()+DISPATCH_WINDOW_SECONDS, len(steps)))
            db.executemany('INSERT INTO steps(goal,ordinal,task,args) VALUES(?,?,?,?)',
                           [(goal, n, uuid.uuid4().hex, json.dumps(step)) for n, step in enumerate(steps)])
        return {'valid': True, 'goalId': goal, 'status': 'ready', 'stepCount': len(steps)}

    def latest(self):
        with closing(self.connect()) as db:
            row = db.execute('SELECT id,instruction,state,progress,total,revoked FROM goals ORDER BY rowid DESC LIMIT 1').fetchone()
            return dict(row) if row else None

    def revoke(self):
        with closing(self.connect()) as db, db:
            db.execute("""UPDATE goals SET revoked=1, state=CASE WHEN state='ready' THEN 'cancelled' ELSE state END
                          WHERE state IN ('ready','running','reconcile')""")

    def next_step(self, binding, *, check_uncertain=False):
        with closing(self.connect()) as db, db:
            db.execute("""UPDATE goals SET state='expired' WHERE state='ready'
                          AND (expires<=? OR binding<>?)""", (time.time(), binding))
            row = db.execute("""SELECT g.id,g.binding,g.state,s.task,s.args FROM goals g JOIN steps s
                           ON s.goal=g.id AND s.ordinal=g.progress WHERE g.state IN ('ready','running')
                           OR (? AND g.state='reconcile')""", (check_uncertain,)).fetchone()
            if row:
                return {**dict(row), 'args': json.loads(row['args'])}

    def holds_body(self):
        with closing(self.connect()) as db:
            return db.execute("SELECT 1 FROM goals WHERE state IN ('ready','running','reconcile')").fetchone() is not None

    def claim(self, step, binding):
        with closing(self.connect()) as db, db:
            return db.execute("""UPDATE goals SET state='running' WHERE id=? AND state='ready' AND revoked=0
                              AND binding=? AND expires>? AND EXISTS(SELECT 1 FROM steps
                              WHERE goal=goals.id AND ordinal=goals.progress AND task=?)""",
                              (step['id'], binding, time.time(), step['task'])).rowcount == 1

    def authorized(self, step, binding):
        with closing(self.connect()) as db:
            return db.execute("""SELECT 1 FROM goals g JOIN steps s ON s.goal=g.id AND s.ordinal=g.progress
                               WHERE g.state='running' AND g.revoked=0 AND g.binding=? AND g.expires>?
                               AND s.task=?""", (binding, time.time(), step['task'])).fetchone() is not None

    def settle(self, task, result):
        with closing(self.connect()) as db, db:
            db.execute('BEGIN IMMEDIATE')
            row = db.execute("""SELECT g.*,s.args FROM goals g JOIN steps s ON s.goal=g.id AND s.ordinal=g.progress
                                WHERE s.task=? AND g.state IN ('running','reconcile') AND s.receipt IS NULL""",
                             (task,)).fetchone()
            if not row:
                return
            step = json.loads(row['args'])
            verified = (result.get('actionId') == task and result.get('operation') == step['operation']
                        and result.get('targetTask') == step['arguments'] and result.get('valid') is True
                        and result.get('completed') is True and result.get('status') == 'completed'
                        and result.get('effectVerified') is True and result.get('inputsReleased') is True
                        and (step['operation'] != 'mine_target' or task_outcome([result]) == 'completed'))
            pickup = ((result.get('task') or {}).get('result') or {}).get('evidence') or {}
            if verified and step['operation'] == 'collect_drops' and pickup.get('outcome') in ('none', 'partial'):
                # A successful native scan or capped batch cannot certify the collection step.
                state, verified = 'blocked', False
            elif verified and step['operation'] == 'collect_drops' and pickup.get('outcome') != 'collected':
                state, verified = 'reconcile', False
            elif verified:
                state = 'completed' if row['progress'] + 1 == row['total'] else 'ready'
            elif result.get('dispatchState') == 'not_admitted' or (
                    result.get('valid') is True and result.get('actionId') == task and result.get('inputsReleased') is True
                    and result.get('status') in ('cancelled','failed','timeout')):
                state = 'blocked'
            else:
                state = 'reconcile'
            if state != 'reconcile':
                db.execute('UPDATE steps SET receipt=? WHERE task=?', (json.dumps(result), task))
                if row['revoked']:
                    state = 'cancelled'
            else:
                db.execute('UPDATE steps SET uncertainty=? WHERE task=?', (json.dumps(result), task))
            expires = time.time() + DISPATCH_WINDOW_SECONDS if state == 'ready' else row['expires']
            db.execute('UPDATE goals SET state=?,progress=progress+?,expires=? WHERE id=?',
                       (state, int(verified), expires, row['id']))
