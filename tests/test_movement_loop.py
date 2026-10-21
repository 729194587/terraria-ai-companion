"""Offline decision-loop tests: mock model/positions, no Terraria physics or real model."""
import asyncio
import copy
import json
from pathlib import Path
import sys
import unittest
from unittest.mock import AsyncMock, patch

sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'python'))
from agent import Agent
from control import BridgeError
from llm import LLMError
from movement_loop import MovementLoop
from test_agent import Game, Model, event, tool, final
from test_exploration import recorded
from test_movement import MovementBridge


class Scene(Game):
    """Synthetic moving positions over the recorded observation shape; not live movement evidence."""
    def __init__(self):
        super().__init__()
        self.x=0
        self.generation=0
        self.reads=0
        self.dead=False
        self.stale=False
        self.world='world'
        self.repeat=False
        self.fail_read=False
    def get_observation(self):
        if self.fail_read:raise BridgeError('offline fixture read error')
        if not self.repeat:self.reads+=1
        source=recorded()
        source.update(gameUpdate=100+self.reads,observationId=self.world+':'+str(self.reads),worldSessionId=self.world)
        for entity in source['entities']['items']:
            entity['sampleGameUpdate']=source['gameUpdate']
            entity['instanceKey']=source['observationId']+':'+entity['kind']+':'+str(entity['slot'])
        source['player'].update(positionWorldPixels={'x':self.x,'y':0},dead=self.dead,life=0 if self.dead else 100)
        source['player']['boundsWorldPixels'].update(x=self.x,y=0)
        if self.stale:
            source['status']='stale';source['camera']['status']='stale'
        return {'observation':source,'controlGeneration':self.generation}


def movement(direction='right',duration=500,target=None):
    args={'direction':direction,'duration_ms':duration}
    if target is not None:args['target_position']={'x':target,'y':0}
    return tool('move',json.dumps(args))


def result_from(request):
    return json.loads(next(m['content'] for m in reversed(request) if m['role']=='tool'))


def end_receipt(world, tick):
    return {'serverConfirmed': True, 'endEvidenceStatus': 'known', 'endEvidence': {'worldSessionId': world, 'gameUpdate': tick,
            'phase': 'PostUpdateRunSpeeds_before_bridge_input'}}


async def run_scene(answers,game=None,delta=20,result=None,**budgets):
    game=game or Scene();agent=Agent(game,Model(*answers),**budgets)
    async def act(bridge,direction,duration_ms,chat_id,expected_generation):
        if result is None:
            game.x+=delta if direction=='right' else -delta
            game.generation+=2
            return {'valid':True,'status':'completed','completed':True,'cancelled':False,'controlGeneration':game.generation,
                    **end_receipt(game.world, 100+game.reads)}
        return copy.deepcopy(result)
    with (patch('agent.move',side_effect=act) as action,patch('cost_metrics.emit'),
          patch('agent.stop_movement',new_callable=AsyncMock,return_value={'valid':True,'status':'idle'})):
        agent.receive(event(1));await agent.task
    return agent,game,action


