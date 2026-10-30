"""Loopback receipts and real Lua Worker; no native game or model calls."""
import unittest
import test_server_ai_backend as backend
import atomic_operations as atomic
import lua_agent
from lua_runtime import LuaRuntime, Limits
from server_ai import ServerAiBridge


class NavigationPerceptionTests(unittest.IsolatedAsyncioTestCase):
    setUp = backend.BackendTests.setUp
    tearDown = backend.BackendTests.tearDown

    def enable(self):
        self.bridge.close()
        self.host.actions = self.host.scene = self.host.perception = True
        self.bridge = ServerAiBridge(self.host.server_address[1], timeout=1)

    async def test_distinct_navigation_reasons_reach_python_and_lua(self):
        self.enable()
        for reason in ('navigation_stalled', 'navigation_action_limit', 'navigation_search_timeout'):
            def failed(a):
                a.update(status='failed', reason=reason, reasonDetail=reason)
                a['endEvidence']['effectVerified'] = False
                a['evidence'].update(arrived=False, navigation=dict(reason=reason,
                    timeBudgetReached=reason == 'navigation_search_timeout'))
            self.host.action_transform = failed
            result = await atomic.dispatch(self.bridge, 'navigate_to_x', dict(targetX=200))
            self.assertEqual(reason, result['reason'], result)
            self.assertFalse(result['completed'])
            result = await LuaRuntime(self.bridge, evidence_root=self.directory.name).execute(
                "return api('navigate_to_x',{targetX=200})", limits=Limits(seconds=8))
            self.assertEqual('failed', result['status'], result)
            self.assertEqual(reason, result['lastAction']['reason'], result)

    async def test_failed_receipt_preserves_block_evidence_and_release(self):
        self.enable()
        blocks = [dict(x=2, y=0, contentKey='Terraria/Stone', basis='rejected_root_forward_walk_contact')]
        def failed(a):
            a.update(status='failed', reason='native_navigation_failed')
            a['evidence'].update(arrived=False, navigation=dict(blockingTiles=blocks))
            a['endEvidence']['effectVerified'] = False
        self.host.action_transform = failed
        r = await atomic.dispatch(self.bridge, 'navigate_to_x', dict(targetX=200))
        self.assertEqual('failed', r['status'], r)
        self.assertFalse(r['completed'])
        self.assertEqual(blocks, r['evidence']['navigation']['blockingTiles'])
        self.assertTrue(r['endEvidence']['inputsReleased'])
        self.assertEqual('known', r['endEvidenceStatus'])

    async def test_diagnostic_does_not_allow_pcall_to_resume_failed_action(self):
        self.enable()
        def failed(a):
            a.update(status='failed', reason='native_navigation_failed')
            a['evidence'].update(arrived=False, navigation=dict(blockingTiles=[dict(x=2, y=0, contentKey='Terraria/Stone')]))
            a['endEvidence']['effectVerified'] = False
        self.host.action_transform = failed
        r = await LuaRuntime(self.bridge, evidence_root=self.directory.name).execute(
            "pcall(function() api('navigate_to_x',{targetX=200}) end); return api('navigate_to_x',{targetX=300})",
            limits=Limits(seconds=8, actions=2))
        self.assertNotEqual('completed', r['status'], r)
        self.assertEqual(1, sum(q['op'] == 'start' for q in self.host.sent))

    async def test_model_contract_defines_center_body_and_evidence_scope(self):
        self.enable()
        instructions, _ = lua_agent.server_contract(self.bridge)
        for term in ('bodyTileBounds', 'mapTile是中心格', 'blockingTiles', '未可靠定位'):
            self.assertIn(term, instructions)
        for op in ('navigate_to_x', 'navigate_to_landing_region'):
            tool = next(t for t in atomic.TOOLS if t['function']['name'] == op)
            self.assertIn('evidence.navigation.blockingTiles', tool['function']['description'])


if __name__ == '__main__':
    unittest.main()
