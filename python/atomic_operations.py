"""LLM atomic operations. One Bridge session, game-thread actions and release receipts."""
import asyncio
import copy
import math
import re
from time import perf_counter
from control import BridgeError


class PreDispatchRejected(BridgeError):
    """A local capability guard rejected the call before any game RPC.

    Keep the BridgeError contract for direct callers, while allowing the Lua
    host to distinguish this positive refusal from transport/receipt failures.
    Never use this type for errors inferred from a missing action or start task.
    """


def tool(name, description, properties=None, required=None):
    return {"type": "function", "function": {"name": name, "description": description,
        "parameters": {"type": "object", "properties": properties or {}, "required": required or [], "additionalProperties": False}}}


TILE = {"x": {"type": "integer", "minimum": 0, "maximum": 100000},
        "y": {"type": "integer", "minimum": 0, "maximum": 100000}}
TYPE_IDS = {"type": "array", "items": {"type": "integer", "minimum": 0, "maximum": 65535},
            "minItems": 1, "maxItems": 32, "uniqueItems": True}
INVENTORY_SLOT = {"type": "integer", "minimum": 0, "maximum": 49}
INVENTORY_TOKEN = {"type": "string", "minLength": 64, "maxLength": 64, "pattern": "^[0-9a-f]{64}$"}
INVENTORY_GUARDS = {"sourceSlot": INVENTORY_SLOT, "sourceToken": INVENTORY_TOKEN,
                    "destinationToken": INVENTORY_TOKEN}
