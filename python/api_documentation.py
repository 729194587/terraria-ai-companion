"""Supplementary Lua documentation. Parameter and result schemas stay with their owners."""
import re

import atomic_operations as atomic
import api_result_contracts as results
import program_tasks


OBSERVATION_RESULT = ('观察通常返回{valid,available,data,worldSessionId,actorGeneration,actorSlot,gameUpdate,observedAtUnixMs,clock}；'
                      'ServerAiBridge附加连接binding；该字段来自Python，不是C#查询封套的一部分。'
                      '例外见各API。多次调用非同帧；coverage/complete/truncated/未知各自独立；runtimeInventory是宿主AI背包摘要。')

def loadout_example(name, schema, capabilities):
    if name == 'configure_combat_loadout':
        if 'server_ai_combat_inventory_v1' in capabilities and {'primary','secondary','summon'} <= schema['properties'].keys():
            return '''local q=api("inspect_inventory",{mode="hotbar"})
if not q.valid or not q.available then return q end
local roles={melee="primary",ranged="secondary",magic="secondary",summon="summon"}
local config={}
for _,candidate in ipairs(q.data.combatCandidates) do
    local role=roles[candidate.family]
    if config[role] then return {chooseSlotFor=role} end
    for _,row in ipairs(q.data.inventory) do
        if row.slot==candidate.slot then
            config[role]={hotbarKey=row.hotbarKey,expectedTypeId=row.typeId,inventoryToken=row.inventoryToken}
        end
    end
end
if not (config.primary or config.secondary) then return {missingAttackWeapon=true} end
return api("configure_combat_loadout",config)'''
        return '''local q=api("inspect_inventory",{mode="selected"})
if not q.valid or not q.available then return q end
local row=q.data.inventory[1]
return api("configure_combat_loadout",{primary={hotbarKey=row.hotbarKey,expectedTypeId=row.typeId,inventoryToken=row.inventoryToken}})'''


