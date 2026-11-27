"""Stateful HTTP and stdio MCP transports sharing the Brain Lease lifetime."""
import asyncio
import base64
from contextlib import contextmanager
from dataclasses import dataclass, field
import hashlib
import hmac
import json
import math
import os
from pathlib import Path
import re
import secrets
import sys
import threading

import anyio
from mcp import types
from mcp.server.lowlevel import Server
from mcp.server.stdio import stdio_server
from mcp.server.streamable_http import StreamableHTTPServerTransport
from starlette.datastructures import Headers
from starlette.responses import JSONResponse, Response

import agent_diagnostics as diagnostics
import agent_trace as tracing
from control import BridgeError
from external_brain import EXPLORER_GUIDE, exploration_map_help
from terrain_view import LEGEND, terrain_png


INSTRUCTIONS = (
    '你在驾驶 Terraria 专服角色。按玩家目标直接选用工具。guide() 列出可用游戏 API，guide(names=[...]) 查指定函数。'
    '用 execute_lua 组合操作，同时读取 value 和一次性交付的 events。用 read_skill 查攻略，get_events 收消息，say 回复游戏聊天。'
    '按授权行动，结果 unknown 时停止变更。取消 Lua 后，已受理的独立任务仍需显式取消并确认释放。'
)
MAX_BODY = 128 * 1024
MAX_REQUESTS = 16


def token_digest(token):
    if not isinstance(token, str) or not re.fullmatch(r'[A-Za-z0-9_-]{32,128}', token):
        raise ValueError('TERRARIA_MCP_TOKEN requires 32–128 URL-safe characters from a random generator')
    return hashlib.sha256(token.encode('ascii')).digest()


@contextmanager
def external_host_lock(ai_port):
    """One External Brain process per AI IPC endpoint in this workspace."""
    folder = Path(__file__).resolve().parents[1] / '.local' / 'diagnostics'
    folder.mkdir(parents=True, exist_ok=True)
    with (folder / f'external-brain-{ai_port}.lock').open('a+b') as lock:
        try:
            if sys.platform == 'win32':
                import msvcrt
                lock.seek(0)
                msvcrt.locking(lock.fileno(), msvcrt.LK_NBLCK, 1)
            else:
                import fcntl
                fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except OSError as error:
            raise BridgeError(f'Cannot reserve the External Brain Host for AI port {ai_port}; '
                              'close the other Host before connecting') from error
        yield  # Closing the handle (including on process exit) releases the OS lock.


@dataclass
class Session:
    id: str
    transport: StreamableHTTPServerTransport | None = None
    opening: asyncio.Task | None = None
    server: asyncio.Task | None = None
    closing: asyncio.Task | None = None
    initialized: bool = False
    ready: asyncio.Event = field(default_factory=asyncio.Event)
    jobs: dict = field(default_factory=dict)
    cancelled: set = field(default_factory=set)
    request_ids: set = field(default_factory=set)