class MovementLoopTests(unittest.IsolatedAsyncioTestCase):
    async def test_stop_preserves_arrival_no_progress_and_cancellation(self):
        cases=[('arrived',100,None,[tool('get_exploration_observation'),movement(target=100),
                                   tool('get_exploration_observation')]),
               ('no_progress',0,None,[tool('get_exploration_observation'),movement(target=100),
                                     tool('get_exploration_observation'),movement(),tool('get_exploration_observation')]),
               ('cancelled',0,{'valid':True,'status':'cancelled','completed':False,'reason':'manual_override'},
                [tool('get_exploration_observation'),movement(target=100)])]
        for expected,delta,result,sequence in cases:
            with self.subTest(status=expected):
                a,g,act=await run_scene(sequence+[tool('stop_movement'),final()],delta=delta,result=result)
                before_stop=result_from(a.llm.requests[-2])['movementLoop']
                after_stop=result_from(a.llm.requests[-1])['movementLoop']
                self.assertEqual(before_stop['status'],expected)
                self.assertEqual(after_stop['status'],expected)
                self.assertEqual(after_stop['reason'],before_stop['reason'])
                self.assertEqual(a.last_movement_result['status'],expected)
                if expected == 'arrived':
                    self.assertEqual(before_stop['goal'], after_stop['goal'])
                    self.assertEqual(after_stop['goal']['status'], 'achieved')
                    self.assertEqual(after_stop['execution']['status'], 'completed')
                if expected=='arrived':self.assertIn('确认到达',g.sent[-1][1]['text'])

    async def test_state_query_requires_observation_without_invalidating_cached_evidence(self):
        a,g,act=await run_scene([tool('get_exploration_observation'),tool('get_state'),movement(),
                                tool('get_exploration_observation'),movement(),tool('get_exploration_observation'),final()])
        after_state=result_from(a.llm.requests[2])['movementLoop']
        self.assertTrue(after_state['needsObservation'])
        self.assertEqual(after_state['observationId'],'world:1')
        self.assertEqual(after_state['terrainObservationStatus'],'not_refreshed')
        self.assertEqual(result_from(a.llm.requests[3])['reason'],'terrain_observation_not_refreshed')
        self.assertEqual(act.await_count,1)
        self.assertEqual(a.last_movement_result['status'],'stopped')

    async def test_state_query_after_move_cannot_replace_post_action_observation(self):
        a,g,act=await run_scene([tool('get_exploration_observation'),movement(),tool('get_state'),movement(),
                                tool('get_exploration_observation'),movement(),tool('get_exploration_observation'),final()])
        after_state=result_from(a.llm.requests[3])['movementLoop']
        self.assertTrue(after_state['needsObservation'])
        self.assertEqual(after_state['terrainObservationStatus'],'not_refreshed')
        self.assertEqual(act.await_count,2)
        self.assertEqual(a.last_movement_result['status'],'budget_exhausted')

    async def test_failed_state_query_also_requires_new_observation(self):
        class FailedState(Scene):
            def get_state(self):raise BridgeError('fixture state-only query failure')
        a,g,act=await run_scene([tool('get_exploration_observation'),tool('get_state'),movement(),final()],game=FailedState())
        self.assertEqual(act.await_count,0)
        query=result_from(a.llm.requests[2])['movementLoop']
        self.assertEqual(query['terrainObservationStatus'],'not_refreshed')
        self.assertEqual(query['observationId'],'world:1')
        self.assertEqual(result_from(a.llm.requests[3])['reason'],'terrain_observation_not_refreshed')
        self.assertEqual(a.last_movement_result['status'],'stopped')

    async def test_model_decides_again_using_latest_observation_and_verified_goal(self):
        game=Scene();decisions=[]
        class Decisions(Model):
            async def complete(self,messages,tools):
                self.requests.append(copy.deepcopy(messages))
                if len(self.requests)==1:return tool('get_exploration_observation')
                last=result_from(messages)
                if 'exploration' not in last:return tool('get_exploration_observation')
                x=last['exploration']['player']['positionWorldPixels']['x']
                if x==100:return final('arrived')
                direction='right' if x<100 else 'left'
                duration=500 if x==0 else 200
                decisions.append((x,direction,duration,last['exploration']['observationId']))
                return movement(direction,duration,100 if x==0 else None)
        model=Decisions();agent=Agent(game,model)
        async def act(bridge,direction,duration_ms,chat_id,expected_generation):
            game.x=140 if len(decisions)==1 else 100
            game.generation+=2
            return {'valid':True,'status':'completed','completed':True,'cancelled':False,'controlGeneration':game.generation,
                    **end_receipt(game.world, 100+game.reads)}
        with patch('agent.move',side_effect=act) as action,patch('cost_metrics.emit'):
            agent.receive(event(1));await agent.task
        self.assertEqual(decisions,[(0,'right',500,'world:1'),(140,'left',200,'world:2')])
        self.assertEqual(action.await_count,2)
        self.assertEqual(game.reads,3)
        self.assertEqual(len(model.requests),6)
        self.assertEqual(agent.last_movement_result['status'],'arrived')
        self.assertEqual(agent.last_movement_result['reservedMovementMs'],700)
        self.assertIn('确认到达',game.sent[-1][1]['text'])

    async def test_batch_of_observe_and_moves_is_entirely_rejected(self):
        batch=tool('get_exploration_observation')
        second=movement(target=100)['tool_calls'][0];second['id']='second'
        batch['tool_calls'].append(second)
        a,g,act=await run_scene([batch,tool('get_exploration_observation'),movement(target=100),
                                 tool('get_exploration_observation'),final()],delta=100)
        self.assertEqual(act.await_count,1)
        self.assertEqual(g.reads,2)
        rejected=[json.loads(m['content']) for m in a.llm.requests[1] if m['role']=='tool']
        self.assertEqual(len(rejected),2)
        self.assertTrue(all('entire batch not executed' in r['error'] for r in rejected))
        self.assertEqual(a.last_movement_result['status'],'arrived')

    async def test_move_requires_observation_and_post_move_reobservation(self):
        a,g,act=await run_scene([movement(),tool('get_exploration_observation'),movement(),movement(),
                               tool('get_exploration_observation'),final('I arrived')])
        self.assertEqual(act.await_count,1)
        self.assertEqual(result_from(a.llm.requests[1])['reason'],'fresh_exploration_or_full_observation_required')
        self.assertEqual(result_from(a.llm.requests[4])['reason'],'fresh_exploration_or_full_observation_required')
        self.assertEqual(a.last_movement_result['status'],'stopped')
        self.assertIn('未确认抵达',g.sent[-1][1]['text'])

    async def test_tool_and_model_budgets_report_failure_not_arrival(self):
        sequence=[tool('get_exploration_observation'),movement(target=100),tool('get_exploration_observation'),movement()]
        for budgets in ({'max_tool_calls':3},{'max_model_calls':3}):
            a,g,act=await run_scene(sequence,**budgets)
            self.assertEqual(act.await_count,1)
            self.assertEqual(a.last_movement_result['status'],'budget_exhausted')
            self.assertIn('未确认抵达',g.sent[-1][1]['text'])
        # The last allowed model turn cannot execute a tool with no opportunity to return its result.
        self.assertEqual(g.reads,1)

    async def test_cumulative_time_cap_and_no_refunds(self):
        sequence=[tool('get_exploration_observation')]
        for i in range(4):sequence += [movement(duration=2000,target=999 if i==0 else None),tool('get_exploration_observation')]
        sequence += [final()]
        a,g,act=await run_scene(sequence,max_tool_calls=12,max_model_calls=13)
        self.assertEqual(act.await_count,3)
        self.assertEqual(a.last_movement_result['reservedMovementMs'],6000)
        self.assertEqual(a.last_movement_result['status'],'budget_exhausted')

    async def test_two_no_progress_actions_stop_even_with_more_budget(self):
        sequence=[tool('get_exploration_observation'),movement(target=100),tool('get_exploration_observation'),
                  movement(),tool('get_exploration_observation'),movement(),final()]
        a,g,act=await run_scene(sequence,delta=0)
        self.assertEqual(act.await_count,2)
        self.assertEqual(a.last_movement_result['status'],'no_progress')
        self.assertIn('连续两次',g.sent[-1][1]['text'])

    async def test_unknown_cancelled_and_rejected_never_retry(self):
        for status in ('unknown','cancelled','rejected'):
            result={'valid':status=='cancelled','status':status,'completed':False,'reason':'fixture'}
            sequence=[tool('get_exploration_observation'),movement(target=100),tool('get_exploration_observation'),
                      movement(),final('arrived')]
            a,g,act=await run_scene(sequence,result=result)
            self.assertEqual(act.await_count,1)
            self.assertEqual(a.last_movement_result['status'], 'failed' if status=='rejected' else status)
            self.assertIn('未确认抵达',g.sent[-1][1]['text'])

    async def test_death_does_not_resume_in_same_chat_after_revive(self):
        game=Scene()
        class DeathModel(Model):
            async def complete(self,messages,tools):
                result=await super().complete(messages,tools)
                if len(self.requests)==3:game.dead=True
                if len(self.requests)==4:game.dead=False
                return result
        answers=[tool('get_exploration_observation'),movement(target=100),tool('get_exploration_observation'),
                 tool('get_exploration_observation'),movement(),final()]
        agent=Agent(game,DeathModel(*answers))
        async def completed(*args,**kwargs):
            game.generation+=2
            return {'valid':True,'status':'completed','completed':True,'controlGeneration':game.generation,
                    **end_receipt(game.world, 100+game.reads)}
        with (patch('agent.move',side_effect=completed) as act,
              patch('cost_metrics.emit')):
            agent.receive(event(1));await agent.task
        self.assertEqual(act.await_count,1)
        self.assertEqual(agent.last_movement_result['status'],'cancelled')
        # A new user task may issue a genuinely new action after revival.
        agent.llm=Model(tool('get_exploration_observation'),movement(),tool('get_exploration_observation'),final())
        with (patch('agent.move',side_effect=completed) as act,
              patch('cost_metrics.emit')):
            agent.receive(event(2));await agent.task
        self.assertEqual(act.await_count,1)

    async def test_unchanged_tick_stale_camera_and_world_change_block_next_action(self):
        for mode in ('repeat','stale','world','fail_read'):
            game=Scene()
            async def act(bridge,direction,duration_ms,chat_id,expected_generation):
                if mode=='world':game.world='other-world'
                else:setattr(game,mode,True)
                game.generation+=2
                return {'valid':True,'status':'completed','completed':True,'controlGeneration':game.generation,
                        **end_receipt(game.world, 100+game.reads)}
            sequence=[tool('get_exploration_observation'),movement(),tool('get_exploration_observation'),movement(),final()]
            agent=Agent(game,Model(*sequence))
            with patch('agent.move',side_effect=act) as action,patch('cost_metrics.emit'):
                agent.receive(event(1));await agent.task
            self.assertEqual(action.await_count,1,mode)
            self.assertIn(agent.last_movement_result['status'],('failed','cancelled'))

    async def test_completed_without_post_observation_or_goal_cannot_claim_arrival(self):
        a,g,act=await run_scene([tool('get_exploration_observation'),movement(target=100),final('arrived')])
        self.assertEqual(a.last_movement_result['status'],'failed')
        self.assertEqual(a.last_movement_result['goal']['status'], 'unknown')
        self.assertEqual(a.last_movement_result['execution']['status'], 'completed')
        self.assertIn('未确认抵达',g.sent[-1][1]['text'])

    async def test_explicit_stop_terminates_loop(self):
        a,g,act=await run_scene([tool('get_exploration_observation'),movement(),tool('stop_movement'),movement(),final()])
        # Mock stop without needing a native game session.
        self.assertEqual(act.await_count,1)
        self.assertEqual(a.last_movement_result['status'],'stopped')

    async def test_llm_failure_after_move_reports_unconfirmed_failure(self):
        a,g,act=await run_scene([tool('get_exploration_observation'),movement(),LLMError('fixture failure')])
        self.assertEqual(a.last_movement_result['status'],'failed')
        self.assertIn('未确认抵达',g.sent[-1][1]['text'])

    async def test_control_revocation_between_model_decisions_cannot_resume(self):
        # Exercise the real Python move implementation, not a mocked action.
        for timing in ('before_first_move','after_completed_move'):
            game=MovementBridge()
            sequence=([tool('get_observation'),movement(),final()] if timing=='before_first_move' else
                      [tool('get_observation'),movement(),tool('get_observation'),movement(),final()])
            revoke_at=2 if timing=='before_first_move' else 4
            class Revoked(Model):
                async def complete(self,messages,tools):
                    answer=await super().complete(messages,tools)
                    if len(self.requests)==revoke_at:
                        game.generation+=2  # Death+revival or manual takeover+release while model was deciding.
                    return answer
            agent=Agent(game,Revoked(*sequence))
            with patch('cost_metrics.emit'):
                agent.receive(event(1));await agent.task
            started=[p for cmd,p in game.calls if cmd=='move_for']
            self.assertEqual(len(started),0 if timing=='before_first_move' else 1)
            self.assertEqual(agent.last_movement_result['status'],'cancelled')
            self.assertIn('未确认抵达',game.sent[-1][1]['text'])

    async def test_model_final_cannot_override_observed_missed_target(self):
        a,g,act=await run_scene([tool('get_exploration_observation'),movement(target=100),
                                tool('get_exploration_observation'),final('I reached the target!')],delta=20)
        self.assertEqual(a.last_movement_result['status'],'stopped')
        self.assertIn('未确认抵达',g.sent[-1][1]['text'])

    async def test_last_allowed_observation_can_confirm_arrival(self):
        sequence=[tool('get_exploration_observation')]
        for i in range(3):sequence += [movement(duration=2000,target=60 if i==0 else None),tool('get_exploration_observation')]
        a,g,act=await run_scene(sequence+[final()],delta=20)
        self.assertEqual(act.await_count,3)
        self.assertEqual(a.last_movement_result['status'],'arrived')
        self.assertEqual(a.last_movement_result['toolCalls'],7)
        self.assertEqual(a.last_movement_result['modelCalls'],8)