def help_result_contract(name):
    if name == 'inspect_housing':
        return ('data.status 为 valid、invalid 或 unknown；failureStage 和 reason 说明原生判定。',
                '外层 valid 表示观察可用。未执行阶段返回 null。homeTile 是评分选出的住所格。')
    if name == 'build_diff':
        return ('data.entries 逐项目给出绝对坐标、占地、inReach 与 status；matched/pending/placeable 汇总，materials 给出所需、已有和缺少的数量。',
                '只读快照。already_matches 表示采样时已满足；placeable 是当前预检结果；unknown_or_not_loaded 表示未知。')
    if name=='configure_combat_loadout':
        return ('动作回执，evidence 含版本与各武器槽。','每次完整替换配置，省略的角色会清除；空表清空全部。示例按所需配装执行。先用 query_combat_loadout 查看已有角色，保留的角色也要重新取 token。')
    if name == 'wait':
        return ('{valid,status,elapsedMilliseconds,clock,effectStatus}',
                'completed表示宿主计时结束；clock为host_monotonic，effectStatus为not_applicable。')
    if name == 'query_task':
        return (
            '外层 Program Task 视图：{taskId,operation,status,valid,completed,dispatchState,origin,retryAllowed,'
            'action?,result?,resultSequence?}。未知 taskId 返回 {status="unknown",taskId,reason="unknown_task",retryAllowed=false}。'
            'action 是最近的原生动作快照，action.task 是游戏任务视图；终态 result 是宿主核验后的动作回执，'
            '其中 result.task 也可携带游戏任务视图。游戏任务视图含 phase、status、reason、progress、currentChild、lastResult 和 result。'
            '受理阶段或未创建游戏任务槽时可能只有精简视图；执行中游戏任务的最终 result 为 null。',
            'phase 描述执行阶段，terminal 表示已结束，结果以 status 和回执为准。'
            'currentChild 是当前子任务的视图或 null；lastResult 是最近结束的子任务回执，可能失败。'
            'progress.children 是有界的已结束子任务历史，childrenTruncated/omittedChildren 表示裁剪；'
            'settledChildren 统计所有已结束子任务，completedChildren 只统计成功项。'
            'progress.stage/activity 描述执行阶段和活动，target 描述目标，search/planning/opening 提供局部规划或开路信息。'
            'dugTiles/placedTiles 和 verifiedTerrainActions 记录已核验的开路改造，不能当作目标矿块完成数量。'
            '要报告部分完成，检查子任务 result 的具体 evidence、effectVerified 和 inputsReleased，说明已核验的效果；'
            '角色位移、动作成功、阶段变化和子任务计数都不能单独证明任务目标取得进展。'
            '整体完成以外层 completed/status 和最终 result 的操作专属效果及释放证据为准；'
            '游戏任务视图中的 result 属于该层任务，子任务的 result 或根任务 lastResult 不代表根任务完成。'
            'failed/timeout/cancelled 时查看最终 reason 和证据，仍可报告已核验的部分效果。'
            'unknown 表示结果未确认，既不是成功也不是普通失败，不据此重试。'
            '若 actionResults.items[].summaryOnly 或 runtimeTasks.results[].resultSummaryOnly 为 true，'
            '用原 taskId 调用 query_task 获取当前连接保留的完整任务回执；'
            '查询无法补齐游戏侧已经裁剪的子任务历史。')
    if name in program_tasks.NAMES:
        return ('任务视图：{taskId,operation,status,valid,completed,dispatchState,action?,result?,waitStatus?}',
                '受理或running不等于完成；await_task的waitStatus=timeout仅结束等待。'
                '终态查看result和结束证据。宿主可在失败、取消或unknown时终止Lua并保留任务视图。')
    if name in atomic.READS:
        returns = ('{valid,available,player,motion,...}；成功时player与motion位于顶层。'
                   if name == 'get_player_state' else OBSERVATION_RESULT)
        return returns, 'valid/available表示是否取得当前观察；同时检查具体数据的状态、时效、覆盖和截断。'
    statuses = ('只有valid、completed和已知结束证据才支持动作完成；'
                '核对操作专属effect，unknown停止变更，不自动重试。失败可能使宿主直接终止Lua。')
    if name in atomic.CONTAINER_BATCHES:
        statuses += 'moves列出每次已核验的搬运。blocked列出剩余源槽及原因。部分效果返回failed和effectStatus=partial。无符合条件的物品时completed且effectStatus=none。每次最多128次槽位搬运。'
    if name in atomic.CONTAINER_TRANSFERS:
        statuses += '容量部分满足时返回failed且evidence.effectStatus=partial，并提供movedCount、remainingCount和blockedSlot。再次操作前重新观察两侧。'
    if name == 'stop_action':
        statuses += 'noOp=true表示无当前动作；停止回执不证明原动作成功。'
    return '{valid,status,completed,reason,actionId?,evidence?,endEvidence?,endEvidenceStatus?,...}', statuses



READ_EXAMPLES = {
    'get_player_state': '''local q=api("get_player_state",{})
if not q.valid or not q.available then return q end
return {player=q.player,motion=q.motion}''',
    'inspect_inventory': '''local q=api("inspect_inventory",{mode="full"})
if not q.valid or not q.available then return q end
local counts={}
for _, item in ipairs(q.data.inventory) do
    if not item.empty then
        counts[item.contentKey]=(counts[item.contentKey] or 0)+item.stack
    end
end
return counts''',
    'plan_materials': 'return api("plan_materials",{typeId=4956,count=1})',
    'query_item_sources': 'return api("query_item_sources",{typeId=38})',
    'query_item_info': 'return api("query_item_info",{typeId=8})',
    'resolve_content': 'return api("resolve_content",{kind="item",query="Terraria/Torch"})',
    'query_recipes': 'return api("query_recipes",{resultType=8,availableOnly=false})',
    'scan_entities': '''local q=api("scan_entities",{category="npc",radius=320})
if not q.valid or not q.available then return q end
return {status=q.data.status,first=q.data.items[1],matched=q.data.matchedCount,truncated=q.data.truncated}''',
    'inspect_container': 'return api("inspect_container",{})',
    'inspect_housing': 'return api("inspect_housing",{x=100,y=200})',
    'build_diff': 'return api("build_diff",{blueprint={origin={x=100,y=200},tiles={{x=0,y=0,itemType=9}}}})',
    'observe_scene': '''local q=api("observe_scene",{layers={"objects"}})
if not q.valid or not q.available then return q end
return {objects=q.data.objects,coverage=q.data.coverage}''',
    'query_map': '''local q=api("query_map",{mode="overview"})
if not q.valid or not q.available then return q end
return {row=q.data.rows[1],x=q.data.originX,y=q.data.originY,cellSize=q.data.cellSize,knowledge=q.data.knowledge}''',
}

