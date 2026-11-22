"""Dedicated server E1 scenarios. Filesystem sampling does not drive the game task."""
import asyncio
import time
import uuid

from jsonschema import Draft202012Validator
from api_result_contracts import schema_for


async def verify_encounter(mode, bridge, lua, manager, scene, results, profile):
    assert 'server_ai_boss_encounter_v1' in bridge.hello['capabilities']
    if mode.startswith('whole-'):
        from native_boss_strategy import verify
        return await verify(mode[6:],lua,manager,scene,results,bridge,profile)
    await scene('boss-setup')
    configured = await lua.execute("""
local rows=api('inspect_inventory',{mode='slots',slots={0,2}}).data.inventory
local function role(r)
    return {hotbarKey=r.hotbarKey,expectedTypeId=r.typeId,inventoryToken=r.inventoryToken}
end
return api('configure_combat_loadout',{primary=role(rows[1]),secondary=role(rows[2])})
""")
    assert configured['value']['completed'], configured
    if mode.startswith('evasion-'):
        from native_direct_input import wait_file
        pattern = mode.removeprefix('evasion-')
        for arm in ('off', 'on'):
            seed = await scene(f'evasion-{pattern}-{arm}')
            task = uuid.uuid4().hex
            ack = bridge.task_request('start', task, operation='boss_encounter', arguments={'entityRef': seed['entityRef']},
                                      expiresAtUnixMs=int(time.time()*1000)+1800)
            assert ack['status']=='running', ack
            sample = await wait_file(profile / f'evasion-{pattern}-{arm}.json')
            active = bridge.task_request('action',task)
            assert active['status']=='running', active
            stopped = bridge.task_request('cancel',task)
            assert stopped['endEvidence']['inputsReleased'], stopped
            sample['progress']=active['task']['progress']
            sample['initial']=seed['initial']
            results[arm]=sample
            assert len(sample['samples'])==96 and sample['conflicts']==0, sample
            await scene('evasion-clear')
        assert results['off']['initial']==results['on']['initial']
        off,on=results['off'],results['on']
        assert off['progress']['evasionDecisions']==0 and on['progress']['evasionDecisions']>0
        assert off['lostLife']>0 and off['opportunities'] and on['opportunities']
        assert off['impacts'] and on['impacts'] and on['plannerWorst']>0
        if pattern=='trapped':
            assert on['progress']['noSafeRoute'] and on['lostLife']>0
        elif pattern=='beam':
            assert all(w['projectile']>0 for w in off['wounds'])
        else:
            assert on['lostLife']<off['lostLife'], (pattern,on['wounds'],off['wounds'])
        if pattern=='edge':
            assert max(r['Y'] for r in on['samples'])<=on['initial']['Y']+1
            assert max(r['Y'] for r in off['samples'])>off['initial']['Y']+16
        return
    if mode.startswith('members-'):
        if mode in ('members-pierce','members-sentry'):
            await scene('members-kit')
            staff=await lua.execute("""
local rows=api('inspect_inventory',{mode='slots',slots={0,2,3}}).data.inventory
local function role(r)
    return {hotbarKey=r.hotbarKey,expectedTypeId=r.typeId,inventoryToken=r.inventoryToken}
end
return api('configure_combat_loadout',CONFIG)
""".replace('CONFIG', '{primary=role(rows[2])}' if mode=='members-pierce' else '{primary=role(rows[1]),summon=role(rows[3])}'))
            assert staff['value']['completed']
            if mode=='members-sentry':
                for _ in range(80):
                    state=await scene('audit')
                    if state['summons'] and state['summons'][0]['authorized'] and state['selectedItem']==0:break
                    await asyncio.sleep(.05)
                else:raise AssertionError('No verified sentry deployment')
        await verify_members(mode,bridge,scene,results)
        return
    root = await scene('boss-short' if mode == 'win' else 'boss-seed')
    args = {'entityRef': root['entityRef']}
    if mode == 'win':
        submitted = await lua.execute("""
local roots=api('scan_entities',{category='npc',radius=640,typeIds={4}}).data.items
return api('submit_task',{operation='boss_encounter',arguments={entityRef=roots[1].details.entityRef}})
""")
        assert submitted['status'] == 'completed', submitted
        task = submitted['value']
        assert task['action']['targetTask'] == args
        view = await manager.invoke('await_task', {'taskId': task['taskId'], 'milliseconds': 25000})
        results['win'] = view
        assert view['completed'], view
        Draft202012Validator(schema_for('await_task')).validate(view)
        proof = view['result']['task']['result']['evidence']
        assert proof['encounterId'] == task['taskId'] and proof['lastHit']['Defeated']
        return
    if mode.startswith('regroup-'):
        args['regroupTeammateRef']='a'*32
    if mode == 'timeout':
        args['durationSeconds'] = 5
    task_id = uuid.uuid4().hex
    started = time.monotonic()
    # No TaskCoordinator monitor in this scenario. After the ACK no query/start/
    # renew is sent over IPC until the observation interval has elapsed.
    ack = bridge.task_request('start', task_id, operation='boss_encounter', arguments=args,
                              expiresAtUnixMs=int(time.time()*1000)+1800)
    assert ack['status'] == 'running'
    results['ack'] = ack
    if mode == 'long':
        await asyncio.sleep(36)
    deadline = time.monotonic() + 65
    while time.monotonic() < deadline:
        sample = await scene('boss-audit')
        impacts = [h for h in sample['hitTrace'] if h['nativeDamage'] and h['entityRef'] == root['entityRef']]
        impacts += [h for h in sample['projectileHits'] if h['nativeDamage'] and h['target'] == root['entityRef']]
        if impacts and (mode != 'long' or max(h['tick'] for h in impacts) - min(h['tick'] for h in impacts) > 1800):
            break
        if not sample['running'] and time.monotonic()-started > 2:
            raise AssertionError(bridge.task_request('action', task_id))
        await asyncio.sleep(.15)
    else:
        raise AssertionError('Expected native hits missing')
    results['beforeStop'] = sample
    results['secondsSinceStart'] = time.monotonic()-started
    if mode.startswith('regroup-'):
        await scene('summon-death')
        await asyncio.sleep(.25)
        waiting = bridge.task_request('action', task_id)
        assert waiting['task']['phase']=='waiting_for_respawn', waiting
        sample = await scene('boss-audit')
        assert sample['dead'] and sample['released'] and not sample['running'], sample
        if mode=='regroup-cancel':
            end = bridge.task_request('cancel', task_id)
            assert end['status']=='cancelled' and end['endEvidence']['inputsReleased'], end
        else:
            deadline=time.monotonic()+100
            while time.monotonic()<deadline:
                sample = await scene('boss-audit')
                if not sample['dead']:break
                await asyncio.sleep(.35)
            from control import BridgeError
            from server_ai import ServerAiBridge
            port=bridge.sock.getpeername()[1]
            try:
                bridge.task_request('action', task_id)
            except BridgeError as error:
                assert 'stale_binding' in str(error),error
            else:
                raise AssertionError('Old connection should expire on natural respawn')
            assert bridge._retired_binding and not bridge._closed
            observer=ServerAiBridge(port)
            try:
                end=observer.command('query_atomic',operation='query_combat',arguments={})['data']['data']['action']
                assert end['actionId']!=task_id and end['generation']==ack['generation']+1, end
                assert end['task']['result']['evidence']['previousTaskId']==task_id, end
            finally:
                observer.close()
            assert not sample['dead'] and end['reason'] in ('boss_original_root_gone','teammate_session_unavailable'), end
        results.update(waiting=waiting,terminal=end)
        return
    if mode == 'long':
        active = bridge.task_request('action', task_id)
        results['running'] = active
        assert active['status'] == 'running' and sample['running'] and results['secondsSinceStart'] > 35
        assert active['task']['progress']['lastHit']['GameUpdate'] in {h['tick'] for h in impacts} or active['task']['progress']['lastHit']['GameUpdate'] >= sample['tick']
        terminal = bridge.task_request('cancel', task_id)
        assert terminal['status'] == 'cancelled' and terminal['endEvidence']['inputsReleased']
        results['terminal'] = terminal
    elif mode == 'timeout':
        await asyncio.sleep(5.1)
        terminal = bridge.task_request('action', task_id)
        results['terminal'] = terminal
        assert terminal['status'] == 'timeout' and terminal['reason'] == 'execution_deadline' and terminal['endEvidence']['inputsReleased']
    elif mode == 'disconnect':
        bridge.close()
    elif mode in ('generation','death'):
        results['boundary'] = await scene('summon-' + mode)
    else:
        raise AssertionError(mode)
    await asyncio.sleep(.35)
    first = await scene('boss-audit')
    await asyncio.sleep(.45)
    last = await scene('boss-audit')
    results['release'] = [first, last]
    assert first['released'] and last['released'] and not first['running'] and not last['running']
    assert last['scopeReleased'] and first['frames'] == last['frames'] and first['life'] == last['life']
    assert first['tick'] < last['tick']
    if mode == 'generation':
        assert last['generation'] > sample['generation']
    if mode == 'death':
        assert results['boundary']['dead']


