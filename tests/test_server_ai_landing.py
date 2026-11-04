"""Real server socket + dispatcher/worker; native movement is an explicit substitute."""
import asyncio
import json
import unittest
import test_server_ai_backend as backend
import atomic_operations as atomic
from control import BridgeError
from lua_runtime import LuaRuntime, Limits
from server_ai import ServerAiBridge
from lua_agent import server_contract

NAME = 'navigate_to_landing_region'
GOAL = dict(minPlayerLeftX=164, maxPlayerLeftX=180, surfaceY=192)


class LandingBackendTests(unittest.IsolatedAsyncioTestCase):
    setUp = backend.BackendTests.setUp
    tearDown = backend.BackendTests.tearDown

    def connect(self, *, actions=True, landing=True):
        self.bridge.close()
        self.host.actions, self.host.landing = actions, landing
        self.bridge = ServerAiBridge(self.host.server_address[1], timeout=1)

    async def test_capability_requires_both_native_actions_and_region_support(self):
        for actions, landing in ((True, False), (False, True)):
            self.connect(actions=actions, landing=landing)
            self.assertNotIn(NAME, self.bridge.supported_atomic_operations)
            with self.assertRaises(BridgeError):
                await atomic.dispatch(self.bridge, NAME, GOAL)
        self.assertFalse(any(r['op'] == 'start' for r in self.host.sent))

    async def test_generated_lua_uses_exact_region_and_verified_completion(self):
        self.connect()
        instructions, _ = server_contract(self.bridge)
        self.assertIn(NAME, instructions)
        r = await LuaRuntime(self.bridge, evidence_root=self.directory.name).execute(
            "local p=api('get_player_state',{}); return api('" + NAME + "',"
            "{minPlayerLeftX=p.player.position.x+64,maxPlayerLeftX=180,surfaceY=192})",
            limits=Limits(seconds=8, actions=1))
        self.assertTrue(r['value']['completed'], r)
        self.assertEqual(GOAL, r['value']['targetRegion'])
        self.assertTrue(r['value']['evidence']['settled'])
        self.assertEqual('released', r['actionState'])

    async def test_x_only_or_unsettled_or_wrong_region_is_never_success(self):
        for override in ({'y': 300}, {'x': 181}, {'vy': .2}, {'grounded': False},
                         {'safeSupport': False}, {'settled': False}, {'x': True},
                         {'targetRegion': {**GOAL, 'surfaceY': 208}}):
            with self.subTest(override=override):
                self.connect()
                self.host.landing_override = override
                r = await atomic.dispatch(self.bridge, NAME, GOAL)
                self.assertEqual('unknown', r['status'], r)
                self.assertFalse(r['completed'])

    async def test_changed_target_is_unknown_without_replay(self):
        self.connect()
        self.host.mode = 'changed_region'
        r = await atomic.dispatch(self.bridge, NAME, GOAL)
        self.assertEqual('unknown', r['status'], r)
        self.assertEqual(1, sum(r['op'] == 'start' for r in self.host.sent))

    async def test_cancel_releases_same_region(self):
        self.connect()
        self.host.mode = 'pending'
        task = asyncio.create_task(atomic.dispatch(self.bridge, NAME, GOAL))
        for _ in range(100):
            if self.host.action or task.done():
                break
            await asyncio.sleep(.01)
        if task.done():
            self.assertFalse(task.done(), task.result())
        self.assertIsNotNone(self.host.action)
        task.cancel()
        with self.assertRaises(asyncio.CancelledError):
            await task
        self.assertEqual('cancelled', self.host.action['status'])
        self.assertEqual(GOAL, self.host.action['targetRegion'])
        self.assertTrue(self.host.action['endEvidence']['inputsReleased'])


if __name__ == '__main__':
    unittest.main()