# Examples read the first tile row (world Y = data.map.originY).
SCENE_MAP_ROWS = {
    'local': {
        'path': 'data.map.rows',
        'encoding': '每行是已绘制的地图字符串，可直接读取；Lua 行号从 1 开始。',
        'example': '''local q=api("observe_scene",{layers={"map"}})
if not q.valid or not q.available then return q end
return q.data.map.rows[1]''',
    },
    'region': {
        'path': 'data.map.terrain.rows',
        'encoding': '地形网格 encodingVersion=2。逐行检查 encoding：rle 的 data 交替存放长度与索引；'
                    'hex_indices 按该行 indexDigits 个十六进制字符读取一个索引。索引从 0 起，Lua 调色板从 1 起。'
                    '角色与物件标记另见 data.map.actor、data.map.markers。',
        'example': '''local q=api("observe_scene",{scale="region",layers={"map"}})
if not q.valid or not q.available then return q end
local grid=q.data.map.terrain
assert(grid.encodingVersion==2)
local row=grid.rows[1]
local cells={}
if row.encoding=="rle" then
    for i=1,#row.data,2 do
        local glyph=grid.palette[row.data[i+1]+1]
        for n=1,row.data[i] do cells[#cells+1]=glyph end
    end
elseif row.encoding=="hex_indices" then
    local digits=row.indexDigits
    for i=1,#row.data,digits do
        local index=tonumber(string.sub(row.data,i,i+digits-1),16)
        cells[#cells+1]=grid.palette[index+1]
    end
else
    error("unknown map row encoding")
end
return table.concat(cells)''',
    },
}
CRAFT = """local q=api("query_recipes",{resultType=8,availableOnly=true})
if not q.valid or not q.available then return q end
for _,r in ipairs(q.data.items) do
    if r.supported and r.available and r.hasOutputSpace and r.output.typeId==8 then
        return api("craft_recipe",{recipeId=r.recipeId,recipeToken=r.recipeToken,crafts=1})
    end
end
return q"""
TRANSFER = """local bag=api("inspect_inventory",{mode="slots",slots={0}})
if not bag.valid or not bag.available then return bag end
local chest=api("inspect_container",{})
if not chest.valid or not chest.available or chest.data.status~="open" then return chest end
local row=bag.data.inventory[1]
local c=chest.data.container
local slot=c.slots[1]
if not row or type(row.inventoryToken)~="string" then return bag end
if SOURCE.stack<1 then return {status="empty_source"} end
return api("OP",{inventorySlot=row.slot,containerSlot=slot.slot,count=1,
    inventoryToken=row.inventoryToken,slotToken=slot.slotToken,containerToken=c.containerToken})"""
SHOP = """local q=api("scan_entities",{category="npc",radius=320})
if not q.valid or not q.available then return q end
for _,npc in ipairs(q.data.items) do
    if npc.typeId==17 and type(npc.details.entityRef)=="string" then
        return api("inspect_npc_shop",{entityRef=npc.details.entityRef})
    end
end
return q"""