class GuardTests(unittest.TestCase):
    def test_later_observation_still_must_follow_last_action_end(self):
        guard = self.started()
        guard.moved({'valid': True, 'status': 'completed', 'completed': True,
                     'controlGeneration': 2, **end_receipt('world', 10)})
        fresh = self.observation(x=20, tick=11)
        fresh['controlGeneration'] = 2
        guard.observe(fresh)
        self.assertFalse(guard.pending_observation)
        self.assertEqual(guard.snapshot()['goal']['status'], 'not_achieved')
        guard.observe({'valid': True, 'player': {}})
        old = self.observation(x=100, tick=10)
        old['controlGeneration'] = 2
        guard.observe(old)
        self.assertEqual(guard.terminal, 'failed')
        self.assertEqual(guard.snapshot()['goal']['status'], 'not_achieved')
        self.assertEqual(guard.snapshot()['goal']['evidence']['gameUpdate'], 11)

    def test_observation_must_be_strictly_after_release_update(self):
        for observation_tick, expected in ((9, 'failed'), (10, 'failed'), (11, 'arrived')):
            guard = self.started()
            guard.moved({'valid': True, 'status': 'completed', 'completed': True,
                         'controlGeneration': 2, **end_receipt('world', 10)})
            observed = self.observation(x=100, tick=observation_tick)
            observed['controlGeneration'] = 2
            guard.observe(observed)
            self.assertEqual(guard.terminal, expected)
            self.assertEqual(guard.snapshot()['observation']['endGameUpdate'], 10)
            if expected == 'failed':
                self.assertEqual(guard.reason, 'post_action_observation_not_after_end')
                self.assertEqual(guard.snapshot()['goal']['status'], 'unknown')
                self.assertTrue(guard.snapshot()['needsObservation'])
            else:
                self.assertEqual(guard.snapshot()['goal']['evidence']['gameUpdate'], 11)

    def test_legacy_missing_or_invalid_end_receipt_never_verifies_arrival(self):
        cases = [{}, {'endGameUpdate': 1}, end_receipt('world', True), end_receipt('world', -1),
                 {**end_receipt('world', 1), 'endEvidenceStatus': 'unknown'},
                 end_receipt('world', 0),
                 {'serverConfirmed': True, 'endEvidence': {'worldSessionId': 'world', 'gameUpdate': 1,
                                                          'phase': 'request_received'}},
                 {**end_receipt('world', 1), 'serverConfirmed': False}]
        for receipt in cases:
            guard = self.started()
            guard.moved({'valid': True, 'status': 'completed', 'completed': True,
                         'controlGeneration': 2, **receipt})
            guard.observe(self.observation(x=100, tick=2))
            self.assertEqual(guard.terminal, 'unknown', receipt)
            self.assertEqual(guard.snapshot()['goal']['status'], 'unknown')
            self.assertIsNone(guard.snapshot()['observation']['endGameUpdate'])
            self.assertIsNotNone(guard.prepare({'direction': 'right', 'duration_ms': 100}))

    def test_end_world_mismatch_and_stale_observation_reject_verification(self):
        for world in ('world', 'other-world'):
            guard = self.started()
            guard.moved({'valid': True, 'status': 'completed', 'completed': True,
                         'controlGeneration': 2, **end_receipt(world, 1)})
            observation = self.observation(x=100, tick=2)
            observation['exploration']['status'] = 'stale'
            observation['exploration']['camera']['status'] = 'stale'
            guard.observe(observation)
            self.assertEqual(guard.terminal, 'failed' if world == 'world' else 'cancelled')
            self.assertEqual(guard.snapshot()['goal']['status'], 'unknown')

    def started(self, target=None):
        guard = MovementLoop()
        guard.observe(self.observation())
        args = {'direction': 'right', 'duration_ms': 500,
                'target_position': target or {'x': 100, 'y': 0}}
        self.assertIsNone(guard.prepare(args))
        return guard

    def completed(self, guard, x):
        guard.moved({'valid': True, 'status': 'completed', 'completed': True, 'serverConfirmed': True,
                     **end_receipt('world', 1),
                     'controlGeneration': 2, 'beforePositionWorldPixels': {'x': 0, 'y': 0},
                     'afterPositionWorldPixels': {'x': x, 'y': 0}})

    def test_execution_displacement_and_xy_goal_have_separate_evidence(self):
        for x, status in ((100, 'achieved'), (0, 'not_achieved')):
            guard = self.started()
            self.completed(guard, x)
            feedback = guard.snapshot()
            self.assertEqual(feedback['execution']['status'], 'completed')
            self.assertEqual(feedback['effects']['displacementWorldPixels'], {'x': x, 'y': 0})
            self.assertEqual(feedback['goal']['status'], 'unknown')
            self.assertIsNone(feedback['goal']['evidence'])
            self.assertTrue(feedback['observation']['required'])
            guard.observe(self.observation(x=x, tick=2))
            result = guard.snapshot()
            self.assertEqual(result['goal']['status'], status)
            self.assertEqual(result['goal']['kind'], 'position_xy')
            self.assertEqual(result['goal']['evidence']['observationId'], 'world:2')
            self.assertEqual(result['effects']['source'], 'fresh_terrain_observation')
            self.assertEqual(feedback['goal']['status'], 'unknown')  # Earlier results remain immutable.

    def test_crossing_x_is_not_xy_arrival_and_navigation_status_is_not_imported(self):
        guard = self.started({'x': 100, 'y': 50})
        self.completed(guard, 100)
        observed = self.observation(x=100, tick=2)
        observed['navigation'] = {'status': 'arrived', 'targetX': 100}
        guard.observe(observed)
        self.assertEqual(guard.snapshot()['goal']['status'], 'not_achieved')
        self.assertIsNone(guard.terminal)
        with self.assertRaises(ValueError):
            guard.prepare({'direction': 'right', 'duration_ms': 100, 'targetX': 100})

    def test_stale_same_tick_and_state_only_do_not_verify_goal_or_erase_displacement(self):
        for kind in ('stale', 'same_tick', 'state_only'):
            guard = self.started()
            self.completed(guard, 100)
            observed = self.observation(x=100, tick=2)
            if kind == 'stale':
                observed['exploration']['camera']['status'] = 'stale'
            elif kind == 'same_tick':
                observed['exploration']['gameUpdate'] = 1
            else:
                observed = {'valid': True, 'player': {'position': {'x': 100, 'y': 0}}}
            guard.observe(observed)
            result = guard.snapshot()
            self.assertEqual(result['goal']['status'], 'unknown', kind)
            self.assertIsNone(result['goal']['evidence'])
            self.assertEqual(result['effects']['displacementWorldPixels'], {'x': 100, 'y': 0})
            self.assertTrue(result['observation']['required'])

    def test_cancellation_and_unknown_keep_effects_without_goal_success_or_resume(self):
        for status in ('cancelled', 'unknown'):
            guard = self.started()
            guard.moved({'valid': status == 'cancelled', 'status': status,
                         'serverConfirmed': status == 'cancelled', 'reason': 'manual_override',
                         'beforePositionWorldPixels': {'x': 0, 'y': 0},
                         'afterPositionWorldPixels': {'x': 20, 'y': 0} if status == 'cancelled' else None})
            guard.observe(self.observation(x=100, tick=2))
            result = guard.snapshot()
            self.assertEqual(result['status'], status)
            self.assertEqual(result['goal']['status'], 'unknown')
            self.assertEqual(result['effects']['displacementWorldPixels'],
                             {'x': 20, 'y': 0} if status == 'cancelled' else None)
            self.assertIsNotNone(guard.prepare({'direction': 'right', 'duration_ms': 100}))

    def test_world_generation_and_death_changes_never_verify_old_goal(self):
        for kind in ('world', 'generation', 'death'):
            guard = self.started()
            self.completed(guard, 20)
            changed = self.observation(x=100, tick=2)
            if kind == 'world': changed['exploration']['worldSessionId'] = 'other'
            elif kind == 'generation': changed['controlGeneration'] = 4
            else: changed['exploration']['player']['dead'] = True
            guard.observe(changed)
            guard.observe(self.observation(x=100, tick=2))
            result = guard.snapshot()
            self.assertEqual(result['status'], 'cancelled')
            self.assertEqual(result['goal']['status'], 'unknown')
            self.assertEqual(result['effects']['displacementWorldPixels'], {'x': 20, 'y': 0})
            self.assertIsNotNone(guard.prepare({'direction': 'right', 'duration_ms': 100}))

    def observation(self,x=0,tick=1):
        return {'valid':True,'controlGeneration':2*(tick-1),'exploration':{'worldSessionId':'world','observationId':'world:'+str(tick),'gameUpdate':tick,
                'status':'ok','camera':{'status':'recent_render'},'player':{'positionWorldPixels':{'x':x,'y':0},'life':100},
                'terrain':{'tileSize':16,'bounds':{'x':0,'y':0,'width':1,'height':1},
                           'encoding':'semantic_rectangles_v1','regions':[[0,0,1,1,{'knowledge':'read'}]]}}}

    def test_expired_observation_and_invalid_target_never_reserve_time(self):
        now=[0];guard=MovementLoop(clock=lambda:now[0]);guard.observe(self.observation());now[0]=31
        self.assertEqual(guard.prepare({'direction':'right','duration_ms':500})['reason'],'observation_older_than_30_seconds')
        self.assertEqual(guard.reserved_ms,0)
        for target,tolerance in (({'x':True,'y':0},16),({'x':float('nan'),'y':0},16),({'x':0,'y':0},999)):
            guard.observe(self.observation())
            with self.assertRaises(ValueError):guard.prepare({'direction':'right','duration_ms':500,
                'target_position':target,'arrival_tolerance':tolerance})
        self.assertEqual(guard.reserved_ms,0)

    def test_target_is_immutable_and_arrival_uses_both_axes(self):
        guard=MovementLoop();guard.observe(self.observation())
        args={'direction':'right','duration_ms':500,'target_position':{'x':100,'y':50}}
        self.assertIsNone(guard.prepare(args));guard.moved({'valid':True,'status':'completed','completed':True,'controlGeneration':2,
                                                         **end_receipt('world', 1)})
        guard.observe(self.observation(x=100,tick=2))
        self.assertFalse(guard.at_target())
        self.assertIsNotNone(guard.prepare({**args,'target_position':{'x':100,'y':0}}))
        self.assertEqual(guard.terminal,'failed')

    def test_already_at_goal_starts_no_action(self):
        guard=MovementLoop();guard.observe(self.observation())
        result=guard.prepare({'direction':'right','duration_ms':500,'target_position':{'x':0,'y':0}})
        self.assertEqual(result['status'],'not_started')
        self.assertEqual(guard.terminal,'arrived');self.assertEqual(guard.reserved_ms,0)

    def test_old_bridge_observation_is_readable_but_cannot_authorize_movement(self):
        guard=MovementLoop();result=self.observation();del result['controlGeneration']
        guard.observe(result)
        reply=guard.prepare({'direction':'right','duration_ms':500})
        self.assertEqual(reply['reason'],'control_generation_unavailable_update_bridge')
        self.assertEqual(guard.reserved_ms,0)

    def test_missing_terrain_is_invalid_and_cannot_authorize_move(self):
        for kind in ('exploration','observation'):
            guard=MovementLoop();result=self.observation()
            source=result.pop('exploration');result[kind]=source
            source['terrain']=None
            guard.observe(result)
            reply=guard.prepare({'direction':'right','duration_ms':500})
            self.assertIsNotNone(reply)
            self.assertEqual(reply['movementLoop']['terrainObservationStatus'],'invalid')
            self.assertEqual(guard.reserved_ms,0)

    def test_readonly_query_does_not_refresh_age_or_claim_world_changed(self):
        now=[0];guard=MovementLoop(clock=lambda:now[0]);guard.observe(self.observation())
        evidence=copy.deepcopy(guard.observed);now[0]=10
        guard.observe({'valid':True,'player':{'position':{'x':1,'y':0}}})
        self.assertEqual(guard.observed,evidence)
        self.assertEqual(guard.observed_at,0)
        self.assertIsNone(guard.terminal)
        self.assertEqual(guard.expected_generation,0)
        now[0]=31
        reply=guard.prepare({'direction':'right','duration_ms':500})
        self.assertEqual(reply['reason'],'observation_older_than_30_seconds')
        self.assertEqual(reply['movementLoop']['terrainObservationStatus'],'invalid')

    def test_query_does_not_hide_a_later_cross_world_observation(self):
        guard=MovementLoop();guard.observe(self.observation())
        guard.observe({'valid':True,'player':{}})
        other=self.observation();other['exploration']['worldSessionId']='other-world'
        guard.observe(other)
        self.assertEqual(guard.terminal,'cancelled')
        self.assertEqual(guard.reason,'world_changed')

    def test_refresh_in_same_update_before_any_action_is_not_a_state_change(self):
        guard=MovementLoop();result=self.observation();guard.observe(result)
        guard.observe({'valid':True,'player':{}})
        guard.observe(result)
        self.assertIsNone(guard.terminal)
        self.assertEqual(guard.snapshot()['terrainObservationStatus'],'authorized')
        self.assertIsNone(guard.prepare({'direction':'right','duration_ms':500}))

    def test_new_id_without_later_update_after_action_cannot_reauthorize(self):
        guard=MovementLoop();guard.observe(self.observation())
        guard.prepare({'direction':'right','duration_ms':500})
        guard.moved({'valid':True,'status':'completed','completed':True,'controlGeneration':2, **end_receipt('world', 1)})
        guard.observe({'valid':True,'player':{}})
        result=self.observation();result['controlGeneration']=2
        result['exploration']['observationId']='world:new-id-same-update'
        guard.observe(result)
        self.assertEqual(guard.terminal,'failed')
        self.assertEqual(guard.reason,'post_action_observation_not_newer')


if __name__=='__main__':unittest.main()
