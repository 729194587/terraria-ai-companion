"""Durable, bounded player commissions. A native terminal receipt advances one step."""
from contextlib import closing
import json
import sqlite3
import time
import uuid

import atomic_operations as atomic
from agent_run import task_outcome


MAX_STEPS = 8
SUPPORTED = {'mine_target', 'place_item'}


def definition(operations):
    step = {'type': 'object', 'additionalProperties': False, 'required': ['operation','arguments'],
            'properties': {'operation': {'enum': sorted(SUPPORTED.intersection(operations))},
                           'arguments': {'type':'object','properties':atomic.TILE,'required':['x','y'],
                                         'additionalProperties':False}}}
    return atomic.tool('schedule_player_goal',
        '登记玩家明确要求的有序指定格任务。坐标来自玩家或现场观察。每步原生验证效果、释放后才继续。'
        'mine_target 证明该格挖除；place_item 使用当前选中的放置物品。不能用于证明入包。'
        '登记后只报告已排队。', {'steps': {'type':'array', 'minItems':2, 'maxItems':MAX_STEPS, 'items':step}}, ['steps'])


def exploration_definition():
    schema = atomic.SCHEMAS['explore_frontier']
    return atomic.tool('schedule_exploration',
        '玩家明确授权自主探索时登记一次有界往返。radiusTiles 是以出发点为中心的 Tile 半径，'
        'maxSegments 是外出尝试上限。默认仅移动，玩家明确允许清障才开启 allowDig。'
        '完成要求实际开图、返回安全锚点并释放输入。', schema['properties'], schema['required'])


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
                    or step['operation'] not in SUPPORTED.intersection(operations)
                    or not isinstance(step['arguments'], dict) or set(step['arguments']) != {'x','y'}):
                raise ValueError('Expected an available task and absolute Tile x/y')
            atomic.validate(step['operation'], step['arguments'])
        return self._save(steps, binding, chat, sender, instruction)

    def create_exploration(self, args, binding, chat, sender, instruction, operations, budget):
        if 'explore_frontier' not in operations or budget < 1:
            raise ValueError('Exploration admission unavailable')
        step = {'operation': 'explore_frontier', 'arguments': atomic.validate('explore_frontier', args)}
        return self._save([step], binding, chat, sender, instruction)

    def _save(self, steps, binding, chat, sender, instruction):
        goal = uuid.uuid4().hex
        with closing(self.connect()) as db, db:
            db.execute("INSERT INTO goals(id,binding,chat,sender,instruction,expires,total,state) VALUES(?,?,?,?,?,?,?,'ready')",
                       (goal, binding, chat, str(sender), instruction, time.time()+300, len(steps)))
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

    def exploration_owner(self, binding):
        with closing(self.connect()) as db:
            row = db.execute("""SELECT g.id,s.task,s.args FROM goals g JOIN steps s
                ON s.goal=g.id AND s.ordinal=g.progress WHERE g.binding=?
                AND g.state IN ('ready','running','reconcile')""", (binding,)).fetchone()
            if row and json.loads(row['args'])['operation'] == 'explore_frontier':
                return row['id'], row['task']

    def revoke_goal(self, goal):
        with closing(self.connect()) as db, db:
            db.execute("""UPDATE goals SET revoked=1, state=CASE WHEN state='ready' THEN 'cancelled' ELSE state END
                WHERE id=? AND state IN ('ready','running','reconcile')""", (goal,))

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
                        and (step['operation'] not in ('mine_target','explore_frontier') or task_outcome([result]) == 'completed'))
            if verified:
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
            db.execute('UPDATE goals SET state=?,progress=progress+? WHERE id=?',
                       (state, int(verified), row['id']))