TOOLS = [tool("observe_scene", "服务器AI局部45×29二维侧视语义图：几何/对象/危险来源、可直接用于导航的landingRegions.items[].target和place_tile候选。单次游戏线程采样；候选不保证可达/放置成功，树组件不是整树计数；检查coverage与各集合truncated。",
    {key:{"type":"integer","minimum":1,"maximum":64 if key=="maxHazards" else 24 if key=="maxRegions" else 32}
     for key in ("maxObjects","maxRegions","maxPlacements","maxHazards")}),tool("get_player_state", "读取真实玩家位置/速度/生命和地面支撑及运动模型支持状态。"),
    tool("get_player_status", "紧凑玩家状态：生命/魔力、运动和液体、所选完整物品、非空装备及活跃Buff；背包仅槽位摘要。不是完整背包，Mod自定义状态未知。"),
    tool("resolve_content", "按内容键或名称解析当前已加载的item/tile/wall ID。item支持本地化名称；tile/wall名称仅内容符号（如Terraria/Dirt），不是本地化地图名称。默认精确匹配，search为包含搜索；歧义必须选择候选，不猜ID。",
         {"kind": {"type": "string", "enum": ["item", "tile", "wall"]},
          "query": {"type": "string", "minLength": 1, "maxLength": 150},
          "mode": {"type": "string", "enum": ["exact", "search"]},
          "maxResults": {"type": "integer", "minimum": 1, "maximum": 32}}, ["kind", "query"]),
    tool("observe_exploration", "Lua可调用的有界视口探索观察：以玩家为中心裁剪Tile半径（默认8，最大8），最多16实体及32调查候选，保留未知/时效/覆盖说明。不是寻路或全世界；没有返回的区域/实体不能视为空。",
         {"radiusTiles": {"type": "integer", "minimum": 1, "maximum": 8},
          "maxEntities": {"type": "integer", "minimum": 1, "maximum": 16}}),
    tool("wait", "可取消的宿主真实时间等待，milliseconds为1..10000。不会推进游戏时间、持有按键或确认游戏效果；受Lua总时限和工具次数预算约束。",
         {"milliseconds": {"type": "integer", "minimum": 1, "maximum": 10000}}, ["milliseconds"]),
    tool("observe_terrain", "复用get_terrain读取45×29局部碰撞及同次玩家状态；Tile为16像素。"),
    tool("inspect_tile", "读取指定Tile类型、Frame、形状、液体、墙/线/颜色，附内容键及距离/工具等preflight局部前提（不保证可达或可执行）；越界或未加载显式未知。", TILE, ["x", "y"]),
    tool("scan_entities", "按世界像素半径扫描客户端已知NPC、掉落物、其他玩家；先按typeIds/npcFilter筛选，再按玩家中心距离排序截断；npcFilter须category=npc，typeIds须npc/item。NPC entityRef仅当前世界实例有效；截断不等于没有。",
         {"category": {"type": "string", "enum": ["all", "npc", "item", "player"]}, "radius": {"type": "number", "minimum": 1, "maximum": 1600},
          "typeIds": TYPE_IDS, "maxResults": {"type": "integer", "minimum": 1, "maximum": 128},
          "npcFilter": {"type": "string", "enum": ["all", "hostile_creature", "friendly_npc", "critter", "unknown"]}}, ["category", "radius"]),
    tool("scan_tiles", "按Tile格坐标闭区间[minX,maxX]×[minY,maxY]扫描指定tileTypes；1格16像素，Y向下。面积<=4096，先按玩家中心距离排序再截断，最多返回256；未加载不等于空。类型ID来自inspect_tile/观察。",
         {**{k: {"type": "integer", "minimum": 0, "maximum": 100000} for k in ('minX', 'minY', 'maxX', 'maxY')},
          "tileTypes": {"type": "array", "items": {"type": "integer", "minimum": 0, "maximum": 65535}, "minItems": 1, "maxItems": 32, "uniqueItems": True},
          "maxResults": {"type": "integer", "minimum": 1, "maximum": 256}}, ['minX', 'minY', 'maxX', 'maxY', 'tileTypes']),
    tool("inspect_inventory", "默认仅选中物品；mode=slots配slots读取指定内部索引（0..58，最多16项），返回inventoryToken供背包动作防旧快照校验；快捷栏用hotbar；item按精确contentKey或本地名称查数量/槽位；full查全部背包/装备及活跃Buff。背包动作仅0..49普通物品槽，hotbarKey是游戏按键，Mod自定义状态未知。",
         {"mode": {"type": "string", "enum": ['selected', 'hotbar', 'item', 'full', 'slots']},
          "item": {"type": "string", "minLength": 1, "maxLength": 150},
          "slots": {"type": "array", "items": {"type": "integer", "minimum": 0, "maximum": 58},
                    "minItems": 1, "maxItems": 16, "uniqueItems": True}}),
    tool("transfer_inventory_item", "单人：在0..49普通背包槽搬运指定count；空目标可拆分，兼容同类目标可堆叠，容量不足则整个请求不执行。sourceToken/destinationToken须取本轮inspect_inventory对应槽。经原生拆分/堆叠钩子，核对数量与下一更新状态；不碰鼠标、箱子、装备、钱币或弹药槽。失败/未知后停止。",
         {**INVENTORY_GUARDS, "destinationSlot": INVENTORY_SLOT,
          "count": {"type": "integer", "minimum": 1, "maximum": 9999}},
         ['sourceSlot', 'destinationSlot', 'sourceToken', 'destinationToken', 'count']),
    tool("swap_inventory_slots", "单人：交换两个0..49普通背包槽的完整物品，不合并、不丢弃；保留物品实例。两个token须来自当前槽位观察；下一游戏更新核验。鼠标持物、使用中、旧快照或多人会拒绝。",
         {**INVENTORY_GUARDS, "destinationSlot": INVENTORY_SLOT},
         ['sourceSlot', 'destinationSlot', 'sourceToken', 'destinationToken']),
    tool("hold_inventory_item", "单人：将sourceSlot完整物品与指定hotbarKey对应槽交换后选择该快捷键；目标原物品回源槽。即使同类也不合并，source已在目标槽则只选择。sourceToken/destinationToken来自两槽当前观察；核验槽位与下一更新持用状态。不自动使用物品。",
         {**INVENTORY_GUARDS, "hotbarKey": {"type": "integer", "minimum": 0, "maximum": 9}},
         ['sourceSlot', 'hotbarKey', 'sourceToken', 'destinationToken']),
    tool("get_world_state", "读取客户端昼夜、天气、事件、生物群系和深度。"),
    tool("navigate_to_x", "复用连续导航至玩家左上角水平世界像素targetX；30秒上限，普通角色运动限制仍适用；arrived由游戏物理状态校验。",
         {"targetX": {"type": "number", "minimum": 0, "maximum": 1000000}}, ["targetX"]),
    tool("navigate_to_landing_region", "二维落脚导航：全部为世界像素，Y向下；minPlayerLeftX/maxPlayerLeftX是玩家碰撞框左边缘的闭区间，surfaceY是脚底目标水平表面高度（非玩家顶部）。原有普通角色限制；落点支持完整方块顶面和水平单向平台，不支持斜平台/半砖；30秒上限。仅实际进入X区间、脚底高度误差<=0.5px、安全着地且停稳后完成，不能只经过目标X。",
         {key: {"type": "number", "minimum": 0, "maximum": 1000000}
          for key in ("minPlayerLeftX", "maxPlayerLeftX", "surfaceY")},
         ["minPlayerLeftX", "maxPlayerLeftX", "surfaceY"]),
    tool("drop_through_platform", "按原生Down直接穿过当前水平单向平台，不绕到边缘。须已停稳，自动检查同X下方64像素内的第一个安全落点；穿过当前层即松Down，实际落在下一层并停稳才完成，3秒上限。"),
    tool("select_hotbar_slot", "按游戏显示的快捷键选择：1..9为前九格，0为最右第十格。slot不是背包数组索引。核对实际选中按键。", {"slot": {"type": "integer", "minimum": 0, "maximum": 9}}, ["slot"]),
    tool("use_item", "用当前物品完成一次原生使用周期；target可选世界像素{x,y}。completed只证明使用周期，具体效果需感知核验。无target保留原生鼠标，不限制画面缩放；有target支持当前缩放，仍限正常重力及80像素。",
         {"target": {"type": "object", "properties": {"x": {"type": "number", "minimum": 0, "maximum": 1000000}, "y": {"type": "number", "minimum": 0, "maximum": 1000000}}, "required": ["x", "y"], "additionalProperties": False}}),
    tool("mine_tile", "以选中镐原生挖掘80像素内Tile，支持当前缩放，仍限正常重力；移除才成功，10秒上限。调用前inspect_inventory确认所选为镐，必要时先select_hotbar_slot。不在该原子操作内自动选工具/移动。", TILE, ["x", "y"]),
    tool("attack_entity", "以当前物品攻击scan_entities给出的NPC entityRef；80像素内，生命下降才成功，失活但无伤害证据失败，其他伤害来源可能存在。10秒上限。",
         {"entityRef": {"type": "string", "minLength": 1, "maxLength": 150}}, ["entityRef"]),
    tool("mine_wall", "选中纯锤原生挖背景墙；80像素内，前景必须为空；实际墙消失才成功。多人需同版本服务器确认；10秒上限，可取消。", TILE, ['x', 'y']),
    tool("place_wall", "选中原版消耗型墙物品，在80像素内无墙格原生放置；正确墙类型和消耗一件共同核验，多人需服务器确认；自动补邻墙或自动涂漆不支持，10秒上限。", TILE, ['x', 'y']),
    tool("hammer_tile", "选中纯锤对80像素内原版普通方块/平台原生锤击；目标无背景墙，实际形状变化才成功，多人需服务器确认。不支持家具/破坏型锤目标。10秒上限。", TILE, ['x', 'y']),
    tool("interact_tile", "80像素内原版门、未锁宝箱、开关、拉杆基础交互；多格任一占用格可指定；宝箱核验打开/关闭，开关及拉杆核验Frame切换，不证明下游电路效果。多人需同版本服务器确认。", TILE, ["x", "y"]),
    tool("place_tile", "用选中消耗品在80像素内空格放单格方块，支持当前画面缩放，仍限正常重力；Tile类型与物品消耗共同核验；不支持多格家具或ModItem。10秒上限。", TILE, ["x", "y"]),
    tool("stop_action", "取消当前Agent动作并等待游戏线程输入释放证据，不抢占手动控制。")]
