import asyncio
import json
import unittest
from exploration_fixture import ExplorationHost
import test_server_ai_agent as server_fixture


class ExplorationAgentTests(unittest.IsolatedAsyncioTestCase):
    host_type = ExplorationHost
    program = server_fixture.ServerAiAgentTests.program
    receive = server_fixture.ServerAiAgentTests.receive

    async def asyncSetUp(self):
        await server_fixture.ServerAiAgentTests.asyncSetUp(self)

    async def asyncTearDown(self):
        await server_fixture.ServerAiAgentTests.asyncTearDown(self)

    async def test_one_chat_one_program_explores_three_segments_then_replies(self):
        self.answers=[self.program("return explore({direction='right',maxSteps=3})"),
                      dict(role='assistant',content='已完成三段有界探索并停稳。')]
        await self.receive('explore-1','开始探索附近世界')
        await self.agent.task
        self.assertEqual(2,len(self.requests))
        self.assertIn('不是默认或必经方案',self.requests[0]['messages'][0]['content'])
        result=json.loads(next(m['content'] for m in self.requests[1]['messages'] if m['role']=='tool'))
        self.assertTrue(result['programCompleted'],result)
        self.assertEqual('step_limit',result['value']['reason'])
        self.assertEqual(3,len(result['value']['steps']))
        self.assertEqual(3,len([r for r in self.aihost.sent if r['op']=='start']))
        self.assertEqual('released',result['actionState'])
        self.assertEqual(1,len(self.ai.reply_receipts))
        self.assertEqual('unknown',result['taskOutcome'])

    async def test_new_stop_chat_cancels_loop_without_restart_or_old_reply(self):
        self.aihost.mode='pending'
        self.answers=[self.program("return explore({maxSteps=6})"),dict(role='assistant',content='已停止探索。')]
        await self.receive('explore-old','开始探索附近世界')
        for _ in range(400):
            if any(r['op']=='start' for r in self.aihost.sent):break
            await asyncio.sleep(.01)
        self.assertTrue(any(r['op']=='start' for r in self.aihost.sent))
        old=self.agent.task
        await self.receive('stop-new','停止')
        await self.agent.task
        self.assertTrue(old.cancelled())
        self.assertEqual(1,sum(r['op']=='start' for r in self.aihost.sent))
        self.assertEqual(1,sum(r['op']=='cancel' for r in self.aihost.sent))
        self.assertEqual('cancelled',self.aihost.action['status'])
        self.assertTrue(self.aihost.action['endEvidence']['inputsReleased'])
        self.assertEqual(1,len(self.ai.reply_receipts))
        self.assertEqual('stop-new',self.chathost.sent[-1]['chatId'])
