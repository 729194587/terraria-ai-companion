"""Exclusive handoff between the local Agent and a single external Brain."""
import asyncio
from collections import deque
import copy
import uuid

import agent_diagnostics as diagnostics
import agent_trace as tracing
from agent_run import AgentRun, interruption
from agent_tools import ToolExecutor
import atomic_operations as atomic
from control import BridgeError
import evidence
import gameplay_skills
import lua_agent
from lua_runtime import LuaRuntime


class BrainHost:
    def __init__(self, agent, is_chat):
        self.agent = agent
        self.bridge = agent.bridge
        self.is_chat = is_chat
        self.guide, native_tools = lua_agent.server_contract(self.bridge, compact=True)
        self.tools = native_tools + [
            atomic.tool('guide', '读取当前 Server AI 游戏契约。'),
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
        self.phase = 'acquiring'
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
        if self.bridge._closed or getattr(self.bridge, '_lua_poisoned', False):
            self.isolate()
            raise ValueError('Server AI state is unresolved; restart only after inspecting the Actor')
        if name == 'guide':
            return {'instructions': self.guide}
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
        if name not in ('execute_lua', 'say'):
            raise ValueError('Unknown MCP tool')
        if self.execution_busy:
            raise ValueError('An execution tool is already active')
        self.execution_busy = True
        try:
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

    async def runtime_context(self):
        inventory = await self.agent.runtime_inventory()
        context = {
            'runtimeInventory': inventory,
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
        self.events.clear()
        self.event_ready.clear()
        context.update(events=events, eventCoverage={'maxEvents': self.events.maxlen,
                       'evictedCount': self.evicted, 'serverDroppedCount': self.dropped})
        return context
