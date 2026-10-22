"""LLM atomic operations. One Bridge session, game-thread actions and release receipts."""
import asyncio
import math
from control import BridgeError


def tool(name, description, properties=None, required=None):
    return {"type": "function", "function": {"name": name, "description": description,
        "parameters": {"type": "object", "properties": properties or {}, "required": required or [], "additionalProperties": False}}}


TILE = {"x": {"type": "integer", "minimum": 0, "maximum": 100000},
        "y": {"type": "integer", "minimum": 0, "maximum": 100000}}
TOOLS = [tool("get_player_state", "读取真实玩家位置/速度/生命和地面支撑及运动模型支持状态。"),
    tool("observe_terrain", "复用get_terrain读取45×29局部碰撞及同次玩家状态；Tile为16像素。"),
    tool("inspect_tile", "读取指定Tile类型、Frame、形状、液体、墙/线/颜色；越界或未加载显式未知。", TILE, ["x", "y"]),
    tool("scan_entities", "按世界像素半径扫描客户端已知NPC、掉落物、其他玩家；NPC entityRef仅当前世界实例有效；截断不等于没有。",
         {"category": {"type": "string", "enum": ["all", "npc", "item", "player"]}, "radius": {"type": "number", "minimum": 1, "maximum": 1600}}, ["category", "radius"]),
    tool("inspect_inventory", "读取背包、快捷栏、选中槽位和原版装备数组；Mod自定义装备槽未知。"),
    tool("get_world_state", "读取客户端昼夜、天气、事件、生物群系和深度。"),
    tool("navigate_to_x", "复用连续导航至玩家左上角水平世界像素targetX；30秒上限，普通角色运动限制仍适用；arrived由游戏物理状态校验。",
         {"targetX": {"type": "number", "minimum": 0, "maximum": 1000000}}, ["targetX"]),
    tool("navigate_to_landing_region", "二维落脚导航：全部为世界像素，Y向下；minPlayerLeftX/maxPlayerLeftX是玩家碰撞框左边缘的闭区间，surfaceY是脚底目标水平表面高度（非玩家顶部）。原有普通角色/实心平顶限制；30秒上限。仅实际进入X区间、脚底高度误差<=0.5px、安全着地且停稳后完成，不能只经过目标X。",
         {key: {"type": "number", "minimum": 0, "maximum": 1000000}
          for key in ("minPlayerLeftX", "maxPlayerLeftX", "surfaceY")},
         ["minPlayerLeftX", "maxPlayerLeftX", "surfaceY"]),
    tool("select_hotbar_slot", "选择0..9快捷栏槽位并核对选中状态。", {"slot": {"type": "integer", "minimum": 0, "maximum": 9}}, ["slot"]),
    tool("use_item", "用当前物品完成一次原生使用周期；target可选世界像素{x,y}。completed只证明使用周期，具体效果需感知核验。默认缩放及保守80像素距离。",
         {"target": {"type": "object", "properties": {"x": {"type": "number", "minimum": 0, "maximum": 1000000}, "y": {"type": "number", "minimum": 0, "maximum": 1000000}}, "required": ["x", "y"], "additionalProperties": False}}),
    tool("mine_tile", "以选中镐原生挖掘80像素内Tile，移除才成功；10秒上限。不自动选工具/移动。", TILE, ["x", "y"]),
    tool("attack_entity", "以当前物品攻击scan_entities给出的NPC entityRef；80像素内，生命下降才成功，失活但无伤害证据失败，其他伤害来源可能存在。10秒上限。",
         {"entityRef": {"type": "string", "minLength": 1, "maxLength": 150}}, ["entityRef"]),
    tool("interact_tile", "首版仅支持单人模式原版门开关，80像素内；其他对象/多人返回具体失败原因。", TILE, ["x", "y"]),
    tool("place_tile", "用选中消耗品在80像素内空格放单格方块；Tile类型与物品消耗共同核验；不支持多格家具或ModItem。10秒上限。", TILE, ["x", "y"]),
    tool("stop_action", "取消当前Agent动作并等待游戏线程输入释放证据，不抢占手动控制。")]