class McpBrain:
    def __init__(self, brain):
        self.brain = brain
        self.session = None
        self.closed = False
        instructions = INSTRUCTIONS
        if any(tool['function']['name'] == 'explore' for tool in brain.tools):
            instructions += '\n' + EXPLORER_GUIDE + exploration_map_help(brain.bridge)
        self.sdk = Server('terraria', instructions=instructions)
        self.definitions = [types.Tool(name=f['name'], description=f['description'],
                                      inputSchema=f['parameters'])
                            for item in brain.tools for f in (item['function'],)]
        self.sdk.list_tools()(self.list_tools)
        self.sdk.call_tool()(self.call_tool)

    async def list_tools(self):
        return self.definitions

    async def call_tool(self, name, args):
        ctx = self.sdk.request_context
        session = self.session
        if session is None or session.closing is not None or not session.initialized:
            raise ValueError('External Brain lease is unavailable')
        if name == 'get_events' and not math.isfinite(args.get('wait_seconds', 0)):
            raise ValueError('wait_seconds must be finite')
        trace = tracing.context.set({'mcp_session_id': session.id,
                                     'mcp_request_id': ctx.request_id, 'mcp_tool_name': name})
        try:
            job = asyncio.create_task(self.brain.invoke(name, args))
            session.jobs[job] = name
            tracing.trace.emit('mcp_tool_start')
            diagnostics.emit('mcp_tool_start')
            try:
                result = await asyncio.shield(job)
                tracing.trace.emit('mcp_tool_end', status=result.get('status'), valid=result.get('valid'))
                if name == 'look_around' and result.get('valid'):
                    view = result['data']
                    metadata = {key: value for key, value in view.items() if key != 'rows'}
                    metadata['gameUpdate'] = result['gameUpdate']
                    position = view['actor']
                    caption = (f"角色世界格 ({position['x']}, {position['y']})。"
                               f"视窗 {view['width']}×{view['height']} 格，照亮 {view['visibleTiles']} 格。{LEGEND}")
                    png = base64.b64encode(terrain_png(view)).decode('ascii')
                    return types.CallToolResult(content=[types.TextContent(type='text', text=caption),
                        types.ImageContent(type='image', mimeType='image/png', data=png)], structuredContent=metadata)
                return result
            except asyncio.CancelledError:
                self.cancel_job(session, job)
                # SDK cancellation is an AnyIO scope. Give our separately owned
                # asyncio job time to finish shielded Lua/game cleanup.
                with anyio.CancelScope(shield=True):
                    await asyncio.gather(asyncio.shield(job), return_exceptions=True)
                tracing.trace.emit('mcp_tool_cancelled')
                raise
            except BridgeError as error:
                if self.brain.bridge._closed or error.status in ('stale_binding', 'identity_unavailable', 'engine_unavailable'):
                    self.brain.isolate()
                    return {'valid': False, 'status': 'unknown', 'reason': 'native_outcome_unconfirmed'}
                return {'valid': False, 'status': 'failed', 'reason': error.status or 'native_request_rejected'}
            finally:
                if job.done():
                    session.jobs.pop(job, None)
                    session.cancelled.discard(job)
        finally:
            tracing.context.reset(trace)

    @staticmethod
    def cancel_job(session, job):
        # A say may already be waiting for its native publication receipt.
        # Drain it under the reply fence rather than abandoning its IPC thread.
        if not job.done() and job not in session.cancelled and session.jobs.get(job) != 'say':
            session.cancelled.add(job)
            job.cancel()

    def revoke(self, session, reason):
        if session.closing is None:
            self.brain.begin_release()
            for job in session.jobs:
                self.cancel_job(session, job)
            session.closing = asyncio.create_task(self.release(session, reason))
        return session.closing

    async def release(self, session, reason):
        safe = False
        try:
            if session.opening is not None:
                await asyncio.gather(session.opening, return_exceptions=True)
            if session.jobs:
                await asyncio.gather(*tuple(session.jobs), return_exceptions=True)
            if session.transport is not None:
                await session.transport.terminate()
            if session.server is not None:
                await asyncio.gather(session.server, return_exceptions=True)
            safe = await self.brain.finish_release()
        except Exception as error:
            diagnostics.emit('mcp_lease_cleanup_error', mcp_session_id=session.id,
                             error_type=type(error).__name__)
        finally:
            if not safe:
                self.brain.isolate()
            self.session = None
            diagnostics.emit('mcp_lease_released', mcp_session_id=session.id, reason=reason, safe=safe)
            tracing.trace.emit('mcp_lease_released', mcp_session_id=session.id, reason=reason, safe=safe)

    async def watch_game_connection(self):
        while not self.closed:
            await asyncio.sleep(1)
            session = self.session
            unavailable = self.brain.bridge._closed or getattr(self.brain.agent.chat_bridge, '_failure', None)
            if not unavailable:
                try:
                    await self.brain.poll_pickup_notices()
                except (BridgeError, OSError):
                    unavailable = True
            if unavailable:
                self.brain.isolate()
                if session is not None:
                    await asyncio.shield(self.revoke(session, 'game_connection_unavailable'))
                return

    async def close(self):
        self.closed = True
        self.brain.agent._closing = True
        if self.session is not None:
            await asyncio.shield(self.revoke(self.session, 'host_shutdown'))


