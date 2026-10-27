"""Existing Agent + HTTP model substitute + Lua worker + two real loopback bridges.

Chat/game storage are substitutes. No graphical or external-model acceptance claim.
"""
import asyncio
import copy
import io
import json
from pathlib import Path
import socketserver
import sys
import tempfile
import threading
import unittest
from unittest.mock import patch

import httpx

sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'python'))
from agent import Agent
from control import Bridge
from llm import LLMClient
from lua_agent import ProgramStore, server_contract
from server_ai import ServerAiBridge
from test_server_ai_backend import Host


class AiHost(Host):
    def query(self,operation,args):
        result=super().query(operation,args)
        data=result['data']
        if operation=='get_player_state' and self.action and self.action['status']=='completed' and self.action['operation']=='navigate_to_x':
            start=next(r for r in reversed(self.sent) if r['op']=='start' and r['actionId']==self.action['actionId'])
            data['player']['position']['x']=start['arguments']['targetX']
        if operation=='inspect_inventory' and args.get('mode')=='full':
            data['hotbarKeyBindings']=[dict(key=(slot+1)%10,inventorySlot=slot) for slot in range(10)]
            for item in data['inventory']:
                if not item['empty']:
                    item['use'].update({k:0 for k in ('damage','axe','hammer','useStyle','useTime','useAnimation','ammo','shoot')})
                    item['use'].update(createTile=-1,createWall=-1,consumable=False)
                    item['maxStack']=1;item['favorited']=False;item['prefix']=0;item['customState']='not_applicable'
                    item['contentKey']='Terraria/CopperPickaxe'
        return result


class ChatHandler(socketserver.StreamRequestHandler):
    def handle(self):
        h=self.server
        self.write(dict(v=1,type='hello',capabilities=['chat_events','send_chat','local_ai_chat_v1']))
        h.handler=self;h.ready.set()
        while line:=self.rfile.readline():
            r=json.loads(line);h.sent.append(r)
            ok=r['cmd']=='send_ai_chat' and r['chatId']==h.latest
            self.write(dict(v=1,type='reply',id=r['id'],ok=ok,status='sent' if ok else 'superseded'))
    def write(self,value):
        with self.server.write_lock:
            self.wfile.write((json.dumps(value)+'\n').encode());self.wfile.flush()


class ChatHost(socketserver.ThreadingTCPServer):
    allow_reuse_address=True
    daemon_threads=True
    def __init__(self):
        super().__init__(('127.0.0.1',0),ChatHandler)
        self.ready=threading.Event();self.write_lock=threading.Lock();self.latest=None;self.sent=[]
    def emit(self,id,text,**extra):
        e=dict(v=1,type='chat_message',eventId=id,senderId=0,senderName='Local player',
               text=text,isSelf=True,source='local_ai_command')
        e.update(extra)
        if e.get('source')=='local_ai_command' and e['isSelf']:
            self.latest=id
        self.handler.write(e)


class ServerAiAgentTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.stderr=patch('sys.stderr',new_callable=io.StringIO);self.stderr.start()
        self.directory=tempfile.TemporaryDirectory()
        self.aihost=AiHost();self.aihost.actions=True
        self.chathost=ChatHost()
        self.threads=[]
        for host in (self.aihost,self.chathost):
            t=threading.Thread(target=host.serve_forever,daemon=True);t.start();self.threads.append(t)
        self.ai=ServerAiBridge(self.aihost.server_address[1],timeout=2)
        self.events=asyncio.Queue();loop=asyncio.get_running_loop()
        self.chat=Bridge(self.chathost.server_address[1],timeout=2,on_event=lambda e:loop.call_soon_threadsafe(self.events.put_nowait,e))
        self.chathost.ready.wait(2)
        self.requests=[];self.answers=[]
        def respond(request):
            body=json.loads(request.content);self.requests.append(body)
            if not self.answers:raise AssertionError('unexpected model request')
            message=self.answers.pop(0)
            return httpx.Response(200,json={'choices':[{'message':message,'finish_reason':'tool_calls' if message.get('tool_calls') else 'stop'}]})
        self.llm=LLMClient(base_url='https://model.invalid/v1',api_key='offline-test-key',model='offline-model',transport=httpx.MockTransport(respond))
        self.agent=Agent(self.ai,self.llm,chat_bridge=self.chat,lua_store=ProgramStore(Path(self.directory.name)/'sources'))
    async def asyncTearDown(self):
        await self.agent.close();self.ai.close();self.chat.close();await self.llm.close()
        for host in (self.aihost,self.chathost):host.shutdown();host.server_close()
        for t in self.threads:t.join()
        self.directory.cleanup();self.stderr.stop()
    def program(self,source):
        return dict(role='assistant',content=None,tool_calls=[dict(id='lua-1',type='function',function=dict(name='execute_lua',arguments=json.dumps(dict(source=source))))])
    async def receive(self,id='task-1',text='向右移动80像素'):
        self.chathost.emit(id,text)
        self.agent.receive(await asyncio.wait_for(self.events.get(),2))
    async def test_chat_http_model_lua_bound_execution_and_chat_reply(self):
        self.answers=[self.program("""
            local before=api('get_player_state',{})
            assert(before.valid and before.player.source=='server_ai_player')
            local move=api('navigate_to_x',{targetX=before.player.position.x+80})
            assert(move.valid and move.completed)
            local after=api('get_player_state',{})
            return {action=move,after=after.player.position}
        """),dict(role='assistant',content='已向右移动约80像素并停稳。')]
        await self.receive();await self.agent.task
        self.assertEqual(2,len(self.requests))
        self.assertEqual(['execute_lua'],[t['function']['name'] for t in self.requests[0]['tools']])
        start=next(r for r in self.aihost.sent if r['op']=='start')
        self.assertEqual('navigate_to_x',start['operation']);self.assertEqual(180,start['arguments']['targetX'])
        self.assertEqual(self.ai.binding,start['binding'])
        self.assertEqual(['send_ai_chat'],[r['cmd'] for r in self.chathost.sent])
        self.assertTrue(self.chathost.sent[0]['text'].startswith('[AI] '))
        runtime=json.loads(self.requests[0]['messages'][-1]['content'])
        self.assertEqual('available',runtime['runtimeInventory']['status'])
        result=json.loads(next(m['content'] for m in self.requests[1]['messages'] if m['role']=='tool'))
        self.assertTrue(result['actionSuccess']);self.assertEqual('released',result['actionState'])
        self.assertNotIn('movementLoop',result)
        self.assertEqual(180,result['value']['after']['x'])
    async def test_ordinary_chat_remote_prefix_self_echo_and_game_messages_cannot_start_or_cancel(self):
        for e in (dict(type='chat_message',eventId='ordinary',text='hello',isSelf=False),
                  dict(type='chat_message',eventId='remote',text='!ai move',isSelf=False,source='local_ai_command'),
                  dict(type='chat_message',eventId='echo',text='!ai move',isSelf=True),dict(type='game_message',eventId='notice')):
            self.agent.receive(e)
        self.assertIsNone(self.agent.task);self.assertEqual(0,self.agent.generation)
        self.assertEqual([],self.requests)
        self.chathost.emit('echo','[AI] !ai move',source=None)
        await asyncio.sleep(.05);self.assertTrue(self.events.empty())
    async def test_new_command_cancels_lua_before_next_task_and_ignores_duplicate(self):
        self.aihost.mode='pending'
        self.answers=[self.program("return api('navigate_to_x',{targetX=180})"),
                      self.program("return api('get_player_state',{}).player.life"),dict(role='assistant',content='生命73。')]
        await self.receive()
        for _ in range(200):
            if self.aihost.action is not None:break
            await asyncio.sleep(.01)
        self.assertIsNotNone(self.aihost.action)
        previous=self.agent.task
        await self.receive('task-2','查询生命');await self.agent.task
        self.assertTrue(previous.cancelled())
        self.assertEqual(1,sum(r['op']=='cancel' for r in self.aihost.sent))
        self.assertEqual(1,len(self.chathost.sent));self.assertEqual('task-2',self.chathost.sent[0]['chatId'])
        self.agent.receive(dict(type='chat_message',eventId='task-2',text='重复',source='local_ai_command',isSelf=True))
        self.assertEqual(2,self.agent.generation)
    async def test_unknown_identity_never_reconnects_or_reports_success(self):
        self.aihost.mode='change_on_poll'
        self.answers=[self.program("return api('navigate_to_x',{targetX=180})"),dict(role='assistant',content='错误地声称完成。')]
        await self.receive();await self.agent.task
        self.assertTrue(self.ai._closed)
        self.assertEqual(1,sum(r['op']=='bind' for r in self.aihost.sent))
        self.assertNotIn('错误地声称完成',self.chathost.sent[-1]['text'])
        self.assertIn('未完成',self.chathost.sent[-1]['text'])
    async def test_unadvertised_direct_tools_never_reach_human_or_ai_execution(self):
        self.answers=[dict(role='assistant',content=None,tool_calls=[dict(id='bad',type='function',function=dict(name='get_state',arguments='{}'))]),dict(role='assistant',content='该工具未执行。')]
        await self.receive();await self.agent.task
        self.assertFalse(any(r['op']=='start' for r in self.aihost.sent))
        self.assertEqual(['send_ai_chat'],[r['cmd'] for r in self.chathost.sent])
    async def test_backend_contract_contains_only_negotiated_apis(self):
        prompt,tools=server_contract(self.ai)
        definitions=json.loads(prompt.split('当前后端API契约：\n')[1])
        self.assertEqual(set(self.ai.supported_atomic_operations),{t['function']['name'] for t in definitions})
        self.assertEqual({},next(t['function']['parameters']['properties'] for t in definitions if t['function']['name']=='use_item'))
        for unsupported in ('observe_exploration','get_observation','navigate_to_landing_region','attack_entity','transfer_inventory_item'):
            self.assertNotIn(unsupported,prompt);self.assertNotIn(unsupported,tools[0]['function']['description'])

    async def test_model_failure_replies_on_chat_channel_without_actions(self):
        from unittest.mock import AsyncMock
        from llm import LLMError
        self.llm.complete=AsyncMock(side_effect=LLMError('fixture provider unavailable'))
        await self.receive();await self.agent.task
        self.assertIn('未完成',self.chathost.sent[-1]['text'])
        self.assertFalse(any(r['op']=='start' for r in self.aihost.sent))

    async def test_server_mode_constructor_refuses_human_execution_backend(self):
        from control import BridgeError
        with self.assertRaises(BridgeError):
            Agent(self.chat,self.llm,chat_bridge=self.chat)
        self.assertEqual([],self.chathost.sent)

    async def test_prefixed_reply_is_bounded_and_exact_sent_text_is_retained(self):
        self.answers=[dict(role='assistant',content='好'*399)]
        await self.receive();await self.agent.task
        sent=self.chathost.sent[-1]['text']
        self.assertTrue(sent.startswith('[AI] '));self.assertLessEqual(len(sent.encode('utf-16-le')),800)
        self.assertEqual(sent,self.agent.history[-1]['content'])


if __name__=='__main__':unittest.main()
