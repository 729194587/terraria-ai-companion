"""Stateful HTTP SDK, existing Agent/Worker and loopback game substitutes."""
import asyncio
import os
from pathlib import Path
import secrets
import socket
import sys
import threading
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
import directional_explorer
from external_brain import explorer_available
from agent import is_server_ai_chat, run
from control import BridgeError
from external_brain import BrainHost
from external_mcp import ExternalMcp, token_digest


MOVE = "return api('navigate_to_x',{targetX=180})"
EXPLORE = dict(direction='right', objective='survey the corridor')


class ExplorerGame(fixture.AiHost):
    extra_capabilities = ('server_ai_navigation_v1', 'server_ai_program_tasks_v1', 'server_ai_tasks_v1',
                          'server_ai_region_resources_v1', 'server_ai_scene_region_v1',
                          'server_ai_resources_v1', 'server_ai_directional_landings_v1')

    def __init__(self):
        super().__init__()
        self.scene = self.perception = True
        self.complete = True
        self.hits = []
        self.x = 100
        self.action_transform = self.arrive

    def arrive(self, action):
        if action['operation'] != 'navigate_to' or action['status'] != 'completed':
            return
        target = action['targetTask']
        self.x = target['x'] - 10
        arrival = dict(target=target, arrived=True, grounded=True, safeSupport=True, settled=True,
                       x=self.x, feetY=target['y'])
        action['evidence']['task'] = dict(taskId=action['actionId'], taskName='navigate_to', status='completed',
            currentChild=None, result=dict(inputsReleased=True, effectVerified=True, evidence=arrival))

    def query(self, operation, args):
        if operation != 'observe_scene':
            return super().query(operation, args)
        data = dict(actor=dict(boundsWorldPixels=dict(x=self.x, width=20), feetY=200))
        if args.get('scale') == 'region':
            assert (args['width'], args['height'], args['layers']) == (120, 68, ['resources'])
            data.update(resources=dict(items=self.hits, gameUpdate=33, complete=self.complete, truncated=False,
                unknownCells=int(not self.complete), bounds=dict(minX=0, maxX=119, minY=0, maxY=67)),
                coverage=dict(requestedTiles=8160, unknownByReason={}),
                sample=dict(elapsedMilliseconds=1, samplingBudgetReached=False))
        else:
            data['landingRegions'] = dict(truncated=False, items=[dict(target=dict(
                minPlayerLeftX=self.x, maxPlayerLeftX=self.x+600, surfaceY=200))])
            if args.get('includePassages'):
                data.update(gameUpdate=33,hazards={},passages=dict(originResolved=True,junction=False,
                    returnTarget=dict(x=self.x+10,y=200),exits=[],routes=[dict(direction='right',kind='lateral_landing',
                        target=dict(x=self.x+330,y=200),visitState='no_recent_visit',unknownNearbyTiles=24)]))
        return dict(available=True, worldSessionId=self.binding['world'], actorGeneration=self.binding['generation'],
                    actorSlot=self.binding['slot'], gameUpdate=33, clock='server_utc_at_projection',
                    observedAtUnixMs=1793070000123, data=data)