async def verify_members(mode,bridge,scene,results):
    seed=await scene(mode)
    results['initial']=await scene('members-state')
    task=uuid.uuid4().hex
    bridge.task_request('start',task,operation='boss_encounter',arguments={'entityRef':seed['entityRef']},
                        expiresAtUnixMs=int(time.time()*1000)+1800)

    async def until(predicate):
        for _ in range(180):
            action=bridge.task_request('action',task)
            progress=action['task'].get('progress') or {}
            if action['status']!='running':
                results['terminal']=action
                raise AssertionError((action['status'],action['reason'],progress))
            if predicate(progress):
                return progress
            await asyncio.sleep(.05)
        raise AssertionError(progress)

    if mode=='members-ambiguous':
        await asyncio.sleep(.3)
        action=bridge.task_request('action',task)
        results['refusal']=action
        assert action['reason']=='boss_twins_pair_ambiguous' and action['status']=='failed',action
        return
    if mode=='members-destroyers':
        proof=await until(lambda p:any(i['hit'].get('Projectile') for i in p.get('impacts',[])))
        raw=await scene('members-state')
        results.update(proof=proof,raw=raw)
        arrow=next(i for i in proof['impacts'] if i['hit'].get('Projectile'))
        assert proof['memberCount']==82 and raw['foreignLife']==80000
        assert arrow['member']['TypeId']==135 and arrow['carrier']['EntityRef']==seed['entityRef']
        assert arrow['aim']==arrow['member'] and arrow['currentTarget']!=arrow['member']
        assert arrow['heldType']==24 and arrow['hit']['Projectile']['Weapon']==39
        assert arrow['amount']==arrow['hit']['DamageDone']==arrow['hit']['LifeBefore']-arrow['hit']['LifeAfter']
        assert arrow['memberAfter']==arrow['hit']['LifeAfter']
        assert proof['verifiedDamage']==results['initial']['life']-proof['impacts'][-1]['hit']['LifeAfter']
        assert proof['verifiedHits']==len(proof['impacts']) and proof['verifiedDamage']==sum(i['amount'] for i in proof['impacts'])
        assert all(i['carrier']['EntityRef']==seed['entityRef'] for i in proof['impacts'])
        assert any(h['nativeDamage'] and h['target']==arrow['member']['EntityRef'] and h['held']==24 for h in raw['projectileHits'])
    elif mode=='members-pierce':
        def pierced(p):
            shots={}
            for impact in p.get('impacts',[]):
                bolt=impact['hit'].get('Projectile')
                if bolt:
                    key=(bolt['Identity'],bolt['Launched'])
                    shots.setdefault(key,set()).add(impact['member']['EntityRef'])
            return any(len(struck)>=2 for struck in shots.values())
        proof=await until(pierced)
        raw=await scene('members-state')
        results.update(proof=proof,raw=raw)
        assert raw['foreignLife']==80000 and all(i['carrier']['EntityRef']==seed['entityRef'] for i in proof['impacts'])
    elif mode=='members-sentry':
        proof=await until(lambda p:any(i['hit'].get('Summon',{}).get('ParentIdentity') is not None for i in p.get('impacts',[])))
        raw=await scene('members-state')
        results.update(proof=proof,raw=raw)
        assert raw['foreignLife']==80000
        child=next(i for i in proof['impacts'] if 'Summon' in i['hit'])
        assert child['carrier']['EntityRef']==seed['entityRef'] and child['heldType']==24
    elif mode=='members-reuse':
        for _ in range(90):
            raw=await scene('members-state')
            if raw['crossedReplacement']:break
            await asyncio.sleep(.05)
        assert raw['crossedReplacement'],raw
        await asyncio.sleep(.25)
        raw=await scene('members-state')
        results['raw']=raw
        old=raw['shots'][0]['target']
        new=raw['replacement']
        assert new['entityRef']!=old and int(old.split(':')[-2])==new['slot']
        assert not raw['projectileHits'] and new['life']==80000 and raw['foreignLife']==80000
    elif mode=='members-moon':
        p=await until(lambda p:p.get('memberCount',0)>=4 and p['target']['TypeId'] in (396,397))
        raw=await scene('members-state')
        results.update(proof=p,raw=raw)
        assert p['rootDeathUpdate'] is None and raw['dontTakeDamage'] and raw['active']
    else:
        threshold={'members-eater':30,'members-twins':2,'members-skeleton':3}[mode]
        results['before']=await until(lambda p:p.get('memberCount',0)>=threshold)
        results['cut']=await scene('members-cut')
        p=await until(lambda p:p.get('headCount')==2 if mode=='members-eater' else p.get('memberCount')==threshold-1)
        assert p['rootDeathUpdate'] is None
        results['after']=p
        if mode=='members-twins':
            assert p['target']['TypeId']==126
            await scene('members-cut')
            for _ in range(25):
                terminal=bridge.task_request('action',task)
                if terminal['status']!='running':break
                await asyncio.sleep(.05)
            results['terminal']=terminal
            from atomic_operations import verified_boss_deaths
            proof=terminal['task']['result']['evidence']
            assert terminal['status']=='completed' and terminal['endEvidence']['inputsReleased']
            assert verified_boss_deaths(proof)
            return
    stopped=bridge.task_request('cancel',task)
    assert stopped['status']=='cancelled' and stopped['endEvidence']['inputsReleased']
