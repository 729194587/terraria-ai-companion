import copy
import threading
import time
import unittest

from test_server_ai_backend import Host
from server_ai import ServerAiBridge, operations_for_capabilities
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
    def query(self, op, args):
        if op == 'observe_scene':
            return dict(available=True, worldSessionId='world-1', actorGeneration=1, actorSlot=254,
                        gameUpdate=10, observedAtUnixMs=1793520000000, clock='server_utc_at_projection',
                        data=dict(objects=dict(items=[dict(kind='ore_component', target=copy.deepcopy(TARGET))])))
        return super().query(op, args)


class MineTaskAgentTests(unittest.IsolatedAsyncioTestCase):
    host_type = TaskAgentHost
    asyncTearDown = agent_fixture.ServerAiAgentTests.asyncTearDown
    program = agent_fixture.ServerAiAgentTests.program
    receive = agent_fixture.ServerAiAgentTests.receive
    async def asyncSetUp(self):
        await agent_fixture.ServerAiAgentTests.asyncSetUp(self)
        self.bridge = self.ai
        MineTargetTests.enable(self, {'target': TARGET})
        self.agent.history[0]['content'], self.agent.tools = agent_fixture.server_contract(self.ai)
    async def test_chat_model_substitute_submits_one_structured_task(self):
        source = "local q=api('observe_scene',{layers={'objects'}});return api('mine_target',{target=q.data.objects.items[1].target})"
        self.answers = [self.program(source), dict(role='assistant', content='指定矿物已挖掘，输入已释放。')]
        await self.receive('mine-task', '挖掘观察到的铜矿')
        await self.agent.task
        self.assertEqual(2, len(self.requests))
        self.assertIn('mine_target', self.requests[0]['messages'][0]['content'])
        starts = [r for r in self.aihost.sent if r['op'] == 'start']
        self.assertEqual(1, len(starts))
        self.assertEqual('mine_target', starts[0]['operation'])
        self.assertEqual({'target': TARGET}, starts[0]['arguments'])
        self.assertEqual(['execute_lua'], [t['function']['name'] for t in self.requests[0]['tools']])
        self.assertEqual(254, self.ai.reply_receipts[-1]['authorSlot'])
        import json
        receipt = json.loads(next(m['content'] for m in self.requests[1]['messages'] if m['role'] == 'tool'))
        self.assertEqual('completed', receipt['taskOutcome'])
        self.assertEqual('unknown', receipt['goalOutcome'])
        self.assertEqual('unknown', self.agent.runs[-1].goal_outcome)


if __name__ == '__main__':
    unittest.main()