PREREQUISITES = {
    'inspect_housing': '示例使用绝对 Tile 坐标；验收时替换为目标房间的室内格。',
    'build_diff': '替换为玩家要求的蓝图和绝对 Tile 原点。',
    'submit_task': '从 parameters 选择当前开放的 operation，并提供该操作所需的 arguments。施工前调查同一蓝图。',
    'inspect_container': '先在游戏中打开指定箱子，检查 data.status 后再访问 data.container.slots。',
    'craft_recipe': '选择当前可用配方；令牌来自同次配方观察，crafts 是执行次数。',
    'deposit_container_item': '确认已打开目标箱子且已授权将背包槽 0 中的一件物品存到箱子槽 0。',
    'withdraw_container_item': '确认已打开目标箱子且已授权从箱子槽 0 取一件物品至背包槽 0。',
}
RELATED = {
    'inspect_inventory': ('hold_inventory_item','inspect_container'),
    'plan_materials': ('resolve_content', 'query_recipes', 'query_item_sources'),
    'query_item_sources': ('resolve_content', 'query_item_info'),
    'query_item_info': ('resolve_content',),
    'resolve_content': ('query_item_info','query_recipes','scan_tiles'),
    'query_recipes': ('resolve_content','craft_recipe'),
    'craft_recipe': ('query_recipes','inspect_inventory'),
    'inspect_container': ('open_container','deposit_container_item','withdraw_container_item'),
    'build_diff': ('submit_task','inspect_inventory','inspect_housing'),
    'submit_task': ('build_diff','await_task','query_task'),
    'scan_entities': ('inspect_npc_shop','inspect_npc_housing'),
    'inspect_housing': ('build_diff','inspect_npc_housing'),
}
RETURN_NOTES = {
    'inspect_inventory': '必填 mode。data.inventory 是槽位行数组；row.slot 是游戏槽位编号，Lua 数组下标从 1 开始。',
    'plan_materials': 'count 是目标背包总量。steps 按先材料后产物排列，recipeIds 为候选，recipeId 为选中项。inventoryUsed 是已抵扣背包，deficits 是仍需外部获取的材料及数量。cycle 表示循环需外部起点。按配方标称用量估算，不保证条件满足或路径全局最优。',
    'query_item_sources': 'sources 每行是 NPC 专属掉落表的一条原生报告。probability 为规则条件成立时的报告概率（1 为必掉），数量含两端。同一 NPC 可有多行。条件附类型和可用的本地化文本，未判定当前是否满足。排除全局掉落和开袋奖励。空数组表示没有匹配报告。',
    'query_item_info': 'typeId 取自 resolve_content 当前候选。data.tooltip 是普通本地化描述行数组，data.extraTooltip 仅有扩展时返回，可含多行。stats.valueCopper 以铜币为单位。未注册的 ID 返回 available=false。',
    'resolve_content': '检查 data.status 和 data.candidates；唯一精确匹配时取 typeId。',
    'query_recipes': 'data.items 含 output.count、ingredients、stations、conditions 和 hasOutputSpace；检查 truncated 和 offset。',
    'craft_recipe': 'evidence.completedCrafts 为完成次数，producedCount 为件数；完成时核对 inventoryVerified。部分制作保留原生回执。',
    'inspect_container': 'data.status=open 时 data.container 含 containerToken 和 slots；每行含 slot、stack 和 slotToken。',
    'scan_entities': 'data.items 内城镇 NPC 的 name 是个人名字，details.contentKey 和 details.typeName 分别是注册键和本地化种类名（旧后端可能缺失），centerWorldPixels.x/y 是世界像素中心；details.entityRef 是当前实例引用。检查范围与 truncated。',
}


def _lua_value(value):
    """Render validated read arguments (strings, integers and arrays) in a Lua example."""
    if isinstance(value, list):
        return '{' + ','.join(_lua_value(v) for v in value) + '}'
    if isinstance(value, str):
        escaped = re.sub(r'["\\\x00-\x1f]', lambda m: '\\%03d' % ord(m[0]), value)
        return '"' + escaped + '"'
    return str(value)


def read_usage_help(name, schema, supplied):
    selector, conditions = atomic.READ_PARAMETER_RULES[name]
    projected = atomic.read_parameter_schema(name, schema, supplied)
    required = projected['required']
    missing = [key for key in required if key not in supplied]
    # Missing top-level arguments are useful information in help. Still check every supplied value.
    partial = {**projected, 'required': [key for key in required if key in supplied]}
    args = atomic.validate_schema(name, supplied, partial)
    data = results.READ_DATA_SCHEMAS[name]
    fields = data['properties']
    summary = dict(selector=selector, mode=args.get(selector), parameters=projected['properties'], required=required,
                   missing=missing, paths=['data.' + key for key in fields],
                   dataFields=dict(required=data['required'], optional=sorted(fields.keys() - data['required'])),
                   examples=[])
    if selector not in args:
        summary['allowedWhen'] = {key: {selector: list(values)} for key, values in conditions.items()
                                  if key in projected['properties']}
    if not missing:
        values = ','.join(k + '=' + _lua_value(v) for k, v in args.items())
        example = (f'local q=api("{name}",{{{values}}})\n'
                   'if not q.valid or not q.available then return q end\nreturn q.data')
        summary.update(example=example, examples=[example])
    return summary