SCHEMAS = {t['function']['name']: t['function']['parameters'] for t in TOOLS}
READS = {'get_player_state', 'observe_terrain', 'inspect_tile', 'scan_tiles', 'scan_entities', 'inspect_inventory', 'get_world_state'}
READS |= {'observe_scene', 'get_player_status', 'resolve_content', 'observe_exploration'}
UTILITIES = {'wait'}
ACTIONS = set(SCHEMAS) - READS - UTILITIES
BUDGETED_ACTIONS = ACTIONS - {'stop_action'}


def validate(name, arguments):
    """Return a validated copy, losslessly normalizing integer-valued JSON numbers."""
    def check(value, schema, path):
        kind = schema['type']
        if kind == 'object':
            if not isinstance(value, dict) or set(value) - schema['properties'].keys() or not set(schema.get('required', [])) <= value.keys():
                raise ValueError(f'{path}: unexpected or missing atomic parameters')
            return {key: check(item, schema['properties'][key], f'{path}.{key}') for key, item in value.items()}
        if kind == 'array':
            if not isinstance(value, list) or not schema.get('minItems', 0) <= len(value) <= schema.get('maxItems', math.inf):
                raise ValueError(f'{path}: invalid array length')
            normalized = [check(entry, schema['items'], f'{path}[{i}]') for i, entry in enumerate(value, 1)]
            if schema.get('uniqueItems') and len(set(normalized)) != len(normalized):
                raise ValueError(f'{path}: duplicate array items')
            return normalized
        if kind in ('number', 'integer'):
            low, high = schema.get('minimum', -math.inf), schema.get('maximum', math.inf)
            numeric = type(value) is int or type(value) is float and math.isfinite(value)
            integral = kind != 'integer' or type(value) is int or type(value) is float and value.is_integer()
            if not numeric or not integral or not low <= value <= high:
                shown = repr(value)[:80] if type(value) in (int, float, bool) else type(value).__name__
                raise ValueError(f'{path}: expected finite {kind} in [{low}, {high}], got {shown}; fractional values are not rounded')
            return int(value) if kind == 'integer' else value
        if kind == 'string':
            if not isinstance(value, str) or not schema.get('minLength', 0) <= len(value) <= schema.get('maxLength', math.inf) or 'enum' in schema and value not in schema['enum'] or 'pattern' in schema and re.fullmatch(schema['pattern'], value) is None:
                raise ValueError(f'{path}: invalid string, category or entity reference')
        return value
    if name not in SCHEMAS:
        raise ValueError('Unsupported atomic operation')
    arguments = check(arguments, SCHEMAS[name], name)
    if name == 'scan_tiles':
        width, height = arguments['maxX'] - arguments['minX'] + 1, arguments['maxY'] - arguments['minY'] + 1
        if width <= 0 or height <= 0 or width * height > 4096:
            raise ValueError(f'scan_tiles.bounds: inclusive area {width} x {height} = {width * height}; require ordered bounds and area <=4096 tiles. Use observe_terrain bounds (45 x 29) or split into bounded scans')
    if name == 'inspect_inventory' and (('item' in arguments) != (arguments.get('mode') == 'item')):
        raise ValueError('item is required only for mode=item')
    if name == 'inspect_inventory' and (('slots' in arguments) != (arguments.get('mode') == 'slots')):
        raise ValueError('slots is required only for mode=slots')
    if name in ('transfer_inventory_item', 'swap_inventory_slots') and arguments['sourceSlot'] == arguments['destinationSlot']:
        raise ValueError('Inventory source/destination must be different slots')
    if name == 'scan_entities':
        if 'typeIds' in arguments and arguments['category'] not in ('npc', 'item'):
            raise ValueError('typeIds requires category=npc or item')
        if 'npcFilter' in arguments and arguments['category'] != 'npc':
            raise ValueError('npcFilter requires category=npc')
    if name == 'navigate_to_landing_region' and arguments['minPlayerLeftX'] > arguments['maxPlayerLeftX']:
        raise ValueError('Landing X bounds must be ordered')

    return arguments


