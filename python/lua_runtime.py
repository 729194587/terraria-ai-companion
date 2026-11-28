"""Async host for whole Lua programs; Bridge and atomic supervision stay here."""
import argparse
import asyncio
import copy
import hashlib
import json
import multiprocessing as mp
from pathlib import Path
from time import monotonic
import uuid

import agent_trace as tracing
import agent_diagnostics as diagnostics
import evidence
import atomic_operations as atomic
import action_recovery
import navigation_recovery
import lua_modules
import program_tasks
import directional_explorer as explorer
from sensed_resources import ResourceHistory
from control import Bridge, BridgeError, command_owner
from lua_limits import Limits
from lua_worker import (worker, encode, decode, ValueLimit, SOURCE_BYTES,
                        REQUEST_BYTES, VALUE_BYTES, FRAME_BYTES, EXPLORATION_SOURCE, SEMANTIC_SOURCE)


class Interrupted(Exception):
    pass


class LuaExplorer(explorer.OwnedExplorer):
    def __init__(self, runtime, owner, origin, out, limits, recovery, on_result):
        self.agent = runtime
        self.owner = owner
        self.origin = origin
        self.out = out
        self.limits = limits
        self.recovery = recovery
        self.on_result = on_result
        self.binding = dict(runtime.bridge.binding)
        self.mailbox = program_tasks.coordinator(runtime.bridge)
        self.resources = ResourceHistory()
        self.progress = {}
        self.task_id = None
        self.reported = None
        self.revoked = False

    @property
    def version(self):
        if not self.revoked and self.agent.bridge._lua_owner is self.owner and self.agent.bridge.binding == self.binding:
            return self.owner

    def charge(self):
        if self.out['calls'] >= self.limits.calls:
            raise Interrupted('call_limit')
        self.out['calls'] += 1

    def can_start_segment(self):
        # Admission, landing/map verification and up to two resource samples.
        return self.out['actions'] < self.limits.actions and self.out['calls']+5 <= self.limits.calls

    async def read_map(self, args):
        self.charge()
        return await atomic.dispatch(self.agent.bridge, 'query_map', args, owner=self.owner)

    async def read_scene(self, args):
        self.charge()
        return await atomic.dispatch(self.agent.bridge, 'observe_scene', args, owner=self.owner)

    async def submit_owned(self, operation, args, version):
        if self.recovery.blocked(operation, args):
            return None, atomic.not_admitted_result(operation, 'unchanged_ineffective_action')
        if self.out['actions'] >= self.limits.actions:
            raise Interrupted('action_limit')
        self.charge()
        chat_id = self.origin['chatId']
        if (self.task_id is None and self.agent.player_instruction and chat_id is not None
                and not await self.mailbox.release_previous_follow(chat_id)):
            raise Interrupted('previous_follow_release_unconfirmed')
        self.out['actions'] += 1
        return await super().submit_owned(operation, args, version)

    async def terminal(self, record):
        await record['done'].wait()
        receipt = record['view']['result']
        if self.reported != self.task_id:
            self.reported = self.task_id
            released = (receipt.get('actionId') == self.task_id and safe_terminal(receipt)
                        and receipt.get('inputsReleased') and receipt.get('endEvidenceStatus') == 'known')
            refused = receipt.get('dispatchState') == 'not_admitted' and receipt.get('inputsAcquired') is False
            self.out['actionState'] = 'released' if released or refused else 'unknown'
            self.out['lastAction'] = bounded_action(receipt)
            self.recovery.observe('navigate_to', record['args']['arguments'], receipt)
            if self.on_result:
                self.on_result('navigate_to', record['args']['arguments'], receipt)
        if self.out['actionState'] == 'unknown':
            return {**receipt, 'status': 'unknown', 'dispatchState': 'unknown'}
        return receipt

    async def travel(self, direction, seek):
        self.objective = direction or 'explore'
        try:
            baseline = await self.resource_scan(self.owner)
            return await explorer.run(self, direction, self.owner, baseline, seek=seek)
        finally:
            self.revoked = True
            try:
                await self.stop_child()
            except BaseException:
                self.out['actionState'] = 'unknown'
                raise


def safe_terminal(result):
    return (isinstance(result, dict) and result.get('valid') is True
            and result.get('status') in ('completed', 'failed', 'timeout', 'cancelled')
            and not result.get('supervisorError'))


def bounded_action(result):
    try:
        encode(result, VALUE_BYTES)
        return result
    except ValueLimit:
        # Preserve the terminal ore certificate when verbose navigation history
        # cannot fit in one Lua message. The host still retains the raw receipt.
        task = result.get('task')
        if (isinstance(task, dict) and task.get('taskName') == 'mine_target'
                and (result.get('targetTask') or {}).get('cluster') is True):
            progress = dict(task.get('progress') or {})
            progress.update(children=[], childrenTruncated=True, omittedChildren=progress.get('settledChildren'))
            progress.pop('excavation', None)  # terminal result contains the canonical copy
            summary = dict(task, progress=progress, lastResult=None, historyOmitted=True)
            compact = dict(result, task=summary, evidence=dict(result.get('evidence') or {}, task=summary))
            try:
                encode(compact, VALUE_BYTES)
                return compact
            except ValueLimit:
                pass
        return {'valid': result.get('valid'), 'status': result.get('status'),
                'actionId': result.get('actionId'), 'reason': 'action_receipt_limit',
                'payloadOmitted': True}


