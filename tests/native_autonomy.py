"""Bounded copied-world acceptance. The command source is a test client."""
import argparse
import asyncio
import json
import sys
from pathlib import Path

sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'python'))
from native_direct_input import wait_file
from native_server_ai_agent import ChatSink
from agent import Agent
from autonomous_mode import AutonomousMode, INSTRUCTIONS, REVIEW
from control import autonomy_version
from llm import LLMClient
from server_ai import ServerAiBridge
import atomic_operations as atomic


class FixtureModel:
    def __init__(self, travel_review=None):
        self.travel_review=travel_review
        self.reviews=[]
        self.samples=[]
        self.travelling=False
    async def complete(self,messages,tools):
        if messages[0]['content']==REVIEW:
            event=json.loads(messages[1]['content'])
            self.reviews.append(event)
            new_crystal=any(h['kind']=='life_crystal' and h['anchor']['x']>self.samples[0]['resources']['bounds']['maxX']
                            for sample in event['samples'] for h in sample['findings'])
            choice=self.travel_review if len(self.samples)==1 and self.travel_review and new_crystal else 'continue'
            return dict(tool_calls=[dict(function=dict(name='review_discovery',arguments=json.dumps(dict(
                choice=choice,reason='reviewed the newly scanned Tiles'))))])
        if messages[0]['content'] != INSTRUCTIONS:
            # Run the normal manual Agent / Lua / native reply path.
            if any(m['role']=='tool' for m in messages):
                return dict(role='assistant',content='Selected.')
            return dict(role='assistant',content=None,tool_calls=[dict(type='function',id='manual-tool',function=dict(
                name='execute_lua',arguments=json.dumps(dict(source="return api('select_hotbar_slot',{slot=1})"))))])
        observation=json.loads(messages[1]['content'])
        self.samples.append(observation)
        candidates=(observation['nearbyBosses'] or {}).get('candidates',[])
        for boss in candidates:
            if boss['admissible']:
                return dict(tool_calls=[dict(id='boss',function=dict(name='submit_task',arguments=json.dumps(dict(
                    objective='join the active encounter',operation='boss_encounter',arguments=dict(entityRef=boss['entityRef'])))))])
        if self.travel_review and len(self.samples)==1:
            return dict(tool_calls=[dict(function=dict(name='navigate_to',arguments=json.dumps(dict(
                objective='explore east past the buried crystals',x=int(observation['player']['position']['x']+250),
                y=int(observation['localScene']['actor']['feetY'])))))])
        if self.travel_review and len(self.samples)==3 and observation.get('originalExploration'):
            route=observation['originalExploration']
            return dict(tool_calls=[dict(function=dict(name=route['operation'],arguments=json.dumps(
                dict(**route['arguments'],objective=route['objective'],originalRoute='replace'))))])
        if self.travelling:
            target=observation['player']['position']['x']-200
            return dict(tool_calls=[dict(id='task',function=dict(name='submit_task',arguments=json.dumps(dict(
                objective='survey the western ground',operation='navigate_to',arguments=dict(x=int(target),y=int(observation['localScene']['actor']['feetY']))))))])
        if len(self.samples)>1:
            discovery = observation.get('travelDiscovery') or {}
            found = [h for sample in discovery.get('samples',[]) for h in sample['findings']]
            if (discovery.get('choice')=='investigate' or any(hit['kind']=='life_crystal' for hit in found)
                    or any(hit['kind']=='life_crystal' and hit['status']=='complete' and hit['freshness']=='new'
                           for hit in observation['resources']['items'])):
                return dict(tool_calls=[dict(id='prepare',function=dict(name='select_hotbar_slot',arguments=json.dumps(
                    dict(objective='prepare the pick for the newly discovered life crystal',slot=2))))])
            return dict(content='rest')
        left=observation['player']['position']['x']
        return dict(tool_calls=[dict(id='move',function=dict(name='navigate_to_x',
            arguments=json.dumps(dict(objective='inspect the eastern edge',targetX=left+80))))])


