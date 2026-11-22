"""Real Worker/production dispatcher; deterministic loopback game evidence."""
import copy
import json
import unittest

import test_server_ai_backend as backend
from control import BridgeError
import lua_agent
from lua_runtime import LuaRuntime, Limits
from server_ai import ServerAiBridge


class RecoveryTests(unittest.IsolatedAsyncioTestCase):
    setUp = backend.BackendTests.setUp
    tearDown = backend.BackendTests.tearDown

    def enable(self, extra=None):
        self.bridge.close()
        self.host.scene = self.host.perception = self.host.actions = self.host.object_placement = True
        original = self.host.query
        self.scene = dict(sceneId='scene-recovery', gameUpdate=33,
                          map=dict(rows=['.' * 45] * 29, originX=100, originY=200),
                          geometry=dict(rows=['.' * 45] * 29, originX=100, originY=200),
                          coverage=dict(unknownCells=2, geometryComplete=False),
                          placementCandidates=dict(items=[dict(target=dict(x=9, y=20), solidNeighbor=dict(x=9, y=21))], truncated=False),
                          objectPlacementCandidates=dict(items=[dict(target=dict(x=x, y=20), footprint=dict(x=x, y=20, width=2, height=1), support=dict(x=x, y=21, width=2, height=1)) for x in (10, 12)], truncated=True))
        def query(operation, args):
            r = original('get_player_state' if operation == 'observe_scene' else operation, args)
            r.update(observedAtUnixMs=1793250000000, clock='server_utc_at_projection')
            if operation == 'observe_scene':
                r['data'] = copy.deepcopy(self.scene)
                if extra:
                    r['data'].update(extra)
            return r
        self.host.query = query
        self.bridge = ServerAiBridge(self.host.server_address[1], timeout=2)

    def reject_first(self, alter=None):
        def transform(a):
            if a.get('targetTile', {}).get('x') != 10:
                return
            a.update(status='failed', reason='object_footprint_occupied', reasonDetail='occupiedTile=(11,20); tileType=1',
                     evidence=dict(operation=a['operation'], mutationAttempted=False, effectStatus='not_started'))
            a['endEvidence'].update(inputsAcquired=False, inputsReleased=True, effectVerified=False)
            if alter:
                alter(a)
        self.host.action_transform = transform

    async def test_error_keeps_detached_observation_and_next_program_reuses_it(self):
        self.enable()
        runtime = LuaRuntime(self.bridge, evidence_root=self.directory.name)
        r = await runtime.execute("local s=api('observe_scene',{}); s.data.coverage.unknownCells=0; error('deliberate')", limits=Limits(seconds=8))
        self.assertEqual('script_error', r['status'], r)
        self.assertIsNone(r['value'])
        obs = r['lastObservation']
        self.assertEqual(('observe_scene', 1, 33, 'scene-recovery'), (obs['operation'], obs['callId'], obs['gameUpdate'], obs['sceneId']))
        self.assertEqual(2, obs['result']['data']['coverage']['unknownCells'])
        self.assertTrue(obs['result']['data']['objectPlacementCandidates']['truncated'])
        self.assertFalse(obs['payloadOmitted'])
        row = obs['result']['data']['geometry']['rows'][0]
        again = await runtime.execute('local row=' + json.dumps(row) + '; return string.sub(row,1,2)', limits=Limits(seconds=8))
        self.assertEqual('..', again['value'], again)
        self.assertEqual(0, again['calls'])
        self.assertEqual(1, sum(q['op'] == 'query' for q in self.host.sent))
        summarized = lua_agent.summarize(r, [])
        self.assertFalse(summarized['programCompleted'])
        self.assertEqual(obs, summarized['lastObservation'])

    async def test_observation_payload_limit_is_explicit_and_syntax_error_fabricates_no_read(self):
        self.enable(extra=dict(blob='x' * 70000))
        runtime = LuaRuntime(self.bridge, evidence_root=self.directory.name)
        r = await runtime.execute("api('observe_scene',{}); error('deliberate')", limits=Limits(seconds=8))
        self.assertEqual('script_error', r['status'], r)
        self.assertTrue(r['lastObservation']['payloadOmitted'])
        self.assertIsNone(r['lastObservation']['result'])
        self.assertEqual('last_observation_limit', r['lastObservation']['omissionReason'])
        syntax = await runtime.execute("api('observe_scene',{}); local =", limits=Limits(seconds=8))
        self.assertEqual('script_error', syntax['status'])
        self.assertEqual(0, syntax['calls'])
        self.assertIsNone(syntax['lastObservation'])

    async def test_safe_rejection_allows_different_object_candidate_in_same_budget(self):
        self.enable(); self.reject_first()
        source = """
        local s=api('observe_scene',{}).data
        local first=api('place_object',s.objectPlacementCandidates.items[1].target)
        assert(first.status=='failed' and first.continuationAllowed)
        local second=api('place_object',s.objectPlacementCandidates.items[2].target)
        return {first=first,second=second}
        """
        r = await LuaRuntime(self.bridge, evidence_root=self.directory.name).execute(source, limits=Limits(seconds=8, calls=3, actions=2))
        self.assertEqual('completed', r['status'], r)
        self.assertTrue(r['value']['second']['completed'])
        self.assertEqual((3, 2, 'released'), (r['calls'], r['actions'], r['actionState']))
        self.assertEqual([10, 12], [q['arguments']['x'] for q in self.host.sent if q['op'] == 'start'])

    async def test_safe_rejection_does_not_reset_action_budget(self):
        self.enable(); self.reject_first()
        r = await LuaRuntime(self.bridge, evidence_root=self.directory.name).execute("api('place_object',{x=10,y=20}); api('place_object',{x=12,y=20})", limits=Limits(seconds=8, actions=1))
        self.assertEqual('action_limit', r['status'], r)
        self.assertEqual(1, sum(q['op'] == 'start' for q in self.host.sent))

    async def test_unsupported_item_repeated_across_programs_then_changes_entry_point(self):
        self.enable()
        self.host.placement = True
        def refuse_use(action):
            if action['operation'] == 'use_item':
                action.update(status='failed', reason='unsupported_selected_item',
                              reasonDetail='Rope requires place_tile',
                              evidence=dict(operation='use_item', mutationAttempted=False, effectStatus='not_started'))
                action['endEvidence'].update(inputsAcquired=False, effectVerified=False)
        self.host.action_transform = refuse_use
        runtime = LuaRuntime(self.bridge, evidence_root=self.directory.name)
        first = await runtime.execute("return api('use_item',{})", limits=Limits(seconds=8))
        self.assertEqual('unsupported', first['recovery']['kind'], first)
        self.assertTrue(first['value']['continuationAllowed'])
        second = await runtime.execute("""
            local repeat_attempt=api('use_item',{})
            assert(repeat_attempt.reason=='unchanged_ineffective_action')
            assert(repeat_attempt.recovery.reason=='unsupported_selected_item')
            assert(repeat_attempt.recovery.unchangedAttemptSuppressed)
            return api('place_object',{x=12,y=20})
        """, limits=Limits(seconds=8))
        self.assertTrue(second['value']['completed'], second)
        self.assertTrue(second['recovery']['unchangedAttemptSuppressed'])
        event, = second['recoveryEvents']
        self.assertEqual(1, event['callId'])
        self.assertEqual(first['value']['recovery']['nextStep'], event['nextStep'])
        self.assertEqual(['use_item', 'place_object'], [q['operation'] for q in self.host.sent if q['op']=='start'])

    async def test_argument_rejection_can_be_corrected_in_same_worker(self):
        self.enable()
        runtime = LuaRuntime(self.bridge, evidence_root=self.directory.name)
        result = await runtime.execute("""
            local ok, rejection=pcall(function() return api('place_object',{x=12}) end)
            assert(not ok)
            return {rejection=rejection, action=api('place_object',{x=12,y=20})}
        """, limits=Limits(seconds=8))
        self.assertTrue(result['value']['action']['completed'], result)
        self.assertEqual('bad_argument', result['recovery']['kind'])
        self.assertIn('help', result['recovery']['nextStep'])
        event, = result['recoveryEvents']
        rejection = result['value']['rejection']
        self.assertEqual(event, rejection['recovery'])
        self.assertEqual(rejection['diagnostics'], event['diagnostics'])
        self.assertEqual(1, event['callId'])
        self.assertEqual('place_object', event['operation'])
        self.assertEqual(1, sum(q['op']=='start' for q in self.host.sent))

    async def test_final_execution_receipt_keeps_each_failure_after_success(self):
        self.enable()
        self.reject_first()
        reject_object = self.host.action_transform

        def refuse(action):
            if action['operation'] == 'use_item':
                action.update(status='failed', reason='unsupported_selected_item',
                              evidence=dict(operation='use_item', mutationAttempted=False, effectStatus='not_started'))
                action['endEvidence'].update(inputsAcquired=False, effectVerified=False)
            else:
                reject_object(action)

        self.host.action_transform = refuse
        receipts = []
        runtime = LuaRuntime(self.bridge, evidence_root=self.directory.name)
        result = await runtime.execute("""
            assert(api('use_item',{}).recovery.kind=='unsupported')
            assert(api('place_object',{x=10,y=20}).recovery.reason=='object_footprint_occupied')
            return api('place_object',{x=12,y=20})
        """, limits=Limits(seconds=8), on_result=lambda _, args, reply: receipts.append(reply))
        final = lua_agent.summarize(result, receipts)
        self.assertTrue(final['programCompleted'], final)
        self.assertTrue(final['lastAction']['completed'])
        self.assertNotIn('recovery', final['lastAction'])
        first, second = final['recoveryEvents']
        self.assertEqual([(1, 'use_item', 'unsupported_selected_item'),
                          (2, 'place_object', 'object_footprint_occupied')],
                         [(event['callId'], event['operation'], event['reason']) for event in (first, second)])
        for item, event in zip(final['actionResults']['items'], (first, second)):
            action = item['result']
            self.assertEqual(event, action['recovery'])
            self.assertEqual(event['reason'], action['reason'])
            self.assertFalse(action['evidence']['mutationAttempted'])
            self.assertTrue(action['endEvidence']['inputsReleased'])
            self.assertTrue(action['continuationAllowed'])
        self.assertEqual(second['nextStep'], final['recovery']['nextStep'])

    async def test_repeated_motion_without_displacement_allows_another_direction(self):
        self.enable()
        self.host.direct_input = True
        self.bridge.close()
        self.bridge = ServerAiBridge(self.host.server_address[1], timeout=2)
        def observe_motion(action):
            if action['operation'] == 'move_input':
                action['evidence']['noProgress'] = action['requestedInputs']['horizontal'] == 'right'
        self.host.action_transform = observe_motion
        runtime = LuaRuntime(self.bridge, evidence_root=self.directory.name)
        result = await runtime.execute("""
            local args={horizontal='right',vertical='none',jumpHoldUpdates=0,maxUpdates=20}
            assert(api('move_input',args).completed)
            assert(api('move_input',args).completed)
            assert(api('move_input',args).reason=='unchanged_ineffective_action')
            args.horizontal='left'
            return api('move_input',args)
        """, limits=Limits(seconds=8))
        self.assertTrue(result['value']['completed'], result)
        self.assertEqual('no_progress', result['recovery']['kind'])
        self.assertEqual(['right', 'right', 'left'],
                         [q['arguments']['horizontal'] for q in self.host.sent if q['op']=='start'])

    async def test_uncertain_or_started_failures_keep_cross_operation_barrier(self):
        cases = {
            'unknown': lambda a: a.update(status='unknown'),
            'timeout': lambda a: a.update(status='timeout'),
            'cancelled': lambda a: a.update(status='cancelled'),
            'started': lambda a: a['evidence'].update(mutationAttempted=True),
            'input_acquired': lambda a: a['endEvidence'].update(inputsAcquired=True),
            'consumption_unknown': lambda a: a['evidence'].update(uncertainNativeEffect=True),
            'null_uncertainty': lambda a: a['evidence'].update(uncertainNativeEffect=None),
            'consumed': lambda a: a['evidence'].update(consumedCount=1),
            'missing_proof': lambda a: a['evidence'].pop('mutationAttempted'),
            'release_unknown': lambda a: a['endEvidence'].update(inputsReleased=False),
            'owner_changed': lambda a: a['endEvidence'].update(actorGeneration=2),
        }
        for name, alter in cases.items():
            with self.subTest(case=name):
                self.enable(); self.reject_first(alter); self.host.polls = 0
                r = await LuaRuntime(self.bridge, evidence_root=self.directory.name).execute("local r=api('place_object',{x=10,y=20}); if r.continuationAllowed then error('unsafe continuation') end; return api('use_item',{})", limits=Limits(seconds=8))
                self.assertEqual(1, sum(q['op'] == 'start' for q in self.host.sent), r)
                self.assertNotIn('unsafe continuation', r.get('error', ''))
                if r['actionState'] != 'unknown':
                    self.assertIsNone(r['value'], r)
                    self.assertNotEqual('completed',r['status'])
                    self.assertEqual('known',r['lastAction']['endEvidenceStatus'])
                self.host.sent.clear()

    async def test_string_allowlist_parses_full_scene_and_metatable_stays_removed(self):
        self.enable()
        source = """
        assert(string.dump==nil and string.rep==nil and string.format==nil)
        assert(load==nil and debug==nil and os==nil and io==nil and require==nil)
        local s=api('observe_scene',{}).data
        assert(s.geometry.originX==s.map.originX and s.geometry.originY==s.map.originY)
        local n=0
        for y=1,#s.geometry.rows do
          local row=s.geometry.rows[y]; assert(string.len(row)==45)
          for x=1,string.len(row) do if string.sub(row,x,x)=='.' then n=n+1 end end
        end
        return n
        """
        runtime = LuaRuntime(self.bridge, evidence_root=self.directory.name)
        r = await runtime.execute(source, limits=Limits(seconds=8, calls=1, actions=0))
        self.assertEqual(1305, r['value'], r)
        self.assertEqual(['observe_scene'], [q['operation'] for q in self.host.sent if q['op'] == 'query'])
        bad = await runtime.execute("local row='abc'; return row:sub(1,1)", limits=Limits(seconds=8))
        self.assertEqual('script_error', bad['status'])
        instructions, _ = lua_agent.server_contract(self.bridge)
        for fragment in ('不能传给place_object', 'objectPlacementCandidates.items', 'string.sub(row', 'continuationAllowed=true', 'lastObservation'):
            self.assertIn(fragment, instructions)


if __name__ == '__main__':
    unittest.main()