LAST_OBSERVATION_BYTES = 65_536


def retained_observation(operation, call_id, run_id, result):
    """Detached host evidence, separate from the program's return value."""
    snapshot = {'operation': operation, 'callId': call_id, 'runId': run_id}
    try:
        checked = decode(encode(result, VALUE_BYTES))
        data = checked.get('data')
        data = data if isinstance(data, dict) else checked
        for key in ('worldSessionId', 'actorGeneration', 'actorSlot', 'gameUpdate', 'observedAtUnixMs', 'clock'):
            value = checked.get(key, data.get(key))
            if type(value) in (int, float, bool) or type(value) is str and len(value) <= 512:
                snapshot[key] = value
        scene_id = data.get('sceneId')
        if isinstance(scene_id, str) and len(scene_id) <= 512:
            snapshot['sceneId'] = scene_id
        snapshot.update(result=checked, payloadOmitted=False)
        encode(snapshot, LAST_OBSERVATION_BYTES)
    except ValueLimit:
        snapshot.update(result=None, payloadOmitted=True, omissionReason='last_observation_limit')
    return snapshot


def safe_preflight_rejection(result, lifecycle):
    """Only bound server evidence can authorize another mutation in this run."""
    if (not isinstance(result, dict) or result.get('valid') is not True
            or result.get('status') != 'failed' or result.get('supervisorError')
            or lifecycle.get('uncertain') or result.get('endEvidenceStatus') != 'known'):
        return False
    proof, end, action = result.get('evidence'), result.get('endEvidence'), lifecycle.get('action')
    if not all(isinstance(v, dict) for v in (proof, end, action)):
        return False
    if any(k in proof and proof[k] is not False
           for k in ('uncertainNativeEffect', 'targetChanged', 'placedByActor', 'destroyedByActor')):
        return False
    if any(k in proof and (type(proof[k]) is not int or proof[k] != 0)
           for k in ('consumedCount', 'nativePlacementEvents')):
        return False
    bound = (end.get('inputsReleased') is True and type(end.get('inputsAcquired')) is bool
            and end.get('phase') == 'ServerAI_game_thread_inputs_released'
            and end.get('effectVerified') is False and end.get('operation') == result.get('operation')
            and type(end.get('gameUpdate')) is int and end['gameUpdate'] >= 0
            and action.get('status') == 'failed' and action.get('operation') == result.get('operation')
            and isinstance(result.get('actionId'), str) and bool(result['actionId'])
            and result['actionId'] == action.get('actionId')
            and isinstance(action.get('session'), str) and bool(action['session'])
            and result.get('session') == action['session']
            and isinstance(action.get('worldSessionId'), str) and bool(action['worldSessionId'])
            and end.get('worldSessionId') == action['worldSessionId']
            and isinstance(action.get('actorId'), str) and bool(action['actorId'])
            and end.get('actorId') == action['actorId']
            and type(action.get('generation')) is int and end.get('actorGeneration') == action['generation']
            and type(action.get('actorSlot')) is int and type(end.get('actorSlot')) is int
            and end.get('actorSlot') == action['actorSlot']
            and type(end.get('actorGeneration')) is int
            and result.get('actionGeneration') == action['generation']
            and result.get('controlGeneration') == action['generation'])
    if not bound:
        return False
    preflight = (proof.get('mutationAttempted') is False and proof.get('effectStatus') == 'not_started'
                 and proof.get('operation') == result.get('operation') and end['inputsAcquired'] is False)
    if preflight:
        return True
    def zero(*keys):
        return all(type(proof.get(k)) is int and proof[k] == 0 for k in keys)
    if result.get('operation') == 'mine_tile':
        return (zero('Ticks', 'Swings', 'Strikes', 'PeakDamage', 'Transforms')
                and proof.get('targetUnchanged') is True
                and all(proof.get(k) is False for k in ('targetChanged', 'destroyedByActor', 'nativeDestroyed', 'targetAbsent'))
                and proof.get('effectStatus') == 'not_started' and end['inputsAcquired'] is False)
    if result.get('operation') == 'place_object':
        return (zero('nativePlacementEvents', 'consumedCount')
                and all(proof.get(k) is False for k in ('targetChanged', 'uncertainNativeEffect', 'placedByActor', 'fullFootprintVerified', 'targetPresent'))
                and proof.get('objectPlacement') is True and proof.get('effectStatus') == 'not_confirmed')
    return False


