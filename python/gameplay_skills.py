"""Bundled, read-only gameplay references for the Server AI model."""
from dataclasses import dataclass
from functools import lru_cache
from pathlib import Path
import json
import re

from atomic_operations import tool


TOOL_NAME = 'read_skill'
ID_PATTERN = r'[a-z][a-z0-9_]{0,63}'
MAX_SKILL_BYTES = 16 * 1024
MAX_SKILLS = 32
TOOL = tool(TOOL_NAME, '按 available_skills 索引中的 ID 只读加载一篇 Markdown 攻略。'
            '占一次模型工具调用，不调用游戏或执行攻略中的代码。',
            {'name': {'type': 'string', 'pattern': '^' + ID_PATTERN + '$'}}, ['name'])


@dataclass(frozen=True)
class Skill:
    name: str
    description: str
    game_version: str
    scope: str
    path: Path


def read_metadata(stream, path):
    fields = {}
    if stream.readline(1024).strip() != b'---':
        raise ValueError(f'Missing gameplay skill metadata: {path.parent.name}')
    for _ in range(4):
        line = stream.readline(1024).decode('utf-8')
        key, separator, value = line.rstrip('\r\n').partition(': ')
        if not separator or not value.strip() or key in fields or not line.endswith('\n'):
            raise ValueError(f'Invalid gameplay skill metadata: {path.parent.name}')
        fields[key] = value.strip()
    if (stream.readline(1024).strip() != b'---'
            or set(fields) != {'name', 'description', 'game_version', 'scope'}
            or fields['name'] != path.parent.name or len(fields['description']) > 160):
        raise ValueError(f'Invalid gameplay skill metadata: {path.parent.name}')
    return Skill(path=path, **fields)


class SkillRegistry:
    """Index bundled metadata and load registered Markdown on demand."""

    def __init__(self, directory):
        self._directory = directory.resolve()
        self._skills = {}
        for path in sorted(self._directory.glob('*/SKILL.md')):
            if len(self._skills) == MAX_SKILLS:
                raise ValueError('Too many bundled gameplay skills')
            if not re.fullmatch(ID_PATTERN, path.parent.name):
                raise ValueError(f'Invalid bundled gameplay skill ID: {path.parent.name}')
            self._check_file(path)
            with path.open('rb') as stream:
                skill = read_metadata(stream, path)
            self._skills[skill.name] = skill

    def _check_file(self, path):
        if path.resolve() != path or not path.is_file():
            raise ValueError(f'Invalid bundled gameplay skill file: {path.parent.name}')
        if path.stat().st_size > MAX_SKILL_BYTES:
            raise ValueError(f'Bundled gameplay skill exceeds size limit: {path.parent.name}')

    def render_index(self):
        return '\n'.join(f'- {skill.name} — {skill.description}' for skill in self._skills.values())

    def load(self, arguments):
        if (not isinstance(arguments, dict) or set(arguments) != {'name'}
                or not isinstance(arguments['name'], str)
                or not re.fullmatch(ID_PATTERN, arguments['name'])):
            raise ValueError('read_skill requires only a valid registered skill name')
        skill = self._skills.get(arguments['name'])
        if skill is None:
            return {'valid': False, 'error': 'unknown_skill'}
        self._check_file(skill.path)
        with skill.path.open('rb') as stream:
            if read_metadata(stream, skill.path) != skill:
                raise ValueError('Gameplay skill metadata changed; restart the Agent to refresh the index')
            raw = stream.read(MAX_SKILL_BYTES - stream.tell() + 1)
            if stream.tell() > MAX_SKILL_BYTES:
                raise ValueError('Gameplay skill exceeds size limit')
        content = raw.decode('utf-8').strip()
        if not content:
            raise ValueError('Gameplay skill body is empty')
        result = {'valid': True, 'name': skill.name, 'description': skill.description,
                  'game_version': skill.game_version, 'scope': skill.scope, 'content': content}
        knowledge_path = skill.path.with_name('knowledge.json')
        if knowledge_path.exists():
            self._check_file(knowledge_path)
            if knowledge_path.stat().st_size + skill.path.stat().st_size > MAX_SKILL_BYTES:
                raise ValueError('Gameplay skill and knowledge exceed size limit')
            knowledge = json.loads(knowledge_path.read_text(encoding='utf-8'))
            if knowledge.get('game_version') != skill.game_version:
                raise ValueError('Gameplay knowledge version differs from skill version')
            result['knowledge'] = knowledge
        return result


@lru_cache(maxsize=1)
def bundled_skills():
    return SkillRegistry(Path(__file__).resolve().parents[1] / 'skills')
