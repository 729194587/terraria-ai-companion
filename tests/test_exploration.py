"""Recorded-game geometry fixture plus explicitly synthetic edge cases."""
import copy
import gzip
import json
from pathlib import Path
import sys
import unittest
from unittest.mock import AsyncMock, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'python'))
from exploration import CELL_DEFAULTS, project_observation, read_exploration_observation
from observation import row_indices
from agent import Agent, TOOLS
from test_agent import Game, Model, event, tool, final

FIXTURE = Path(__file__).parent / 'fixtures/exploration-real-observation.json.gz'


def recorded():
    return json.loads(gzip.decompress(FIXTURE.read_bytes()))


def synthetic(cells, width=None):
    """Synthetic tile patterns grafted onto the recorded metadata, not real terrain."""
    source = recorded()
    width = width or len(cells)
    source['terrain'].update(bounds=dict(x=-4, y=0, width=width, height=len(cells)//width),
        palette=cells, rows=[{'encoding':'indices','data':list(range(y,y+width))}
                            for y in range(0,len(cells),width)],
        unknownCount=sum(c['knowledge']!='read' for c in cells),
        uninterpretedCount=sum(c['collisionKnowledge']!='known' for c in cells))
    source['status']='partial'
    source['completeness'].update(completeWithinScope=False, reasons=['synthetic_fixture'])
    source['player']['boundsWorldPixels'].update(x=0,y=-42,width=20,height=42)
    source['player']['movement']['gravDir']=1
    return source


def cell(**extra):
    return dict(CELL_DEFAULTS, knowledge='read', collisionKnowledge='known', **{}) | extra


class ProjectionTests(unittest.TestCase):
    def assert_semantics(self, source, projected):
        terrain = source['terrain']; bounds=terrain['bounds']; actual={}
        for x,y,width,height,state in projected['terrain']['regions']:
            self.assertGreater(width,0); self.assertGreater(height,0)
            for ty in range(y,y+height):
                for tx in range(x,x+width):
                    self.assertNotIn((tx,ty),actual)
                    actual[tx,ty]=state
        self.assertEqual(len(actual),bounds['width']*bounds['height'])
        for y,row in enumerate(terrain['rows']):
            for x,index in enumerate(row_indices(row,len(terrain['palette']),bounds['width'])):
                raw=terrain['palette'][index]; state=actual[bounds['x']+x,bounds['y']+y]
                self.assertEqual(state['knowledge'],raw['knowledge'])
                if raw['knowledge']!='read':
                    self.assertNotIn('solid',state)
                    continue
                self.assertEqual(state['collisionKnowledge'],raw['collisionKnowledge'])
                restored={**projected['terrain']['readCellDefaults'],**state}
                for key in CELL_DEFAULTS:
                    self.assertEqual(restored[key],raw[key],key)
        return actual

    def test_recorded_semantics_coordinates_entities_and_exclusions(self):
        source=recorded(); before=copy.deepcopy(source); out=project_observation(source)
        self.assert_semantics(source,out)
        self.assertEqual(source,before)
        for key in ('camera','completeness','observationId','sampledAt','status'):
            self.assertEqual(out[key],source[key])
        for key in ('inventory','equipment','buffs','dyes','miscEquipment','miscDyes'):
            self.assertNotIn(key,out['player'])
        for key in ('palette','rows','types'):
            self.assertNotIn(key,out['terrain'])
        self.assertNotIn('environment',out)
        self.assertEqual(out['player']['boundsWorldPixels'],source['player']['boundsWorldPixels'])
        self.assertEqual(out['entities']['coverage'],source['entities']['coverage'])
        for old,new in zip(source['entities']['items'],out['entities']['items']):
            for key in ('instanceKey','kind','boundsWorldPixels','velocityPixelsPerTick','classificationBasis'):
                self.assertEqual(new[key],old[key])
            if old['kind'] in ('item','projectile'):
                self.assertNotIn('life',new)
        self.assertLess(len(json.dumps(out).encode()),len(json.dumps(source).encode())//2)

    def test_recorded_candidate_runs_preserve_every_anchor_and_uncertainty(self):
        source=recorded(); out=project_observation(source)['investigate']; restored=[]
        for group in out['groups']:
            count=0
            for x,y,n in group['anchorRuns']:
                restored.extend((group['category'],group['typeId'],group['interactionKnowledge'],x+i,y) for i in range(n))
                count+=n
            self.assertEqual(count,group['count'])
        expected=[(i['category'],i['typeId'],i['interactionKnowledge'],i['anchorTile']['x'],i['anchorTile']['y'])
                  for i in source['interactables']['items']]
        self.assertEqual(sorted(restored),sorted(expected))
        self.assertEqual(out['candidateCount'],329)
        self.assertEqual(len(out['groups']),9)
        self.assertTrue(all(g['interactionKnowledge']=='unknown' for g in out['groups']))

    def test_synthetic_shape_fluid_hazard_actuator_and_unknown_combinations(self):
        cells=[cell(hasTile=True,solid=True,halfBlock=True,wireMask=15,actuator=True),
               cell(hasTile=True,solidTop=True,slope=1),cell(hasTile=True,solid=True,slope=2,actuated=True),
               cell(hasTile=True,solid=True,slope=3,hot=True,touchDamage=20),
               cell(hasTile=True,solid=True,slope=4,bleeding=True,boulder=True,destroyOnTouch=True),
               cell(liquidType=1,liquidAmount=255),cell(liquidType=2,liquidAmount=64),
               cell(liquidType=3,liquidAmount=128),cell(),cell(knowledge='unloaded'),
               cell(knowledge='out_of_world'),cell(hasTile=True,solid=True,collisionKnowledge='unknown_mod_hooks',
                                                  tileType=9999,frameX=18,frameY=36)]
        source=synthetic(cells); out=project_observation(source)
        states=self.assert_semantics(source,out)
        self.assertEqual(states[7,0]['frame'],[18,36])
        self.assertEqual(out['terrain']['unknownCount'],2)
        self.assertEqual(out['terrain']['uninterpretedCount'],1)
        broken=copy.deepcopy(source); del broken['terrain']['palette'][0]['solid']
        with self.assertRaisesRegex(ValueError,'missing tile property'):
            project_observation(broken)

    def test_synthetic_support_drop_unknown_and_inverted_gravity(self):
        cells=[cell() for _ in range(25)]
        cells[2*5]=cell(hasTile=True,solid=True)
        cells[4*5+1]=cell(hasTile=True,solidTop=True)
        cells[2]=cell(knowledge='unloaded')
        cells[3]=cell(hasTile=True,solid=True,actuated=True)
        source=synthetic(cells,5);out=project_observation(source)
        columns=out['terrain']['supportColumns']
        self.assertEqual(columns[:3],[[-4,1,2,'potential_support_tile'],[-3,1,4,'potential_support_tile'],
                                     [-2,1,None,'unknown_before_support']])
        self.assertEqual(columns[3],[-1,2,None,'no_support_observed_to_viewport_bottom'])
        source['player']['movement']['gravDir']=-1
        self.assertEqual(project_observation(source)['terrain']['supportColumns'],
                         [[-4,5,None,'unsupported_or_unknown_gravity']])

    def test_underfoot_uses_recorded_feet_row_not_body_row(self):
        # Reported empty mine target (2183,233); actual adjacent ground row235.
        cells=[cell(hasTile=y>=235,solid=y>=235,tileType=0 if y>=235 else None)
               for y in range(230,238) for x in range(2180,2188)]
        source=synthetic(cells,8)
        source['terrain']['bounds'].update(x=2180,y=230)
        source['player']['boundsWorldPixels'].update(x=34934.523,y=3718,width=20,height=42)
        snapshot=copy.deepcopy(source)
        footing=project_observation(source)['terrain']['underfoot']
        self.assertEqual({'x':34944.523,'y':3760},footing['feetWorldPixels'])
        self.assertEqual({'x':2184,'y':235},footing['centerFootTile'])
        self.assertEqual([(2183,235),(2184,235)],[(c['x'],c['y']) for c in footing['candidates']])
        self.assertTrue(all(c['hasTile'] and c['tileType']==0 for c in footing['candidates']))
        self.assertEqual(source,snapshot)

    def test_underfoot_preserves_empty_unknown_outside_and_gravity_semantics(self):
        source=synthetic([cell(),cell(knowledge='unloaded'),cell(),cell()],4)
        source['terrain']['bounds'].update(x=0,y=0)
        footing=project_observation(source)['terrain']['underfoot']
        self.assertFalse(footing['candidates'][0]['hasTile'])
        self.assertNotIn('hasTile',footing['candidates'][1])
        self.assertEqual('unloaded',footing['candidates'][1]['stateOverrides']['knowledge'])
        source['player']['boundsWorldPixels']['y']=1000
        footing=project_observation(source)['terrain']['underfoot']
        self.assertEqual('outside_observation',footing['candidates'][0]['stateOverrides']['knowledge'])
        source['player']['movement']['gravDir']=-1
        footing=project_observation(source)['terrain']['underfoot']
        self.assertEqual('unsupported_or_unknown_gravity',footing['status'])
        self.assertEqual([],footing['candidates'])

    def test_underfoot_exact_boundary_does_not_include_next_column(self):
        source=synthetic([cell() for _ in range(8)],8)
        source['player']['boundsWorldPixels'].update(x=0,y=-42,width=16,height=42)
        self.assertEqual([0],[c['x'] for c in project_observation(source)['terrain']['underfoot']['candidates']])

    def test_synthetic_stale_dead_truncated_and_unavailable(self):
        source=recorded();source['status']='stale';source['camera']['status']='stale'
        source['player']['dead']=True
        source['completeness']['reasons']+=['local_player_dead_or_ghost','camera_stale','entity_limit']
        source['entities']['coverage'].update(matchedCount=47,omittedCount=1)
        out=project_observation(source)
        self.assertEqual(out['status'],'stale');self.assertTrue(out['player']['dead'])
        self.assertEqual(out['completeness'],source['completeness'])
        self.assertEqual(out['entities']['coverage']['omittedCount'],1)
        source.update(status='unavailable',player=None,terrain=None,entities=None,camera=None,environment=None,interactables=None)
        out=project_observation(source)
        self.assertIsNone(out['terrain']);self.assertIsNone(out['investigate']);self.assertIsNone(out['player'])


class ToolTests(unittest.IsolatedAsyncioTestCase):
    async def test_exploration_move_exploration_uses_existing_budget(self):
        class Bridge(Game):
            def __init__(self):super().__init__();self.reads=0
            def get_observation(self):
                self.reads+=1
                source=recorded()
                source['gameUpdate']+=self.reads
                source['observationId']='recorded-world:'+str(source['gameUpdate'])
                for entity in source['entities']['items']:
                    entity['sampleGameUpdate']=source['gameUpdate']
                source['player']['positionWorldPixels']['x']+=self.reads
                source['player']['boundsWorldPixels']['x']+=self.reads
                return {'observation':source,'controlGeneration':2*(self.reads-1)}
        bridge=Bridge();model=Model(tool('get_exploration_observation'),
            tool('move','{"direction":"right","duration_ms":500}'),
            tool('get_exploration_observation'),final('checked'))
        agent=Agent(bridge,model)
        with (patch('agent.move',new_callable=AsyncMock,return_value={'valid':True,'status':'completed','completed':True,'controlGeneration':2,
                    'serverConfirmed': True, 'endEvidenceStatus': 'known', 'endEvidence': {'worldSessionId': recorded()['worldSessionId'],
                    'gameUpdate': recorded()['gameUpdate']+1, 'phase': 'PostUpdateRunSpeeds_before_bridge_input'}}) as movement,
              patch('cost_metrics.emit')):
            agent.receive(event(1));await agent.task
        movement.assert_awaited_once_with(bridge,'right',500,chat_id='1',expected_generation=0)
        self.assertEqual(bridge.reads,2)
        self.assertEqual(len(model.requests),4)
        self.assertEqual(agent.last_movement_result['status'],'stopped')
        self.assertIn('未确认抵达',bridge.sent[-1][1]['text'])

    async def test_agent_dispatch_single_read_and_expired_history(self):
        class RecordedBridge(Game):
            def __init__(self):super().__init__();self.reads=0
            def get_observation(self):
                self.reads+=1
                return {'observation':recorded()}
        bridge=RecordedBridge();model=Model(tool('get_exploration_observation'),final('seen'),final('hi'))
        agent=Agent(bridge,model)
        with patch('cost_metrics.emit'):
            agent.receive(event(1));await agent.task
            result=json.loads(model.requests[1][-2]['content'])
            self.assertIn('exploration',result);self.assertTrue(result['valid'])
            self.assertEqual(bridge.reads,1)
            agent.receive(event(2));await agent.task
        old=json.loads(next(m['content'] for m in model.requests[2] if m['role']=='tool'))
        self.assertTrue(old['expired']);self.assertIn('get_exploration_observation',old['reason'])
        names={t['function']['name'] for t in TOOLS}
        self.assertTrue({'get_exploration_observation','get_observation','get_state','move'}<=names)

    async def test_invalid_parameters_do_not_read_and_unavailable_is_invalid(self):
        bridge=Game();model=Model(tool('get_exploration_observation','{"radius":10}'),final())
        agent=Agent(bridge,model)
        with patch('cost_metrics.emit'):
            agent.receive(event(1));await agent.task
        self.assertFalse(json.loads(model.requests[1][-2]['content'])['valid'])
        source=recorded();source.update(status='unavailable',player=None,terrain=None,entities=None,camera=None,
                                       environment=None,interactables=None)
        source['completeness']['completeWithinScope']=False
        class Bridge:
            def get_observation(self):return {'observation':source}
        self.assertFalse(read_exploration_observation(Bridge())['valid'])


if __name__=='__main__':unittest.main()
