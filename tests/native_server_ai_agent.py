"""Existing Agent -> model -> Lua -> real isolated native AI. Chat endpoint is a substitute.

Only invoke with the separate copied-world native harness. --live-model uses configured credentials.
"""
import argparse
import asyncio
import json
from pathlib import Path
import sys

import httpx

ROOT=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(ROOT/'python'))
from agent import Agent
from llm import LLMClient
from lua_agent import ProgramStore
from server_ai import ServerAiBridge
from lua_runtime import Limits


class ChatSink:
    """No game client connection: records what would be sent through the chat bridge."""
    hello={'capabilities':['local_ai_chat_v1','chat_events','send_chat']}
    def __init__(self):self.sent=[]
    def command(self,cmd,**args):
        assert cmd=='send_ai_chat' and args['chatId']=='isolated-local-ai-task'
        self.sent.append(dict(cmd=cmd,**args));return dict(ok=True,status='sent')


async def verify(args):
    bridge=ServerAiBridge(args.port,timeout=5);chat=ChatSink();requests=[]
    before=bridge.command('query_atomic',operation='get_player_state',arguments={})['data']
    turn=0
    def respond(request):
        nonlocal turn
        turn+=1
        if turn==1:
            source="""local before=api('get_player_state',{})
assert(before.valid and before.player.source=='server_ai_player')
local action=api('navigate_to_x',{targetX=before.player.position.x+48})
assert(action.valid and action.completed)
local after=api('get_player_state',{})
return {action=action,after=after.player.position}"""
            message=dict(role='assistant',content=None,tool_calls=[dict(id='native-lua',type='function',function=dict(name='execute_lua',arguments=json.dumps(dict(source=source))))])
        else:message=dict(role='assistant',content='已依据服务器AI的实际结束证据向右移动并停稳。')
        return httpx.Response(200,json={'choices':[dict(message=message,finish_reason='tool_calls' if turn==1 else 'stop')]})
    llm=LLMClient(timeout=30,max_tokens=4096) if args.live_model else LLMClient(base_url='https://offline.invalid/v1',api_key='isolated-fixture',model='offline-response',transport=httpx.MockTransport(respond))
    original=llm.complete
    async def record(messages,tools):
        requests.append(dict(toolNames=[t['function']['name'] for t in tools],runtime=json.loads(messages[-1]['content'])))
        return await original(messages,tools)
    llm.complete=record
    agent=Agent(bridge,llm,chat_bridge=chat,max_model_calls=4,max_tool_calls=3,
        lua_limits=Limits(seconds=20,calls=12,actions=2),lua_store=ProgramStore(args.output.parent/'agent-programs'))
    try:
        agent.receive(dict(type='chat_message',source='local_ai_command',isSelf=True,eventId='isolated-local-ai-task',
            senderId=0,senderName='Isolated harness',text='这是复制世界的联通测试，脚下已准备安全平地。请用execute_lua读取服务器AI实际位置，向右移动约48像素，停稳后再次读取位置并简短回复。不要选栏、使用或挖掘，不要重试失败动作。'))
        await asyncio.wait_for(agent.task,150)
        after=bridge.command('query_atomic',operation='get_player_state',arguments={})['data']
        programs=[call for m in agent.history for call in m.get('tool_calls',[]) if call['function']['name']=='execute_lua']
        receipts=[json.loads(m['content']) for m in agent.history if m['role']=='tool']
        completed=[r for r in receipts if r.get('actionSuccess') is True and r.get('actionState')=='released'
                   and (r.get('lastAction') or {}).get('operation')=='navigate_to_x']
        p=after['data']['player'];start=before['data']['player']
        passed=(bool(programs) and bool(completed) and bool(chat.sent) and 36<=p['position']['x']-start['position']['x']<=64
            and p['velocity']==dict(x=0,y=0) and after['binding']==before['binding']
            and all(r['toolNames']==['execute_lua'] for r in requests))
        report=dict(result='passed' if passed else 'failed',mode='isolated real native AI + existing Agent/Lua; synthetic local chat event and chat reply sink; NO graphical Host & Play',
            modelMode='configured external model' if args.live_model else 'scripted HTTP model response via existing LLMClient',
            modelCalls=len(requests),requests=requests,binding=bridge.binding,before=start,after=p,programs=programs,luaReceipts=receipts,chatReplies=chat.sent)
        args.output.write_text(json.dumps(report,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
        print(json.dumps(dict(result=report['result'],modelMode=report['modelMode'],modelCalls=len(requests),output=str(args.output))))
        return 0 if passed else 1
    finally:
        await agent.close();bridge.close();await llm.close()


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--port',type=int,required=True)
    parser.add_argument('--output',type=Path,required=True)
    parser.add_argument('--live-model',action='store_true',help='Make bounded requests to the configured model provider')
    return asyncio.run(verify(parser.parse_args()))


if __name__=='__main__':raise SystemExit(main())