class ExternalMcp(McpBrain):
    def __init__(self, brain, port, digest):
        super().__init__(brain)
        self.digest = digest
        self.hosts = {f'127.0.0.1:{port}', f'localhost:{port}'}
        self.requests = 0

    async def open_session(self, session):
        if not await self.brain.acquire() or session.closing is not None:
            return False
        session.server = asyncio.create_task(self.serve(session))
        await session.ready.wait()
        return not session.server.done()

    async def serve(self, session):
        try:
            async with session.transport.connect() as (reader, writer):
                session.ready.set()
                await self.sdk.run(reader, writer, self.sdk.create_initialization_options())
        finally:
            session.ready.set()
            self.revoke(session, 'sdk_session_ended')

    @staticmethod
    async def read_body(receive):
        body = bytearray()
        while True:
            message = await receive()
            if message['type'] == 'http.disconnect':
                return None
            body.extend(message.get('body', b''))
            if len(body) > MAX_BODY:
                raise OverflowError
            if not message.get('more_body', False):
                return bytes(body)

    async def __call__(self, scope, receive, send):
        async def error(status, message, request_id='server-error'):
            await JSONResponse({'jsonrpc': '2.0', 'id': request_id,
                                'error': {'code': -32000, 'message': message}},
                               status_code=status)(scope, receive, send)

        if scope['path'] != '/mcp':
            return await error(404, 'Not found')
        headers = Headers(scope=scope)
        if (len(headers.getlist('host')) != 1 or headers.get('host') not in self.hosts
                or len(headers.getlist('origin')) > 1
                or 'origin' in headers and headers['origin'] not in {f'http://{host}' for host in self.hosts}):
            return await error(403, 'Loopback Host/Origin required')
        auth = headers.get('authorization', '')
        if (len(headers.getlist('authorization')) != 1 or not auth.startswith('Bearer ')
                or not hmac.compare_digest(hashlib.sha256(auth[7:].encode('utf-8')).digest(), self.digest)):
            return await error(401, 'Valid Bearer token required')
        if self.closed or self.requests >= MAX_REQUESTS:
            return await error(503, 'Host unavailable or busy')
        method = scope['method']
        if method not in ('POST', 'DELETE'):
            # The app has no unsolicited server push. POST get_events is the event channel.
            return await Response(status_code=405, headers={'Allow': 'POST, DELETE'})(scope, receive, send)
        if len(headers.getlist('mcp-session-id')) > 1:
            return await error(400, 'Invalid session header')
        session_id = headers.get('mcp-session-id')
        self.requests += 1
        try:
            body = await asyncio.wait_for(self.read_body(receive), timeout=5)
            if body is None:
                session = self.session
                if method == 'POST' and session is not None and session.id == session_id:
                    await asyncio.shield(self.revoke(session, 'http_disconnect'))
                return
            if self.closed:
                return await error(503, 'Host shutting down')
            if method == 'POST':
                if headers.get('content-type', '').partition(';')[0].strip() != 'application/json':
                    return await error(415, 'Content-Type must be application/json')
                if 'application/json' not in headers.get('accept', ''):
                    return await error(406, 'Client must accept application/json')
                try:
                    message = types.JSONRPCMessage.model_validate_json(body).root
                except ValueError:
                    return await error(400, 'Invalid JSON-RPC message')
            else:
                message = None
            opening = session_id is None
            if opening:
                if not isinstance(message, types.JSONRPCRequest) or message.method != 'initialize':
                    return await error(400, 'Initialize a stateful MCP session first')
                try:
                    types.InitializeRequest.model_validate(message.model_dump(by_alias=True))
                except ValueError:
                    return await error(400, 'Invalid initialize request')
                if self.session is not None or not self.brain.reserve():
                    return await error(409, 'Brain Lease unavailable', message.id)
                sid = secrets.token_hex(16)
                session = self.session = Session(sid, StreamableHTTPServerTransport(
                    mcp_session_id=sid, is_json_response_enabled=True))
                session.opening = asyncio.create_task(self.open_session(session))
            else:
                session = self.session
                if (session is None or session.id != session_id or not session.initialized
                        or session.closing is not None):
                    return await error(404, 'MCP session not found')
                if isinstance(message, types.JSONRPCRequest) and message.method == 'initialize':
                    return await error(400, 'Session already initialized')
            request_id = str(message.id) if isinstance(message, types.JSONRPCRequest) else None
            if request_id is not None and request_id in session.request_ids:
                return await error(409, 'JSON-RPC request ID is already in flight')
            if request_id is not None:
                session.request_ids.add(request_id)
            try:
                # The SDK and tool request context only need the session and
                # protocol headers after the Host has checked the credential.
                sdk_scope = {**scope, 'headers': [(key, value) for key, value in scope['headers']
                                                  if key != b'authorization']}
                await self.exchange(session, opening, method, body, sdk_scope, receive, send, error)
            finally:
                session.request_ids.discard(request_id)
        except OverflowError:
            await error(413, 'MCP request body too large')
        except asyncio.TimeoutError:
            await error(408, 'MCP request body timed out')
        finally:
            self.requests -= 1

    async def exchange(self, session, opening, method, body, scope, receive, send, error):
        disconnect = asyncio.create_task(receive())
        status = None
        delivered = False

        async def receive_buffered():
            nonlocal body
            if body is not None:
                message = {'type': 'http.request', 'body': body, 'more_body': False}
                body = None
                return message
            return await asyncio.shield(disconnect)

        async def send_response(message):
            nonlocal status, delivered
            if message['type'] == 'http.response.start':
                status = message['status']
                if method == 'DELETE' and status == 200:
                    await asyncio.shield(self.revoke(session, 'client_delete'))
            if opening and message['type'] == 'http.response.body' and status == 200:
                payload = json.loads(message['body'])
                session.initialized = isinstance(payload.get('result'), dict)
            await send(message)
            if message['type'] == 'http.response.body' and not message.get('more_body', False):
                delivered = True

        async def dispatch():
            if not opening or await asyncio.shield(session.opening):
                await session.transport.handle_request(scope, receive_buffered, send_response)
            elif session.closing is None:
                await error(409, 'Unable to verify safe Brain handoff')

        handler = asyncio.create_task(dispatch())
        try:
            done, _ = await asyncio.wait((handler, disconnect), return_when=asyncio.FIRST_COMPLETED)
            if disconnect in done and not delivered:
                await asyncio.shield(self.revoke(session, 'http_disconnect'))
            if handler in done:
                await handler
        finally:
            if not delivered and (opening or method == 'POST'):
                self.revoke(session, 'incomplete_exchange')
            if opening and not session.initialized:
                self.revoke(session, 'initialize_rejected')
            for pending in (disconnect, handler):
                if not pending.done():
                    pending.cancel()
            await asyncio.gather(disconnect, handler, return_exceptions=True)


