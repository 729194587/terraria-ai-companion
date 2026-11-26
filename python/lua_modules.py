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
    capability: str = ""
    namespace: str = ""


BUILTINS = (
    Module('npc', '接近可见的指定 NPC 实例并开启对话。',
           ('scan_entities', 'navigate_to', 'open_npc_dialogue'), 'talk_to', 'talk_to(contentKey)',
           'server_ai_general_npc_dialogue_v1'),
    Module('crafting', '按当前可用配方制作指定数量的产物，逐批核验并报告剩余需求。',
           ('query_recipes', 'craft_recipe'), 'make', 'make(resultType, desiredCount, {maxBatches=8})'),
    Module('building', '调查蓝图、提交一次施工、有界等待并在可信终态复查。',
           ('build_diff', 'build_place', 'submit_task', 'await_task'), 'construct',
           'construct(blueprint, {milliseconds=1000})'),
    Module('plan_materials', '一次递归规划目标总数，汇总库存抵扣和缺口。',
           ('plan_materials',), 'plan', 'plan(typeId, count)', namespace='material_plan'),
    Module('material_plan', '只读计算新制一把熔岩镐的背包材料缺口。',
           ('resolve_content', 'inspect_inventory', 'query_recipes'), 'molten_pickaxe', 'molten_pickaxe()'),
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
    return tuple(module for module in BUILTINS if operations.issuperset(module.requires)
                 and (not module.capability or module.capability in bridge.hello['capabilities']))


def sources(names):
    """Only the host-selected catalogue can resolve source files."""
    modules = []
    catalogue = {m.name: m for m in BUILTINS}
    for name in names:
        module = catalogue[name]
        source = (Path(__file__).parent / 'lua' / 'modules' / (name + '.lua')).read_text(encoding='utf-8')
        modules.append(dict(name=name, namespace=module.namespace or name, export=module.export, source=source))
    return modules


def index(bridge):
    return '\n'.join(f'terraria.{m.namespace or m.name}.{m.signature} — {m.summary} 依赖 {", ".join(m.requires)}。'
                     for m in available(bridge))