class LuaRuntime:
    """One exclusive run per Bridge. Unknown action state permanently fences it.

    execute() accepts a complete program, so a future Agent tool can await it with
    its chat_id and cancel its task. Model-facing policy lives in lua_agent/agent.
    """
    _active_runs = 0
    max_workers = 2

    def __init__(self, bridge, *, evidence_root=None):
        self.bridge = bridge
        self.evidence_root = evidence_root
        self.last_result = None
        self.player_instruction = True

    async def execute(self, source, *, chat_id=None, limits=Limits(), cancel=None,
                      allow_actions=True, on_result=None):
        limits.validate()
        out = {'runId': uuid.uuid4().hex, 'status': 'unknown', 'calls': 0, 'value': None,
               'actions': 0, 'logs': [], 'actionState': 'idle', 'lastAction': None, 'lastObservation': None,
               'cleanup': None, 'trace': [], 'recoveryEvents': []}
        if type(source) is not str or len(source) > SOURCE_BYTES or len(source.encode('utf-8')) > SOURCE_BYTES:
            return {**out, 'status': 'source_limit'}
        if getattr(self.bridge, '_lua_poisoned', False):
            return {**out, 'status': 'blocked', 'actionState': 'unknown'}
        if (getattr(self.bridge, '_lua_owner', None) is not None
                or getattr(self.bridge, '_atomic_inflight', 0)
                or LuaRuntime._active_runs >= LuaRuntime.max_workers):
            return {**out, 'status': 'busy'}
        if cancel is not None and cancel.is_set():
            return {**out, 'status': 'cancelled'}
        owner = object()
        self.bridge._lua_owner = owner
        LuaRuntime._active_runs += 1
        owner_token = command_owner.set(owner)
        process, connection, active = None, None, None
        active_role = None
        lifecycle = {}
        scope = (chat_id, copy.deepcopy(getattr(self.bridge, 'binding', None)))
        recovery = getattr(self.bridge, '_lua_action_recovery', None)
        if recovery is None or recovery.scope != scope:
            recovery = self.bridge._lua_action_recovery = action_recovery.Recovery(scope)
        handoff = getattr(self.bridge, '_lua_navigation_handoff', None)
        if handoff is not None and handoff.chat_id != chat_id:
            self.bridge._lua_navigation_handoff = None
            handoff = None  # A new user turn does not inherit a previous turn's fallback grant.
        if handoff is not None: handoff.observed = False
        deadline = monotonic() + limits.seconds
        bundle = evidence.current.get()
        owns_bundle = bundle is None
        if owns_bundle:
            bundle = evidence.Bundle(self.evidence_root,metadata={'chat_event_id':chat_id,'lua_run_id':out['runId']})
        evidence_token = evidence.current.set(bundle)
        digest = bundle.source(source)
        helper_digest = bundle.source(EXPLORATION_SOURCE)
        semantic_digest = bundle.source(SEMANTIC_SOURCE)
        token = tracing.context.set({**tracing.context.get(), 'task_id': bundle.id,
                                    'lua_run_id': out['runId'], 'source_sha256': digest})
        call_token, call_open, call_meta, failure_call = None, False, {}, None

        terminal_module_status = None
        building_survey_args = None
        postflight = None

        async def report_terminal(status, receipt, *, reconcile=False):
            # A trusted builtin may describe its failure. The ordinary Lua program
            # never resumes. The sole optional game call is the original read-only diff.
            nonlocal terminal_module_status, postflight, active, active_role
            terminal_module_status = status
            postflight = building_survey_args if reconcile else None
            frame = encode(dict(type='module_terminal', id=sequence, value=receipt))
            active = asyncio.create_task(asyncio.to_thread(connection.send_bytes, frame))
            active_role = "send"
            await watch(active)
            active = None

        def record_recovery(hint, **details):
            out['recovery'] = hint
            event = {**hint, **details, 'callId': call_meta['id']}
            out['recoveryEvents'].append(event)
            return event

        def emit(event, **fields):
            tracing.trace.emit(event, **fields)
            diagnostics.emit(event, **fields)
        pending_reads = {}

        def call_end(status, *, result=None, error=None):
            nonlocal call_open, failure_call
            if not call_open:
                return
            row = {**call_meta, 'status': status, 'elapsed_ms': (monotonic()-call_started)*1000}
            if result is not None:
                row.update(valid=result.get('valid'), reason=result.get('reason'),
                           observation=diagnostics.observation_summary(call_meta['operation'], result))
                if call_meta['operation'] in atomic.ACTIONS:
                    row['action'] = bounded_action(result)
            if error is not None:
                row['error'] = str(error)[:512]
            if error is not None or status=='unknown':
                failure_call = dict(call_meta)
            entry = {key:row[key] for key in ('id','operation','line','status','valid','reason','elapsed_ms','error') if key in row}
            out['trace'].append(entry)
            if result is not None and row['operation'] in atomic.READS:
                available = result.get('available')
                if available is False or (pending_reads and available is True and result.get('valid') is True):
                    key = (row['operation'], json.dumps(args, sort_keys=True, separators=(',', ':')))
                    if available is False:
                        entry['available'] = False
                        pending_reads.setdefault(key, []).append(entry)
                    else:
                        for earlier in pending_reads.pop(key, ()):
                            earlier['resolvedBy'] = row['id']
            emit('lua_api_result' if result is not None else 'lua_api_error', **row)
            call_open = False
        emit('lua_start', semantic_helper_sha256=semantic_digest, exploration_helper_sha256=helper_digest, source_sha256=digest, source_bytes=len(source.encode()), limits=vars(limits), chat_id=chat_id)

        async def watch(task):
            while True:
                # Cancellation/deadline win over late results and new requests.
                if cancel is not None and cancel.is_set():
                    raise Interrupted('cancelled')
                if monotonic() >= deadline:
                    raise Interrupted('timeout')
                if task.done():
                    return task.result()
                # A worker may exit after publishing its final frame while the
                # preceding send's thread is still completing. Drain the pipe.
                if process is not None and active_role in ('dispatch','explore') and not process.is_alive():
                    raise Interrupted('worker_error')
                await asyncio.sleep(.01)

        async def poison():
            self.bridge._lua_poisoned = True
            out['actionState'] = 'unknown'
            # Closing stops renewal and revokes the connection. It is NOT release evidence.
            await asyncio.to_thread(self.bridge.close)

        async def cleanup():
            nonlocal active
            if active is not None:
                if not active.done():
                    active.cancel()  # Exactly once; do not interrupt dispatch's shielded start/stop.
                done, _ = await asyncio.wait({active}, timeout=limits.cleanup_seconds)
                if not done:
                    out['cleanup'] = {'valid': False, 'status': 'unknown', 'reason': 'cleanup_deadline'}
                    await poison()
                    # Bridge.close unblocks real pending RPCs; drain without a second cancellation.
                    await asyncio.wait({active}, timeout=.2)
                else:
                    try:
                        result = active.result()
                    except asyncio.CancelledError:
                        result = lifecycle.get('cleanup')
                    except Exception:
                        result = None
                    if active_role == 'dispatch' and lifecycle.get('start_attempted'):
                        out['cleanup'] = bounded_action(result) if result is not None else {
                            'valid': False, 'status': 'unknown', 'reason': 'cleanup_unconfirmed'}
                        if lifecycle.get('dispatchState') == 'not_admitted' and lifecycle.get('action') is None and not lifecycle.get('uncertain'):
                            out['lastAction'] = bounded_action(result)
                        elif lifecycle.get('uncertain') or not safe_terminal(result):
                            await poison()
                        else:
                            out['actionState'] = 'released'
                            out['lastAction'] = bounded_action(result)
                if active_role == 'dispatch':
                    emit('lua_cleanup', action=lifecycle.get('action'),
                                       start_attempted=lifecycle.get('start_attempted', False),
                                       cleanup=out['cleanup'], action_state=out['actionState'])
            if out['actionState'] == 'unknown' and not getattr(self.bridge, '_lua_poisoned', False):
                await poison()

        cancelled = False
        try:
            # Do not take over an action started by another caller or a previous run.
            active = asyncio.create_task(atomic.command(self.bridge, 'get_movement'))
            active_role = 'preflight'
            initial = await watch(active)
            active = None
            current = initial['movement']['action']
            if current is not None and (current.get('status') == 'running'
                                        or current.get('endEvidenceStatus') != 'known'):
                out['status'] = 'busy'
                return out
            context = mp.get_context('spawn')
            connection, child = context.Pipe(duplex=True)
            module_names = tuple(m.name for m in lua_modules.available(self.bridge))
            process = context.Process(target=worker, args=(child, source, limits, EXPLORATION_SOURCE, module_names), daemon=True)
            try:
                process.start()
            finally:
                child.close()
            sequence, halted = 0, not allow_actions
            explored = False
            while True:
                if call_token is not None:
                    tracing.context.reset(call_token)
                    call_token = None
                active = asyncio.create_task(asyncio.to_thread(connection.recv_bytes, FRAME_BYTES))
                active_role = 'receive'
                message = decode(await watch(active))
                active = None
                if message.get('type') == 'log':
                    if (message.get('id') != len(out['logs'])+1 or len(out['logs'])>=32
                            or type(message.get('line')) is not int or not 0 <= message['line'] <= source.count('\n')+1
                            or type(message.get('message')) is not str or len(message['message'].encode('utf-8'))>512):
                        raise Interrupted('protocol_error')
                    out['logs'].append({k:message[k] for k in ('id','line','message')})
                    emit('lua_progress', **out['logs'][-1])
                    continue
                if message.get('type') == 'done':
                    if terminal_module_status:
                        if message.get('status') == 'module_terminal':
                            out['moduleResult'] = message.get('moduleResult')
                        break
                    if message.get('status') not in ('completed', 'script_error', 'instruction_limit',
                            'memory_limit', 'value_limit', 'serialization_error', 'bad_argument', 'protocol_error', 'worker_error', 'log_limit'):
                        raise Interrupted('protocol_error')
                    out.update(status=message['status'], value=message.get('value'))
                    if 'error' in message:
                        out['error'] = message['error']
                    if type(message.get('line')) is int and 0 <= message['line'] <= source.count('\n')+1:
                        out['errorLine'] = message['line']
                    break
                if (message.get('type') != 'call' or type(message.get('id')) is not int
                        or message['id'] != sequence + 1):
                    raise Interrupted('protocol_error')
                encode(message, REQUEST_BYTES)
                sequence += 1
                name, args = message.get('name'), message.get('arguments')
                module = message.get('module')
                if module is not None and module not in module_names:
                    raise Interrupted('protocol_error')
                if terminal_module_status:
                    if module != 'building' or name != 'build_diff' or postflight is None or args != postflight:
                        break
                    postflight = None
                if module == 'building' and name == 'submit_task':
                    building_survey_args = args.get('arguments') if args.get('operation') == 'build_place' else None
                line = message.get('line', 0)
                if type(line) is not int or not 0 <= line <= source.count('\n')+1:
                    raise Interrupted('protocol_error')
                call_meta = {'id':sequence, 'operation':name, 'line':line}
                call_token = tracing.context.set({**tracing.context.get(), 'lua_call_id':sequence, 'lua_line':line})
                call_started, call_open = monotonic(), True
                emit('lua_api_start', **call_meta, arguments=args)
                if out['calls'] >= limits.calls:
                    raise Interrupted('call_limit')
                out['calls'] += 1
                if type(name) is not str:
                    raise Interrupted('bad_argument')
                if type(message.get('protected', False)) is not bool:
                    raise Interrupted('protocol_error')
                if name == '_directional_explore':
                    if explored or halted or handoff is not None or not explorer.available(self.bridge):
                        raise Interrupted('exploration_unavailable')
                    if not explorer.valid_options(args,self.bridge):
                        raise ValueError('Invalid explore direction or seek for this server')
                    origin = {k:v for k,v in tracing.context.get().items() if k in tracing.MCP_CORRELATION}
                    origin.update(chatId=chat_id, programId=out['runId'], source='lua_explore')
                    trip = LuaExplorer(self, owner, origin, out, limits, recovery, on_result)
                    explored = True
                    active = asyncio.create_task(trip.travel(args.get('direction'), args.get('seek')))
                    active_role = 'explore'
                    result = await watch(active)
                    active = None
                    call_end(result['status'], result=result)
                    if out['actionState'] == 'unknown' or result['status'] == 'unknown':
                        await poison()
                        out['status'] = 'action_unknown'
                        break
                    # A failed child stops this program's later mutations.
                    halted = not result['completed']
                    frame = encode(dict(type='result', id=sequence, value=explorer.brief(result['explorationResult'])))
                    active = asyncio.create_task(asyncio.to_thread(connection.send_bytes, frame))
                    active_role = 'send'
                    await watch(active)
                    active = None
                    continue
                if name == 'help':
                    # Host-local metadata uses the same bounded Worker reply
                    # path, without dispatch or changing observation/continuation state.
                    from server_ai_contract import api_help
                    result = api_help(self.bridge, args)
                    call_end(result['status'], result=result)
                    active = asyncio.create_task(asyncio.to_thread(connection.send_bytes,
                        encode({'type': 'result', 'id': sequence, 'value': result})))
                    active_role = 'send'
                    await watch(active)
                    active = None
                    continue
                try:
                    args = program_tasks.validate(name, args) if name in program_tasks.NAMES else atomic.validate(
                        name, args, server="server_ai_atomic_v1" in getattr(self.bridge, "hello", {}).get("capabilities", ()))
                except ValueError as exc:
                    if name not in atomic.SCHEMAS.keys() | program_tasks.NAMES:
                        raise
                    # Only this local schema/range check is recoverable. No
                    # dispatch, network call or game action has begun here.
                    rejection = {'status': 'bad_argument', 'error': str(exc)[:512],
                                 'argumentOperation': name}
                    details = {}
                    if isinstance(exc, atomic.ArgumentValidationError):
                        details = dict(diagnostics=exc.diagnostics, diagnosticsTruncated=exc.diagnostics_truncated)
                        rejection.update(details)
                    from server_ai_contract import argument_usage
                    hint = action_recovery.guidance(name, 'bad_argument', detail=str(exc))
                    usage = argument_usage(self.bridge, name, args)
                    if usage:
                        hint.update(usage, nextStep='核对 signature；需要参数或返回字段详情时执行 helpQuery。')
                    rejection['recovery'] = record_recovery(hint, **details)
                    if not message.get('protected', False):
                        raise
                    call_end('bad_argument', error=rejection['error'])
                    active = asyncio.create_task(asyncio.to_thread(connection.send_bytes,
                        encode({'type': 'local_argument_rejected', 'id': sequence, 'value': rejection})))
                    active_role = 'send'
                    await watch(active)
                    active = None
                    continue
                synchronous_task = name == 'mine_target' and program_tasks.available(self.bridge)
                if (allow_actions and not halted
                        and out['actions'] < limits.actions and (name in atomic.BUDGETED_ACTIONS or name in ('submit_task','resume_task'))):
                    if (self.player_instruction and chat_id is not None and program_tasks.available(self.bridge)
                            and not await program_tasks.coordinator(self.bridge).release_previous_follow(chat_id)):
                        raise Interrupted('previous_follow_release_unconfirmed')
                if name == 'cancel_task' and not allow_actions:
                    raise Interrupted('read_only_turn')
                if (allow_actions and not halted and handoff is None and name in atomic.BUDGETED_ACTIONS
                        and (guidance := recovery.blocked(name, args)) is not None):
                    result = {'valid': False, 'status': 'failed', 'completed': False,
                              'operation': name, 'reason': 'unchanged_ineffective_action',
                              'dispatchState': 'not_admitted', 'continuationAllowed': True,
                              'recovery': record_recovery({**guidance, 'unchangedAttemptSuppressed': True})}
                    call_end('failed', result=result)
                    active = asyncio.create_task(asyncio.to_thread(connection.send_bytes,
                        encode({'type': 'result', 'id': sequence, 'value': result})))
                    active_role = 'send'
                    await watch(active)
                    active = None
                    continue
                if name in program_tasks.NAMES or synchronous_task:
                    if not program_tasks.available(self.bridge):
                        raise ValueError('Program task coordination unsupported')
                    if name in ('submit_task', 'resume_task') or synchronous_task:
                        if halted or not allow_actions:
                            raise Interrupted('previous_action_failed')
                        if out['actions'] >= limits.actions:
                            raise Interrupted('action_limit')
                        out['actions'] += 1
                    manager = program_tasks.coordinator(self.bridge)
                    context = tracing.context.get()
                    origin = {'chatId': chat_id, 'programId': out['runId'], 'runId': context.get('run_id')}
                    origin.update({key: context[key] for key in tracing.MCP_CORRELATION if key in context})
                    async def task_call():
                        if not synchronous_task:
                            return await manager.invoke(name, args, origin)
                        submitted = await manager.invoke('submit_task', {'operation': name, 'arguments': args}, origin)
                        if submitted.get('status') == 'running':
                            record = manager.records[submitted['taskId']]
                            await record['done'].wait()
                            return copy.deepcopy(record['view'])
                        return submitted
                    active = asyncio.create_task(task_call())
                    active_role = 'task'
                    result = await watch(active)
                    active = None
                    out['tasks'] = manager.projection()
                    terminal_result = result.get('result')
                    can_continue = synchronous_task and terminal_result is not None and action_recovery.released_task_failure(terminal_result)
                    if can_continue:
                        terminal_result = {**terminal_result, 'continuationAllowed': True}
                    if synchronous_task and terminal_result is not None:
                        if (hint := recovery.observe(name, args, terminal_result)) is not None:
                            terminal_result = {**terminal_result, 'recovery': record_recovery(hint)}
                    if terminal_result is not None and on_result is not None:
                        on_result(result.get('operation', 'mine_target'), args if synchronous_task else {}, terminal_result)
                    if synchronous_task and terminal_result is not None:
                        out['lastAction'] = bounded_action(terminal_result)
                        # A bound refusal has no action or inputs to clean up. Preserve
                        # the prior body state so a later investigation can use this lease.
                        refused = (terminal_result.get('dispatchState')=='not_admitted'
                                   and terminal_result.get('inputsAcquired') is False
                                   and terminal_result.get('actionId') is None)
                        if not refused:
                            released = (safe_terminal(terminal_result) and terminal_result.get('inputsReleased') is True
                                        and terminal_result.get('endEvidenceStatus')=='known'
                                        and terminal_result.get('actionId')==result.get('taskId'))
                            out['actionState'] = 'released' if released else 'unknown'
                    call_end(result.get('status'), result=result)
                    if result.get('status') == 'unknown':
                        out.update(status='action_unknown', actionState='unknown')
                        if module == 'building':
                            await report_terminal('action_unknown', result)
                            continue
                        break
                    if result.get('status') in ('failed', 'timeout', 'cancelled') and not can_continue:
                        out['status'] = result['status']
                        if module == 'building':
                            released = safe_terminal(terminal_result) and terminal_result.get('inputsReleased') is True
                            await report_terminal(out['status'], result, reconcile=released)
                            continue
                        break
                    reply = terminal_result if synchronous_task else result
                    if synchronous_task:
                        summary = out['lastAction'].get('task')
                        if isinstance(summary, dict) and summary.get('historyOmitted'):
                            reply = out['lastAction']
                    active = asyncio.create_task(asyncio.to_thread(connection.send_bytes,
                        encode({'type': 'result', 'id': sequence, 'value': reply})))
                    active_role = 'send'
                    await watch(active)
                    active = None
                    continue
                if (name in atomic.BUDGETED_ACTIONS and program_tasks.available(self.bridge)
                        and program_tasks.coordinator(self.bridge).blocked()):
                    out.update(status='failed', error='active_task_conflict; explicitly cancel and confirm release before replacing')
                    break
                fallback_attempt = False
                if handoff is not None:
                    if name in atomic.BUDGETED_ACTIONS:
                        if name != 'move_input' or not handoff.observed:
                            out.update(status='failed', error='navigation_handoff_requires_fresh_scene_then_move_input')
                            break
                        fallback_attempt = True
                        self.bridge._lua_navigation_handoff = None
                        handoff = None  # One attempt only; no automatic fallback or replay.
                    else:
                        handoff.observed = False  # Scene must immediately precede the next mutation.
                if halted and name in atomic.BUDGETED_ACTIONS:
                    # No dispatch occurred: this is not an unknown game action.
                    # stop_action still dispatches and must prove release below.
                    result = {'valid': False, 'status': 'blocked', 'reason': 'previous_action_failed', 'blocked': True}
                    if on_result is not None:
                        on_result(name, args, result)
                else:
                    lifecycle = {}
                    if name in atomic.BUDGETED_ACTIONS:
                        if out['actions'] >= limits.actions:
                            raise Interrupted('action_limit')
                        out['actions'] += 1
                    active = asyncio.create_task(atomic.dispatch(self.bridge, name, args, chat_id,
                                                                 lifecycle=lifecycle, owner=owner))
                    active_role = 'dispatch'
                    rejected_before_dispatch = False
                    try:
                        result = await watch(active)
                    except atomic.PreDispatchRejected as error:
                        # Only this explicit local refusal proves no RPC/action
                        # occurred. No-start by itself does NOT prove safety:
                        # the initial movement query can fail ambiguously too.
                        rejected_before_dispatch = not (lifecycle.get('start_attempted')
                            or lifecycle.get('action') is not None or lifecycle.get('uncertain'))
                        result = {'valid': False, 'status': 'failed' if rejected_before_dispatch else 'unknown',
                                  'completed': False, 'reason': str(error),
                                  'dispatchState': 'not_admitted' if rejected_before_dispatch else 'unknown'}
                    admission_refused = (lifecycle.get('dispatchState') == 'not_admitted'
                        and result.get('dispatchState') == 'not_admitted'
                        and lifecycle.get('action') is None and not lifecycle.get('uncertain'))
                    rejected_before_dispatch |= admission_refused
                    active = None
                    if (name in atomic.READS and result.get('available') is False and 'binding' in result
                            and 'observedAtUnixMs' not in result):
                        raise BridgeError('Server read unavailable without a confirmed observation')
                    if (name in atomic.READS and result.get('valid') is True
                            and result.get('available') is not False
                            and result.get('status') not in ('failed', 'timeout', 'cancelled', 'unknown', 'unavailable')):
                        out['lastObservation'] = retained_observation(name, sequence, out['runId'], result)
                    if handoff is not None and name in atomic.READS:
                        if (result.get('valid') is not True or result.get('status') in ('unknown', 'timeout', 'cancelled')
                                or name == 'observe_scene' and not handoff.observe_scene(result)):
                            out.update(status='action_unknown', actionState='unknown', error='navigation_handoff_observation_unconfirmed')
                            break
                    if name in atomic.ACTIONS:
                        navigation_continuation = ({'observe_scene', 'move_input'} <= set(getattr(self.bridge, 'supported_atomic_operations', ()))
                            and navigation_recovery.confirmed_failure(result, lifecycle) and not fallback_attempt)
                        result = {**result, 'continuationAllowed': navigation_continuation or
                                  (not fallback_attempt and (safe_preflight_rejection(result, lifecycle)
                                   or admission_refused or action_recovery.released_task_failure(result)))}
                        if navigation_continuation:
                            handoff = navigation_recovery.Handoff(result, lifecycle['action'], chat_id)
                            self.bridge._lua_navigation_handoff = handoff
                            result['continuationScope'] = 'observe_scene_then_move_input'
                        if (hint := recovery.observe(name, args, result)) is not None:
                            result['recovery'] = record_recovery(hint)

                    if on_result is not None:
                        on_result(name, args, result)
                    call_end(result.get('status'), result=result)
                    if name in atomic.ACTIONS:
                        out['lastAction'] = bounded_action(result)
                        if lifecycle.get('uncertain') or not (rejected_before_dispatch or safe_terminal(result)):
                            out['actionState'] = 'unknown'
                            out['status'] = 'action_unknown'
                            if module in ('crafting', 'container', 'inventory'):
                                await report_terminal('action_unknown', {**result,'status':'unknown','completed':False})
                                continue
                            break
                        if not rejected_before_dispatch:
                            out['actionState'] = 'released'
                        halted |= result.get('status') != 'completed' and not result['continuationAllowed']
                call_end(result.get('status'), result=result)
                if (name in atomic.ACTIONS and result.get('status') != 'completed'
                        and result.get('continuationAllowed') is not True):
                    # Only the trusted module can finish its report. User code
                    # cannot resume after this receipt, including inside pcall.
                    out['status'] = result['status']
                    failure_call = dict(call_meta)
                    if module in ('crafting', 'container', 'inventory'):
                        await report_terminal(out['status'], result)
                        continue
                    break
                try:
                    encode(result, VALUE_BYTES)
                except ValueLimit:
                    out['status'] = 'result_limit'
                    break
                frame = {'type': 'result', 'id': sequence, 'value': result}
                if module == 'inventory' or (module == 'container' and name == 'inspect_container'):
                    frame['remainingBudget'] = dict(actions=limits.actions-out['actions'], calls=limits.calls-out['calls'])
                active = asyncio.create_task(asyncio.to_thread(connection.send_bytes, encode(frame)))
                active_role = 'send'
                await watch(active)
                active = None
        except asyncio.CancelledError:
            cancelled = True
            out['status'] = 'cancelled'
        except Interrupted as exc:
            out['status'] = str(exc)
        except ValueLimit:
            out['status'] = 'value_limit'
        except atomic.ArgumentValidationError as exc:
            out.update(status='bad_argument', error=str(exc)[:512],
                       diagnostics=exc.diagnostics, diagnosticsTruncated=exc.diagnostics_truncated,
                       argumentOperation=exc.operation)
        except ValueError as exc:
            out.update(status='bad_argument', error=str(exc)[:512])
        except (EOFError, OSError):
            out['status'] = 'worker_error'
        except (BridgeError, KeyError, TypeError) as exc:
            out.update(status='host_error', error=str(exc)[:512])
        finally:
            # Worker shutdown freezes requests; game cleanup has an independent budget.
            async def finish():
                if process is not None and process.pid is not None:
                    if process.is_alive():
                        process.terminate()
                    await asyncio.to_thread(process.join, .2)
                    if process.is_alive():
                        process.kill()
                        await asyncio.to_thread(process.join, .2)
                    if not process.is_alive():
                        process.close()
                if connection is not None:
                    connection.close()
                await cleanup()
            # Shield against repeated host cancellation while mandatory cleanup proceeds.
            finishing = asyncio.create_task(finish())
            while True:
                try:
                    await asyncio.shield(finishing)
                    break
                except asyncio.CancelledError:
                    cancelled = True
                    out['status'] = 'cancelled'
            if terminal_module_status and out['status'] not in ('action_unknown', 'cancelled'):
                out['status'] = terminal_module_status
            if out['actionState'] == 'unknown':
                out['value'] = None
            call_end(out['status'], error=out.get('error',out['status']))
            if out['status'] != 'completed':
                out['failure'] = {'kind': 'limit' if 'limit' in out['status'] else out['status'],
                    'message': out.get('error',out['status']), 'call': failure_call,
                    'line': out.get('errorLine',(failure_call or {}).get('line')), 'retryAllowed': False,
                    'actionState': out['actionState']}
            self.bridge._lua_owner = None
            manager = getattr(self.bridge, '_program_tasks', None)
            if manager is not None:
                out['tasks'] = manager.projection()
            LuaRuntime._active_runs -= 1
            command_owner.reset(owner_token)
            self.last_result = out
            if call_token is not None:
                tracing.context.reset(call_token)
            if out['status']=='completed':
                diagnostics.exploration_report(out.get('value'))
            tracing.trace.emit('lua_end', **out)
            diagnostics.emit('lua_end', status=out['status'], calls=out['calls'], actions=out['actions'],
                 actionState=out['actionState'], lastAction=out['lastAction'], cleanup=out['cleanup'],
                 failure=out.get('failure'), runtimeLogHealth=diagnostics.log.health(),
                 traceHealth=tracing.trace.health(), elapsed_ms=(limits.seconds-(deadline-monotonic()))*1000)
            if owns_bundle:
                bundle.finish(status=out['status'], actionState=out['actionState'], runId=out['runId'])
            out['evidence'] = bundle.receipt()
            evidence.current.reset(evidence_token)
            tracing.context.reset(token)
        if cancelled:
            raise asyncio.CancelledError
        return out


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('script', type=Path)
    parser.add_argument('--port', type=int, default=18777)
    parser.add_argument('--timeout', type=float, default=2)
    parser.add_argument('--seconds', type=float, default=60)
    args = parser.parse_args()
    with args.script.open('rb') as script:
        raw_source = script.read(SOURCE_BYTES + 1)
    if len(raw_source) > SOURCE_BYTES:
        parser.error('Lua source exceeds 16 KiB')
    source = raw_source.decode('utf-8')
    # Explicit CLI invocation authorizes this fixed program; no model call or registration.
    tracing.trace.enabled = True
    with Bridge(port=args.port, timeout=args.timeout) as bridge:
        runtime = LuaRuntime(bridge)
        result = asyncio.run(runtime.execute(source, limits=Limits(seconds=args.seconds)))
        # Components have independent budgets; do not reapply the per-value
        # depth/node limit to the enclosing host receipt.
        print(json.dumps(result, ensure_ascii=False, allow_nan=False))
    return 0 if result['status'] == 'completed' and result['actionState'] != 'unknown' else 1


if __name__ == '__main__':
    raise SystemExit(main())
