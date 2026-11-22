"""Stateful HTTP SDK, existing Agent/Worker and loopback game substitutes."""
import asyncio
import os
from pathlib import Path
import secrets
import socket
import sys
import unittest
from unittest.mock import patch

import httpx
try:
    import uvicorn
    from mcp import ClientSession
    from mcp.client.streamable_http import streamable_http_client
except ImportError as error:
    raise unittest.SkipTest('install python/requirements-mcp.txt to verify External Brain') from error

import test_server_ai_agent as fixture
from agent import is_server_ai_chat, run
from control import BridgeError
from external_brain import BrainHost
from external_mcp import ExternalMcp, token_digest


MOVE = "return api('navigate_to_x',{targetX=180})"


class ExternalBrainTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        await fixture.ServerAiAgentTests.asyncSetUp(self)
        if self._testMethodName == 'test_external_private_memory':
            from agent_memory import Notebook
            from agent import Agent
            book = Notebook(Path(self.directory.name),'a'*32,'b'*32)
            self.agent = Agent(self.ai,None,chat_bridge=self.chat,lua_store=self.agent.lua_store,notebook=book)
        self.socket = socket.socket()
        self.socket.bind(('127.0.0.1', 0))
        port = self.socket.getsockname()[1]
        self.url = f'http://127.0.0.1:{port}/mcp'
        self.auth = {'Authorization': f'Bearer {secrets.token_urlsafe(32)}'}
        self.brain = BrainHost(self.agent, is_server_ai_chat)
        self.app = ExternalMcp(self.brain, port, token_digest(self.auth['Authorization'][7:]))
        self.server = uvicorn.Server(uvicorn.Config(self.app, lifespan='off', log_level='warning',
                                                   access_log=False, timeout_graceful_shutdown=2))
        self.serving = asyncio.create_task(self.server.serve(sockets=[self.socket]))
        self.game_connection = asyncio.create_task(self.app.watch_game_connection())
        self.http = httpx.AsyncClient(headers={**self.auth, 'Accept': 'application/json, text/event-stream'},
                                      trust_env=False)
        self.rpc_id = 0
        self.headers = {}
        await self.until(lambda: self.server.started)

    async def asyncTearDown(self):
        await self.app.close()
        self.game_connection.cancel()
        await asyncio.gather(self.game_connection, return_exceptions=True)
        await self.http.aclose()
        self.server.should_exit = True
        await self.serving
        self.socket.close()
        await fixture.ServerAiAgentTests.asyncTearDown(self)

    async def until(self, predicate):
        async def wait():
            while not predicate():
                await asyncio.sleep(.01)
        await asyncio.wait_for(wait(), 5)

    def request(self, method, params, request_id=None):
        if request_id is None:
            self.rpc_id += 1
            request_id = self.rpc_id
        return {'jsonrpc': '2.0', 'id': request_id, 'method': method, 'params': params}

    async def initialize(self, client=None, *, request_id=None):
        body = self.request('initialize', {'protocolVersion': '2025-11-25', 'capabilities': {},
                            'clientInfo': {'name': 'fixture', 'version': '1'}}, request_id)
        response = await (client or self.http).post(self.url, json=body)
        if response.status_code == 200:
            self.headers = {'Mcp-Session-Id': response.headers['Mcp-Session-Id'],
                            'MCP-Protocol-Version': response.json()['result']['protocolVersion']}
            initialized = await self.http.post(self.url, headers=self.headers,
                json={'jsonrpc': '2.0', 'method': 'notifications/initialized'})
            self.assertEqual(202, initialized.status_code)
        return response

    async def tool(self, name, arguments=None, *, request_id=None):
        return await self.http.post(self.url, headers=self.headers,
            json=self.request('tools/call', {'name': name, 'arguments': arguments or {}}, request_id))

    async def game_chat(self, event_id, text):
        self.chathost.emit(event_id, text)
        self.brain.receive(await asyncio.wait_for(self.events.get(), 2))

    async def test_mod_managed_http_ready_and_parent_pipe_shutdown(self):
        with socket.socket() as probe:
            probe.bind(('127.0.0.1', 0))
            port = probe.getsockname()[1]
        token = secrets.token_urlsafe(32)
        proc = await asyncio.create_subprocess_exec(
            sys.executable, '-B', str(Path(fixture.__file__).resolve().parents[1] / 'python/start_agent.py'),
            '--server-ai', '--external-brain', '--mcp-port', str(port),
            '--port', str(self.chathost.server_address[1]), '--ai-port', str(self.aihost.server_address[1]),
            env={**os.environ, 'COMPANION_MOD_MANAGED_MCP': '1', 'TERRARIA_MCP_TOKEN': token},
            stdin=asyncio.subprocess.PIPE, stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.PIPE)
        try:
            ready = await asyncio.wait_for(proc.stdout.readline(), 15)
            self.assertEqual(b'COMPANION_MCP_READY', ready.strip())
            async with httpx.AsyncClient(headers={'Authorization': 'Bearer ' + token}, trust_env=False) as http:
                async with streamable_http_client(f'http://127.0.0.1:{port}/mcp', http_client=http) as (r, w, _):
                    async with ClientSession(r, w) as session:
                        await session.initialize()
                        self.assertFalse((await session.call_tool('guide', {})).isError)
                        try:
                            reply = await asyncio.wait_for(session.call_tool('execute_lua', {
                                'source': "return api('get_player_state', {})"}), timeout=15)
                        except Exception:
                            # Unblock shutdown even if Worker startup regresses.
                            proc.stdin.close()
                            raise
                        result = reply.structuredContent
                        self.assertEqual('completed', result['status'])
                        self.assertEqual(self.aihost.binding['world'], result['value']['worldSessionId'])
                        self.assertEqual(self.aihost.binding['slot'], result['value']['actorSlot'])
                        self.assertTrue(result['value']['player']['inWorld'])
            proc.stdin.close()
            self.assertEqual(0, await asyncio.wait_for(proc.wait(), 12))
        finally:
            if proc.returncode is None:
                proc.kill()
                await proc.wait()

    async def test_external_private_memory(self):
        await self.initialize()
        async def call(tool_name, **args):
            response = await self.tool(tool_name, args)
            return response.json()['result']
        guide = (await call('guide',full=True))['structuredContent']
        self.assertEqual(self.agent.history[0]['content'],guide['instructions'])
        self.assertIn('值得主动记',guide['instructions'])
        self.assertEqual([],guide['privateNotes'])
        await self.game_chat('m1','矿石放东箱')
        note = dict(command='remember', name='仓库', description='矿石放东箱', quote='矿石放东箱')
        self.assertTrue((await call('memory',**note))['isError'])
        await call('get_events')
        self.assertTrue((await call('memory',**note))['structuredContent']['valid'])
        recalled = (await call('memory',command='recall',name='仓库'))['structuredContent']['note']
        self.assertEqual('m1',recalled['origin']['turnId'])
        self.assertEqual('仓库',(await call('get_events'))['structuredContent']['privateNotes'][0]['name'])
        await self.game_chat('m2','去基地')
        self.assertTrue((await call('memory',**note))['isError'])
        self.assertEqual([],self.requests)

    async def test_sdk_session_tools_events_say_and_native_resume(self):
        async with httpx.AsyncClient(headers=self.auth, trust_env=False) as client:
            async with streamable_http_client(self.url, http_client=client) as (read, write, _):
                async with ClientSession(read, write) as session:
                    info = await session.initialize()
                    self.assertIn('guide', info.instructions)
                    tools = await session.list_tools()
                    self.assertEqual({'guide', 'execute_lua', 'read_skill', 'get_events', 'say'},
                                     {tool.name for tool in tools.tools})
                    guide = (await session.call_tool('guide', {})).structuredContent
                    self.assertIn('return api("help")', guide['instructions'])
                    self.assertIn('return api("help",{name="get_player_state"})', guide['instructions'])
                    self.assertIn('returnsSchema', guide['instructions'])
                    self.assertIn('Lua Runtime Reference', guide['instructions'])
                    self.assertNotIn('"parameters":{"type":"object"', guide['instructions'])
                    self.assertLess(len(guide['instructions']), len(self.agent.history[0]['content']))
                    sent_before_help = len(self.aihost.sent)
                    scoped = (await session.call_tool('guide', {'names': ['get_player_state', 'missing_api']})).structuredContent
                    self.assertEqual(['get_player_state'], [fn['name'] for fn in scoped['functions']])
                    self.assertEqual(['missing_api'], scoped['unavailable'])
                    self.assertNotIn('instructions', scoped)
                    self.assertEqual(sent_before_help, len(self.aihost.sent))
                    help_result = (await session.call_tool('execute_lua', {'source':
                        "return {index=api('help'),detail=api('help',{name='get_player_state'})}"})).structuredContent
                    self.assertEqual('completed', help_result['status'])
                    self.assertEqual(0, help_result['actions'])
                    self.assertEqual('get_player_state', help_result['value']['detail']['name'])
                    self.assertEqual('https://json-schema.org/draft/2020-12/schema',
                                     help_result['value']['detail']['returnsSchema']['$schema'])
                    self.assertIn('Player', help_result['value']['detail']['returnsSchema']['$defs'])
                    # execute_lua still attaches the ordinary runtime inventory
                    # context. Help itself does not execute get_player_state.
                    self.assertTrue(all(r['operation'] == 'inspect_inventory'
                                        for r in self.aihost.sent[sent_before_help:] if r['op'] == 'query'))
                    index = {name for category in help_result['value']['index']['categories'] for name in category['apis']}
                    self.assertEqual(set(self.ai.supported_atomic_operations), index)
                    self.assertFalse(any(r['op'] == 'start' for r in self.aihost.sent))
                    skill = await session.call_tool('read_skill', {'name': 'mining_and_ores'})
                    self.assertTrue(skill.structuredContent['valid'])
                    for request_id in (0, 'client-b'):
                        busy = await self.initialize(request_id=request_id)
                        self.assertEqual(409, busy.status_code)
                        self.assertEqual(request_id, busy.json()['id'])
                        self.assertEqual('Brain Lease unavailable', busy.json()['error']['message'])
                    await self.game_chat('external-chat', '向右移动')
                    self.brain.receive({'type': 'game_message', 'eventId': 'sys-1', 'text': 'example',
                                        'droppedBefore': 2})
                    self.assertEqual([], self.requests)
                    moving = asyncio.create_task(session.call_tool('execute_lua', {'source':
                        "local p=api('get_player_state'); assert(p.valid); " + MOVE}))
                    await self.until(lambda: self.brain.active_program is not None)
                    await self.game_chat('during-move', '到了告诉我')
                    result = (await moving).structuredContent
                    self.assertTrue(result['actionSuccess'])
                    self.assertEqual('known', result['lastAction']['endEvidenceStatus'])
                    self.assertEqual('available', result['runtimeInventory']['status'])
                    self.assertEqual(['external-chat', 'sys-1', 'during-move'], [e['eventId'] for e in result['events']])
                    self.assertEqual(2, result['eventCoverage']['serverDroppedCount'])
                    events = await session.call_tool('get_events', {})
                    self.assertEqual([], events.structuredContent['events'])
                    reply = await session.call_tool('say', {'text': '已移动。'})
                    self.assertEqual(254, reply.structuredContent['receipt']['authorSlot'])
        await self.until(lambda: self.brain.phase == 'native')
        self.answers = [{'role': 'assistant', 'content': '你好！'}]
        await self.game_chat('native-chat', '你好')
        await self.agent.task
        self.assertEqual(1, len(self.requests))
        self.assertEqual(200, (await self.initialize()).status_code)
        self.assertEqual(1, sum(r['op'] == 'bind' for r in self.aihost.sent))

    async def test_cancel_notifications_drain_poll_and_program_without_releasing_lease(self):
        self.assertEqual(200, (await self.initialize()).status_code)
        for name, args, request_id, started in (
            ('get_events', {'wait_seconds': 45}, 20, lambda: self.brain.polling),
            ('execute_lua', {'source': MOVE}, 21, lambda: self.aihost.action is not None),
        ):
            self.aihost.mode = 'pending'
            call = asyncio.create_task(self.tool(name, args, request_id=request_id))
            await self.until(started)
            cancelled = await self.http.post(self.url, headers=self.headers, json={
                'jsonrpc': '2.0', 'method': 'notifications/cancelled', 'params': {'requestId': request_id}})
            self.assertEqual(202, cancelled.status_code)
            # The SDK can answer the abandoned request before game cleanup ends.
            await self.until(lambda: not self.app.session.jobs)
            await asyncio.wait_for(call, 5)
            self.assertEqual('external', self.brain.phase)
        self.assertEqual(1, sum(r['op'] == 'cancel' for r in self.aihost.sent))
        self.assertEqual('released', self.agent._interrupted[-1]['actionState'])
        deleted = await self.http.delete(self.url, headers=self.headers)
        self.assertEqual(200, deleted.status_code)
        self.assertEqual('native', self.brain.phase)
        self.assertEqual(404, (await self.tool('guide')).status_code)

    async def test_dropped_http_post_waits_for_cleanup_and_unknown_isolates(self):
        self.assertEqual(200, (await self.initialize()).status_code)
        self.aihost.mode = 'cancel_unknown'
        # Cancel the client HTTP read while the real loopback POST is in flight.
        call = asyncio.create_task(self.tool('execute_lua', {'source': MOVE}))
        await self.until(lambda: self.aihost.action is not None)
        call.cancel()
        await asyncio.gather(call, return_exceptions=True)
        await self.until(lambda: self.app.session is None)
        self.assertEqual('isolated', self.brain.phase)
        self.assertEqual(1, sum(r['op'] == 'cancel' for r in self.aihost.sent))
        self.assertEqual(409, (await self.initialize()).status_code)
        await self.game_chat('must-not-resume', '向右移动')
        self.assertEqual([], self.requests)

    async def test_auth_host_origin_and_body_limit(self):
        for headers, status in (({'Authorization': 'Bearer invalid'}, 401),
                                ({'Host': 'not-loopback.example'}, 403),
                                ({'Origin': 'https://not-loopback.example'}, 403)):
            self.assertEqual(status, (await self.http.post(self.url, headers=headers, content=b'{}')).status_code)
        self.assertEqual(413, (await self.http.post(self.url, content=b'x' * (128 * 1024 + 1))).status_code)
        self.assertIsNone(self.app.session)

    async def test_idle_session_keeps_exclusive_lease_until_delete(self):
        self.assertEqual(200, (await self.initialize()).status_code)
        session = self.app.session
        loop = asyncio.get_running_loop()
        real_time = loop.time
        # Keep the clock advanced through teardown so scheduled timers stay due.
        clock = patch.object(loop, 'time', lambda: real_time() + 121)
        clock.start()
        self.addCleanup(clock.stop)
        await asyncio.sleep(.1)
        self.assertIs(session, self.app.session)
        self.assertEqual('external', self.brain.phase)
        self.assertEqual(409, (await self.initialize()).status_code)
        guide = await self.tool('guide')
        self.assertEqual(200, guide.status_code)
        self.assertIn('instructions', guide.json()['result']['structuredContent'])
        self.assertEqual(200, (await self.http.delete(self.url, headers=self.headers)).status_code)
        self.assertEqual('native', self.brain.phase)
        self.assertEqual(404, (await self.tool('guide')).status_code)

    async def test_external_only_host_does_not_construct_llm(self):
        with (patch('agent.LLMClient') as llm, patch('agent.Bridge', return_value=self.chat),
              patch('server_ai.ServerAiBridge', return_value=self.ai), patch('external_mcp.serve_host') as serve):
            await run(18777, 2, server_ai=True, mcp_port=18790, mcp_digest=self.app.digest)
            llm.assert_not_called()
            self.assertIsNone(serve.await_args.args[0].brain.agent.llm)

    async def test_acquisition_drains_in_flight_native_lua(self):
        self.aihost.mode = 'pending'
        self.answers = [fixture.ServerAiAgentTests.program(self, MOVE)]
        await self.game_chat('native-active', '移动')
        await self.until(lambda: self.aihost.action is not None)
        native = self.agent.task
        self.assertEqual(200, (await self.initialize()).status_code)
        self.assertTrue(native.cancelled())
        self.assertEqual(1, sum(r['op'] == 'cancel' for r in self.aihost.sent))
        self.assertEqual('released', self.agent._interrupted[-1]['actionState'])
        self.assertEqual(1, len(self.requests))

    async def test_binding_change_during_delete_does_not_resume_native(self):
        self.assertEqual(200, (await self.initialize()).status_code)
        self.aihost.binding = {**self.aihost.binding, 'generation': 2}
        self.assertEqual(200, (await self.http.delete(self.url, headers=self.headers)).status_code)
        self.assertEqual('isolated', self.brain.phase)
        self.assertTrue(self.ai._closed)

    async def test_chat_channel_failure_still_cleans_up_the_execution_channel(self):
        self.assertEqual(200, (await self.initialize()).status_code)
        self.aihost.mode = 'pending'
        call = asyncio.create_task(self.tool('execute_lua', {'source': MOVE}))
        await self.until(lambda: self.aihost.action is not None)
        self.chat._failure = BridgeError('fixture chat disconnected')
        await self.until(lambda: self.app.session is None)
        await asyncio.wait_for(call, 5)
        self.assertEqual('isolated', self.brain.phase)
        self.assertEqual(1, sum(r['op'] == 'cancel' for r in self.aihost.sent))
        self.assertEqual('released', self.agent._interrupted[-1]['actionState'])
