"""Program-independent receipt mailbox. C# remains the task execution authority."""
import asyncio
import copy
import contextvars
import re
import time
import uuid

import atomic_operations as atomic
import agent_diagnostics as diagnostics
import agent_trace as tracing
from control import BridgeError, NotAdmittedError
from lua_action_results import action_results

NAMES = frozenset({'submit_task', 'query_task', 'await_task', 'cancel_task', 'resume_task'})
OPERATIONS = ('mine_target', 'combat_attack', 'boss_encounter', 'collect_drops', 'place_item', 'build_place')


def available(bridge):
    return ({'server_ai_atomic_v1', 'server_ai_actions_v1', 'server_ai_tasks_v1', 'server_ai_program_tasks_v1'} <= set(getattr(bridge, 'hello', {}).get('capabilities', ()))
            and 'mine_target' in getattr(bridge, 'supported_atomic_operations', ())
            and callable(getattr(bridge, 'task_request', None)))


def validate(name, args):
    if not isinstance(args, dict):
        raise ValueError('Task arguments must be an object')
    if name == 'submit_task':
        if set(args) != {'operation', 'arguments'} or args['operation'] not in OPERATIONS:
            raise ValueError('submit_task requires a supported task operation and its arguments')
        return {**args, 'arguments': atomic.validate(args['operation'], args['arguments'])}
    if name=='cancel_task' and 'retreatTo' in args:
        dest=args['retreatTo']
        if not isinstance(dest,dict) or set(dest)!={'feetX','feetY'} or any(type(v) is not int or not 0<=v<=2**31-1 for v in dest.values()):
            raise ValueError('retreatTo requires nonnegative integer feetX/feetY world pixels')
        validate(name,{k:v for k,v in args.items() if k!='retreatTo'})
        return args
    allowed = {'taskId', 'milliseconds'} if name == 'await_task' else {'taskId'}
    if set(args) - allowed or 'taskId' not in args or not isinstance(args['taskId'], str) or not re.fullmatch('[0-9a-f]{32}', args['taskId']):
        raise ValueError('An explicit taskId (32 lowercase hex characters) is required')
    if name == 'await_task':
        ms = args.get('milliseconds', 1000)
        if type(ms) is not int or not 1 <= ms <= 30000:
            raise ValueError('milliseconds must be an integer in 1..30000')
        return {**args, 'milliseconds': ms}
    return args


def definitions(*, regroup=False, excavation=False, combat=False, weapon_button=False, boss=False, members=False, strategy=False, collection=False, placement=False, building=False):
    rows = []
    for name in sorted(NAMES):
        properties = {'taskId': {'type': 'string', 'pattern': '^[0-9a-f]{32}$'}}
        required = ['taskId']
        description = {'submit_task': '提交独立游戏任务；仅等待ACK。Program/新聊天结束不取消任务；受理unknown不重放。',
            'query_task': '查询指定任务的权威状态与已核验结果。',
            'await_task': '有界等待指定任务；waitStatus=timeout仅表示等待到期，不取消任务或改变任务期限。',
            'cancel_task': '显式取消指定任务；仅可信终态和释放证据证明停止。',
            'resume_task': '仅对危险中断且已安全释放的任务显式重新提交；新身份，不自动重试，游戏侧重新验证环境/目标/工具。'}[name]
        if name == 'submit_task':
            operations = ['mine_target']
            if combat:
                operations.append('combat_attack')
            if boss:
                operations.append('boss_encounter')
                roots = '克苏鲁之眼、吞噬者头、毁灭者头、双子、骷髅王头、月总核心' if members else '克苏鲁之眼'
                if strategy:
                    description+=' boss_encounter 先配武器，再用 discover_bosses 的 admissible 和 reason 选择原版或扩展根的 entityRef。受理后专服自主交战；通过 query_task 查看 verifiedHits、verifiedDamage 和 deathProof。'
                else:
                    description += f' boss_encounter 支持{roots}。先配武器，再传 scan_entities 根实体行的 details.entityRef。受理后专服自主交战，可查询或取消。'
            if collection:
                operations.append('collect_drops')
            if placement:
                operations.append('place_item')
                description += ' place_item 使用当前选中的快捷栏物品在绝对 Tile 坐标 x/y 放置。终态回执核验占地、消耗和箱子存储记录。'
            if building:
                operations.append('build_place')
                description += ' build_place 仅接收 blueprint，与 build_diff 共用输入。施工只沿可证安全的纯移动路径接近目标，无法通行则报告阻塞。检查逐项结果和输入释放。已匹配项跳过；部分施工或取消后先重新观察，再显式提交。unknown 不重放。'
            mining = copy.deepcopy(atomic.SCHEMAS['mine_target'])
            if not excavation:
                mining['properties'].pop('cluster', None)
            variants = [mining, *(copy.deepcopy(atomic.SCHEMAS[op]) for op in operations[1:])]
            if combat and not weapon_button:
                variants[operations.index('combat_attack')]['properties'].pop('button',None)
            if boss:
                boss_schema=variants[operations.index('boss_encounter')]
                if regroup:
                    description+=' 可指定 regroupTeammateRef，在自然复活后使用虫洞药水归队并重新交战。先用 list_teammates 确认同队引用。'
                else:
                    boss_schema['properties'].pop('regroupTeammateRef',None)
            arguments = {'anyOf': variants} if len(variants) > 1 else mining
            if collection:
                description += ' collect_drops 默认收集320像素内各类掉落物。参数 radiusPixels 为16..960，typeIds 与 entityRef 互斥；entityRef 取自 scan_entities。查看 collected、remaining 和 unavailable。批次完成后 canContinue=true 可重新扫描继续收集，nextTarget 提供仍有效的候补目标。'
            properties = {'operation': {'type': 'string', 'enum': operations}, 'arguments': arguments}
            if combat:
                description += ' combat_attack 使用 scan_entities 的 entityRef；query_combat 也可发现游戏侧自卫任务。'
            required = ['operation', 'arguments']
        elif name=='cancel_task' and strategy:
            properties['retreatTo']={'type':'object','properties':{k:{'type':'integer','minimum':0,'maximum':2147483647} for k in ('feetX','feetY')},
                                    'additionalProperties':False,'required':['feetX','feetY']}
            description+=' Boss 可选 retreatTo，以世界像素指定已观察的安全落点脚底坐标。受理后查询同一任务，boss_retreat_verified 表示到达并脱离。'
        elif name == 'await_task':
            properties['milliseconds'] = {'type': 'integer', 'minimum': 1, 'maximum': 30000, 'default': 1000}
        rows.append({'type': 'function', 'function': {'name': name, 'description': description,
            'parameters': {'type': 'object', 'properties': properties, 'required': required, 'additionalProperties': False}}})
    return rows


