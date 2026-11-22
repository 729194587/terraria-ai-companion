"""Private durable notes for one server character in one world."""
from contextlib import closing
import json
from pathlib import Path
import re
import sqlite3

LIMIT = 32
MEMORY_RULES = """
私人笔记
privateNotes 是你在当前世界的笔记索引，需要细节时用 recall 读正文。
如果这次学到的东西能改变你下次的做法，就值得主动记。例如连续飞过两道缺口时中途掉了下来，后来落在中间平台再起飞才通过，可以记下当时用的翅膀、走法和结果。
普通游玩可以一条也不写。玩家自然提到的习惯（如“矿我一般都扔右边那排箱子里”），以后用得上也可以记。明确要求保存时，写入成功后再确认。
背包余量、脚下方块和箱子当前库存按需现场查。引用本轮玩家原话用 quote，推测写清依据。旧笔记只提供线索，行动仍须当前授权。
再次使用一条经验时核对装备、环境和游戏规则。后来的尝试推翻了旧判断，就修正或忘掉它。
"""
FIELDS = {'command': {'type': 'string', 'enum': ['remember', 'recall', 'forget']},
          'name': {'type': 'string', 'minLength': 1, 'maxLength': 48},
          'description': {'type': 'string', 'minLength': 1, 'maxLength': 120},
          'content': {'type': 'string', 'maxLength': 1500},
          'quote': {'type': 'string', 'minLength': 1, 'maxLength': 240}}
TOOL = {'type': 'function', 'function': {
    'name': 'memory', 'description': '当前同伴在本世界的私人笔记。remember 按 name 写入或替换，description 写一行可独立理解的事实；content 可补充细节。quote 只引用本轮玩家原话。recall 不传 name 返回索引，传 name 读全文。forget 按 name 删除。',
    'parameters': {'type': 'object', 'properties': FIELDS, 'required': ['command'], 'additionalProperties': False}}}
GUID = re.compile('[0-9a-f]{32}')


class Notebook:
    def __init__(self, root, world, character):
        if not all(isinstance(key, str) and GUID.fullmatch(key) for key in (world, character)):
            raise ValueError('Invalid stable memory identity')
        directory = Path(root) / world / character
        directory.mkdir(parents=True, exist_ok=True)
        self.path = directory / 'notes.sqlite3'
        with closing(self.connect()) as db:
            db.execute('CREATE TABLE IF NOT EXISTS notes (name TEXT PRIMARY KEY, description TEXT NOT NULL, '
                       'content TEXT NOT NULL, origin TEXT NOT NULL)')

    @classmethod
    def from_bridge(cls, bridge, root):
        if 'query_map' not in bridge.supported_atomic_operations:
            return None
        # The bridge verifies the observation binding; read the smallest possible map window.
        result = bridge.command('query_atomic', operation='query_map',
                                arguments={'mode': 'region', 'width': 1, 'height': 1, 'cellSize': 1})['data']
        if result.get('available') is not True:
            return None
        data = result['data']
        return cls(root, data.get('worldUid'), data.get('characterId'))

    def connect(self):
        db = sqlite3.connect(self.path, timeout=1)
        db.row_factory = sqlite3.Row
        return db

    def index(self):
        with closing(self.connect()) as db:
            return [dict(row) for row in db.execute('SELECT name, description FROM notes ORDER BY name')]

    def invoke(self, args, turn, session):
        if not isinstance(args, dict):
            raise ValueError('Expected memory arguments')
        command = args.get('command')
        allowed = {'remember': set(FIELDS), 'recall': {'command','name'}, 'forget': {'command','name'}}
        if not isinstance(command,str) or command not in allowed or set(args) - allowed[command]:
            raise ValueError('Invalid memory command or fields')
        for field, value in args.items():
            if field == 'command':
                continue
            schema = FIELDS[field]
            if not isinstance(value,str) or not schema.get('minLength',0) <= len(value) <= schema['maxLength']:
                raise ValueError('Invalid ' + field)
        if command == 'recall' and 'name' not in args:
            return {'valid': True, 'notes': self.index()}
        name = args.get('name','').strip()
        if not name or not name.isprintable():
            raise ValueError('Expected single-line note name')
        with closing(self.connect()) as db, db:
            if command == 'recall':
                row = db.execute('SELECT * FROM notes WHERE name=?',(name,)).fetchone()
                if row is None:
                    return {'valid': False, 'reason': 'note_not_found'}
                return {'valid': True, 'note': {**dict(row), 'origin':json.loads(row['origin'])}}
            if command == 'forget':
                removed = db.execute('DELETE FROM notes WHERE name=?',(name,)).rowcount
                return {'valid': bool(removed), 'reason': 'forgotten' if removed else 'note_not_found'}
            description = args.get('description','').strip()
            if not description or not description.isprintable():
                raise ValueError('Expected single-line note description')
            origin = {'kind':'agent_note', 'turnId':turn.get('eventId'), 'session':session}
            if 'quote' in args:
                quote = args['quote']
                if turn.get('source') == 'host_task_result' or not quote.strip() or quote not in turn.get('text',''):
                    raise ValueError('Quote must occur in the current player message')
                origin.update(kind='player_quote', quote=quote, senderId=turn['senderId'], senderName=turn['senderName'])
            db.execute('BEGIN IMMEDIATE')
            if not db.execute('SELECT 1 FROM notes WHERE name=?',(name,)).fetchone():
                if db.execute('SELECT count(*) FROM notes').fetchone()[0] >= LIMIT:
                    return {'valid':False, 'reason':'notebook_full'}
            db.execute('INSERT INTO notes VALUES(?,?,?,?) ON CONFLICT(name) DO UPDATE SET '
                       'description=excluded.description, content=excluded.content, origin=excluded.origin',
                       (name, description, args.get('content',''), json.dumps(origin, ensure_ascii=False)))
            return {'valid': True, 'reason':'remembered', 'name':name}
