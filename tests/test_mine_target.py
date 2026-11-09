import copy
import json
import re
import threading
import time
import unittest

from test_server_ai_backend import Host
from server_ai import ServerAiBridge, operations_for_capabilities
from gameplay_skills import bundled_skills
from lua_runtime import LuaRuntime, Limits
import atomic_operations as atomic


TARGET = dict(kind='tile', x=10, y=20, typeId=7, worldSessionId='world-1', actorGeneration=1,
              actorSlot=254, observedGameUpdate=10, observedAtUnixMs=1793520000000, targetToken='a'*64)


class MineTargetTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        self.host = Host()
        self.host.actions = self.host.scene = self.host.perception = True
        original = self.host.query
        def query(op, args):
            if op != 'observe_scene':
                return original(op, args)
            return dict(available=True, worldSessionId='world-1', actorGeneration=1, actorSlot=254,
                        gameUpdate=10, observedAtUnixMs=1793520000000, clock='server_utc_at_projection',
                        data=dict(objects=dict(items=[dict(kind='ore_component', target=copy.deepcopy(TARGET))])))
        self.host.query = query
        self.thread = threading.Thread(target=self.host.serve_forever, daemon=True)
        self.thread.start()
        self.bridge = ServerAiBridge(self.host.server_address[1])

    def tearDown(self):
        self.bridge.close()
        self.host.release.set()
        self.host.shutdown(); self.host.server_close(); self.thread.join()

    def enable(self, goal, alter=None):
        self.bridge.hello['capabilities'].append('server_ai_tasks_v1')
        self.bridge.supported_atomic_operations = operations_for_capabilities(self.bridge.hello['capabilities'])
        original = self.bridge._movement
        def movement(action, status='movement'):
            if action:
                action['targetTask'] = copy.deepcopy(goal)
                if action['status'] != 'running':
                    coords = goal.get('target', goal)
                    proof = dict(X=coords['x'], Y=coords['y'], TileType=7, destroyedByActor=True, targetAbsent=True, targetChanged=False)
                    task = dict(taskId=action['actionId'], taskName='mine_target', phase='terminal', status=action['status'],
                                worldSessionId='world-1', actorGeneration=1, actorSlot=254,
                                currentChild=None, progress=dict(completedChildren=2), reason='mine_target_verified',
                                lastResult=dict(status='completed', inputsReleased=True, effectVerified=True, evidence=proof))
                    action['task'] = task
                    action['evidence'] = dict(task=task)
                    if alter:
                        alter(action)
            return original(action, status)
        self.bridge._movement = movement

    def test_schema_and_capability_projection(self):
        for goal in ({'target': TARGET}, {'x': 10, 'y': 20}):
            self.assertEqual(goal, atomic.validate('mine_target', goal))
        for goal in ({}, {'x': 10}, {'target': TARGET, 'x': 10, 'y': 20}, {'target': {**TARGET, 'bad': 1}}, {'target': {**TARGET, 'x': True}}):
            with self.assertRaises(ValueError):
                atomic.validate('mine_target', goal)
        self.assertNotIn('mine_target', operations_for_capabilities(['server_ai_atomic_v1', 'server_ai_actions_v1']))
        self.assertNotIn('mine_target', operations_for_capabilities(['server_ai_atomic_v1', 'server_ai_tasks_v1']))
        self.assertIn('mine_target', operations_for_capabilities(['server_ai_atomic_v1', 'server_ai_actions_v1', 'server_ai_tasks_v1']))

    def test_server_mining_tool_descriptions_share_automatic_selection(self):
        tools = {t['function']['name']: t['function']
                 for t in atomic.server_tools({'mine_tile', 'mine_target'})}
        for definition in tools.values():
            description = definition['description']
            self.assertIn('主背包', description)
            self.assertIn('切换快捷栏', description)
            self.assertIn('原生', description)
            self.assertNotIn('80', description)
            self.assertNotIn('仅选快捷栏', description)
        opening = atomic.server_tools({'mine_target'}, mining_opening=True)[0]['function']
        self.assertIn('串行挖开阻挡格', opening['description'])
        self.assertNotIn('串行挖开阻挡格', tools['mine_target']['description'])
        self.assertEqual(tools['mine_target']['parameters'], opening['parameters'])
        params = tools['mine_tile']['parameters']
        self.assertEqual({'x', 'y'}, set(params['properties']))
        self.assertEqual(['x', 'y'], params['required'])
        self.assertFalse(params['additionalProperties'])
        self.assertTrue(all('绝对 Tile' in p['description'] for p in params['properties'].values()))

    async def test_old_server_refuses_before_rpc(self):
        with self.assertRaises(atomic.PreDispatchRejected):
            await atomic.dispatch(self.bridge, 'mine_target', {'target': TARGET})
        self.assertFalse(any(r['op'] == 'start' for r in self.host.sent))

    async def test_scene_target_passed_unchanged_through_real_worker(self):
        self.enable({'target': TARGET})
        result = await LuaRuntime(self.bridge).execute("local q=api('observe_scene',{layers={'objects'}});return api('mine_target',{target=q.data.objects.items[1].target})", limits=Limits(actions=1))
        self.assertEqual('completed', result['status'], result)
        self.assertTrue(result['value']['completed'])
        starts = [r for r in self.host.sent if r['op'] == 'start']
        self.assertEqual(1, len(starts))
        self.assertEqual({'target': TARGET}, starts[0]['arguments'])
        self.assertEqual('mine_target', result['value']['task']['taskName'])

    async def test_absolute_target_and_incomplete_evidence(self):
        goal = dict(x=10, y=20)
        self.enable(goal)
        out = await atomic.dispatch(self.bridge, 'mine_target', goal)
        self.assertTrue(out['completed'], out)
        self.bridge._movement = ServerAiBridge._movement.__get__(self.bridge)
        changes = [lambda a: a['task']['lastResult']['evidence'].update(X=11),
                   lambda a: a['task']['lastResult'].update(inputsReleased=False),
                   lambda a: a['task'].update(actorGeneration=2),
                   lambda a: a['task']['lastResult']['evidence'].update(targetChanged=True),
                   lambda a: a['task'].update(phase='navigating')]
        for change in changes:
            self.host.polls = 0
            self.enable(goal, change)
            out = await atomic.dispatch(self.bridge, 'mine_target', goal)
            self.assertEqual('unknown', out['status'], out)
            self.bridge._movement = ServerAiBridge._movement.__get__(self.bridge)

    async def test_unknown_task_prevents_second_submit(self):
        def unknown(a):
            a.update(status='unknown', reason='task_exception')
            a['endEvidence']['effectVerified'] = False
        self.enable(dict(x=10, y=20), unknown)
        r = await LuaRuntime(self.bridge).execute("api('mine_target',{x=10,y=20});api('mine_target',{x=10,y=20})", limits=Limits(actions=2))
        self.assertEqual('unknown', r['actionState'], r)
        self.assertEqual(1, len([a for a in self.host.sent if a['op'] == 'start']))