class TaskCoordinator:
    def __init__(self, bridge, on_ready=None):
        self.bridge, self.on_ready = bridge, on_ready
        self.records = {}
        self.sequence = 0
        self.consumed = 0
        self.closed = False
        self.admission = asyncio.Lock()

    def blocked(self):
        return any(r['view']['status'] in ('submitting', 'running', 'unknown') for r in self.records.values())

    def projection(self):
        pending = sorted((r for r in self.records.values() if r.get('sequence', 0) > self.consumed), key=lambda r:r['sequence'])
        batch = pending[:8]
        history = action_results([r['view']['result'] for r in batch])
        results = []
        for item in history['items']:
            view = {k:copy.deepcopy(v) for k,v in batch[item['sequence']-1]['view'].items() if k not in ('action','result')}
            view.update(result=item['result'], resultSummaryOnly=item['summaryOnly'])
            results.append(view)
        return {'active': [copy.deepcopy(r['view']) for r in self.records.values()
                           if not r['done'].is_set() or r['view']['status'] == 'unknown'],
                'results': results, 'throughSequence': batch[-1]['sequence'] if batch else self.consumed,
                'latestSequence': self.sequence, 'remainingResultCount': len(pending)-len(results),
                'retryAllowed': False, 'scope': 'this connection; up to 8 unconsumed receipts per projection; game authority; no restart recovery'}

    def consume(self, through):
        self.consumed = max(self.consumed, min(through, self.sequence))

    def _finish(self, record, result):
        if record['done'].is_set():
            return
        self.sequence += 1
        record['sequence'] = self.sequence
        record['view'].update(status=result['status'], valid=result.get('valid') is True,
            completed=result.get('completed') is True, result=copy.deepcopy(result), resultSequence=self.sequence)
        record['done'].set()
        diagnostics.emit('game_task_result', taskId=record['view']['taskId'], origin=record['view']['origin'],
                         resultSequence=self.sequence, result=result)
        if self.on_ready is not None and not self.closed:
            self.on_ready()

    async def submit(self, args, origin=None):
        operation = args['operation']
        if operation in ('collect_drops', 'place_item', 'build_place') and operation not in getattr(self.bridge, 'supported_atomic_operations', ()):
            return atomic.not_admitted_result('submit_task', 'collection_capability_unavailable' if operation == 'collect_drops' else 'placement_capability_unavailable')
        try:
            atomic.validate_backend(self.bridge, args['operation'], args['arguments'])
        except atomic.PreDispatchRejected as error:
            return atomic.not_admitted_result('submit_task', str(error))
        async with self.admission:
            conflict = any(r['view']['status'] in ('submitting', 'unknown') or
                           r['view']['status'] == 'running' and
                           (operation != 'combat_attack' or r['view']['operation'] in ('combat_attack', 'boss_encounter'))
                           for r in self.records.values())
            if self.closed or conflict or getattr(self.bridge, '_lua_poisoned', False):
                return atomic.not_admitted_result('submit_task', 'active_task_conflict_or_unresolved')
            # Never evict unconsumed results or unresolved outcomes to make room.
            for key, old in list(self.records.items()):
                if len(self.records) < 64:
                    break
                if old['done'].is_set() and old.get('sequence', 0) <= self.consumed:
                    del self.records[key]
            if len(self.records) >= 64:
                return atomic.not_admitted_result('submit_task', 'task_receipt_capacity')
            task_id = uuid.uuid4().hex
            record = {'args': copy.deepcopy(args), 'done': asyncio.Event(), 'ack': asyncio.Event(), 'lock': asyncio.Lock(),
                      'view': {'taskId': task_id, 'operation': operation, 'status': 'submitting',
                               'valid': False, 'completed': False, 'dispatchState': 'unknown', 'origin': origin, 'retryAllowed': False}}
            self.records[task_id] = record
            # Never inherit a Program's command_owner, evidence bundle or Run
            # reply context into the independent task/result notification.
            monitor_context = contextvars.Context()
            correlation = {key: origin[key] for key in tracing.MCP_CORRELATION if origin and key in origin}
            if correlation:
                monitor_context.run(tracing.context.set, correlation)
            record['monitor'] = monitor_context.run(asyncio.create_task, self._run(record))
        # This shield is essential: Program cancellation does not cancel admission/Task.
        await record['ack'].wait()
        return copy.deepcopy(record['view'])

    async def _run(self, record):
        try:
            view = record['view']
            reply = await asyncio.to_thread(self.bridge.task_request, 'start', view['taskId'],
                operation=view['operation'], arguments=record['args']['arguments'], expiresAtUnixMs=int(time.time()*1000)+1800)
            if (reply.get('operation') != view['operation'] or reply.get('targetTask') != record['args']['arguments']
                    or reply.get('status') not in ('running', 'completed', 'failed', 'cancelled', 'timeout', 'unknown')):
                raise BridgeError('Task admission target/operation mismatch; outcome unknown')
            record['identity'] = copy.deepcopy(reply)
            view.update(status=reply['status'], valid=True, dispatchState='admitted', action=reply)
            record['ack'].set()
            while not record['done'].is_set():
                async with record['lock']:
                    await self._observe(record)
                if not record['done'].is_set():
                    await asyncio.sleep(.1)
        except NotAdmittedError as error:
            record['view']['dispatchState'] = 'not_admitted'
            self._finish(record, atomic.not_admitted_result(record['view']['operation'], str(error)))
        except Exception as error:
            self._unknown(record, error)
            diagnostics.emit('game_task_monitor_failed', taskId=record['view']['taskId'],
                             error_type=type(error).__name__, reason=str(error)[:256])
        finally:
            record['ack'].set()

    def _unknown(self, record, error):
        if record['done'].is_set():
            return
        record['view']['dispatchState'] = 'unknown'
        self._finish(record, {'valid': False, 'status': 'unknown', 'completed': False,
            'operation': record['view']['operation'], 'actionId': record['view']['taskId'], 'reason': str(error)[:256], 'retryAllowed': False})

    async def _reflex(self, name, args):
        task_id = args['taskId']
        unknown = {'status': 'unknown', 'taskId': task_id, 'reason': 'unknown_task', 'retryAllowed': False}
        if name == 'resume_task' or 'server_ai_combat_v1' not in self.bridge.hello.get('capabilities', ()):
            return unknown
        try:
            first = await asyncio.to_thread(self.bridge.task_request, 'action', task_id)
        except BridgeError as error:
            if error.status == 'unknown_action':
                return unknown
            raise
        if first.get('operation') not in ('combat_attack','boss_encounter') or first.get('session') != '0' * 32:
            return unknown
        view = {'taskId': task_id, 'operation': first['operation'], 'status': first['status'], 'valid': True,
                'completed': False, 'dispatchState': 'admitted', 'origin': None, 'retryAllowed': False, 'action': first}
        action = first
        try:
            if name == 'cancel_task' and action['status'] == 'running':
                action = await asyncio.to_thread(self.bridge.task_request, 'cancel', task_id)
            if name == 'await_task':
                deadline = asyncio.get_running_loop().time() + args['milliseconds'] / 1000
                while action['status'] == 'running':
                    remaining = deadline - asyncio.get_running_loop().time()
                    if remaining <= 0:
                        return {**view, 'action': action, 'waitStatus': 'timeout'}
                    await asyncio.sleep(min(remaining, .1))
                    action = await asyncio.to_thread(self.bridge.task_request, 'action', task_id)
                    if not atomic.same_action(first, action):
                        raise BridgeError('Reflex task identity changed')
            if not atomic.same_action(first, action) or action.get('status') not in (
                    'running', 'completed', 'failed', 'cancelled', 'timeout', 'unknown'):
                raise BridgeError('Reflex task identity changed')
            if action['status'] != 'running':
                result = await atomic.terminal(self.bridge, action, action['generation'])
                view.update(status=result['status'], valid=result['valid'], completed=result['completed'], result=result)
            return {**view, 'action': action}
        except (BridgeError, OSError, ValueError, KeyError, TypeError) as error:
            self.bridge._lua_poisoned = True
            return {**view, 'status': 'unknown', 'valid': False, 'dispatchState': 'unknown',
                    'result': {'valid': False, 'status': 'unknown', 'completed': False, 'operation': 'combat_attack',
                               'actionId': task_id, 'reason': str(error)[:256], 'retryAllowed': False}}

    async def _observe(self, record, cancel=False):
        if record['done'].is_set():
            return
        action = await asyncio.to_thread(self.bridge.task_request, 'cancel' if cancel else 'action', record['view']['taskId'])
        if not atomic.same_action(record['identity'], action):
            raise BridgeError('Task identity changed; outcome unknown')
        if action.get('status') not in ('running', 'completed', 'failed', 'cancelled', 'timeout', 'unknown'):
            raise BridgeError('Invalid task status; outcome unknown')
        record['view']['action'] = copy.deepcopy(action)
        if action['status'] != 'running':
            result = await atomic.terminal(self.bridge, action, action['generation'])
            self._finish(record, result)

    async def invoke(self, name, args, origin=None):
        if not available(self.bridge):
            raise atomic.PreDispatchRejected('Program task coordination unsupported')
        args = validate(name, args)
        if name == 'submit_task':
            if args['operation']=='combat_attack' and 'button' in args['arguments'] and 'server_ai_weapon_button_v1' not in self.bridge.hello.get('capabilities',()):
                raise atomic.PreDispatchRejected('Requires server_ai_weapon_button_v1')
            return await self.submit(args, origin)
        record = self.records.get(args['taskId'])
        if 'retreatTo' in args and ('server_ai_boss_strategy_v1' not in self.bridge.hello.get('capabilities',())
                or not record or record['view']['operation']!='boss_encounter'):
            raise atomic.PreDispatchRejected('Retreat requires this connection’s active boss encounter')
        if record is None:
            return await self._reflex(name, args)
        if name == 'resume_task':
            result = record['view'].get('result') or {}
            if (record['args']['operation'] != 'mine_target' or record['args']['arguments'].get('cluster') is True
                    or result.get('valid') is not True or result.get('status') != 'cancelled'
                    or not str(result.get('reason', '')).startswith('task_interrupt_')
                    or (result.get('endEvidence') or {}).get('inputsReleased') is not True):
                return atomic.not_admitted_result('resume_task', 'task_not_safely_resumable')
            resumed = await self.submit(record['args'], origin)
            resumed['resumedFrom'] = args['taskId']
            if resumed.get('taskId') in self.records:
                self.records[resumed['taskId']]['view']['resumedFrom'] = args['taskId']
            return resumed
        await record['ack'].wait()
        if name == 'await_task':
            try:
                await asyncio.wait_for(record['done'].wait(), args['milliseconds']/1000)
            except asyncio.TimeoutError:
                return {**copy.deepcopy(record['view']), 'waitStatus': 'timeout'}
        elif not record['done'].is_set():
            async with record['lock']:
                try:
                    if 'retreatTo' in args:
                        reply=await asyncio.to_thread(self.bridge.task_request,'retreat',args['taskId'],arguments=args['retreatTo'])
                        if not atomic.same_action(record['identity'],reply):raise BridgeError('Retreat task identity changed')
                        record['view']['action']=reply
                    else:await self._observe(record, cancel=name == 'cancel_task')
                except NotAdmittedError as error:
                    raise atomic.PreDispatchRejected(str(error)) from error
                except (BridgeError, OSError, ValueError, KeyError, TypeError) as error:
                    self._unknown(record, error)
        return copy.deepcopy(record['view'])

    async def stop_all(self):
        results = []
        for record in list(self.records.values()):
            if not record['done'].is_set():
                results.append(await self.invoke('cancel_task', {'taskId': record['view']['taskId']}))
            elif record['view']['status'] == 'unknown':
                results.append(copy.deepcopy(record['view']))
        return results

    async def close(self):
        self.closed = True
        await self.stop_all()
        monitors = [r['monitor'] for r in self.records.values()]
        await asyncio.gather(*monitors, return_exceptions=True)


def coordinator(bridge):
    value = getattr(bridge, '_program_tasks', None)
    if value is None:
        value = bridge._program_tasks = TaskCoordinator(bridge)
    return value
