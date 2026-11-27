"""Exclusive handoff between the local Agent and a single external Brain."""
import asyncio
from collections import deque
import copy
import uuid
import sqlite3

import agent_diagnostics as diagnostics
import agent_trace as tracing
from agent_run import AgentRun, interruption
from agent_tools import ToolExecutor
import atomic_operations as atomic
from control import BridgeError
import directional_explorer as explorer
import evidence
import agent_memory
import gameplay_skills
import lua_agent
import program_tasks
from sensed_resources import ResourceHistory
from server_ai_contract import server_definitions
from lua_runtime import LuaRuntime


EXPLORER_GUIDE = (
    '未知区域优先调用 MCP explore(direction,objective)，避免用 Lua 循环查询、导航和扫描。'
    '可先用 exploration_status 比较当前路线的实际足迹、附近未知格数、发现和先前中断的方向。'
    '根据角色需要选择调查、经过或改道。已知目的地使用 navigate_to。'
    '根据 explorationResult 的 returnReason、findings、实际位移和原生回执决定调查、沿原方向续行或改向。'
    '各落点 target 使用世界像素。resumeOptions 保留发现时的位置和原探索方向，调查后可继续。'
    '可选 seek 按线索种类或 contentKey 控制何时停下来。'
    'completed 或 budget_reached 只描述本次有界探索，整体目标仍需核验。'
)


def explorer_available(agent):
    bridge = agent.bridge
    required = {'server_ai_navigation_v1', 'server_ai_perception_v1', 'server_ai_scene_v1', 'server_ai_scene_region_v1',
                'server_ai_resources_v1', 'server_ai_region_resources_v1', 'server_ai_directional_landings_v1'}
    tasks = agent.program_tasks
    return (tasks is not None and tasks.bridge is bridge and not tasks.closed and program_tasks.available(bridge)
            and required.issubset(bridge.hello['capabilities'])
            and {'observe_scene', 'navigate_to'}.issubset(bridge.supported_atomic_operations))


class ExternalExplorer(explorer.ExplorerContext):
    """One MCP call using the current lease's resource and passage history."""

    def __init__(self, host, objective):
        self.host = host
        self.agent = host.agent
        self.mailbox = self.agent.program_tasks
        self.resources = host.resources
        self.passage_memory = host.passage_memory
        self.objective = objective
        self.progress = {}
        self.task_id = None
        self.call = asyncio.current_task()
        self.origin = {k: v for k, v in tracing.context.get().items() if k in tracing.MCP_CORRELATION}
        self.origin.update(source='external_explore', leaseVersion=host.lease_version)

    @property
    def version(self):
        host = self.host
        if (host.phase == 'external' and not self.call.cancelling() and not host.bridge._closed
                and host.bridge is self.agent.bridge and host.bridge.binding == host.lease_binding
                and self.mailbox is self.agent.program_tasks):
            return host.lease_version

    async def submit_owned(self, operation, arguments, version):
        task = program_tasks.validate('submit_task', dict(operation=operation, arguments=arguments))
        # submit registers this ID before its first cancellable wait for native acknowledgement.
        self.task_id = uuid.uuid4().hex
        origin = dict(self.origin, segment=len(self.progress['explorationResult']['segments']) + 1)
        def authorized():
            return self.version == version
        view = await self.mailbox.submit(task, origin, task_id=self.task_id,
                                         authorize=authorized, dispatch_guard=authorized)
        if not view.get('taskId'):
            self.task_id = None
            return None, view
        return self.mailbox.records[self.task_id], None

    async def terminal(self, record):
        # After a stop request the native monitor must still produce a terminal receipt.
        await asyncio.wait_for(record['done'].wait(), self.host.bridge.timeout + 2)
        receipt = record['view']['result']
        if not self.released(receipt):
            self.host.isolate()
            return {**receipt, 'status': 'unknown', 'dispatchState': 'unknown'}
        return receipt

    def released(self, receipt):
        if receipt.get('dispatchState') == 'not_admitted':
            return receipt.get('inputsAcquired') is False
        return (receipt.get('actionId') == self.task_id and receipt.get('inputsReleased') is True
                and self.host._safe_terminal(receipt))

    async def drain(self):
        record = self.mailbox.records.get(self.task_id)
        if record is None:
            return  # Cancellation while waiting for the admission lock: never registered.
        try:
            if not record['done'].is_set():
                await self.mailbox.invoke('cancel_task', dict(taskId=self.task_id))
            receipt = await self.terminal(record)
            safe = self.released(receipt)
        except Exception as error:
            receipt = dict(status='unknown', reason=str(error))
            safe = False
        if not safe:
            self.host.isolate()
        outcome = dict(taskId=self.task_id, receipt=self.receipt_fields(receipt), releaseConfirmed=safe)
        self.host.last_outcome = outcome
        diagnostics.emit('mcp_explore_cleanup', **outcome)

    async def run(self, direction, seek='any'):
        version = self.host.lease_version
        if self.version != version:
            raise BridgeError('External explorer binding unavailable', status='stale_binding')
        try:
            baseline = await self.resource_scan(version)
            result = await explorer.run(self, direction, version, baseline, report_initial_findings=True, seek=seek)
            if result['status'] == 'unknown':
                self.host.isolate()
            return result
        finally:
            await self.drain()


