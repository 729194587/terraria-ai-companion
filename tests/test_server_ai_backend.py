"""Real loopback + existing Lua worker with an offline server fixture, not Terraria."""
import asyncio
import copy
import json
from pathlib import Path
import socketserver
import sys
import tempfile
import threading
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'python'))
import atomic_operations as atomic
from control import BridgeError
from lua_runtime import LuaRuntime, Limits
from server_ai import ServerAiBridge


class Host(socketserver.ThreadingTCPServer):
    allow_reuse_address = True
    daemon_threads = True

    def __init__(self):
        super().__init__(('127.0.0.1', 0), Handler)
        self.mode = 'complete'
        self.sent = []
        self.binding = dict(actor='actor-1', world='world-1', generation=1, slot=254)
        self.selected = 0
        self.action = None
        self.polls = 0
        self.release = threading.Event()


class Handler(socketserver.StreamRequestHandler):
    def handle(self):
        h = self.server
        while line := self.rfile.readline():
            r = json.loads(line)
            h.sent.append(r)
            op = r['op']
            error = None
            data = None
            if op == 'bind':
                data = dict(capabilities=['server_ai_atomic_v1', 'atomic_operations_v1', 'hotbar_keys_v1'])
            elif r['binding'] != h.binding:
                error = 'stale_binding'
            elif op == 'start':
                if h.mode == 'drop_start':
                    return
                if h.mode == 'stall_start':
                    h.release.wait(3)
                    return
                key = r['arguments']['slot']
                h.action = dict(actionId=r['actionId'], operation='select_hotbar_slot', session='connection-1',
                                generation=1, worldSessionId='world-1', actorId='actor-1', actorSlot=254,
                                status='running', reason='accepted', executionState='accepted', durationMs=300,
                                evidence=dict(targetSlot=(key + 9) % 10, selectionApplied=False),
                                endEvidenceStatus='not_ended', endEvidence=None)
                data = dict(action=copy.deepcopy(h.action))
            elif op in ('action', 'cancel'):
                if h.mode == 'change_on_poll':
                    h.binding['generation'] = 2
                    self.wfile.write((json.dumps(dict(v=1, id=r['id'], ok=False, error='stale_binding'))+'\n').encode())
                    continue
                h.polls += 1
                if op == 'cancel' or h.mode != 'pending' and h.polls >= 2:
                    a = h.action
                    h.selected = a['evidence']['targetSlot']
                    a.update(status='cancelled' if op == 'cancel' else 'completed', reason='fixture_end',
                             executionState='terminal', endEvidenceStatus='known',
                             endEvidence=dict(worldSessionId='world-1', gameUpdate=33,
                                              phase='ServerAI_game_thread_no_input', inputsAcquired=False,
                                              actorId='actor-1', actorGeneration=1, actorSlot=254,
                                              selectedSlot=h.selected, selectionVerified=True))
                    a['evidence']['selectionApplied'] = True
                    if h.mode == 'bad_evidence':
                        a['endEvidence']['selectionVerified'] = False
                data = dict(action=copy.deepcopy(h.action))
            reply = dict(v=1, id=r['id'], ok=error is None, binding=h.binding, data=data, error=error)
            self.wfile.write((json.dumps(reply) + '\n').encode())


class BackendTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        self.host = Host()
        self.thread = threading.Thread(target=self.host.serve_forever, daemon=True)
        self.thread.start()
        self.bridge = ServerAiBridge(self.host.server_address[1], timeout=1)
        self.directory = tempfile.TemporaryDirectory()

    def tearDown(self):
        self.bridge.close()
        self.host.release.set()
        self.host.shutdown()
        self.host.server_close()
        self.thread.join()
        self.directory.cleanup()

    async def test_existing_dispatch_verifies_key_zero_terminal_evidence(self):
        result = await atomic.dispatch(self.bridge, 'select_hotbar_slot', {'slot': 0})
        self.assertTrue(result['completed'], result)
        self.assertEqual(9, result['endEvidence']['selectedSlot'])
        self.assertEqual('known', result['endEvidenceStatus'])
        self.assertEqual(1, sum(r['op'] == 'start' for r in self.host.sent))

    async def test_actual_lua_worker_uses_existing_dispatcher_and_receipts(self):
        runtime = LuaRuntime(self.bridge, evidence_root=self.directory.name)
        result = await runtime.execute("return api('select_hotbar_slot', {slot=2})", limits=Limits(seconds=8))
        self.assertEqual('completed', result['status'], result)
        self.assertTrue(result['lastAction']['completed'])
        self.assertEqual('released', result['actionState'])
        self.assertEqual(1, result['actions'])

    async def test_unsupported_operations_reject_before_start(self):
        for name, arguments in [('mine_tile', dict(x=1, y=1)), ('get_player_state', {}), ('inspect_inventory', {})]:
            with self.assertRaises(atomic.PreDispatchRejected):
                await atomic.dispatch(self.bridge, name, arguments)
        self.assertFalse(any(r['op'] == 'start' for r in self.host.sent))

    async def test_missing_selection_evidence_preserves_unknown(self):
        self.host.mode = 'bad_evidence'
        result = await atomic.dispatch(self.bridge, 'select_hotbar_slot', {'slot': 2})
        self.assertEqual('unknown', result['status'])
        self.assertFalse(result['completed'])

    async def test_lost_start_receipt_is_unknown_and_never_retried(self):
        self.host.mode = 'drop_start'
        result = await atomic.dispatch(self.bridge, 'select_hotbar_slot', {'slot': 2})
        self.assertEqual('unknown', result['status'])
        self.assertEqual(1, sum(r['op'] == 'start' for r in self.host.sent))

    async def test_transport_timeout_after_start_is_unknown(self):
        self.host.mode = 'stall_start'
        self.bridge.sock.settimeout(.1)
        result = await atomic.dispatch(self.bridge, 'select_hotbar_slot', {'slot': 2})
        self.assertEqual('unknown', result['status'])
        self.assertTrue(self.bridge._closed)
        self.assertEqual(1, sum(r['op'] == 'start' for r in self.host.sent))

    async def test_generation_change_after_acceptance_is_unknown(self):
        self.host.mode = 'change_on_poll'
        result = await atomic.dispatch(self.bridge, 'select_hotbar_slot', {'slot': 2})
        self.assertEqual('unknown', result['status'])
        self.assertTrue(self.bridge._closed)

    async def test_server_unknown_evidence_poisoning_uses_existing_lua_runtime(self):
        self.host.mode = 'bad_evidence'
        runtime = LuaRuntime(self.bridge, evidence_root=self.directory.name)
        result = await runtime.execute("return api('select_hotbar_slot', {slot=2})", limits=Limits(seconds=8))
        self.assertEqual('unknown', result['actionState'], result)
        self.assertTrue(self.bridge._lua_poisoned)
        self.assertTrue(self.bridge._closed)

    async def test_cancellation_uses_existing_lifecycle(self):
        self.host.mode = 'pending'
        lifecycle = {}
        task = asyncio.create_task(atomic.dispatch(self.bridge, 'select_hotbar_slot', {'slot': 2}, lifecycle=lifecycle))
        while self.host.action is None:
            await asyncio.sleep(.01)
        task.cancel()
        with self.assertRaises(asyncio.CancelledError):
            await task
        self.assertEqual('cancelled', lifecycle['cleanup']['status'])
        self.assertTrue(lifecycle['cleanup']['valid'])

    async def test_generation_change_does_not_rebind_or_touch_human(self):
        self.host.binding['generation'] = 2
        with self.assertRaises(BridgeError):
            self.bridge.command('get_movement')
        self.assertFalse(any(r['op'] == 'start' for r in self.host.sent))

    async def test_end_phase_is_not_accepted_without_server_capability(self):
        result = await atomic.dispatch(self.bridge, 'select_hotbar_slot', {'slot': 2})
        action = self.host.action
        self.bridge.hello['capabilities'].remove('server_ai_atomic_v1')
        result = await atomic.terminal(self.bridge, action, 1)
        self.assertEqual('unknown', result['status'])


if __name__ == '__main__':
    unittest.main()