async def command(bridge, cmd, **args):
    return await asyncio.to_thread(bridge.command, cmd, **args)


def result(action, generation):
    return {'valid': True, 'status': action['status'], 'reason': action['reason'], 'reasonDetail': action.get('reasonDetail'),
            'operation': action.get('operation'), 'actionId': action['actionId'],
            'session': action.get('session'), 'actionGeneration': action.get('generation'), 'targetRegion': action.get('targetRegion'), 'targetTile': action.get('targetTile'),
            'evidence': action.get('evidence'), 'endEvidence': action.get('endEvidence'),
            'endEvidenceStatus': action.get('endEvidenceStatus'), 'controlGeneration': generation,
            'completed': action['status'] == 'completed', 'cancelled': action['status'] == 'cancelled'}


def same_action(expected, current):
    return isinstance(current, dict) and all(current.get(key) == expected.get(key) for key in
        ('actionId', 'session', 'generation', 'worldSessionId', 'operation', 'targetRegion', 'targetTile', 'actorId', 'actorSlot'))


def verify_landing_start(action, arguments, *, server_ai=False):
    region = action.get('targetRegion')
    if (not isinstance(region, dict) or set(region) != set(arguments)
            or any(type(region[key]) not in (int, float) or not math.isfinite(region[key])
                   or not math.isclose(region[key], arguments[key], rel_tol=1e-7, abs_tol=0.001) for key in arguments)
            or (not isinstance(action.get('session'), str) or not action['session']
                if server_ai else type(action.get('session')) is not int or action['session'] <= 0)
            or type(action.get('generation')) is not int or action['generation'] < 0
            or not isinstance(action.get('worldSessionId'), str) or not action['worldSessionId']):
        raise BridgeError('Landing target/identity missing or different in start receipt')


