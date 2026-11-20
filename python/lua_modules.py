"""Version-controlled Lua modules selected from the current server projection."""
from dataclasses import dataclass
from pathlib import Path


@dataclass(frozen=True)
class Module:
    name: str
    summary: str
    requires: tuple
    export: str
    signature: str


BUILTINS = (
    Module('crafting', '按当前可用配方制作指定数量的产物，逐批核验并报告剩余需求。',
           ('query_recipes', 'craft_recipe'), 'make', 'make(resultType, desiredCount, {maxBatches=8})'),
    Module('building', '调查蓝图、提交一次施工、有界等待并在可信终态复查。',
           ('build_diff', 'build_place', 'submit_task', 'await_task'), 'construct',
           'construct(blueprint, {milliseconds=1000})'),
)


def available(bridge):
    if 'server_ai_atomic_v1' not in getattr(bridge, 'hello', {}).get('capabilities', ()):
        return ()
    from server_ai_contract import exposed_operations
    from program_tasks import NAMES, available as has_tasks
    operations = exposed_operations(bridge)
    if has_tasks(bridge):
        operations.update(NAMES)
    from atomic_operations import SCHEMAS
    known = SCHEMAS.keys() | NAMES
    if len({m.name for m in BUILTINS}) != len(BUILTINS):
        raise ValueError('Duplicate builtin module name')
    for module in BUILTINS:
        if (not module.name.isidentifier() or not module.export.isidentifier() or not module.requires
                or not set(module.requires) <= known):
            raise ValueError(f'Invalid builtin module metadata: {module.name}')
    return tuple(module for module in BUILTINS if operations.issuperset(module.requires))


def sources(names):
    """Only the host-selected catalogue can resolve source files."""
    modules = []
    catalogue = {m.name: m for m in BUILTINS}
    for name in names:
        module = catalogue[name]
        source = (Path(__file__).parent / 'lua' / 'modules' / (name + '.lua')).read_text(encoding='utf-8')
        modules.append(dict(name=name, export=module.export, source=source))
    return modules


def index(bridge):
    return '\n'.join(f'terraria.{m.name}.{m.signature} — {m.summary} 依赖 {", ".join(m.requires)}。'
                     for m in available(bridge))
