"""Native lethal encounter during frontier exploration with deterministic model responses."""
import asyncio
import json
import time

import httpx

from agent import Agent
from llm import LLMClient
from server_ai import ServerAiBridge


class TestChat:
    def command(self, command, *, text, chatId, replyTurn):
        assert command == 'authorize_ai_reply'
        return {'status':'authorized','data':{'chatId':chatId,'replyTurn':replyTurn}}


async def exercise(profile, port, bridge, name):
    def connect():
        return ServerAiBridge(port)
    def chat(identifier, text):
        agent.receive(dict(type='chat_message',source='local_ai_command',isSelf=True,
                           eventId=identifier,text=text,senderName='Tester',senderId=0))
    feedback=[]
    requested=set()
    def model(request):
        messages=json.loads(request.content)['messages']
        context=json.loads(next(m['content'] for m in reversed(messages) if m['role']=='user'))
        if life:=context.get('runtimeLife'):
            feedback.append(life)
        message={'role':'assistant','content':'已核验当前状态。'}
        stage='respawn' if life else 'exploration'
        if stage not in requested:
            requested.add(stage)
            source=("return api('select_hotbar_slot',{slot=2})" if life else
                    "return api('submit_task',{operation='explore_frontier',arguments={radiusTiles=18,maxSegments=3,allowDig=false}})")
            message.update(content=None,tool_calls=[dict(id='probe',type='function',function={
                'name':'execute_lua','arguments':json.dumps({'source':source})})])
        return httpx.Response(200,json={'choices':[{'message':message,'finish_reason':'tool_calls' if message.get('tool_calls') else 'stop'}]})
    llm=LLMClient(base_url='https://model.invalid/v1',api_key='test',model='fixture',transport=httpx.MockTransport(model))
    agent=Agent(bridge,llm,chat_bridge=TestChat())
    reader=connect()
    marker=profile/'death-encounter'
    try:
        if name=='death-stop':
            # The previous case respawned this same player. Let native spawn immunity expire.
            start_tick=reader._request('state')['data']['tick']
            async with asyncio.timeout(15):
                while (await asyncio.to_thread(reader._request,'state'))['data']['tick']-start_tick<360:
                    await asyncio.sleep(.2)
        marker.touch()
        chat(name,'探索附近地形')
        original=bridge.binding['generation']
        saw_dead=False
        deadline=time.monotonic()+70
        while time.monotonic()<deadline:
            state=await asyncio.to_thread(reader.life_status)
            await agent.observe_life(state,connect)
            if agent._waiting_respawn and not saw_dead:
                saw_dead=True
                if name=='death-stop':
                    chat('stop','停止')
            if agent.bridge.binding['generation']!=original:
                break
            await asyncio.sleep(.05)
        assert saw_dead and agent.bridge.binding['generation']==original+1, state
        await asyncio.wait_for(agent.task,15)
        await agent.observe_life(await asyncio.to_thread(reader.life_status),connect)
        event=agent._death_context['death']
        assert event['npcIndex']>=0 and event['description'] and event['activity']['operation']=='explore_frontier',event
        assert agent._death_context['playerRequest']['text']=='探索附近地形'
        if name=='death-stop':
            assert event['coinsLostCopper']==0,event
            assert not feedback and agent._last_player_chat['eventId']=='stop'
        else:
            assert 0<event['coinsLostCopper']<=42000,event
            assert feedback and feedback[0]['death']==event and agent.bridge.reply_receipts, feedback
            assert agent.bridge._request('state')['data']['selectedSlot']==1
            assert sum(json.loads(m['content']).get('source')=='host_respawn' for m in agent.history if m['role']=='user')==1
        print(name,event['description'],event['activity']['operation'],original,'->',original+1,flush=True)
        return dict(case=name,death=event,modelReceivedDeath=bool(feedback))
    finally:
        marker.unlink(missing_ok=True)
        await agent.close()
        agent.bridge.close()
        reader.close()
        await llm.close()
