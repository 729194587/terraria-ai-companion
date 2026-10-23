"""Async host for whole Lua programs; Bridge and atomic supervision stay here."""
import argparse
import asyncio
from dataclasses import dataclass
import hashlib
import json
import math
import multiprocessing as mp
from pathlib import Path
from time import monotonic
import uuid

import agent_trace as tracing
import atomic_operations as atomic
from control import Bridge, BridgeError, command_owner
from lua_worker import (worker, encode, decode, ValueLimit, SOURCE_BYTES,
                        REQUEST_BYTES, VALUE_BYTES, FRAME_BYTES)


@dataclass(frozen=True)
class Limits:
    seconds: float = 60
    calls: int = 64
    instructions: int = 1_000_000
    memory: int = 16_000_000
    cleanup_seconds: float = 3

    def validate(self):
        for value, low, high in ((self.seconds, .05, 300), (self.calls, 1, 256),
                                 (self.instructions, 100, 10_000_000),
                                 (self.memory, 100_000, 64_000_000),
                                 (self.cleanup_seconds, .05, 10)):
            if type(value) not in (int, float) or not math.isfinite(value) or not low <= value <= high:
                raise ValueError('Invalid trusted Lua limit')
        if any(type(v) is not int for v in (self.calls, self.instructions, self.memory)):
            raise ValueError('Integer budgets required')


class Interrupted(Exception):
    pass


def safe_terminal(result):
    return (isinstance(result, dict) and result.get('valid') is True
            and result.get('status') in ('completed', 'failed', 'timeout', 'cancelled')
            and not result.get('supervisorError'))


def bounded_action(result):
    try:
        encode(result, VALUE_BYTES)
        return result
    except ValueLimit:
        return {'valid': result.get('valid'), 'status': result.get('status'),
                'actionId': result.get('actionId'), 'reason': 'action_receipt_limit',
                'payloadOmitted': True}


class LuaRuntime:
    """One exclusive run per Bridge. Unknown action state permanently fences it.

    execute() accepts a complete program, so a future Agent tool can await it with
    its chat_id and cancel its task. It does not register an LLM tool in phase 1.
    """
    _active_runs = 0
    max_workers = 2

    def __init__(self, bridge):
        self.bridge = bridge
        self.last_result = None

    async def execute(self, source, *, chat_id=None, limits=Limits(), cancel=None):
        limits.validate()
        out = {'runId': uuid.uuid4().hex, 'status': 'unknown', 'calls': 0, 'value': None,
               'actionState': 'idle', 'lastAction': None, 'cleanup': None, 'trace': []}
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
        deadline = monotonic() + limits.seconds
        token = tracing.context.set({**tracing.context.get(), 'lua_run_id': out['runId']})
        tracing.trace.emit('lua_start', source_sha256=hashlib.sha256(source.encode()).hexdigest(),
                           source_bytes=len(source.encode()), limits=vars(limits), chat_id=chat_id)

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
                if process is not None and active_role == 'dispatch' and not process.is_alive():
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
                        if lifecycle.get('uncertain') or not safe_terminal(result):
                            await poison()
                        else:
                            out['actionState'] = 'released'
                            out['lastAction'] = bounded_action(result)
                if active_role == 'dispatch':
                    tracing.trace.emit('lua_cleanup', action=lifecycle.get('action'),
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
            process = context.Process(target=worker, args=(child, source, limits), daemon=True)
            try:
                process.start()
            finally:
                child.close()
            sequence, halted = 0, False
            while True:
                active = asyncio.create_task(asyncio.to_thread(connection.recv_bytes, FRAME_BYTES))
                active_role = 'receive'
                message = decode(await watch(active))
                active = None
                if message.get('type') == 'done':
                    if message.get('status') not in ('completed', 'script_error', 'instruction_limit',
                            'memory_limit', 'value_limit', 'bad_argument', 'protocol_error', 'worker_error'):
                        raise Interrupted('protocol_error')
                    out.update(status=message['status'], value=message.get('value'))
                    if 'error' in message:
                        out['error'] = message['error']
                    break
                if (message.get('type') != 'call' or type(message.get('id')) is not int
                        or message['id'] != sequence + 1):
                    raise Interrupted('protocol_error')
                encode(message, REQUEST_BYTES)
                if out['calls'] >= limits.calls:
                    raise Interrupted('call_limit')
                sequence += 1
                out['calls'] += 1
                name, args = message.get('name'), message.get('arguments')
                if type(name) is not str:
                    raise Interrupted('bad_argument')
                atomic.validate(name, args)
                if halted and name not in atomic.READS:
                    result = {'valid': False, 'status': 'blocked', 'reason': 'previous_action_failed'}
                else:
                    lifecycle = {}
                    started = monotonic()
                    tracing.trace.emit('lua_api_start', call_id=sequence, operation=name, arguments=args)
                    active = asyncio.create_task(atomic.dispatch(self.bridge, name, args, chat_id,
                                                                 lifecycle=lifecycle, owner=owner))
                    active_role = 'dispatch'
                    result = await watch(active)
                    active = None
                    tracing.trace.emit('lua_api_result', call_id=sequence, operation=name, result=result,
                                       elapsed_ms=(monotonic() - started) * 1000)
                    if name not in atomic.READS:
                        out['lastAction'] = bounded_action(result)
                        if lifecycle.get('uncertain') or not safe_terminal(result):
                            out['actionState'] = 'unknown'
                            out['status'] = 'action_unknown'
                            break
                        out['actionState'] = 'released'
                        halted |= result.get('status') != 'completed'
                out['trace'].append({'id': sequence, 'operation': name, 'valid': result.get('valid'),
                                     'status': result.get('status'),
                                     'reason': str(result['reason'])[:256] if result.get('reason') is not None else None})
                try:
                    encode(result, VALUE_BYTES)
                except ValueLimit:
                    out['status'] = 'result_limit'
                    break
                active = asyncio.create_task(asyncio.to_thread(connection.send_bytes,
                    encode({'type': 'result', 'id': sequence, 'value': result})))
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
            if out['actionState'] == 'unknown':
                out['value'] = None
            self.bridge._lua_owner = None
            LuaRuntime._active_runs -= 1
            command_owner.reset(owner_token)
            self.last_result = out
            tracing.trace.emit('lua_end', **out)
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