import test_server_ai_agent as agent_fixture


class TaskAgentHost(agent_fixture.AiHost):
    def __init__(self):
        super().__init__()
        self.scene = self.perception = True
        self.scene_hazard = False
        self.support_affected = False
        self.pickup_observed = True

    def query(self, op, args):
        result = super().query(op, args)
        if op in ('observe_scene', 'inspect_tile'):
            result.update(observedAtUnixMs=1793520000000, clock='server_utc_at_projection')
        if op == 'observe_scene':
            rows = ['.' * 45 for _ in range(29)]
            rows[14] = '.' * 10 + '#' + '.' * 34
            rows[15] = '#' * 45
            result.update(gameUpdate=10, data=dict(
                geometry=dict(originX=0, originY=6, rows=rows),
                coverage=dict(geometryComplete=True),
                hazards=dict(tiles=dict(matchedCount=int(self.scene_hazard), truncated=False),
                             entities=dict(matchedCount=0, truncated=False)),
                actor=dict(dead=False, navigationProfileSupported=True, velocity=dict(x=0, y=0),
                           boundsWorldPixels=dict(x=112, y=294, width=20, height=42),
                           bodyTileBounds=dict(minX=7, maxX=8, minY=18, maxY=20)),
                objects=dict(items=[dict(kind='ore_component', contentKey='Terraria/Copper',
                                         target=copy.deepcopy(TARGET))], truncated=False)))
        if op == 'inspect_tile':
            result['data'] = dict(
                cell=dict(knowledge='read', hasTile=True, tileType=7),
                preflight=dict(withinConservativeReach=True),
                miningSupportImpact=dict(available=True, affectsCurrentStance=self.support_affected,
                                         pose=dict(grounded=True, px=112, py=294, vx=0, vy=0),
                                         afterRemoval=dict(rejection=None)))
        if op == 'inspect_inventory' and args.get('mode') == 'item':
            completed = bool(self.action and self.action['status'] == 'completed')
            result['gameUpdate'] += int(completed)
            result['data'].update(typeId=12, count=int(completed and self.pickup_observed))
        return result


def mining_example(content):
    source, = re.findall(r'```lua\r?\n(.*?)```', content, re.S)
    return source