async def route_events(brain, events):
    while True:
        brain.receive(await events.get())


async def serve_host(app, port, events):
    import uvicorn

    managed = os.environ.get('COMPANION_MOD_MANAGED_MCP') == '1'

    class ManagedServer(uvicorn.Server):
        async def on_tick(self, counter):
            if counter % 5 == 0:
                # A pending ReadFile on the inherited stdin pipe can stall a
                # spawned Windows Lua Worker. Sample readiness without reading.
                if sys.platform == 'win32':
                    import _winapi
                    import msvcrt
                    handle = msvcrt.get_osfhandle(sys.stdin.fileno())
                    try:
                        available, _ = _winapi.PeekNamedPipe(handle, 0)
                    except BrokenPipeError:
                        return True
                    if available:
                        return True
                else:
                    import select
                    if select.select([sys.stdin], [], [], 0)[0]:
                        return True
            return await super().on_tick(counter)

        async def startup(self, sockets=None):
            await super().startup(sockets=sockets)
            if self.started:
                print('COMPANION_MCP_READY', flush=True)

    server_type = ManagedServer if managed else uvicorn.Server
    server = server_type(uvicorn.Config(app, host='127.0.0.1', port=port, lifespan='off', ws='none',
                                           proxy_headers=False, access_log=False,
                                           timeout_graceful_shutdown=5))
    router = asyncio.create_task(route_events(app.brain, events))
    game_connection = asyncio.create_task(app.watch_game_connection())
    if managed:
        game_connection.add_done_callback(lambda _: setattr(server, 'should_exit', True))
    try:
        await server.serve()
    finally:
        for task in (router, game_connection):
            task.cancel()
        await asyncio.gather(router, game_connection, return_exceptions=True)
        await app.close()


