"""E4 strategy through the regular Lua task mailbox, with no LLM or combat control loop."""
import asyncio
import time
import uuid
from native_direct_input import wait_file
import atomic_operations as atomic
from jsonschema import Draft202012Validator
from api_result_contracts import schema_for


async def verify(case, lua, manager, scene, results, bridge, profile):
    async def call(code):
        response = await lua.execute(code)
        assert response['status']=='completed', response
        return response['value']

    arm=None
    if case.startswith('arena-'):
        _,case,arm=case.split('-')
        assert ((case in ('twins','destroyer','moon') and arm in ('flat','platform','shield'))
                or (case=='eye' and arm in ('bare','cloud','wings')))
    await scene('whole-kit-'+arm if arm else 'whole-kit')
    example = await call("return api('help',{name='configure_combat_loadout'}).example")
    results['config'] = await call(example)
    loadout = await call("return api('query_combat_loadout',{}).data")
    assert loadout['primary']['available'] and loadout['secondary']['available'], loadout
    fixture = await scene('whole-'+case)
    results['initial'] = fixture
    assert fixture['life']==fixture['lifeMax']
    if case not in ('sample','retreat','cancel'):
        if arm:await wait_file(profile/'arena-ready.json')
        discovery=bridge.command('query_atomic',operation='discover_bosses',arguments={})['data']
        results['discovery']=discovery
        roots=[r for r in discovery['data']['candidates'] if r['admissible']]
        assert roots,discovery
        ident=uuid.uuid4().hex
        ack=bridge.task_request('start',ident,operation='boss_encounter',arguments={'entityRef':roots[0]['entityRef']},
                                expiresAtUnixMs=int(time.time()*1000)+1800)
        results['ack']=ack
        if arm:
            (profile/'arena-release').touch()
            sample=await wait_file(profile/'arena-result.json',90)
            results['arena']=sample
            action=bridge.task_request('action',ident)
            if action['status']=='running':action=bridge.task_request('cancel',ident)
            assert action['endEvidence']['inputsReleased'],action
            assert sample['arenaArm']==arm and sample['arenaHits']>0 and sample['arenaDamage']>0,sample
            if arm in ('bare','cloud','wings'):
                assert sample['updates']==1200,sample
                assert (sample['cloudStarts']>0)==(arm=='cloud') and (sample['cloudPlans']>0)==(arm=='cloud'),sample
                assert (sample['wingSpent']>0)==(arm=='wings') and (sample['wingPlans']>0)==(arm=='wings'),sample
                assert (sample['abilityLandings']>0)==(arm!='bare'),sample
            elif arm=='flat':
                assert not sample['changes'],sample
            else:
                assert sample['openingDistance']>0 and any(c['jumpSinceSupport'] and c['to']>c['from'] for c in sample['changes']),sample
            assert sample['updates']==1200 or sample['dead'] or action['status']=='completed',sample
            return
        # Observe only the game-thread terminal file until the fight is over.
        notice=await wait_file(profile/'whole-terminal.json',310)
        assert notice['id']==ident
        action=bridge.task_request('action',ident)
        result=await atomic.terminal(bridge,action,action['generation'])
        results['result']={'action':action,'result':result}
        assert action['endEvidence']['inputsReleased'] and action['reason']==notice['reason'],action
        if case in ('destroyer','twins','moon') and action['reason']=='actor_not_alive':
            outcome=await wait_file(profile/'boss-finished.json')
            assert outcome['dead'] and outcome['active'] and not result['completed']
            assert action['task']['result']['evidence']['verifiedDamage']>0
            results['outcome']='actor_defeated'
            return
        assert result['completed'],(case,action['reason'],action.get('reasonDetail'))
        proof=result['task']['result']['evidence']
        assert proof['verifiedHits'] and proof['deathProof']
        results['outcome']='boss_defeated'
        return
    issued=await call("""
local scan=api('discover_bosses',{})
for _,candidate in ipairs(scan.data.candidates) do
    if candidate.admissible then
        local task=api('submit_task',{operation='boss_encounter',arguments={entityRef=candidate.entityRef}})
        return {discovery=scan,task=task}
    end
end
return {discovery=scan}
""")
    discovery=results['discovery']=issued['discovery']
    Draft202012Validator(schema_for('discover_bosses')).validate(discovery)
    task=issued.get('task')
    assert task,discovery
    ident=task['taskId']
    if case in ('retreat','cancel'):
        await asyncio.sleep(.1)
        active=await call('return api("query_task",{taskId="'+ident+'"})')
        assert active['status']=='running',active
        if case=='cancel':
            await lua.execute('return api("cancel_task",{taskId="'+ident+'"})')
        else:
            await lua.execute("""
local data=api('observe_terrain',{}).data
local t=data.terrain
local col=math.floor(data.state.centerWorldPixels.x/16)+15
local start=math.floor(data.state.centerWorldPixels.y/16)
for y=start,t.originY+#t.rows-1 do
    if string.sub(t.rows[y-t.originY+1],col-t.originX+1,col-t.originX+1)=='#' then
        return api('cancel_task',{taskId='TASK',retreatTo={feetX=col*16+8,feetY=y*16}})
    end
end
error('No observed destination')
""".replace('TASK',ident))
    # Local event set by the regular receipt monitor. No further Lua calls or control requests.
    await asyncio.wait_for(manager.records[ident]['done'].wait(),320)
    view=manager.records[ident]['view']
    results['result']=view
    action=view['action']
    assert action['endEvidence']['inputsReleased'],action
    Draft202012Validator(schema_for('query_task')).validate(view)
    if case in ('retreat','cancel'):
        assert view['result']['valid'],view['result']
        assert view['status']=='cancelled' and action['reason']==('explicit_cancel' if case=='cancel' else 'boss_retreat_verified'),action
    else:
        assert view['completed'],(case,view['status'],action['reason'],action.get('reasonDetail'))
        proof=view['result']['task']['result']['evidence']
        if case=='sample':
            assert proof['peakMemberCount']==3 and proof['customTrajectorySamples']>0 and proof['predictionFallbackSamples']>0 and discovery['data']['extensionConflicts']==1,proof
        assert proof['deathProof'] and proof['verifiedHits']>0 and proof['verifiedDamage']>0