def verified_server_landing(action, evidence):
    if action.get('operation') != 'navigate_to_landing_region':
        return False
    region = action.get('targetRegion')
    if (not isinstance(region, dict) or set(region) != {'minPlayerLeftX', 'maxPlayerLeftX', 'surfaceY'}
            or evidence.get('targetRegion') != region
            or any(evidence.get(key) is not True for key in ('arrived', 'grounded', 'safeSupport', 'settled'))):
        return False
    values = [region[key] for key in ('minPlayerLeftX', 'maxPlayerLeftX', 'surfaceY')]
    values += [evidence.get('x'), evidence.get('y'), evidence.get('vx'), evidence.get('vy')]
    if any(type(v) not in (int, float) or not math.isfinite(v) for v in values):
        return False
    low, high, surface, x, y, vx, vy = values
    return low <= x <= high and abs(y + 42 - surface) <= 0.5 and abs(vx) <= 0.1 and abs(vy) <= 0.1


async def terminal(bridge, action, generation):
    deadline = asyncio.get_running_loop().time() + 1.0
    while action.get('endEvidenceStatus') == 'pending' and asyncio.get_running_loop().time() < deadline:
        await asyncio.sleep(0.05)
        reply = await command(bridge, 'get_movement')
        current = reply['movement']['action']
        if (current is None or current['actionId'] != action['actionId']
                or not same_action(action, current)):
            return {'valid': False, 'status': 'unknown', 'reason': 'action_changed_before_release_evidence'}
        action, generation = current, reply['movement']['generation']
    out = result(action, generation)
    end = action.get('endEvidence')
    server_ai_end = (isinstance(end, dict)
                     and 'server_ai_atomic_v1' in bridge.hello.get('capabilities', [])
                     and action.get('operation') == 'select_hotbar_slot'
                     and end.get('phase') == 'ServerAI_game_thread_no_input'
                     and end.get('inputsAcquired') is False
                     and isinstance(action.get('actorId'), str) and bool(action['actorId'])
                     and end.get('actorId') == action['actorId']
                     and end.get('actorGeneration') == action.get('generation')
                     and end.get('actorSlot') == action.get('actorSlot')
                     and (action.get('status') != 'completed'
                          or end.get('selectionVerified') is True
                          and action.get('evidence', {}).get('selectionApplied') is True
                          and end.get('selectedSlot') == action.get('evidence', {}).get('targetSlot')))
    native_evidence = action.get('evidence') or {}
    native_evidence = native_evidence if isinstance(native_evidence, dict) else {}
    native_verified = {
        'navigate_to_x': native_evidence.get('arrived') is True,
        'navigate_to_landing_region': verified_server_landing(action, native_evidence),
        'use_item': native_evidence.get('cycleVerified') is True,
        'place_tile': (isinstance(action.get('targetTile'), dict)
                       and set(action['targetTile']) == {'x', 'y'}
                       and all(type(action['targetTile'][key]) is int and type(native_evidence.get(key)) is int for key in ('x', 'y'))
                       and native_evidence.get('x') == action['targetTile'].get('x')
                       and native_evidence.get('y') == action['targetTile'].get('y')
                       and native_evidence.get('placedByActor') is True
                       and native_evidence.get('targetPresent') is True
                       and native_evidence.get('targetChanged') is False
                       and type(native_evidence.get('consumedCount')) is int and native_evidence['consumedCount'] == 1),
        'mine_tile': (native_evidence.get('destroyedByActor') is True
                      and native_evidence.get('targetAbsent') is True
                      and native_evidence.get('targetChanged') is False),
    }.get(action.get('operation'), False)
    native_end = (isinstance(end, dict)
                  and 'server_ai_actions_v1' in bridge.hello.get('capabilities', [])
                  and action.get('operation') in ('navigate_to_x', 'navigate_to_landing_region', 'use_item', 'mine_tile', 'place_tile')
                  and end.get('phase') == 'ServerAI_game_thread_inputs_released'
                  and end.get('operation') == action.get('operation')
                  and end.get('inputsReleased') is True
                  and type(end.get('inputsAcquired')) is bool
                  and isinstance(action.get('actorId'), str) and bool(action['actorId'])
                  and end.get('actorId') == action['actorId']
                  and end.get('actorGeneration') == action.get('generation')
                  and end.get('actorSlot') == action.get('actorSlot')
                  and (action.get('status') != 'completed'
                       or end.get('effectVerified') is True and native_verified))
    server_ai_end = server_ai_end or native_end
    valid_phase = server_ai_end if 'server_ai_atomic_v1' in bridge.hello.get('capabilities', []) else (
        isinstance(end, dict) and end.get('phase') in ('PostUpdateRunSpeeds_before_bridge_input', 'UpdateDead_after_revoke'))
    if (action.get('endEvidenceStatus') != 'known' or not isinstance(end, dict)
            or type(end.get('gameUpdate')) is not int or end['gameUpdate'] < 0
            or not isinstance(end.get('worldSessionId'), str) or not end['worldSessionId']
            or end['worldSessionId'] != action.get('worldSessionId')
            or not valid_phase):
        out.update(valid=False, status='unknown', completed=False, reason='missing_game_thread_release_evidence', terminalActionStatus=action['status'])
    return out