SCHEMAS = {t['function']['name']: t['function']['parameters'] for t in TOOLS}
READS = {'get_player_state', 'observe_terrain', 'inspect_tile', 'scan_entities', 'inspect_inventory', 'get_world_state'}


def validate(name, arguments):
    def check(value, schema):
        kind = schema['type']
        if kind == 'object':
            if not isinstance(value, dict) or set(value) - schema['properties'].keys() or not set(schema.get('required', [])) <= value.keys():
                raise ValueError('Unexpected or missing atomic parameters')
            for key, item in value.items():
                check(item, schema['properties'][key])
        elif kind in ('number', 'integer'):
            if type(value) not in ((int,) if kind == 'integer' else (int, float)) or not math.isfinite(value) or not schema.get('minimum', -math.inf) <= value <= schema.get('maximum', math.inf):
                raise ValueError('Invalid finite coordinate, radius or slot')
        elif kind == 'string':
            if not isinstance(value, str) or not schema.get('minLength', 0) <= len(value) <= schema.get('maxLength', math.inf) or 'enum' in schema and value not in schema['enum']:
                raise ValueError('Invalid category or entity reference')
    if name not in SCHEMAS:
        raise ValueError('Unsupported atomic operation')
    check(arguments, SCHEMAS[name])
    if name == 'navigate_to_landing_region' and arguments['minPlayerLeftX'] > arguments['maxPlayerLeftX']:
        raise ValueError('Landing X bounds must be ordered')


async def command(bridge, cmd, **args):
    return await asyncio.to_thread(bridge.command, cmd, **args)


def result(action, generation):
    return {'valid': True, 'status': action['status'], 'reason': action['reason'],
            'operation': action.get('operation'), 'actionId': action['actionId'],
            'session': action.get('session'), 'actionGeneration': action.get('generation'), 'targetRegion': action.get('targetRegion'),
            'evidence': action.get('evidence'), 'endEvidence': action.get('endEvidence'),
            'endEvidenceStatus': action.get('endEvidenceStatus'), 'controlGeneration': generation,
            'completed': action['status'] == 'completed', 'cancelled': action['status'] == 'cancelled'}


def same_landing_action(expected, current):
    return isinstance(current, dict) and all(current.get(key) == expected.get(key) for key in
        ('actionId', 'session', 'generation', 'worldSessionId', 'operation', 'targetRegion'))


def verify_landing_start(action, arguments):
    region = action.get('targetRegion')
    if (not isinstance(region, dict) or set(region) != set(arguments)
            or any(type(region[key]) not in (int, float) or not math.isfinite(region[key])
                   or not math.isclose(region[key], arguments[key], rel_tol=1e-7, abs_tol=0.001) for key in arguments)
            or type(action.get('session')) is not int or action['session'] <= 0
            or type(action.get('generation')) is not int or action['generation'] < 0
            or not isinstance(action.get('worldSessionId'), str) or not action['worldSessionId']):
        raise BridgeError('Landing target/identity missing or different in start receipt')


async def terminal(bridge, action, generation):
    deadline = asyncio.get_running_loop().time() + 1.0
    while action.get('endEvidenceStatus') == 'pending' and asyncio.get_running_loop().time() < deadline:
        await asyncio.sleep(0.05)
        reply = await command(bridge, 'get_movement')
        current = reply['movement']['action']
        if (current is None or current['actionId'] != action['actionId']
                or action.get('operation') == 'navigate_to_landing_region' and not same_landing_action(action, current)):
            return {'valid': False, 'status': 'unknown', 'reason': 'action_changed_before_release_evidence'}
        action, generation = current, reply['movement']['generation']
    out = result(action, generation)
    end = action.get('endEvidence')
    if (action.get('endEvidenceStatus') != 'known' or not isinstance(end, dict)
            or type(end.get('gameUpdate')) is not int or end['gameUpdate'] < 0
            or not isinstance(end.get('worldSessionId'), str) or not end['worldSessionId']
            or end['worldSessionId'] != action.get('worldSessionId')
            or end.get('phase') not in ('PostUpdateRunSpeeds_before_bridge_input', 'UpdateDead_after_revoke')):
        out.update(valid=False, status='unknown', completed=False, reason='missing_game_thread_release_evidence', terminalActionStatus=action['status'])
    return out