# Only expose argument-specific help when a selector changes inputs or the payload.
ARGUMENT_HELP_APIS = frozenset(atomic.READ_PARAMETER_RULES) | {'inspect_inventory', 'observe_scene'}


def usage_help(name, schema, supplied, capabilities=()):
    """Project one read usage without querying the game or relaxing dispatch validation."""
    if name in atomic.READ_PARAMETER_RULES:
        return read_usage_help(name, schema, supplied)
    if name not in ARGUMENT_HELP_APIS:
        raise ValueError(f'forArgs is unavailable for {name}')
    props = schema['properties']
    unknown = set(supplied) - props.keys()
    if unknown:
        raise ValueError('Unknown or unavailable argument: ' + ', '.join(sorted(unknown)))
    args = dict(supplied)
    required = list(schema.get('required', []))
    allowed = list(props)
    if name == 'inspect_inventory':
        mode = args.get('mode')
        if 'mode' not in args:
            if not args:
                return dict(required=required, parameters=props, missing=['mode'],
                            usage='Choose a mode; use full for inventory totals.')
            mode = 'item' if 'item' in args else 'slots'
            args['mode'] = mode
        if isinstance(mode, str) and mode in atomic.INVENTORY_MODE_ARGUMENTS:
            key = atomic.INVENTORY_MODE_ARGUMENTS[mode]
            required.append(key)
            args.setdefault(key, 'Terraria/Wood' if mode == 'item' else [0])
        allowed = required
    args = atomic.validate(name, args)
    call = 'api("' + name + '",{' + ','.join(k+'='+_lua_value(v) for k,v in args.items()) + '})'
    prefix = 'local q=' + call + '\nif not q.valid or not q.available then return q end\n'
    if name == 'inspect_inventory':
        paths = ['data.inventory']
        reads = 'return q.data.inventory'
        status = 'Check valid and available before reading the requested slots.'
        if mode == 'full':
            reads = READ_EXAMPLES[name].split('\n', 2)[2]
        elif mode == 'item':
            paths = ['data.status','data.count','data.slots','data.candidates']
            status = 'ok: count and matching slots; unknown_item/ambiguous_item: candidates and truncated.'
            reads = 'if q.data.status~="ok" then return q.data end\nreturn {count=q.data.count,slots=q.data.slots}'
        elif mode == 'slots':
            status += ' Check data.status for inventory_slot_out_of_range.'
            reads = 'if q.data.status~="ok" then return q.data end\n' + reads
    else:
        mode = args.get('scale', 'local')
        layers = args.get('layers') or (atomic.REGION_DEFAULT_LAYERS if mode == 'region' else atomic.SCENE_LAYERS[:-1])
        if set(args.get('layers', ())) - set(props['layers']['items']['enum']):
            raise ValueError('Unavailable scene layer')
        paths = ['data.'+layer for layer in layers]
        if args.get('includePassages'):
            paths.append('data.passages')
        status = 'Check valid/available, coverage and the requested collections’ truncation flags.'
        reads = 'return {' + ','.join(layer+'=q.data.'+layer for layer in layers) + ',coverage=q.data.coverage}'
        if 'map' in layers:
            summary = SCENE_MAP_ROWS[mode]
            paths[paths.index('data.map')] = summary['path']
            reads = summary['example'].split('\n', 2)[2]
            status += ' ' + summary['encoding']
        allowed = [k for k in allowed if k not in atomic.SCENE_EXCLUDED_PARAMS[mode]]
    parameters = {k:props[k] for k in allowed}
    selector = 'mode' if name == 'inspect_inventory' else 'scale'
    if selector in parameters:
        parameters[selector] = {**parameters[selector], 'enum': [mode]}
    if name == 'observe_scene' and mode == 'region':
        layer = props['layers']
        names = [n for n in atomic.REGION_LAYERS if n != 'resources' or 'server_ai_region_resources_v1' in capabilities]
        if set(args.get('layers', ())) - set(names):
            raise ValueError('Unavailable regional layer')
        parameters['layers'] = {**layer, 'items': {**layer['items'], 'enum': names}}
        parameters['layers']['maxItems'] = len(names)
        status += (f' Default dimensions {atomic.REGION_DEFAULT_WIDTH}x{atomic.REGION_DEFAULT_HEIGHT};'
                   f' width * height <= {atomic.REGION_MAX_AREA}.')
    if name == 'inspect_inventory':
        # Each status has its own payload. Preserve the required/optional distinction.
        fields = {}
        for branch in results.INVENTORY['anyOf']:
            props = branch['properties']
            if mode not in props['inventoryScope']['properties']['mode']['enum']:
                continue
            state = props.get('status', {})
            for label in state.get('enum', [state.get('const', 'full')]):
                fields[label] = dict(required=branch['required'], optional=sorted(props.keys() - branch['required']))
    else:
        variant = 'SceneRegional' if mode == 'region' else 'SceneLocal'
        if 'resources' in layers:
            variant += 'Resources'
        branch = results.DEFS[variant]
        fields = list(branch['required'])
        for condition in branch['allOf']:
            if condition['if']['properties']['includedLayers']['contains']['const'] in layers:
                fields.extend(condition['then']['required'])
        if args.get('includePassages'):
            fields.append('passages')
            reads = 'return q.data.passages'
    return dict(mode=mode, parameters=parameters, required=required, dataFields=fields,
                missing=[k for k in required if k not in supplied], paths=paths,
                statusSemantics=status, example=prefix+reads, examples=[prefix+reads])


