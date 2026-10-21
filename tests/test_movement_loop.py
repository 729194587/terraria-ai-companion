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


async def run_scene(answers,game=None,delta=20,result=None,**budgets):
    game=game or Scene();agent=Agent(game,Model(*answers),**budgets)
    async def act(bridge,direction,duration_ms,chat_id,expected_generation):
        if result is None:
            game.x+=delta if direction=='right' else -delta
            game.generation+=2
            return {'valid':True,'status':'completed','completed':True,'cancelled':False,'controlGeneration':game.generation}
        return copy.deepcopy(result)
    with (patch('agent.move',side_effect=act) as action,patch('cost_metrics.emit'),
          patch('agent.stop_movement',new_callable=AsyncMock,return_value={'valid':True,'status':'idle'})):
        agent.receive(event(1));await agent.task
    return agent,game,action


class MovementLoopTests(unittest.IsolatedAsyncioTestCase):
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
            return {'valid':True,'status':'completed','completed':True,'cancelled':False,'controlGeneration':game.generation}
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
            return {'valid':True,'status':'completed','completed':True,'controlGeneration':game.generation}
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
                return {'valid':True,'status':'completed','completed':True,'controlGeneration':game.generation}
            sequence=[tool('get_exploration_observation'),movement(),tool('get_exploration_observation'),movement(),final()]
            agent=Agent(game,Model(*sequence))
            with patch('agent.move',side_effect=act) as action,patch('cost_metrics.emit'):
                agent.receive(event(1));await agent.task
            self.assertEqual(action.await_count,1,mode)
            self.assertIn(agent.last_movement_result['status'],('failed','cancelled'))

    async def test_completed_without_post_observation_or_goal_cannot_claim_arrival(self):
        a,g,act=await run_scene([tool('get_exploration_observation'),movement(target=100),final('arrived')])
        self.assertEqual(a.last_movement_result['status'],'failed')
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
    def observation(self,x=0,tick=1):
        return {'valid':True,'controlGeneration':2*(tick-1),'exploration':{'worldSessionId':'world','observationId':'world:'+str(tick),'gameUpdate':tick,
                'status':'ok','camera':{'status':'recent_render'},'player':{'positionWorldPixels':{'x':x,'y':0},'life':100}}}

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
        self.assertIsNone(guard.prepare(args));guard.moved({'valid':True,'status':'completed','completed':True,'controlGeneration':2})
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


if __name__=='__main__':unittest.main()