async def stop(bridge, action_id=None, expected_action=None):
    reply = await command(bridge, 'get_movement')
    action = reply['movement']['action']
    if action is None:
        if action_id is not None:
            return {'valid': False, 'status': 'unknown', 'completed': False, 'reason': 'action_missing', 'actionId': action_id}
        return {'valid': True, 'status': 'completed', 'reason': 'no_agent_action', 'noOp': True}
    if (action_id is not None and action['actionId'] != action_id
            or expected_action is not None and not same_action(expected_action, action)):
        return {'valid': False, 'status': 'cancelled', 'reason': 'ownership_changed'}
    if action['status'] == 'running':
        owned = action
        reply = await command(bridge, 'cancel_move', actionId=action['actionId'])
        action = reply['movement']['action']
        if not same_action(owned, action) or action['status'] == 'running':
            return {'valid': False, 'status': 'unknown', 'completed': False, 'reason': 'cancellation_unconfirmed'}
    return await terminal(bridge, action, reply['movement']['generation'])


async def dispatch(bridge, name, arguments, chat_id=None, *, lifecycle=None, owner=None):
    # A Lua run owns the whole atomic session, including its cancellation cleanup.
    if getattr(bridge, '_lua_poisoned', False):
        raise BridgeError('Lua session has unresolved action state; reconnect required')
    if getattr(bridge, '_lua_owner', None) is not owner:
        raise BridgeError('Atomic session is owned by a Lua run')
    bridge._atomic_inflight = getattr(bridge, '_atomic_inflight', 0) + 1
    try:
        return await _dispatch(bridge, name, arguments, chat_id, lifecycle=lifecycle)
    finally:
        bridge._atomic_inflight -= 1