class StdioInput:
    """Bounded pipe input that can stop even if the client keeps stdin open."""
    def __init__(self, fd):
        self.loop = asyncio.get_running_loop()
        self.lines = asyncio.Queue(maxsize=1)
        self.closed = threading.Event()
        # Use a private stream. A blocked read must not pin the SDK's worker
        # thread or hold the interpreter's standard input buffer at shutdown.
        stream = os.fdopen(os.dup(fd), 'rb')
        threading.Thread(target=self.read, args=(stream,), daemon=True, name='mcp-stdin').start()

    def read(self, stream):
        with stream:
            while not self.closed.is_set():
                try:
                    line = stream.readline(MAX_BODY + 1)
                except OSError:
                    line = b''
                if len(line) > MAX_BODY:
                    print('MCP stdin frame exceeds 128 KiB; closing the connection', file=sys.stderr)
                    line = b''
                if self.closed.is_set():
                    return
                # The queue applies backpressure instead of buffering arbitrary
                # client requests in the event loop.
                put = self.lines.put(line)
                try:
                    asyncio.run_coroutine_threadsafe(put, self.loop).result()
                except RuntimeError:
                    put.close()
                    return
                except Exception:
                    return
                if not line:
                    return

    async def __aiter__(self):
        while not self.closed.is_set():
            line = await self.lines.get()
            if not line:
                return
            yield line.decode('utf-8', errors='replace')

    def close(self):
        self.closed.set()
        if not self.lines.full():
            self.lines.put_nowait(b'')


async def serve_stdio(brain, events, stdin_fd, stdout):
    app = McpBrain(brain)
    if not brain.reserve():
        raise BridgeError('External Brain lease unavailable')
    session = app.session = Session(secrets.token_hex(16))
    router = asyncio.create_task(route_events(brain, events))
    try:
        if not await brain.acquire():
            raise BridgeError('Unable to verify safe Brain handoff')
        session.initialized = True  # The SDK enforces the stdio initialize handshake.
        stdin = StdioInput(stdin_fd)
        game_connection = asyncio.create_task(app.watch_game_connection())
        try:
            async with stdio_server(stdin=stdin, stdout=anyio.wrap_file(stdout)) as (reader, writer):
                async with reader, writer:
                    server = asyncio.create_task(app.sdk.run(reader, writer, app.sdk.create_initialization_options()))
                    try:
                        await asyncio.wait((server, game_connection), return_when=asyncio.FIRST_COMPLETED)
                        if server.done():
                            await server
                    finally:
                        stdin.close()
                        server.cancel()
                        await asyncio.gather(server, return_exceptions=True)
            if game_connection.done():
                raise BridgeError('Game connection unavailable; External Brain stdio session closed')
        finally:
            stdin.close()
            game_connection.cancel()
            await asyncio.gather(game_connection, return_exceptions=True)
    finally:
        router.cancel()
        await asyncio.gather(router, return_exceptions=True)
        await app.close()