def documentation(name, schema, definitions, capabilities):
    returns, semantics = help_result_contract(name)
    example = READ_EXAMPLES.get(name)
    related = RELATED.get(name, ())
    prerequisite = PREREQUISITES.get(name, '')
    if name == 'craft_recipe':
        example = CRAFT
    elif name == 'equip_item':
        prerequisite = ('例子把矿工头盔穿到功能头部槽。sourceToken 来自背包来源格的 inventoryToken，'
                        'targetToken 来自要穿入的装备槽；空装备槽也有令牌。购买后重新读槽位。')
        related = ('inspect_inventory', 'unequip_item')
        example = '''local wanted = 88 -- 示例：已确认的矿工头盔物品类型
local bag = api("inspect_inventory",{mode="full"})
if not bag.valid or not bag.available then return bag end
local source, head
for _,item in ipairs(bag.data.inventory) do
    if item.typeId == wanted then source = item.slot; break end
end
for _,slot in ipairs(bag.data.functionalEquipmentSlots) do
    if slot.kind == "head" and slot.enabled then head = slot; break end
end
if not source or not head then return "未找到目标物品或可用头部槽" end
local fresh = api("inspect_inventory",{mode="slots",slots={source}})
if not fresh.valid or not fresh.available then return fresh end
local item = fresh.data.inventory[1]
if not item or item.slot ~= source or item.typeId ~= wanted then return "来源物品已变化" end
return api("equip_item",{sourceSlot=source,sourceToken=item.inventoryToken,
    targetSlotId=head.slotId,targetToken=head.targetToken})'''
    elif name in atomic.CONTAINER_TRANSFERS:
        example = TRANSFER.replace('SOURCE', 'row' if name.startswith('deposit') else 'slot').replace('OP', name)
        related = ('inspect_inventory','inspect_container')
    elif name == 'open_npc_dialogue':
        example = SHOP.replace('return api("inspect_npc_shop"', 'return api("open_npc_dialogue"')
        related = ('scan_entities','inspect_npc_shop','close_npc_dialogue')
    elif name == 'close_npc_dialogue':
        example = 'return api("close_npc_dialogue",{})'
        related = ('open_npc_dialogue',)
    elif name == 'inspect_npc_shop':
        example = SHOP
        related = ('scan_entities','quote_npc_purchase','quote_npc_sale')
    elif name in ('buy_npc_item', 'quote_npc_purchase'):
        quote = 'local price=api("quote_npc_purchase",{entityRef=npc.details.entityRef,itemType=8})'
        purchase = ('if price.valid and price.available and price.data.canPurchase and type(price.data.quoteToken)=="string" then\n'
                    '            return api("buy_npc_item",{entityRef=npc.details.entityRef,quoteToken=price.data.quoteToken})\n'
                    '        end\n        ') if name == 'buy_npc_item' else ''
        example = SHOP.replace('return api("inspect_npc_shop",{entityRef=npc.details.entityRef})', quote+'\n        '+purchase+'return price')
        prerequisite = '示例目标为普通商人的一支火把。支持批量购买时在报价中传 quantity（1..50）；令牌绑定数量，逐件核验。中途满包等已知拒绝返回 failed；evidence.effectStatus=partial 时 verifiedQuantity 是已成交件数。执行购买前确认物品和支出已经授权。'
        related = ('scan_entities','inspect_npc_shop','quote_npc_purchase')
    elif name == 'interact_tile':
        example = '''-- x、y 为已找到的拉杆组成格
local tile=api("inspect_tile",{x=x,y=y})
if not tile.valid or not tile.available or not tile.data.interaction then return tile end
if tile.data.interaction.active then return tile end
return api("interact_tile",{target=tile.data.interaction.target})'''
    elif name == 'configure_combat_loadout':
        example = loadout_example(name, schema, capabilities)
    elif name == 'submit_task' and 'build_place' in schema['properties']['operation']['enum']:
        example = """-- 将示例蓝图替换为已授权的坐标和材料
local plan={origin={x=100,y=200},tiles={{x=0,y=0,itemType=9}}}
local q=api("build_diff",{blueprint=plan})
if not q.valid or not q.available or q.data.pending==0 then return q end
for _,e in ipairs(q.data.entries) do
    if e.survey=="unknown_or_not_loaded" then return q end
end
return api("submit_task",{operation="build_place",arguments={blueprint=plan}})"""
    elif not example and name in atomic.READS and not schema.get('required'):
        example = f'return api("{name}",{{}})'
    if example and not set(re.findall(r'api\("([a-z_]+)"', example)) <= definitions.keys():
        example = None
    if not example and not prerequisite:
        if 'entityRef' in schema['properties']:
            prerequisite = '从当前 NPC 观察的 details.entityRef 取目标实例；涉及付款时先获取同一对象和操作的有效报价。'
        else:
            prerequisite = '按 parameters 准备已授权的输入；涉及槽位、目标引用或报价时先取得新鲜观察。'
    examples = [example] if example else []
    if name == 'scan_entities' and 'navigate_to_x' in definitions:
        examples.append('''-- 已获授权接近指定 NPC 时，按注册键或本地化种类名选目标。
local function approachNpc(target)
    local q=api("scan_entities",{category="npc",radius=1600})
    if not q.valid or not q.available then return q end
    for _,n in ipairs(q.data.items) do
        if n.details.contentKey==target or n.details.typeName==target then
            return api("navigate_to_x",{targetX=n.centerWorldPixels.x})
        end
    end
    return {matched=q.data.matchedCount,truncated=q.data.truncated}
end''')
    if name == 'observe_scene' and 'server_ai_scene_region_v1' in capabilities:
        examples.append('''local q=api("observe_scene",{width=100,height=40,layers={"objects"},maxObjects=32})
if not q.valid or not q.available then return q end
return {objects=q.data.objects,coverage=q.data.coverage}''')
    if 'open_npc_dialogue' in definitions and name in (
            'inspect_npc_shop', 'quote_npc_purchase', 'buy_npc_item', 'quote_npc_sale', 'sell_npc_item'):
        prerequisite = '先用 open_npc_dialogue 与目标商人交谈。货单、报价和成交均要求同一连接保持与该商人的对话。' + prerequisite
    return dict(returns=RETURN_NOTES.get(name, returns), statusSemantics=semantics,
                prerequisites=prerequisite, examples=examples,
                example=example or '-- '+prerequisite, related=[n for n in related if n in definitions])