class BrainHost:
    def __init__(self, agent, is_chat):
        self.agent = agent
        self.bridge = agent.bridge
        self.is_chat = is_chat
        self.guide, native_tools = lua_agent.server_contract(self.bridge, compact=True)
        if explorer_available(agent):
            native_tools = [*native_tools, explorer.EXPLORE_TOOL]
            if 'server_ai_passages_v1' in self.bridge.hello['capabilities']:
                native_tools.append(atomic.tool('exploration_status','Observe nearby route options, recent foot visits, map unknowns, findings and interrupted headings. Read-only.',{}))
        if agent.notebook is not None:
            self.guide += agent_memory.MEMORY_RULES
            native_tools = [*native_tools, agent_memory.TOOL]
        self.delivered_chat = None
        self.tools = native_tools + [
            atomic.tool('guide', '首次用 full=true 读取原生提示。之后查几个函数的参数用 names，例如 ["scan_entities","equip_item"]；用 Lua api("help",{name="equip_item"}) 查示例。', {
                'full': {'type': 'boolean'},
                'names': {'type': 'array', 'items': {'type': 'string'}, 'minItems': 1, 'maxItems': 12, 'uniqueItems': True}}),
            atomic.tool('get_events', '取走未交付的游戏内指令和系统消息。', {
                'wait_seconds': {'type': 'number', 'minimum': 0, 'maximum': 45, 'default': 0}}),
            atomic.tool('say', '以 AI Player 身份回复当前授权的本地游戏聊天。', {
                'text': {'type': 'string', 'minLength': 1, 'maxLength': 400}}, ['text']),
        ]
        self.events = deque(maxlen=64)
        self.seen = deque(maxlen=256)
        self.event_ready = asyncio.Event()
        self.evicted = 0
        self.dropped = 0
        self.phase = 'native'
        self.lease_version = 0
        self.last_outcome = None
        self.active_program = None
        self.execution_busy = False
        self.polling = False

    def receive(self, event):
        system = event.get('type') == 'game_message'
        if system or self.phase == 'native':
            self.agent.receive(event)
        if self.phase not in ('acquiring', 'external', 'releasing') or not (system or self.is_chat(event)):
            return
        event_id = event['eventId']
        if event_id in self.seen:
            return
        self.seen.append(event_id)
        if not system and self.phase != 'releasing':
            self.agent._fence_reply()
            self.agent._active_turn_id = event_id
        if len(self.events) == self.events.maxlen:
            self.evicted += 1
        if system:
            self.dropped += event.get('droppedBefore', 0)
        self.events.append(dict(event))
        self.event_ready.set()

    def reserve(self):
        if self.phase != 'native':
            return False
        self.delivered_chat = None
        self.phase = 'acquiring'
        self.lease_version += 1
        self.lease_binding = dict(self.bridge.binding)
        self.resources = ResourceHistory()
        self.passage_memory = explorer.PassageMemory()
        self.agent._brain_paused = True
        self.agent._active_turn_id = None
        self.agent._invalidate_turn('external_brain_handoff')
        self.last_outcome = None
        return True

    async def acquire(self):
        await asyncio.gather(*tuple(self.agent._tasks), return_exceptions=True)
        if self.phase != 'acquiring':
            return False
        if await self.quiesce() and self.phase == 'acquiring':
            self.phase = 'external'
            return True
        if self.phase == 'acquiring':
            self.isolate()
        return False

    def begin_release(self):
        if self.phase != 'isolated':
            self.phase = 'releasing'
        self.agent._active_turn_id = None
        self.agent._fence_reply()

    async def quiesce(self):
        """Use the existing task cancellation and the bound game authority."""
        try:
            if self.bridge._closed:
                return False
            tasks = self.agent.program_tasks
            if tasks is not None:
                stopped = await tasks.stop_all()
                if tasks.blocked() or any(not self._safe_terminal(row.get('result')) for row in stopped):
                    return False
            if getattr(self.bridge, '_lua_poisoned', False):
                return False
            movement = await atomic.command(self.bridge, 'get_movement')
            action = movement['movement']['action']
            if action is not None:
                if action['status'] in ('running', 'unknown'):
                    return False
                result = await atomic.terminal(self.bridge, action, action['generation'])
                if not self._safe_terminal(result):
                    return False
            # The existing bound reply-turn RPC checks World/Actor/generation.
            # A chat arriving during acquisition can invalidate an in-flight fence.
            generation = self.agent.generation
            try:
                await asyncio.to_thread(self.bridge.advance_reply_turn, generation)
            except BridgeError as error:
                if error.status != 'superseded':
                    raise
                # Verify binding independently of a changing reply fence.
                await asyncio.to_thread(self.bridge._request, 'state')
            return (not self.bridge._closed and not getattr(self.bridge, '_lua_poisoned', False)
                    and not getattr(self.agent.chat_bridge, '_failure', None))
        except (BridgeError, OSError, ValueError, KeyError, TypeError):
            return False

    @staticmethod
    def _safe_terminal(result):
        if result and result.get('dispatchState') == 'not_admitted' and result.get('inputsAcquired') is False:
            return True
        if not result or not result.get('valid') or result['status'] not in ('completed', 'failed', 'cancelled', 'timeout'):
            return False
        end = result.get('endEvidence') or {}
        return (result.get('endEvidenceStatus') == 'known'
                and (end.get('inputsReleased') is True or end.get('inputsAcquired') is False))

    def isolate(self):
        was_native = self.phase == 'native'
        self.phase = 'isolated'
        self.agent._brain_paused = True
        if was_native:
            self.agent._invalidate_turn('external_brain_host_isolated')

    async def finish_release(self):
        quiescent = await self.quiesce()
        safe = self.phase != 'isolated' and quiescent
        # Undelivered commands belong to the previous driver. They are never replayed.
        self.evicted += len(self.events)
        self.events.clear()
        self.event_ready.clear()
        if safe:
            self.phase = 'native'
            self.agent._brain_paused = False
        else:
            self.isolate()
        return safe

    async def invoke(self, name, args):
        if self.phase != 'external':
            raise ValueError('External Brain lease is unavailable')
        if (self.bridge._closed or getattr(self.bridge, '_lua_poisoned', False)
                or self.agent.bridge is not self.bridge or self.bridge.binding != self.lease_binding):
            self.isolate()
            raise ValueError('Server AI state is unresolved; restart only after inspecting the Actor')
        if name == 'guide':
            if 'names' in args:
                names = args['names']
                if (args.get('full') or not isinstance(names, list) or not 1 <= len(names) <= 12
                        or not all(isinstance(n, str) and n for n in names) or len(set(names)) != len(names)):
                    raise ValueError('Use names with 1..12 distinct API names, without full=true')
                definitions = {d['function']['name']: d['function'] for d in server_definitions(self.bridge)}
                return {'functions': [definitions[n] for n in names if n in definitions],
                        'unavailable': [n for n in names if n not in definitions]}
            instructions = self.agent.history[0]['content'] if args.get('full') else self.guide
            if explorer_available(self.agent):
                instructions = EXPLORER_GUIDE + '\n' + instructions
            return {'instructions': '外部驾驶时，execute_lua 的顶层 events 与 value 一起读取。回复中已交付的消息不会在 get_events 重发。\n' + instructions, **self.memory_context()}
        if name == 'memory' and self.agent.notebook is not None:
            chat = self.delivered_chat
            if chat is None or chat['eventId'] != self.agent._active_turn_id:
                chat = {}
            return self.agent.notebook.invoke(args, chat, self.bridge.binding['world'])
        if name == 'read_skill':
            return gameplay_skills.bundled_skills().load(args)
        if name == 'get_events':
            if self.polling:
                raise ValueError('Another get_events call is already waiting')
            self.polling = True
            try:
                seconds = args.get('wait_seconds', 0)
                if seconds and not self.events:
                    try:
                        await asyncio.wait_for(self.event_ready.wait(), seconds)
                    except asyncio.TimeoutError:
                        pass
                return await self.runtime_context()
            finally:
                self.polling = False
        if (name not in ('execute_lua','say','explore','exploration_status')
                or name in ('explore','exploration_status') and not explorer_available(self.agent)):
            raise ValueError('Unknown MCP tool')
        if self.execution_busy:
            raise ValueError('An execution tool is already active')
        self.execution_busy = True
        try:
            if name == 'exploration_status':
                if args or 'server_ai_passages_v1' not in self.bridge.hello['capabilities']:
                    raise ValueError('exploration_status requires passage observations and takes no arguments')
                context = ExternalExplorer(self,'')
                version = context.version
                scene = await atomic.dispatch(self.bridge,'observe_scene',dict(
                    layers=['landingRegions','hazards'],includePassages=True,maxRegions=8,maxHazards=6))
                if not scene.get('valid') or version is None or version != context.version:
                    raise BridgeError('Current local exploration observation unavailable')
                scan = await context.resource_scan(version)
                local = scene['data']
                survey = local.get('passages') or {}
                self.passage_memory.observe(survey,local['gameUpdate'])
                return dict(gameUpdate=local['gameUpdate'],actor=local['actor'],
                            passages={k:v for k,v in survey.items() if k!='connectedLandingIds'},
                            hazards=local['hazards'],resources=scan,passageMemory=self.passage_memory.summary(),
                            resumeOptions=list(self.passage_memory.resumptions),**await self.runtime_context())
            if name == 'explore':
                if (not explorer.valid_options({k:v for k,v in args.items() if k!='objective'})
                        or not isinstance(args.get('objective'), str) or not args['objective'].strip()
                        or len(args['objective']) > 240):
                    raise ValueError('explore requires direction and a short objective (1..240 characters)')
                trip = ExternalExplorer(self, args['objective'])
                result = await trip.run(args['direction'], args.get('seek','any'))
                diagnostics.emit('mcp_explore_result', explorationResult=result['explorationResult'])
                return {**result, **await self.runtime_context()}
            if name == 'say':
                chat_id = self.agent._active_turn_id
                if chat_id is None:
                    raise ValueError('say requires an authorized local game chat instruction in this lease')
                generation = self.agent.generation
                await asyncio.to_thread(self.bridge.advance_reply_turn, generation)
                published = await self.agent._send_reply(args['text'], chat_id, generation, receipt=True)
                return {'valid': True, 'status': 'published', 'receipt': published}
            result = await self.execute(lua_agent.validate_arguments(args))
            return {**result, **await self.runtime_context()}
        finally:
            self.execution_busy = False

    async def execute(self, source):
        metadata = {key: tracing.context.get()[key] for key in tracing.MCP_CORRELATION}
        run = AgentRun(self.agent.generation, self.agent._active_turn_id)
        run.phase = 'tools'
        bundle = evidence.Bundle(self.agent.lua_store.directory.parent / 'evidence', metadata=metadata)
        bundle_token = evidence.current.set(bundle)
        run_id = uuid.uuid4().hex
        self.active_program = {'runId': run_id, 'programStatus': 'running', 'goalOutcome': 'unknown'}
        trace_token = tracing.context.set({**tracing.context.get(), 'task_id': bundle.id,
                                          'run_id': run_id, 'chat_event_id': run.chat_id})
        executor = ToolExecutor(LuaRuntime, self.bridge)
        try:
            program = self.agent.lua_store.save(source)
            bundle.source(source)
            tracing.trace.emit('lua_program_saved', **program)
            result = await executor.execute(source, chat_id=run.chat_id, limits=self.agent.lua_limits,
                                            allow_actions=True, on_result=diagnostics.tool_result)
            return result
        except asyncio.CancelledError:
            if executor.receipt is not None:
                run.cancel_reason = 'external_program_cancelled'
                self.agent._interrupted.append({**interruption(run, executor.receipt, executor.actions),
                                                'runId': run_id})
                self.agent._interrupted_total += 1
            raise
        finally:
            receipt = executor.receipt
            if receipt is not None:
                summary = lua_agent.summarize(receipt, executor.actions)
                self.last_outcome = {'runId': run_id, 'luaRunId': receipt['runId'],
                                     'programStatus': receipt['status'], 'actionState': receipt['actionState'],
                                     'taskOutcome': summary['taskOutcome'], 'goalOutcome': summary['goalOutcome']}
                if receipt['actionState'] == 'unknown':
                    self.isolate()
            bundle.finish(status=(receipt or {}).get('status', 'not_started'))
            self.active_program = None
            tracing.context.reset(trace_token)
            evidence.current.reset(bundle_token)

    def memory_context(self):
        if self.agent.notebook is None:
            return {}
        try:
            return {'privateNotes': self.agent.notebook.index()}
        except sqlite3.Error:
            return {'privateNotesError': 'notebook_unavailable'}

    async def runtime_context(self):
        inventory = await self.agent.runtime_inventory()
        context = {
            'runtimeInventory': inventory,
            **self.memory_context(),
            'runtimeLua': {
                'scope': 'per execute_lua call; current result usage is reported separately',
                **lua_agent.runtime_budget(self.agent.lua_limits),
            },
            **self.agent.recent_game_context(),
            'interruptedRuns': copy.deepcopy(list(self.agent._interrupted)),
            'interruptedRunCoverage': {'omittedCount': self.agent._interrupted_total - len(self.agent._interrupted)},
        }
        if self.active_program is not None or self.last_outcome is not None:
            context['runtimeOutcome'] = self.active_program or self.last_outcome
        if self.agent.program_tasks is not None:
            tasks = self.agent.program_tasks.projection()
            context['runtimeTasks'] = tasks
            self.agent.program_tasks.consume(tasks['throughSequence'])
        events = list(self.events)
        for event in reversed(events):
            if self.is_chat(event) and event['eventId'] == self.agent._active_turn_id:
                self.delivered_chat = event
                break
        self.events.clear()
        self.event_ready.clear()
        context.update(events=events, eventCoverage={'maxEvents': self.events.maxlen,
                       'evictedCount': self.evicted, 'serverDroppedCount': self.dropped})
        return context
