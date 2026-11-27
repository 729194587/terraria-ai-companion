"""Exercise the real MCP subprocess with offline loopback game bridges."""
import asyncio
from contextlib import asynccontextmanager
import json
import os
from pathlib import Path
import socket
import sys
import tempfile
import threading
import unittest

from test_server_ai_agent import AiHost, ChatHost

try:
    import mcp  # noqa: F401
except ImportError as error:
    raise unittest.SkipTest('install python/requirements-mcp.txt to verify External Brain') from error


ROOT = Path(__file__).resolve().parents[1]
TOOLS = {'guide', 'execute_lua', 'read_skill', 'get_events', 'say'}


class StdioClient:
    request_id = 0

    async def send(self, child, method, params=None, *, notification=False):
        frame = {'jsonrpc': '2.0', 'method': method, 'params': params or {}}
        if not notification:
            self.request_id += 1
            frame['id'] = self.request_id
        child.stdin.write((json.dumps(frame) + '\n').encode())
        await child.stdin.drain()
        return frame.get('id')

    async def request(self, child, method, params=None):
        request_id = await self.send(child, method, params)
        line = await asyncio.wait_for(child.stdout.readline(), 15)
        assert line, 'MCP subprocess closed before sending a response'
        message = json.loads(line.decode('utf-8', errors='strict'))
        assert (message['jsonrpc'], message['id']) == ('2.0', request_id), message
        assert 'error' not in message, message
        return message['result']

    async def initialize(self, child):
        info = await self.request(child, 'initialize', {
            'protocolVersion': '2025-11-25', 'capabilities': {},
            'clientInfo': {'name': 'stdio-test', 'version': '1'},
        })
        assert info['serverInfo']['name'] == 'terraria'
        await self.send(child, 'notifications/initialized', notification=True)


class StdioBrainTests(StdioClient, unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.ai = AiHost()
        self.ai.actions = self.ai.chat = True
        self.chat = ChatHost()
        for host in (self.ai, self.chat):
            threading.Thread(target=host.serve_forever, daemon=True).start()
            self.addCleanup(host.server_close)
            self.addAsyncCleanup(asyncio.to_thread, host.shutdown)
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.directory = directory.name
        self.request_id = 0

    @asynccontextmanager
    async def host(self, transport='stdio'):
        env = os.environ.copy()
        env.pop('TERRARIA_MCP_TOKEN', None)
        if transport == 'http':
            env['TERRARIA_MCP_TOKEN'] = 'a' * 32
        child = await asyncio.create_subprocess_exec(
            sys.executable, '-B', str(ROOT / 'python' / 'agent.py'),
            '--server-ai', '--external-brain', '--mcp-transport', transport,
            '--port', str(self.chat.server_address[1]), '--ai-port', str(self.ai.server_address[1]),
            '--log-file', str(Path(self.directory) / 'host.jsonl'),
            cwd=ROOT, env=env, stdin=asyncio.subprocess.PIPE,
            stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.PIPE,
            limit=512 * 1024,
        )
        try:
            yield child
        except Exception as error:
            if not child.stdin.is_closing():
                child.stdin.close()
            out, err = await asyncio.wait_for(child.communicate(), 10)
            log = Path(self.directory) / 'host.jsonl'
            facts = [json.loads(row).get('type') for row in log.read_text(encoding='utf-8').splitlines()] if log.exists() else []
            self.fail(f'{error!r}; exit={child.returncode}; stdout={out!r}; stderr={err!r}; log events={facts}')
        finally:
            if not child.stdin.is_closing():
                child.stdin.close()
            try:
                await asyncio.wait_for(child.communicate(), 10)
            except asyncio.TimeoutError:
                child.kill()
                await child.communicate()
                self.fail('MCP subprocess failed to exit after stdin EOF')

    async def test_token_free_tools_and_cross_transport_exclusivity(self):
        async with self.host() as child:
            await self.initialize(child)
            tools = await self.request(child, 'tools/list')
            self.assertEqual(TOOLS, {tool['name'] for tool in tools['tools']})
            guide = await self.request(child, 'tools/call', {'name': 'guide', 'arguments': {}})
            index = guide['structuredContent']['instructions']
            self.assertIn('get_player_state', index)
            self.assertNotIn('Lua Runtime Reference', index)
            self.assertNotIn('open_container', index)  # Unavailable on this bridge.
            value = await self.request(child, 'tools/call', {
                'name': 'execute_lua', 'arguments': {'source': "return {life=api('get_player_state').player.life}"},
            })
            self.assertEqual((73, 0), (value['structuredContent']['value']['life'],
                                      value['structuredContent']['actions']))
            async with self.host('http') as competing:
                out, err = await asyncio.wait_for(competing.communicate(), 10)
                self.assertEqual((1, b''), (competing.returncode, out))
                self.assertIn(b'Cannot reserve the External Brain Host', err)
            child.stdin.close()
            out, err = await asyncio.wait_for(child.communicate(), 10)
            self.assertEqual((0, b''), (child.returncode, out))
            self.assertIn(b'Runtime log:', err)

    async def test_eof_cancels_active_action_and_accepts_fresh_host(self):
        self.ai.mode = 'pending'
        async with self.host() as child:
            await self.initialize(child)
            await self.send(child, 'tools/call', {
                'name': 'execute_lua', 'arguments': {'source': "return api('navigate_to_x',{targetX=180})"},
            })
            async def action_started():
                while self.ai.action is None:
                    await asyncio.sleep(.01)
            await asyncio.wait_for(action_started(), 10)
            child.stdin.close()
            await asyncio.wait_for(child.communicate(), 10)
            self.assertEqual(0, child.returncode)
            self.assertEqual(1, sum(row['op'] == 'cancel' for row in self.ai.sent))
        async with self.host() as child:
            await self.initialize(child)
            self.assertEqual(TOOLS, {tool['name'] for tool in (await self.request(child, 'tools/list'))['tools']})

    async def test_game_connection_loss_exits_with_stdin_still_open(self):
        async with self.host() as child:
            await self.initialize(child)
            self.chat.handler.request.shutdown(socket.SHUT_RDWR)
            await asyncio.wait_for(child.wait(), 10)
            self.assertEqual(1, child.returncode)
            self.assertEqual(b'', await child.stdout.read())
            self.assertIn(b'Game connection unavailable', await child.stderr.read())
        async with self.host() as child:
            await self.initialize(child)

    async def test_killed_host_releases_process_lock(self):
        async with self.host() as child:
            await self.initialize(child)
            child.kill()
            await asyncio.wait_for(child.wait(), 10)
        async with self.host() as child:
            await self.initialize(child)
