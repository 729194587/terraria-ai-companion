"""Real server warning during navigation with deterministic model decisions."""
import asyncio
import json
from agent import Agent
from native_death_feedback import TestChat
from server_ai import ServerAiBridge


async def exercise(profile,port,bridge,name,target):
    reader=ServerAiBridge(port)
    reviews=[]
    action_requests=[]
    travel=[]
    class Model:
        async def complete(self,messages,tools):
            if tools[0]['function']['name']=='reconsider_plan':
                reviews.append(json.loads(messages[-1]['content']))
                before=(await asyncio.to_thread(reader._request,'state'))['data']['position']['x']
                await asyncio.sleep(.35)
                after=(await asyncio.to_thread(reader._request,'state'))['data']['position']['x']
                travel.append(after-before)
                if name=='news-change':
                    return call('reconsider_plan',{'reason':'先停在附近观察'})
            elif (name=='news-change' and not action_requests and
                  any('host_broadcast' in (m.get('content') or '') for m in messages if m['role']=='user')):
                action_requests.append(original)
                return call('execute_lua',{'source':"return api('cancel_task',{taskId='%s'})" % original})
            return {'role':'assistant','content':'维持现状。'}
    def call(name,args):
        return {'role':'assistant','tool_calls':[{'id':'decision','type':'function','function':{
            'name':name,'arguments':json.dumps(args,ensure_ascii=False)}}]}
    agent=Agent(bridge,Model(),chat_bridge=TestChat())
    marker=profile/'news-emit'
    try:
        agent.receive(dict(type='chat_message',source='local_ai_command',isSelf=True,eventId=name,
                           senderId=0,senderName='Tester',text='向东探索'))
        await agent.task
        submitted=await agent.program_tasks.submit(dict(operation='navigate_to',arguments=target))
        original=submitted['taskId']
        task=agent.program_tasks.records[original]
        marker.touch()
        async with asyncio.timeout(40):
            while not agent._game_events:
                snapshot=await asyncio.to_thread(reader.life_status)
                await agent.observe_life(snapshot,None)
                await agent.observe_life(snapshot,None)
                await asyncio.sleep(.05)
            await agent._news_review
            await agent.task
            await task['done'].wait()
        assert len(reviews)==1 and travel[0]>0,(reviews,travel)
        assert reviews[0]['scene']['activity']['actionId']==original,reviews
        notices=reviews[0]['notices']
        assert [e['source'] for e in notices]==['server_direct','server_broadcast'],notices
        assert notices[0]['text']=='private-to-ai' and 'evil presence' in notices[1]['text'],notices
        expected='cancelled' if name=='news-change' else 'completed'
        assert task['view']['status']==expected and len(agent.program_tasks.records)==1,task['view']
        assert bool(action_requests)==(name=='news-change')
        return dict(case=name,notices=notices,reviews=len(reviews),movementDuringReview=travel[0],navigation=expected)
    finally:
        marker.unlink(missing_ok=True)
        await agent.close()
        reader.close()
        bridge.close()