async def run(args):
    profile=args.profile
    assert (profile/'isolated-action-fixture').is_file()
    async with asyncio.timeout(30):
        while not (profile/'action-fixture.json').exists():
            await asyncio.sleep(.1)
    bridge,controller=ServerAiBridge(args.port),ServerAiBridge(args.port)
    llm=LLMClient() if args.live_model else FixtureModel(args.travel_review)
    agent=Agent(bridge,llm,chat_bridge=ChatSink())
    model_calls=[]
    complete=llm.complete
    async def record(messages,tools):
        if messages[0]['content']==INSTRUCTIONS and len(messages)==2:
            report.setdefault('decisions',[]).append(json.loads(messages[1]['content']))
        start=asyncio.get_running_loop().time()
        reply=await complete(messages,tools)
        model_calls.append(dict(response=reply,seconds=asyncio.get_running_loop().time()-start))
        return reply
    llm.complete=record
    mode=agent.autonomy=AutonomousMode(agent,controller)
    report=dict(binding=bridge.binding,model='configured' if args.live_model else 'scripted',playerMessagesBeforeFirstDecision=0)
    try:
        assert not (await mode.control_request('status'))['enabled']
        await mode.command('on')
        async with asyncio.timeout(180):
            decisions=0
            while decisions < 2 or (args.live_model and decisions < 5 and
                                    not any(c['receipt'].get('completed') for c in report['cycles'])):
                life=await asyncio.to_thread(controller.life_status)
                await agent.observe_life(life,lambda: ServerAiBridge(args.port))
                await mode.pulse(life)
                if mode.worker is not None:
                    await mode.worker
                    if mode.error:
                        raise RuntimeError(mode.error)
                    decisions+=1
                    report.setdefault('cycles',[]).append(dict(phase=mode.phase,objective=mode.objective,receipt=mode.progress))
                    mode.worker=None
                await asyncio.sleep(.25)
        if args.travel_review:
            movement=report['cycles'][0]['receipt']
            before,after=llm.samples[:2]
            reviews=[s for s in llm.reviews if s['actionId']==movement['actionId']]
            assert any(h['anchor']['x']>before['resources']['bounds']['maxX']
                       for r in reviews for s in r['samples'] for h in s['findings']),reviews
            assert movement['inputsReleased'] and after['previousReceipt']['actionId']==movement['actionId'],movement
            if args.travel_review=='investigate':
                assert movement['status']=='cancelled' and after['originalExploration']['arguments']==movement['arguments']
                assert report['cycles'][1]['receipt']['operation']=='select_hotbar_slot' and mode.progress['completed']
                async with asyncio.timeout(45):
                    while mode.worker is None:
                        await mode.pulse(await asyncio.to_thread(controller.life_status))
                        await asyncio.sleep(.05)
                    await mode.worker
                assert llm.samples[-1]['originalExploration']==after['originalExploration']
                assert mode.progress['completed'] and mode.progress['arguments']==movement['arguments'],mode.progress
                assert mode.progress['actionId']!=movement['actionId']
                report['resumed']=mode.progress
            else:
                assert movement['completed'],movement
            report.update(reviews=reviews,passed=True)
            await mode.command('off')
            return
        if not args.live_model:
            first,second=llm.samples[:2]
            def findings(obs): return obs['resources']['items']
            assert any(h['kind']=='chest' and h['status']=='complete' and len(h['tiles'])==4 for h in findings(first))
            assert any(h['kind']=='life_crystal' and h['status']=='incomplete' for h in findings(first))
            during=[h for review in llm.reviews for sample in review['samples'] for h in sample['findings']]
            assert any(h['kind']=='life_crystal' and h['freshness']=='new' for h in findings(second)+during)
            navigation=report['cycles'][0]['receipt']
            assert navigation.get('completed') and navigation.get('inputsReleased'),navigation
            assert mode.progress['operation']=='select_hotbar_slot' and mode.progress['completed'],mode.progress
            report['autonomousNavigation']=navigation
        assert len(report['decisions'])>=2 and len(model_calls)>=2 and any(c['receipt'].get('completed') for c in report['cycles']),report
        token=autonomy_version.set(mode.version)
        try:
            selected=await atomic.dispatch(agent.bridge,'select_hotbar_slot',{'slot':5})
            assert selected['completed'],selected
            reject=await atomic.dispatch(agent.bridge,'use_item',{})
            report['bossInitiation']=dict(reason=reject['reason'],status=reject['status'])
            assert reject['reason']=='autonomy_progression_trigger',reject
        finally:
            autonomy_version.reset(token)
        if not args.live_model:
            await takeover_and_death(agent,mode,controller,llm,profile,args.port,report)
            await active_encounter(agent,mode,controller,profile,report)
            await team_recovery(agent,mode,controller,args,report)
        await mode.command('off')
        state=await mode.control_request('status')
        assert not state['enabled'] and state['released']
        report['shutdown']=state
        report['passed']=True
    finally:
        report['modelCalls']=model_calls
        (profile/'autonomy.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
        await agent.close()
        controller.close();agent.bridge.close()
        if args.live_model: await llm.close()


async def takeover_and_death(agent,mode,controller,model,profile,port,report):
    async def tick():
        life=await asyncio.to_thread(controller.life_status)
        await agent.observe_life(life,lambda:ServerAiBridge(port))
        await mode.pulse(life)
        await asyncio.sleep(.05)
        return life

    async def travelling():
        model.travelling=True
        mode.next_check=0
        mode.force_decision=True
        before=set(mode.mailbox.records) if mode.mailbox else set()
        async with asyncio.timeout(15):
            while True:
                await tick()
                if mode.mailbox:
                    for key,r in mode.mailbox.records.items():
                        if (key not in before and r['view']['status']=='running'
                                and (r['view'].get('action',{}).get('task') or {}).get('currentChild')):
                            return r

    task=await travelling()
    calls=len(model.samples)
    await mode.manual(dict(type='chat_message',isSelf=True,source='local_ai_command',eventId='isolated-local-ai-task',
                           text='Select the first hotbar slot.',senderId=0,senderName='Fixture player'))
    model.travelling=False
    async with asyncio.timeout(30):
        while agent.task is None or not agent.task.done() or not task['done'].is_set():
            await tick()
    receipt=task['view']['result']
    assert receipt['status']=='cancelled' and receipt['inputsReleased'] and agent.bridge.reply_receipts,receipt
    assert receipt['endEvidence']['inputsAcquired'],receipt
    report['manualTakeover']=dict(actionId=receipt['actionId'],release=receipt['endEvidence'],
                                  playerReply=agent.bridge.reply_receipts[-1])
    async with asyncio.timeout(12):
        while len(model.samples)==calls: await tick()
    task=await travelling()
    binding=dict(agent.bridge.binding)
    (profile/'autonomy-test-death').touch()
    model.travelling=False
    async with asyncio.timeout(75):
        while not any(s['binding']['generation']>binding['generation'] for s in model.samples):
            await tick()
    recovered=model.samples[-1]
    assert recovered['objective']=='survey the western ground' and recovered['life']['generation']==binding['generation']
    assert recovered['binding']==dict(binding,generation=binding['generation']+1)
    assert (await mode.control_request('status'))['enabled']
    assert task['view']['result']['inputsReleased'],task['view']
    assert recovered['previousReceipt']['actionId']==task['view']['taskId']
    assert recovered['previousReceipt']['inputsReleased'],recovered['previousReceipt']
    report['respawn']=dict(previousAction=task['view']['taskId'],oldRelease=task['view']['result']['endEvidence'],
                           death=recovered['life'],binding=recovered['binding'],objective=recovered['objective'])
    task=await travelling()
    await mode.command('off')
    async with asyncio.timeout(8):
        while not (await mode.control_request('status'))['released'] or not task['done'].is_set(): await tick()
    assert task['view']['result']['inputsReleased'] and task['view']['result']['status']=='cancelled'
    report['offDuringAction']=task['view']['result']['endEvidence']


async def active_encounter(agent,mode,controller,profile,report):
    async def scene(name):
        ready=profile/'combat-ready.json'
        ready.unlink(missing_ok=True)
        temporary=profile/'combat-request.tmp'
        temporary.write_text(name)
        temporary.replace(profile/'combat-request')
        return await wait_file(ready)

    await scene('boss-setup')
    rows=(await atomic.dispatch(agent.bridge,'inspect_inventory',dict(mode='slots',slots=[0,2])))['data']['inventory']
    def role(row):
        return dict(hotbarKey=row['hotbarKey'],expectedTypeId=row['typeId'],inventoryToken=row['inventoryToken'])
    configured=await atomic.dispatch(agent.bridge,'configure_combat_loadout',dict(primary=role(rows[0]),secondary=role(rows[1])))
    assert configured['completed'],configured
    seed=await scene('boss-short')
    await mode.command('on')
    await mode.pulse(await asyncio.to_thread(controller.life_status))
    await asyncio.wait_for(mode.worker,35)
    assert not mode.error,mode.error
    assert mode.progress.get('operation')=='boss_encounter' and mode.progress['completed'],mode.progress
    view=mode.mailbox.records[mode.progress['actionId']]['view']
    assert view['action']['targetTask']['entityRef']==seed['entityRef']
    evidence=view['result']['task']['result']['evidence']
    assert evidence['lastHit']['Defeated'] and view['result']['inputsReleased'],evidence
    report['activeBoss']=dict(source='isolated fixture spawned a live native Eye',entityRef=seed['entityRef'],
                              actionId=mode.progress['actionId'],lastHit=evidence['lastHit'],release=True)


async def team_recovery(agent,mode,controller,args,report):
    async def fixture(op,**fields):
        result=args.profile/'observer-response.json'
        result.unlink(missing_ok=True)
        temp=args.profile/'observer-request.tmp'
        temp.write_text(json.dumps(dict(op=op,**fields)))
        temp.replace(args.profile/'observer-request.json')
        response=await wait_file(result)
        assert 'error' not in response,response
        return response

    peers=[]
    try:
        for _ in range(4):
            peers.append(await asyncio.open_connection('127.0.0.1',args.game_port))
        ports=[w.get_extra_info('sockname')[1] for _,w in peers]
        async with asyncio.timeout(12):
            while (await fixture('setup',ports=ports)).get('waitingForAccept'):
                await asyncio.sleep(.05)
        seed=await fixture('autonomy-boss')
        generation=agent.bridge.binding['generation']

        async def team_task(expected):
            async with asyncio.timeout(90):
                while True:
                    life=await asyncio.to_thread(controller.life_status)
                    await agent.observe_life(life,lambda:ServerAiBridge(args.port))
                    await mode.pulse(life)
                    for obs in reversed(agent.llm.samples):
                        if obs['binding']['generation']!=expected or not obs['activeTeamBosses']:
                            continue
                        ref=obs['activeTeamBosses'][0]['entityRef']
                        if mode.mailbox and mode.mailbox.bridge is agent.bridge:
                            for record in mode.mailbox.records.values():
                                view=record['view']
                                if (view['operation']=='boss_encounter' and view.get('action',{}).get('targetTask',{}).get('entityRef')==ref
                                    and view['status']=='running'):
                                    return obs,record
                        break
                    await asyncio.sleep(.05)
        before,old=await team_task(generation)
        await fixture('autonomy-death')
        after,new=await team_task(generation+1)
        assert old['done'].is_set() and old['view']['result']['inputsReleased'] and old['view']['status']=='cancelled',old['view']
        assert old['view']['taskId']!=new['view']['taskId']
        previous_event=before['activeTeamBosses'][0]
        fresh_event=after['activeTeamBosses'][0]
        assert previous_event['teammateRef']==fresh_event['teammateRef']
        assert previous_event['entityRef']!=fresh_event['entityRef']
        assert previous_event['entityRef'].endswith(seed['spawn']) and fresh_event['entityRef'].endswith(seed['spawn'])
        report['teamBossRecovery']=dict(fixture=seed,oldTask=old['view']['taskId'],oldRelease=old['view']['result']['endEvidence'],
            oldEvent=before['activeTeamBosses'],freshEvent=after['activeTeamBosses'],newTask=new['view']['taskId'],
            newBinding=after['binding'],decision=mode.objective)
        await mode.command('off')
        await asyncio.wait_for(new['done'].wait(),8)
        assert new['view']['result']['inputsReleased'],new['view']
    finally:
        for _,writer in peers:
            writer.close()
            await writer.wait_closed()


if __name__=='__main__':
    parser=argparse.ArgumentParser()
    parser.add_argument('--port',type=int,required=True)
    parser.add_argument('--game-port',type=int,required=True)
    parser.add_argument('--profile',type=Path,required=True)
    parser.add_argument('--travel-review',choices=['continue','investigate'])
    parser.add_argument('--live-model',action='store_true')
    asyncio.run(run(parser.parse_args()))