class MineTaskAgentTests(unittest.IsolatedAsyncioTestCase):
    host_type = TaskAgentHost
    asyncTearDown = agent_fixture.ServerAiAgentTests.asyncTearDown
    program = agent_fixture.ServerAiAgentTests.program
    skill = agent_fixture.ServerAiAgentTests.skill
    receive = agent_fixture.ServerAiAgentTests.receive
    async def asyncSetUp(self):
        await agent_fixture.ServerAiAgentTests.asyncSetUp(self)
        self.bridge = self.ai
        MineTargetTests.enable(self, {'target': TARGET})
        self.agent.history[0]['content'], self.agent.tools = agent_fixture.server_contract(self.ai)
    async def test_chat_model_substitute_submits_one_structured_task(self):
        content = bundled_skills().load({'name': 'mining_and_ores'})['content']
        source = mining_example(content)
        self.answers = [self.skill('mining_and_ores'), self.program(source),
                        dict(role='assistant', content='指定矿物已挖掘，输入已释放。')]
        await self.receive('mine-task', '挖掘观察到的铜矿')
        await self.agent.task
        self.assertEqual(3, len(self.requests))
        prompt = self.requests[0]['messages'][0]['content']
        self.assertIn('mine_target', prompt)
        self.assertIn('mining_and_ores', prompt)
        self.assertIn('行动优先', prompt)
        self.assertNotIn('# 挖矿与矿石', prompt)
        skill = json.loads(next(m['content'] for m in self.requests[1]['messages'] if m['role'] == 'tool'))
        self.assertTrue(skill['valid'])
        self.assertEqual(content, skill['content'])
        before, after = [json.loads(r['messages'][-1]['content'])['runtimeLua'] for r in self.requests[:2]]
        self.assertEqual(before['remainingToolCalls'] - 1, after['remainingToolCalls'])
        for key in ('remainingLuaApiCalls', 'remainingActionAttempts', 'remainingSeconds'):
            self.assertEqual(before[key], after[key])
        starts = [r for r in self.aihost.sent if r['op'] == 'start']
        self.assertEqual(1, len(starts))
        self.assertEqual('mine_target', starts[0]['operation'])
        self.assertEqual({'target': TARGET}, starts[0]['arguments'])
        gameplay = [r['operation'] for r in self.aihost.sent if r['op'] == 'start'
                    or r['op'] == 'query' and (r['operation'] in {'observe_scene', 'inspect_tile'}
                                               or r['arguments'].get('mode') == 'item')]
        self.assertEqual(['observe_scene', 'inspect_tile', 'inspect_inventory',
                          'mine_target', 'inspect_inventory'], gameplay)
        self.assertEqual(['execute_lua', 'read_skill'], [t['function']['name'] for t in self.requests[0]['tools']])
        self.assertEqual(254, self.ai.reply_receipts[-1]['authorSlot'])
        receipt = json.loads(next(m['content'] for m in self.requests[2]['messages'] if m.get('tool_call_id') == 'lua-1'))
        self.assertEqual('completed', receipt['taskOutcome'])
        self.assertEqual('inventory_increase_observed', receipt['value']['status'])
        self.assertEqual(1, receipt['value']['inventoryNetChange'])
        self.assertEqual('unknown', receipt['goalOutcome'])
        self.assertEqual('unknown', self.agent.runs[-1].goal_outcome)

    async def test_skill_stops_before_action_on_hazard_or_support_change(self):
        source = mining_example(bundled_skills().load({'name': 'mining_and_ores'})['content'])
        for hazard, support, status in ((True, False, 'hazards_present_or_unknown'),
                                        (False, True, 'support_unconfirmed')):
            with self.subTest(status=status):
                self.aihost.scene_hazard = hazard
                self.aihost.support_affected = support
                receipt = await LuaRuntime(self.ai).execute(source, limits=Limits(actions=1))
                self.assertEqual('completed', receipt['status'])
                self.assertEqual(status, receipt['value']['status'])
        self.assertFalse(any(r['op'] == 'start' for r in self.aihost.sent))

    async def test_skill_reports_mined_tile_without_claiming_pickup(self):
        self.aihost.pickup_observed = False
        source = mining_example(bundled_skills().load({'name': 'mining_and_ores'})['content'])
        receipt = await LuaRuntime(self.ai).execute(source, limits=Limits(actions=1))
        self.assertEqual('completed', receipt['status'])
        self.assertEqual('completed', receipt['lastAction']['status'])
        self.assertEqual('pickup_unconfirmed', receipt['value']['status'])
        self.assertEqual(0, receipt['value']['inventoryNetChange'])


if __name__ == '__main__':
    unittest.main()