class ExternalBrainTests(unittest.IsolatedAsyncioTestCase):
    async def test_explore_status_reads_options_without_dispatch(self):
        await self.initialize()
        listing=(await self.http.post(self.url,headers=self.headers,json=self.request('tools/list',{}))).json()
        self.assertIn('exploration_status',[tool['name'] for tool in listing['result']['tools']])
        result=(await self.tool('exploration_status',{})).json()['result']
        self.assertFalse(result['isError'],result)
        view=result['structuredContent']
        self.assertEqual(view['passages']['routes'][0]['unknownNearbyTiles'],24)
        self.assertEqual(view['resumeOptions'],[])
        self.assertEqual(self.aihost.x,100)
    async def asyncSetUp(self):
        if self._testMethodName.startswith('test_explore'):
            self.host_type = ExplorerGame
        await fixture.ServerAiAgentTests.asyncSetUp(self)
        if self._testMethodName=='test_explore_status_reads_options_without_dispatch':
            self.ai.hello['capabilities']=[*self.ai.hello['capabilities'],'server_ai_passages_v1']
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

    async def test_explore_schema_baseline_history_and_resumption(self):
        await self.initialize()
        listing = await self.http.post(self.url, headers=self.headers, json=self.request('tools/list', {}))
        schema = next(t['inputSchema'] for t in listing.json()['result']['tools'] if t['name'] == 'explore')
        self.assertEqual(set(schema['properties']), {'direction', 'objective', 'seek'})
        guide = (await self.tool('guide', {'full': True})).json()['result']['structuredContent']
        self.assertIn('explore(direction,objective)', guide['instructions'])
        for bad in ({}, dict(EXPLORE, direction='east'), dict(EXPLORE, objective=' '),
                    dict(EXPLORE, objective='x'*241), dict(EXPLORE, distance=512)):
            self.assertTrue((await self.tool('explore', bad)).json()['result']['isError'])
        for cap in ExplorerGame.extra_capabilities:
            with patch.dict(self.ai.hello, capabilities=[c for c in self.ai.hello['capabilities'] if c != cap]):
                self.assertFalse(explorer_available(self.agent))
        for op in ('observe_scene', 'navigate_to'):
            with patch.object(self.ai, 'supported_atomic_operations', self.ai.supported_atomic_operations - {op}):
                self.assertFalse(explorer_available(self.agent))
        with patch.object(self.agent, 'program_tasks', None):
            self.assertFalse(explorer_available(self.agent))
        tile = dict(x=20, y=12)
        self.aihost.hits = [dict(kind='chest', typeId=21, contentKey='chest', anchor=tile, tiles=[tile],
                                 bounds=dict(**tile,width=1,height=1), status='complete')]

        async def explore():
            response = (await self.tool('explore', EXPLORE)).json()['result']
            self.assertFalse(response['isError'], response)
            return response['structuredContent']['explorationResult']

        with patch.object(directional_explorer, 'run', wraps=directional_explorer.run) as shared:
            first = await explore()
            shared.assert_awaited_once()
        self.assertEqual(('discovery', []), (first['returnReason'], first['segments']))
        self.assertEqual(tile, first['findings'][0]['items'][0]['anchor'])
        self.aihost.complete = False
        self.assertEqual('observation_incomplete', (await explore())['returnReason'])
        self.aihost.complete = True
        # Inspect the discovered resource through the ordinary Lua entry, then resume.
        self.assertFalse((await self.tool('execute_lua', dict(source="return api('get_player_state')"))).json()['result']['isError'])
        with patch.object(directional_explorer, 'MAX_SEGMENTS', 2):
            resumed = await explore()
        self.assertEqual((2, []), (resumed['progress']['completedSegments'], resumed['findings']))
        await self.http.delete(self.url, headers=self.headers)
        await self.initialize()
        self.assertEqual('discovery', (await explore())['returnReason'])
        self.aihost.hits = []
        self.assertEqual('absent_at_last_location', (await explore())['findings'][0]['changes'][0]['state'])

    async def test_explore_cancel_during_admission(self):
        await self.initialize()
        self.aihost.mode = 'pending'
        gate = threading.Event()
        original = self.ai.task_request
        def delayed_ack(op, task_id, **kwargs):
            reply = original(op, task_id, **kwargs)
            if op == 'start':
                gate.wait(5)
            return reply
        with patch.object(self.ai, 'task_request', delayed_ack):
            job = asyncio.create_task(self.tool('explore', EXPLORE, request_id=76))
            await self.until(lambda: self.aihost.action is not None)
            self.assertTrue((await self.tool('execute_lua', dict(source='return 1'))).json()['result']['isError'])
            self.assertTrue((await self.tool('say', dict(text='busy'))).json()['result']['isError'])
            self.assertFalse((await self.tool('get_events')).json()['result']['isError'])
            await self.http.post(self.url, headers=self.headers, json=dict(jsonrpc='2.0',
                method='notifications/cancelled', params=dict(requestId=76)))
            gate.set()
            await job
            await self.until(lambda: not self.brain.execution_busy)
        self.check_explorer_stop('external', True)

    def check_explorer_stop(self, phase, released):
        self.assertEqual(phase, self.brain.phase)
        starts = [r['actionId'] for r in self.aihost.sent if r['op'] == 'start']
        self.assertEqual([self.brain.last_outcome['taskId']], starts)
        self.assertEqual(starts, [r['actionId'] for r in self.aihost.sent if r['op'] == 'cancel'])
        self.assertEqual(released, self.brain.last_outcome['releaseConfirmed'])
        if released:
            self.assertTrue(self.brain.last_outcome['receipt']['inputsReleased'])
            self.assertEqual('known', self.brain.last_outcome['receipt']['endEvidenceStatus'])
        record = self.agent.program_tasks.records[starts[0]]['view']
        self.assertEqual((1, 'explore'), (record['origin']['segment'], record['origin']['mcp_tool_name']))

    async def test_explore_delete_during_navigation(self):
        await self.initialize()
        self.aihost.mode = 'pending'
        job = asyncio.create_task(self.tool('explore', EXPLORE))
        await self.until(lambda: self.aihost.polls > 0)
        await self.http.delete(self.url, headers=self.headers)
        await job
        self.check_explorer_stop('native', True)

    async def test_explore_revoked_before_wire_send(self):
        await self.initialize()
        entered, resume = threading.Event(), threading.Event()
        send = self.ai.task_request
        def queued(op, task_id, **kwargs):
            if op == 'start':
                entered.set()
                resume.wait(5)
            return send(op, task_id, **kwargs)
        with patch.object(self.ai, 'task_request', queued):
            job = asyncio.create_task(self.tool('explore', EXPLORE))
            await self.until(entered.is_set)
            closing = asyncio.create_task(self.http.delete(self.url, headers=self.headers))
            await self.until(lambda: self.brain.phase == 'releasing')
            resume.set()
            await asyncio.gather(job, closing)
        self.assertEqual('native', self.brain.phase)
        self.assertFalse(any(r['op'] == 'start' for r in self.aihost.sent))
        self.assertEqual('not_admitted', self.brain.last_outcome['receipt']['dispatchState'])

    async def test_explore_http_disconnect_unconfirmed_isolates(self):
        await self.initialize()
        self.aihost.mode = 'cancel_unknown'
        job = asyncio.create_task(self.tool('explore', EXPLORE))
        await self.until(lambda: self.aihost.polls > 0)
        job.cancel()
        await asyncio.gather(job, return_exceptions=True)
        await self.until(lambda: self.app.session is None)
        self.check_explorer_stop('isolated', False)
        self.assertEqual(409, (await self.initialize()).status_code)

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
        self.assertTrue(guide['instructions'].endswith(self.agent.history[0]['content']))
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

    async def test_hybrid_native_agent_receives_client_broadcast(self):
        from test_game_messages import notice
        self.ai.hello['capabilities'].extend(['server_ai_life_status_v1','server_ai_messages_v1'])
        async def serve(app,port,events):
            message=notice(27,"fixture broadcast")
            app.brain.receive(message)
            app.brain.receive(message)
            self.assertIs(app.brain.agent.llm,self.llm)
            self.assertEqual([message],app.brain.agent.recent_game_context()['recentGameEvents'])
        with (patch('agent.LLMClient',return_value=self.llm),patch('agent.Bridge',return_value=self.chat),
              patch('server_ai.ServerAiBridge',return_value=self.ai) as connect,
              patch('external_mcp.serve_host',side_effect=serve)):
            await run(18777,2,server_ai=True,mcp_port=18790,mcp_digest=self.app.digest,with_native_agent=True)
            connect.assert_called_once()

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
