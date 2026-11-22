"""Existing Agent -> model -> Lua -> real isolated native AI. Chat endpoint is a substitute.

Only invoke with the separate copied-world native harness. --live-model uses configured credentials.
"""
import argparse
import asyncio
import json
from pathlib import Path
import sys
import time
import uuid

import httpx

ROOT=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(ROOT/'python'))
from control import BridgeError
from agent import Agent
from llm import LLMClient
from lua_agent import ProgramStore
from server_ai import ServerAiBridge
from lua_runtime import Limits


class ChatSink:
    """Client authorization substitute; native server publishes the actual reply."""
    hello={'capabilities':['local_ai_chat_v1','local_ai_chat_v2','chat_events','send_chat','ai_reply_authorization_v2']}
    def __init__(self):self.sent=[]
    def command(self,cmd,**args):
        assert cmd=='authorize_ai_reply' and args['chatId']=='isolated-local-ai-task'
        self.sent.append(dict(cmd=cmd,**args));return dict(ok=True,status='authorized',data=dict(chatId=args['chatId'],replyTurn=args['replyTurn']))


async def verify(args):
    bridge=ServerAiBridge(args.port,timeout=5);chat=ChatSink();requests=[];exploration_reports=[]
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
            if args.exploration:
                source="return explore({direction='left',maxSteps=3,stepPixels=64,maxDistance=256})"
            message=dict(role='assistant',content=None,tool_calls=[dict(id='native-lua',type='function',function=dict(name='execute_lua',arguments=json.dumps(dict(source=source))))])
        else:message=dict(role='assistant',content='已完成本次有界探索并停稳。' if args.exploration else '已依据服务器AI的实际结束证据向右移动并停稳。')
        return httpx.Response(200,json={'choices':[dict(message=message,finish_reason='tool_calls' if turn==1 else 'stop')]})
    llm=LLMClient(timeout=30,max_tokens=4096) if args.live_model else LLMClient(base_url='https://offline.invalid/v1',api_key='isolated-fixture',model='offline-response',transport=httpx.MockTransport(respond))
    original=llm.complete
    async def record(messages,tools):
        requests.append(dict(toolNames=[t['function']['name'] for t in tools],runtime=json.loads(messages[-1]['content'])))
        for message in messages:
            if message.get('role')=='tool':
                result=json.loads(message['content'])
                if isinstance(result.get('value'),dict) and result['value'].get('kind')=='exploration_report':
                    exploration_reports.append(result['value'])
        return await original(messages,tools)
    llm.complete=record
    agent=Agent(bridge,llm,chat_bridge=chat,max_model_calls=4,max_tool_calls=3,
        lua_limits=Limits(seconds=30 if args.exploration else 20,calls=40 if args.exploration else 12,actions=3 if args.exploration else 2),lua_store=ProgramStore(args.output.parent/'agent-programs'))
    try:
        agent.receive(dict(type='chat_message',source='local_ai_command',isSelf=True,eventId='isolated-local-ai-task',
            senderId=0,senderName='Isolated harness',text=('请连续向左探索三段，每段约64像素。只移动观察，不挖掘，不重试失败动作。' if args.exploration else '这是复制世界的联通测试，脚下已准备安全平地。请用execute_lua读取服务器AI实际位置，向右移动约48像素，停稳后再次读取位置并简短回复。不要选栏、使用或挖掘，不要重试失败动作。')))
        await asyncio.wait_for(agent.task,150)
        after=bridge.command('query_atomic',operation='get_player_state',arguments={})['data']
        programs=[call for m in agent.history for call in m.get('tool_calls',[]) if call['function']['name']=='execute_lua']
        receipts=[json.loads(m['content']) for m in agent.history if m['role']=='tool']
        completed=[r for r in receipts if r.get('actionSuccess') is True and r.get('actionState')=='released'
                   and (r.get('lastAction') or {}).get('operation')=='navigate_to_x']
        p=after['data']['player'];start=before['data']['player']
        passed=(bool(programs) and bool(completed) and bool(chat.sent) and bool(bridge.reply_receipts)
            and bridge.reply_receipts[-1]['authorSlot']==bridge.binding['slot']
            and ((-240<=p['position']['x']-start['position']['x']<=-144) if args.exploration else (36<=p['position']['x']-start['position']['x']<=64))
            and p['velocity']==dict(x=0,y=0) and after['binding']==before['binding']
            and all(r['toolNames']==['execute_lua','read_skill'] for r in requests))
        if args.exploration:
            passed=passed and len(exploration_reports)==1 and len(exploration_reports[0]['steps'])==3 and completed[-1]['actions']==3
        bridge.invalidate_replies(2)
        await asyncio.to_thread(bridge.advance_reply_turn,2)
        stale_rejected=False
        try:
            await asyncio.to_thread(bridge._request,'say',text='stale reply must never publish',replyTurn=1,
                messageId=uuid.uuid4().hex,expiresAtUnixMs=int(time.time()*1000)+1800)
        except BridgeError as error:
            stale_rejected=error.status=='superseded'
        passed=passed and stale_rejected
        report=dict(result='passed' if passed else 'failed',mode='isolated real native AI + existing Agent/Lua + native author broadcast; synthetic local chat input/client authorization; NO graphical Host & Play',
            modelMode='configured external model' if args.live_model else 'scripted HTTP model response via existing LLMClient',
            modelCalls=len(requests),explorationReports=exploration_reports,requests=requests,binding=bridge.binding,before=start,after=p,programs=programs,luaReceipts=receipts,
            chatAuthorizations=chat.sent,publishedReplies=bridge.reply_receipts,staleReplyRejected=stale_rejected)
        args.output.write_text(json.dumps(report,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
        print(json.dumps(dict(result=report['result'],modelMode=report['modelMode'],modelCalls=len(requests),output=str(args.output))))
        return 0 if passed else 1
    finally:
        await agent.close();bridge.close();await llm.close()


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--port',type=int,required=True)
    parser.add_argument('--output',type=Path,required=True)
    parser.add_argument('--exploration',action='store_true',help='Exercise three continuous exploration segments through the existing Agent/Lua')
    parser.add_argument('--live-model',action='store_true',help='Make bounded requests to the configured model provider')
    return asyncio.run(verify(parser.parse_args()))


if __name__=='__main__':raise SystemExit(main())