async def _dispatch(bridge, name, arguments, chat_id, *, lifecycle):
    def track(**fields):
        if lifecycle is not None:
            lifecycle.update(copy.deepcopy(fields))
    arguments = validate(name, arguments)
    if name == 'observe_scene' and 'server_ai_scene_v1' not in bridge.hello.get('capabilities', []):
        raise PreDispatchRejected('Requires server_ai_scene_v1; no scene query started')
    supported = getattr(bridge, 'supported_atomic_operations', None)
    if supported is not None and name not in supported:
        raise PreDispatchRejected(f'{name} is unsupported by this backend; no action started')
    if name == 'wait':
        started = perf_counter()
        # asyncio's event-loop clock can have a coarse resolution on Windows and
        # allow early timer callbacks. Keep waiting against a precise monotonic clock.
        duration = arguments['milliseconds'] / 1000
        while (remaining := duration - (perf_counter() - started)) > 0:
            await asyncio.sleep(remaining)
        return {'valid': True, 'status': 'completed', 'elapsedMilliseconds': (perf_counter() - started) * 1000,
                'clock': 'host_monotonic', 'effectStatus': 'not_applicable'}
    if name == 'observe_exploration':
        from exploration import read_bounded_exploration
        return await asyncio.to_thread(read_bounded_exploration, bridge, **arguments)
    if name in {'observe_scene', 'observe_terrain', 'scan_tiles', 'scan_entities', 'inspect_tile', 'resolve_content'} and 'server_ai_perception_v1' in bridge.hello.get('capabilities', []):
        reply = await command(bridge, 'query_atomic', operation=name, arguments=arguments)
        envelope = reply.get('data')
        if not isinstance(envelope, dict):
            raise BridgeError('Missing server AI perception envelope')
        return {'valid': envelope.get('available') is True, **envelope}
    if name == 'get_player_state' and 'server_ai_reads_v1' in bridge.hello.get('capabilities', []):
        reply = await command(bridge, 'query_atomic', operation=name, arguments=arguments)
        envelope = reply.get('data')
        if not isinstance(envelope, dict):
            raise BridgeError('Missing server AI player query data')
        if envelope.get('available') is not True:
            return {'valid': False, **envelope}
        data = envelope.get('data')
        if not isinstance(data, dict) or not isinstance(data.get('player'), dict) or not isinstance(data.get('motion'), dict):
            raise BridgeError('Missing server AI player/motion projection')
        return {'valid': True, **{key: value for key, value in envelope.items() if key != 'data'}, **data}
    if name in ('get_player_state', 'observe_terrain'):
        reply = await asyncio.to_thread(bridge.get_terrain)
        if name == 'observe_terrain':
            return {'valid': True, **reply}
        return {'valid': True, 'player': reply['state'], 'motion': {k: reply['terrain'][k] for k in ('grounded', 'supported', 'playerWidth', 'playerHeight')}}
    if 'atomic_operations_v1' not in bridge.hello.get('capabilities', []):
        raise PreDispatchRejected('Requires atomic_operations_v1; loaded Mod does not support these operations')
    if name == 'navigate_to_landing_region' and 'landing_region_navigation_v1' not in bridge.hello.get('capabilities', []):
        raise PreDispatchRejected('Requires landing_region_navigation_v1; no two-dimensional action started')
    required_capability = {'select_hotbar_slot': 'hotbar_keys_v1', 'drop_through_platform': 'platform_drop_v1'}.get(name)
    if (name in ('get_player_status', 'resolve_content')
            or name == 'scan_entities' and set(arguments) - {'category', 'radius'}):
        required_capability = 'atomic_foundations_v1'
    if name in ('scan_tiles', 'mine_wall', 'place_wall', 'hammer_tile', 'inspect_inventory', 'interact_tile'):
        required_capability = 'atomic_extensions_v1'
    if (name in ('transfer_inventory_item', 'swap_inventory_slots', 'hold_inventory_item')
            or name == 'inspect_inventory' and arguments.get('mode') == 'slots'):
        required_capability = 'inventory_operations_v1'
    if name == 'inspect_inventory' and 'server_ai_reads_v1' in bridge.hello.get('capabilities', []):
        required_capability = 'server_ai_reads_v1'
    if required_capability and required_capability not in bridge.hello.get('capabilities', []):
        raise PreDispatchRejected(f'Requires {required_capability}; no action started')
    if name in ('mine_wall', 'place_wall', 'hammer_tile', 'interact_tile') and 'server_confirmed_interactions_v1' not in bridge.hello.get('capabilities', []):
        raise PreDispatchRejected('Requires server_confirmed_interactions_v1 (CompanionBridge 0.1.37+); no action started')
    if name in READS:
        reply = await command(bridge, 'query_atomic', operation=name, arguments=arguments)
        data = reply.get('data')
        if not isinstance(data, dict):
            raise BridgeError('Missing atomic query data')
        return {'valid': data.get('available') is True, **data}
    if name == 'stop_action':
        return await stop(bridge)
    start_task, action = None, None
    landing_invalid = False
    placement_invalid = False
    placement_identity = None
    try:
        initial = await command(bridge, 'get_movement')
        params = {'operation': name, 'arguments': arguments, 'expectedGeneration': initial['movement']['generation']}
        if chat_id is not None:
            params['chatId'] = chat_id
        track(start_attempted=True)
        start_task = asyncio.create_task(command(bridge, 'start_atomic', **params))
        reply = await asyncio.shield(start_task)
        action = reply['movement']['action']
        track(action=action)
        if action is None or reply['status'] != 'movement_started' or action.get('operation') != name:
            raise BridgeError('Missing atomic start receipt')
        landing_identity = None
        if name == 'navigate_to_landing_region':
            landing_invalid = True
            verify_landing_start(action, arguments, server_ai='server_ai_atomic_v1' in bridge.hello.get('capabilities', []))
            landing_invalid = False
            landing_identity = dict(action)
        if name == 'place_tile' and 'server_ai_place_tile_v1' in bridge.hello.get('capabilities', []):
            placement_invalid = True
            if (action.get('targetTile') != arguments
                    or any(type(value) is not int for value in action['targetTile'].values())):
                raise BridgeError('Placement target missing or different in start receipt')
            placement_invalid = False
            placement_identity = dict(action)
        action_id = action['actionId']
        deadline = asyncio.get_running_loop().time() + action['durationMs'] / 1000 + 1
        while action['status'] == 'running':
            if asyncio.get_running_loop().time() >= deadline:
                stopped = await stop(bridge, action_id, action)
                return {**stopped, 'status': 'timeout' if stopped['valid'] else 'unknown', 'completed': False,
                        'cleanupReason': stopped['reason'], 'reason': 'supervisor_deadline'}
            await asyncio.sleep(0.1)
            reply = await command(bridge, 'get_movement')
            current = reply['movement']['action']
            if landing_identity is not None and current is not None and not same_action(landing_identity, current):
                landing_invalid = True
                raise BridgeError('Landing action target/session/generation/world changed')
            if current is None or not same_action(action, current):
                track(uncertain=True)
                return {'valid': False, 'status': 'unknown', 'reason': 'action_ownership_changed'}
            action = current
            track(action=action)
            if action['status'] == 'running':
                guards = {} if landing_identity is None else {'expectedGeneration': landing_identity['generation'],
                                                              'targetRegion': landing_identity['targetRegion']}
                try:
                    await command(bridge, 'renew_move', actionId=action_id, **guards)
                except BridgeError as error:
                    if error.status != 'action_changed':
                        raise
                    # The game may finish between our running snapshot and the
                    # renewal. Accept only the exact owned terminal action, then
                    # use the normal release-evidence verifier; never retry it.
                    reply = await command(bridge, 'get_movement')
                    current = reply['movement']['action']
                    if placement_identity is not None and not same_action(placement_identity, current):
                        placement_invalid = True
                    if landing_identity is not None and not same_action(landing_identity, current):
                        landing_invalid = True
                    if (not same_action(action, current)
                            or current.get('status') not in ('completed', 'failed', 'timeout', 'cancelled')):
                        raise
                    action = current
                    track(action=action)
        return await terminal(bridge, action, reply['movement']['generation'])
    except asyncio.CancelledError:
        try:
            if action is None and start_task is not None:
                reply = await start_task
                action = reply['movement']['action']
                track(action=action)
            if action is not None:
                track(cleanup=await stop(bridge, action['actionId'], action))
            elif start_task is not None:
                track(uncertain=True)
        except (BridgeError, OSError, KeyError, ValueError):
            track(uncertain=True)
        raise
    except (BridgeError, OSError, KeyError, ValueError) as error:
        if action is not None:
            try:
                stopped = await stop(bridge, action['actionId'], action)
                if landing_invalid or placement_invalid:
                    return {**stopped, 'valid': False, 'status': 'unknown', 'completed': False, 'reason': str(error)}
                return {**stopped, 'supervisorError': str(error)}
            except (BridgeError, OSError, KeyError, ValueError):
                pass
        track(uncertain=bool(start_task is not None))
        return {'valid': False, 'status': 'failed' if getattr(error, 'status', None) else 'unknown', 'completed': False, 'reason': str(error)}
