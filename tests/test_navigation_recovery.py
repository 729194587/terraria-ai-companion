"""Real Lua Worker/dispatcher + loopback receipts; movement here is a test substitute."""
import asyncio
import copy
import unittest

import test_server_ai_backend as backend
from lua_runtime import LuaRuntime, Limits
from server_ai import ServerAiBridge

SOURCE = '''
local n=api('navigate_to_x',{targetX=164})
if n.completed then return {navigation=n} end
assert(n.status=='failed' and n.continuationAllowed and n.continuationScope=='observe_scene_then_move_input')
local s=api('observe_scene',{layers={'geometry','hazards'}})
if s.data.actor.dead or not s.data.coverage.geometryComplete then return {stopped=true,navigation=n} end
local x=s.data.actor.boundsWorldPixels.x
local direction=x<164 and 'right' or 'left'
local m=api('move_input',{horizontal=direction,vertical='none',jumpHoldUpdates=8,maxUpdates=32})
local after=api('get_player_state',{})
return {navigation=n,movement=m,moved=m.completed and m.evidence.endPosition.X>x,after=after.player.position.x}
'''


class NavigationRecoveryTests(unittest.IsolatedAsyncioTestCase):
    setUp = backend.BackendTests.setUp
    tearDown = backend.BackendTests.tearDown

    def enable(self, change=None, scene_change=None, move_change=None, hold_move=False):
        self.bridge.close()
        self.host.actions = self.host.perception = self.host.scene = self.host.direct_input = True
        self.host.mode = "complete"
        self.host.polls = 0
        self.host.sent.clear()
        self.host.action = None
        self.x = 100
        self.transformed = {}
        original = self.host.query
        def query(operation, args):
            r = original('get_player_state', args)
            r.update(gameUpdate=36, observedAtUnixMs=1793250000000, clock='server_utc_at_projection')
            r['data']['player']['position']['x'] = self.x
            if operation == 'observe_scene':
                r['data'] = dict(sceneId='fresh-scene',gameUpdate=36,actor=dict(slot=254,dead=False,
                    boundsWorldPixels=dict(x=self.x,y=200,width=20,height=42),velocity=dict(x=0,y=0)),
                    coverage=dict(geometryComplete=True), geometry=dict(rows=['.']),hazards=dict(items=[]))
                if scene_change:scene_change(r)
            return r
        self.host.query = query
        def transform(a):
            if a['actionId'] in self.transformed:
                a.update(copy.deepcopy(self.transformed[a['actionId']]))
                return
            if a['operation']=='navigate_to_x':
                a.update(status='failed',reason='native_navigation_failed',reasonDetail='no safe local route: fixture obstruction',
                    evidence=dict(recoverableFailure=True,failureKind='no_route',inputReleased=True,alive=True,
                        grounded=True,safeSupport=True,settled=True,x=self.x,y=200,vx=0,vy=0,
                        observedGameUpdate=33,effectStatus='native_navigation_observed'))
                a['endEvidence']['effectVerified']=False
                if change:change(a)
                if hold_move:self.host.mode="pending"
            elif a['operation']=='move_input':
                before=self.x;self.x+=64
                a['evidence'].update(startPosition=dict(X=before,Y=200),endPosition=dict(X=self.x,Y=200))
                if move_change:move_change(a)
            self.transformed[a['actionId']] = copy.deepcopy(a)
        self.host.action_transform=transform
        self.bridge=ServerAiBridge(self.host.server_address[1],timeout=2)

    def starts(self):return [r['operation'] for r in self.host.sent if r['op']=='start']

    async def test_failed_navigation_reobserve_direct_input_and_branch_on_actual_result(self):
        self.enable()
        callbacks=[]
        r=await LuaRuntime(self.bridge,evidence_root=self.directory.name).execute(SOURCE,
            limits=Limits(seconds=8,calls=4,actions=2),on_result=lambda n,a,r:callbacks.append(copy.deepcopy(r)))
        self.assertEqual('completed',r['status'],r)
        self.assertTrue(r['value']['moved'])
        self.assertEqual(164,r['value']['after'])
        self.assertEqual(['navigate_to_x','move_input'],self.starts())
        self.assertEqual((4,2,'released'),(r['calls'],r['actions'],r['actionState']))
        self.assertEqual('failed',callbacks[0]['status'])
        self.assertEqual(100,callbacks[0]['evidence']['x'])
        self.assertTrue(callbacks[0]['endEvidence']['inputsReleased'])

    async def test_success_does_not_create_a_fallback_permission(self):
        self.enable(lambda a:a.update(status='completed',reason='native_navigation_arrival_verified',
            evidence=dict(arrived=True)))
        self.host.action_transform=lambda a:(a.update(evidence=dict(arrived=True)) if a['operation']=='navigate_to_x' else None)
        r=await LuaRuntime(self.bridge).execute(SOURCE)
        self.assertEqual('completed',r['status'],r)
        self.assertTrue(r['value']['navigation']['completed'])
        self.assertEqual(['navigate_to_x'],self.starts())
        self.assertIsNone(getattr(self.bridge,'_lua_navigation_handoff',None))

    async def test_uncertain_cancel_timeout_binding_and_release_still_stop(self):
        changes={
            'unknown':lambda a:a.update(status='unknown'),
            'cancelled':lambda a:a.update(status='cancelled'),
            'timeout':lambda a:a.update(status='timeout'),
            'search_timeout':lambda a:a.update(reason='navigation_search_timeout'),
            'expansion_budget':lambda a:a.update(reason='navigation_search_expansion_budget'),
            'no_native_permission':lambda a:a['evidence'].pop('recoverableFailure'),
            'not_settled':lambda a:a['evidence'].update(settled=False),
            'bad_velocity':lambda a:a['evidence'].update(vx=1),
            'unknown_release':lambda a:a['endEvidence'].update(inputsReleased=False),
            'generation':lambda a:a['endEvidence'].update(actorGeneration=2),
            'world':lambda a:a['endEvidence'].update(worldSessionId='other'),
        }
        for name,change in changes.items():
            with self.subTest(case=name):
                self.enable(change)
                r=await LuaRuntime(self.bridge).execute(SOURCE,limits=Limits(seconds=8))
                self.assertNotEqual('completed',r['status'],r)
                self.assertEqual(['navigate_to_x'],self.starts())
                self.assertFalse(any(q.get('operation')=='observe_scene' for q in self.host.sent))
                self.host.sent.clear()

    async def test_missing_or_stale_scene_and_immediate_navigation_retry_are_blocked(self):
        for next_call in ("api('move_input',{horizontal='right',vertical='none',jumpHoldUpdates=0,maxUpdates=20})",
                          "api('navigate_to_x',{targetX=164})"):
            self.enable()
            r=await LuaRuntime(self.bridge).execute("api('navigate_to_x',{targetX=164}); "+next_call)
            self.assertEqual('failed',r['status'],r)
            self.assertEqual(['navigate_to_x'],self.starts())
            self.host.sent.clear()
        for change in (lambda r:r['data'].update(gameUpdate=32),lambda r:r['data']['actor'].update(dead=True)):
            self.enable(scene_change=change)
            r=await LuaRuntime(self.bridge).execute(SOURCE)
            self.assertEqual('unknown',r['actionState'],r)
            self.assertTrue(self.bridge._lua_poisoned)
            self.assertEqual(['navigate_to_x'],self.starts())
            self.host.sent.clear()

    async def test_direct_input_failure_does_not_grant_another_fallback(self):
        def rejection(a):
            a.update(status='failed',reason='actor_busy',evidence=dict(operation='move_input',mutationAttempted=False,effectStatus='not_started'))
            a['endEvidence'].update(inputsAcquired=False,inputsReleased=True,effectVerified=False)
        self.enable(move_change=rejection)
        source="""api('navigate_to_x',{targetX=164}); api('observe_scene',{});
        api('move_input',{horizontal='right',vertical='none',jumpHoldUpdates=0,maxUpdates=20});
        return api('move_input',{horizontal='right',vertical='none',jumpHoldUpdates=0,maxUpdates=20})"""
        r=await LuaRuntime(self.bridge).execute(source)
        self.assertEqual('failed',r['status'],r)
        self.assertFalse(r['lastAction']['continuationAllowed'])
        self.assertEqual(['navigate_to_x','move_input'],self.starts())

    async def test_cancellation_during_fallback_releases_and_never_replays(self):
        self.enable(hold_move=True)
        runtime=LuaRuntime(self.bridge)
        task=asyncio.create_task(runtime.execute(SOURCE))
        async with asyncio.timeout(10):
            while (self.host.action or {}).get('operation')!='move_input':await asyncio.sleep(.01)
        task.cancel()
        with self.assertRaises(asyncio.CancelledError):await task
        r=runtime.last_result
        self.assertEqual('released',r['actionState'],r)
        self.assertEqual('cancelled',r['lastAction']['status'])
        self.assertTrue(r['lastAction']['endEvidence']['inputsReleased'])
        self.assertEqual(['navigate_to_x','move_input'],self.starts())
        self.assertEqual(1,sum(q['op']=='cancel' for q in self.host.sent))

    async def test_missing_backend_capability_never_grants_fallback(self):
        for missing in ('observe_scene','move_input'):
            self.enable()
            self.bridge.supported_atomic_operations -= {missing}
            r=await LuaRuntime(self.bridge).execute(SOURCE)
            self.assertEqual('failed',r['status'],r)
            self.assertFalse(r['lastAction']['continuationAllowed'])
            self.assertEqual(['navigate_to_x'],self.starts())

    async def test_handoff_and_shared_budgets_cannot_be_reset_by_returning(self):
        self.enable()
        runtime=LuaRuntime(self.bridge)
        r=await runtime.execute("return api('navigate_to_x',{targetX=164})")
        self.assertEqual('completed',r['status'])
        self.assertTrue(r['value']['continuationAllowed'])
        r=await runtime.execute("return api('navigate_to_x',{targetX=164})")
        self.assertEqual('failed',r['status'])
        self.assertEqual(['navigate_to_x'],self.starts())
        for limits,expected in ((Limits(seconds=8,actions=1),'action_limit'),(Limits(seconds=8,calls=2),'call_limit')):
            self.enable()
            r=await LuaRuntime(self.bridge).execute(SOURCE,limits=limits)
            self.assertEqual(expected,r['status'],r)
            self.assertEqual(['navigate_to_x'],self.starts())
            self.host.sent.clear()


if __name__=='__main__':unittest.main()