async def stop(bridge, action_id=None):
    reply = await command(bridge, 'get_movement')
    action = reply['movement']['action']
    if action is None:
        return {'valid': True, 'status': 'completed', 'reason': 'no_agent_action', 'noOp': True}
    if action_id is not None and action['actionId'] != action_id:
        return {'valid': False, 'status': 'cancelled', 'reason': 'ownership_changed'}
    if action['status'] == 'running':
        reply = await command(bridge, 'cancel_move', actionId=action['actionId'])
        action = reply['movement']['action']
    return await terminal(bridge, action, reply['movement']['generation'])


async def dispatch(bridge, name, arguments, chat_id=None):
    validate(name, arguments)
    if name in ('get_player_state', 'observe_terrain'):
        reply = await asyncio.to_thread(bridge.get_terrain)
        if name == 'observe_terrain':
            return {'valid': True, **reply}
        return {'valid': True, 'player': reply['state'], 'motion': {k: reply['terrain'][k] for k in ('grounded', 'supported', 'playerWidth', 'playerHeight')}}
    if 'atomic_operations_v1' not in bridge.hello.get('capabilities', []):
        raise BridgeError('Requires atomic_operations_v1; loaded Mod does not support these operations')
    if name == 'navigate_to_landing_region' and 'landing_region_navigation_v1' not in bridge.hello.get('capabilities', []):
        raise BridgeError('Requires landing_region_navigation_v1; no two-dimensional action started')
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
    try:
        initial = await command(bridge, 'get_movement')
        params = {'operation': name, 'arguments': arguments, 'expectedGeneration': initial['movement']['generation']}
        if chat_id is not None:
            params['chatId'] = chat_id
        start_task = asyncio.create_task(command(bridge, 'start_atomic', **params))
        reply = await asyncio.shield(start_task)
        action = reply['movement']['action']
        if action is None or reply['status'] != 'movement_started' or action.get('operation') != name:
            raise BridgeError('Missing atomic start receipt')
        landing_identity = None
        if name == 'navigate_to_landing_region':
            landing_invalid = True
            verify_landing_start(action, arguments)
            landing_invalid = False
            landing_identity = dict(action)
        action_id = action['actionId']
        deadline = asyncio.get_running_loop().time() + action['durationMs'] / 1000 + 1
        while action['status'] == 'running':
            if asyncio.get_running_loop().time() >= deadline:
                stopped = await stop(bridge, action_id)
                return {**stopped, 'status': 'timeout' if stopped['valid'] else 'unknown', 'completed': False, 'reason': 'supervisor_deadline'}
            await asyncio.sleep(0.1)
            reply = await command(bridge, 'get_movement')
            current = reply['movement']['action']
            if current is None or current['actionId'] != action_id:
                return {'valid': False, 'status': 'unknown', 'reason': 'action_ownership_changed'}
            if landing_identity is not None and not same_landing_action(landing_identity, current):
                landing_invalid = True
                raise BridgeError('Landing action target/session/generation/world changed')
            action = current
            if action['status'] == 'running':
                guards = {} if landing_identity is None else {'expectedGeneration': landing_identity['generation'],
                                                              'targetRegion': landing_identity['targetRegion']}
                await command(bridge, 'renew_move', actionId=action_id, **guards)
        return await terminal(bridge, action, reply['movement']['generation'])
    except asyncio.CancelledError:
        try:
            if action is None and start_task is not None:
                reply = await start_task
                action = reply['movement']['action']
            if action is not None:
                await stop(bridge, action['actionId'])
        except (BridgeError, OSError, KeyError, ValueError):
            pass
        raise
    except (BridgeError, OSError, KeyError, ValueError) as error:
        if action is not None:
            try:
                stopped = await stop(bridge, action['actionId'])
                if landing_invalid:
                    return {**stopped, 'valid': False, 'status': 'unknown', 'completed': False, 'reason': str(error)}
                return {**stopped, 'supervisorError': str(error)}
            except (BridgeError, OSError, KeyError, ValueError):
                pass
        return {'valid': False, 'status': 'failed' if getattr(error, 'status', None) else 'unknown', 'completed': False, 'reason': str(error)}
