"""Version-controlled Lua modules selected from the current server projection."""
from dataclasses import dataclass
from pathlib import Path


@dataclass(frozen=True)
class Module:
    name: str
    purpose: str
    summary: str
    requires: tuple
    signatures: tuple[str, ...]
    capability: str = ""
    namespace: str = ""


BUILTINS = (
    Module('move', '持续跟随队友', '跟随 list_teammates 的同队玩家。distanceTiles 默认 3 格，默认一直跟随，只有玩家指定时长时才传 durationSeconds。返回 taskId，可查询或取消。', ('list_teammates', 'follow_teammate', 'submit_task'),
           ('follow(teammateRef, options?)',), 'server_ai_follow_v2'),
    Module('base_chests', '基地取货先回家查箱', '实际取货先回基地，到达后再查各箱现货；只问库存可原地查。世界出生点周围 64 格内按距离分页，每页最多 32 箱。省略物品会列出空箱。',
           ('list_base_chests',), ('base(contentKey?, offset?)',), namespace='container'),
    Module('container_search', '按物品查附近箱子及各箱数量', '原地查附近未锁普通箱子。默认半径 32 格（1–64）。status=ok 时 chests 按距离排序，每行有箱子格坐标 x、y 和数量 count。totalCount 为范围内总量；truncated 时用 offset+#chests 取下一页。',
           ('find_container_item',), ('find(contentKey, radiusTiles?, offset?)',), namespace='container'),
    Module('container', '按物品和数量存取指定箱子', '按物品和数量存取指定原版箱子。target={x=格坐标,y=格坐标}；put 存入，take 取出。检查 status、moved、remaining。',
           ('inspect_tile', 'navigate_to', 'open_container', 'inspect_container', 'close_container',
            'inspect_inventory', 'transfer_container_amount'),
           ('put(target, contentKey, count)', 'take(target, contentKey, count)')),
    Module('inventory', '给队友物品', '按槽向队友短抛指定数量的物品。有可用虫洞药水时直接传送，否则按需步行。dropped 是已抛总数，drops 记录每次抛出，list_teammates 的 lastDrop 可核实最后一抛的实收。',
           ('list_teammates', 'navigate_to', 'teleport_to_teammate', 'inspect_inventory',
            'drop_inventory_item'), ('give(teammateRef, contentKey, count)',), 'server_ai_nearby_handoff_v1'),
    Module('npc', '找 NPC 对话', '接近可见的指定 NPC 实例并开启对话。',
           ('scan_entities', 'navigate_to', 'open_npc_dialogue'), ('talk_to(contentKey)',),
           'server_ai_general_npc_dialogue_v1'),
    Module('crafting', '原地制作，缺站回基地', '按当前原生配方制作新增数量。missing_crafting_station 时看 stations 和 remaining。用 get_world_state 的 homeBase 判断是否需要返程，到已知的基地制作区续做剩余数量。到制作区仍缺站时说明所需工作站。',
           ('query_recipes', 'craft_recipe'), ('make(resultType, desiredCount, {maxBatches=8})',)),
    Module('building', '按蓝图施工', '调查蓝图、提交一次施工、有界等待并在可信终态复查。',
           ('build_diff', 'build_place', 'submit_task', 'await_task'),
           ('construct(blueprint, {milliseconds=1000})',)),
    Module('material_plan', '递归规划材料缺口', '一次递归规划目标总数，汇总库存抵扣和缺口。',
           ('plan_materials',), ('plan(typeId, count)',)),
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
        if (not module.name.isidentifier() or not module.signatures
                or not all(s.partition('(')[0].isidentifier() for s in module.signatures) or not module.requires
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
        modules.append(dict(name=name, namespace=module.namespace or name,
                            exports=[s.partition('(')[0] for s in module.signatures], source=source))
    return modules


def directory(bridge):
    groups = {}
    for module in available(bridge):
        groups.setdefault(module.namespace or module.name, []).append(module.purpose)
    return '\n'.join(f'- {name}：{"、".join(purposes)}' for name, purposes in groups.items())


def index(bridge, namespace=None):
    return '\n'.join(f'terraria.{m.namespace or m.name}.{signature} — {m.summary} 依赖 {", ".join(m.requires)}。'
                     for m in available(bridge) if namespace is None or (m.namespace or m.name) == namespace
                     for signature in m.signatures)
