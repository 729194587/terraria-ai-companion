"""Copied-world Dedicated Server combat through Lua, IPC and native Player updates."""
import argparse
import asyncio
import json
import os
from pathlib import Path
from jsonschema import Draft202012Validator

from native_direct_input import wait_file
from lua_runtime import LuaRuntime
from program_tasks import coordinator
from server_ai import ServerAiBridge
from api_result_contracts import schema_for


async def verify(args):
    profile = args.profile
    await wait_file(profile / 'action-fixture.json', 90)
    bridge = await asyncio.to_thread(ServerAiBridge, args.port)
    manager = coordinator(bridge)
    lua = LuaRuntime(bridge, evidence_root=profile / 'lua')
    results = {}

    async def scene(mode):
        ready = profile / 'combat-ready.json'
        ready.unlink(missing_ok=True)
        temporary = profile / 'combat-request.tmp'
        temporary.write_text(mode)
        temporary.replace(profile / 'combat-request')
        return await wait_file(ready)

    async def observe():
        data = await asyncio.to_thread(bridge.command, 'query_atomic', operation='query_combat', arguments={})
        return data['data']['data']['action']

    async def wait_for(predicate):
        for _ in range(240):
            action = await observe()
            if action and predicate(action):
                return action
            await asyncio.sleep(.05)
        raise AssertionError(action)

    def completed(view):
        assert view['completed'] and view['valid'], (view['status'], view['action']['task'])
        Draft202012Validator(schema_for('await_task')).validate(view)
        proof = view['result']['task']['result']['evidence']
        assert proof['lastHit']['Defeated'] and proof['lastHit']['LifeAfter'] <= 0, view
        assert view['result']['endEvidence']['inputsReleased'], view
        return proof

    async def defense_start(mode='defense'):
        pair = await scene(mode)
        first = pair['first']['entityRef']
        action = await wait_for(lambda a: a['targetTask']['entityRef'] == first
                                and (a['task'].get('progress') or {}).get('itemChecks', 0) > 0)
        return pair, action['actionId']

    async def defense_finish(action_id, expected, allowed_survivor=None, magic=False):
        result = await manager.invoke('await_task', {'taskId': action_id, 'milliseconds': 30000})
        assert result['completed'] and result['valid'], (result['status'], result['action']['task'])
        Draft202012Validator(schema_for('await_task')).validate(result)
        proof = result['result']['task']['result']['evidence']
        assert result['result']['reason'] == 'combat_threats_cleared' and result['result']['inputsReleased']
        assert result['action']['actionId'] == action_id, result
        encounters = proof['encounters']
        assert {entry['Target']['EntityRef'] for entry in encounters} == set(expected), encounters
        sample = await audit(proof, magic)
        for encounter in encounters:
            reference = encounter['Target']['EntityRef']
            assert encounter['Reason'] == expected[reference], encounter
            # Magic encounters are proved by verified projectile hits; melee
            # encounters by the native strike trace.
            key = 'projectileHits' if magic else 'hitTrace'
            field = 'target' if magic else 'entityRef'
            rows = [row for row in sample[key] if row[field] == reference]
            if encounter['Reason'] == 'combat_target_defeated':
                assert rows and encounter['LastHit']['Defeated'], (encounter, rows)
                if magic:
                    assert rows[-1]['tick'] > rows[-1]['launched']
                assert rows[-1]['tick'] == encounter['LastHit']['GameUpdate'], (encounter, rows[-1])
                assert rows[-1]['life'] == encounter['LastHit']['LifeAfter'] <= 0, (encounter, rows[-1])
            else:
                assert encounter['LastHit'] is None or not encounter['LastHit']['Defeated']
        survivors = {n['entityRef'] for n in sample['survivors']}
        allowed = {allowed_survivor} if allowed_survivor else set()
        if proof.get('summon'):
            # A bystander can take native damage; it must never enter the receipt.
            assert survivors <= allowed and all(hit['target'] in expected for hit in sample['projectileHits']), sample
        else:
            assert survivors == allowed, sample
        if not magic:
            assert len({hit['tick'] for hit in sample['hitTrace']}) == len(sample['hitTrace']), sample
        if magic:
            assert sample['manaPaidWithoutShot'] == 0, sample
        return {'result': result, 'audit': sample}

    async def audit(proof, magic=False):
        sample = await scene('audit')
        assert sample['frames'] == proof['itemChecks'] and sample['hitViolations'] == 0, (sample, proof)
        if magic:
            assert sample['projectileHits'], (sample, proof)
        else:
            assert sample['hits'] > 0, (sample, proof)
        assert sample['inputScopeReleased'] and sample['attackReleased'], sample
        return sample

    async def verify_summons(lifecycle=None):
        async def attack(enemy):
            launch = await manager.invoke('submit_task', {'operation': 'combat_attack',
                                          'arguments': {'entityRef': enemy['entityRef']}})
            assert 'taskId' in launch, launch
            return launch['taskId']

        async def autonomous(family, task, terminal):
            enemy = await scene('autonomous-target')
            for _ in range(150):
                sample = await scene('audit')
                impacts = [hit for hit in sample['summonDamage'] if hit['target'] == enemy['entityRef']]
                if impacts:
                    break
                await asyncio.sleep(.03)
            assert impacts and not any(hit['credited'] for hit in impacts), sample
            assert impacts[0]['life'] < enemy['life'] and impacts[0]['damage'] > 0, impacts
            if family == 'hydra':
                assert any(q['tick'] == impacts[0]['Launched'] and q['Type'] == impacts[0]['type']
                           and q['target'] is None for q in sample['shots']), sample
            again = await manager.invoke('await_task', {'taskId':task,'milliseconds':1000})
            assert again['status'] == terminal['status'] and again['result']['task'] == terminal['result']['task'], again
            return {'spawn':enemy, 'hit':impacts[0]}

        for family in ('slime', 'hydra'):
            enemy = await scene(f'summon-{family}-cancel')
            task = await attack(enemy)
            for _ in range(100):
                sample = await scene('audit')
                if sample['projectileHits']:
                    break
                await asyncio.sleep(.04)
            assert sample['projectileHits'], sample
            result = await manager.invoke('cancel_task', {'taskId': task})
            assert result['status'] == 'cancelled' and result['result']['inputsReleased'], result
            sample = await scene('audit')
            roots = [q for q in sample['summons'] if q['type'] == enemy['shoot']]
            assert sample['summonSyncPackets'] > 0 and sample['focus'] == -1, sample
            assert len(roots) == 1 and roots[0]['sentry'] == (family == 'hydra'), sample
            assert sample['attackReleased'] and sample['inputScopeReleased'], sample
            assert any(row['debit'] == enemy['manaCost'] and row['shots'] == 1 for row in sample['manaTrace']), sample
            assert len([q for q in sample['shots'] if q['PersistentRoot']]) == 1, sample
            if family == 'slime':
                assert sample['babySlime'] and roots[0]['minionSlots'] == sample['maxMinions'] == 1, sample
            else:
                assert sample['maxTurrets'] == 1 and all(h['type'] != enemy['shoot'] for h in sample['projectileHits']), sample
            results[family+'Cast'] = sample
            results[family+'AfterCancel'] = await autonomous(family, task, result)
            later = await scene('audit')
            assert sample['projectileHits'] == later['projectileHits'] and roots[0] in later['summons'], later
            enemy = await scene(f'summon-{family}-reuse')
            task = await attack(enemy)
            result = await manager.invoke('await_task', {'taskId':task,'milliseconds':30000})
            proof = completed(result)
            sample = await scene('audit')
            assert roots[0] in sample['summons'] and not any(q['PersistentRoot'] for q in sample['shots']), sample
            final = sample['projectileHits'][-1]
            assert final['target'] == enemy['entityRef'] and final['tick'] == proof['lastHit']['GameUpdate'], sample
            assert final['life'] == proof['lastHit']['LifeAfter'] <= 0 and final['nativeDamage'], sample
            observation = proof['summon']
            assert not observation['InputAttempted'] and not observation['NativeShootObserved'], observation
            assert observation['ObservedRootIdentity'] == roots[0]['identity'] and observation['UnconfirmedChildren'] == 0, observation
            if family == 'hydra':
                assert observation['VerifiedChildren'] > 0 and observation['BuffObserved'] is None, observation
            impact = proof['lastHit']['Summon']
            assert impact['ProjectileType'] == final['type'] and impact['Owner'] == roots[0]['owner'], impact
            assert impact['ParentIdentity'] == (None if family == 'slime' else roots[0]['identity']), impact
            results[family+'Reuse'] = {'result': result, 'audit': sample}

            results[family+'AfterCompletion'] = await autonomous(family, task, result)
            if family == 'slime':
                await scene('clear-fixture-summons')

        pair, task_id = await defense_start('summon-defense')
        joined = await scene('summon-defense-join')
        expected = {enemy['entityRef']: 'combat_target_defeated' for enemy in (pair['first'], joined['added'])}
        defense = await defense_finish(task_id, expected, joined['bystander']['entityRef'], magic=True)
        encounters = defense['result']['result']['task']['result']['evidence']['encounters']
        impacts = [enc['LastHit']['Summon'] for enc in encounters]
        assert [impact['TargetEpoch'] for impact in impacts] == [1, 2], impacts
        assert all(impact['ParentIdentity'] == roots[0]['identity'] for impact in impacts), impacts
        assert all(shot['Type'] != enemy['shoot'] for shot in defense['audit']['shots']), defense
        results['summonDefense'] = defense

        # Replace the targeted NPC in place while a persistent attacker is live.
        enemy = await scene('summon-hydra-cancel')
        task = await attack(enemy)
        await wait_for(lambda a: a['actionId'] == task and (a['task'].get('progress') or {}).get('itemChecks', 0) > 0)
        replacement = await scene('replace')
        assert replacement['slot'] == enemy['whoAmI'] and replacement['entityRef'] != enemy['entityRef'], replacement
        result = await manager.invoke('await_task', {'taskId':task,'milliseconds':5000})
        assert result['result']['reason'] == 'combat_target_replaced' and not result['result']['effectVerified'], result
        sample = await scene('audit')
        assert all(hit['target'] != replacement['entityRef'] for hit in sample['projectileHits']), sample
        results['summonReplace'] = result

        # Same NPC and owner in both actions. Only the issuing task and
        # projectile birth distinguish the lethal bolt from a new task's hit.
        await scene('clear-fixture-summons')
        enemy = await scene('summon-inflight')
        first_task = await attack(enemy)
        for _ in range(150):
            sample = await scene('audit')
            if sample['parkedIce'] is not None:
                break
            await asyncio.sleep(.03)
        launch_tick = sample['parkedIce']
        assert launch_tick is not None and not sample['summonDamage'], sample
        cancelled = await manager.invoke('cancel_task', {'taskId':first_task})
        assert cancelled['status'] == 'cancelled', cancelled
        second_task = await attack(enemy)
        await wait_for(lambda a: a['actionId'] == second_task and (a['task'].get('progress') or {}).get('itemChecks', 0) > 0)
        await scene('release-ice')
        terminal = await manager.invoke('await_task', {'taskId':second_task,'milliseconds':5000})
        sample = await scene('audit')
        assert terminal['result']['reason'] == 'combat_target_invalidated' and not terminal['result']['effectVerified'], terminal
        assert any(h['Launched'] == launch_tick and h['life'] <= 0 and not h['credited']
                   and h['target'] == enemy['entityRef'] for h in sample['summonDamage']), sample
        assert not sample['projectileHits'], sample
        results['oldIce'] = {'result':terminal,'hits':sample['summonDamage']}
        if not lifecycle:
            await scene('clear-fixture-summons')

        if lifecycle:
            # Exercise revocation with both a contact minion and a firing sentry.
            slime_enemy = await scene('summon-slime-cancel')
            slime_task = await attack(slime_enemy)
            for _ in range(100):
                sample = await scene('audit')
                if any(h['type'] == slime_enemy['shoot'] and h['credited'] for h in sample['summonDamage']):
                    break
                await asyncio.sleep(.03)
            assert any(h['type'] == slime_enemy['shoot'] and h['credited'] for h in sample['summonDamage']), sample
            stopped = await manager.invoke('cancel_task', {'taskId':slime_task})
            assert stopped['status'] == 'cancelled', stopped
            enemy = await scene('summon-hydra-cancel')
            task = await attack(enemy)
            await wait_for(lambda a: a['actionId'] == task and (a['task'].get('progress') or {}).get('itemChecks', 0) > 0)
            before = await scene('audit')
            assert {q['type'] for q in before['summons'] if q['authorized']} == {slime_enemy['shoot'], enemy['shoot']}, before
            if lifecycle == 'disconnect':
                bridge.close()
                # Audit through the fixture filesystem after closing the owner connection.
                for _ in range(50):
                    first = await scene('audit')
                    await asyncio.sleep(.12)
                    later = await scene('audit')
                    if later['frames'] == first['frames']:
                        break
                await asyncio.sleep(1.2)
                later = await scene('audit')
                assert first['frames'] == later['frames'] and first['projectileHits'] == later['projectileHits'], later
                assert later['attackReleased'] and later['inputScopeReleased'] and later['summons'], later
                results[lifecycle] = later
                return
            boundary = await scene('summon-' + lifecycle)
            assert boundary['dead'] if lifecycle == 'death' else boundary['generation'] > boundary['before'], boundary
            first = await scene('audit')
            await asyncio.sleep(1)
            later = await scene('audit')
            assert later['inputScopeReleased'] and later['attackReleased'], later
            assert not any(q['authorized'] for q in later['summons']), later
            assert first['projectileHits'] == later['projectileHits'], later
            assert len(later['survivors']) == 1 and later['survivors'][0]['life'] == boundary['life'], later
            results[lifecycle] = {'boundary': boundary, 'audit': later}

    try:
        case = os.environ.get('COMPANION_COMBAT_CASE')
        if case and case.startswith('boss-'):
            from native_boss_encounter import verify_encounter
            await verify_encounter(case[5:], bridge, lua, manager, scene, results, profile)
            return
        if case == 'ordinary-evasion':
            await scene('boss-setup')
            for pattern in ('charge', 'bolt'):
                for arm in ('off', 'on'):
                    enemy = await scene(f'evasion-{pattern}-{arm}-ordinary')
                    task = await manager.invoke('submit_task', {'operation':'combat_attack','arguments':{'entityRef':enemy['entityRef']}})
                    assert 'taskId' in task, task
                    sample = await wait_file(profile / f'evasion-{pattern}-{arm}.json')
                    stopped = await manager.invoke('cancel_task', {'taskId':task['taskId']})
                    assert stopped['status']=='cancelled', stopped
                    await scene('evasion-clear')
                    results[f'{pattern}-{arm}']=sample
                baseline, active = (results[f'{pattern}-{arm}'] for arm in ('off', 'on'))
                assert active['lostLife'] < baseline['lostLife'], (pattern, active['wounds'], baseline['wounds'])
                assert active['impacts'] and active['opportunities'] and active['conflicts']==0, pattern
            return
        if case == 'weapon-trigger':
            await scene('dual-setup')
            await asyncio.sleep(.8)
            admitted=await scene('weapon-census')
            (profile/'weapon-admission.json').write_text(json.dumps(admitted))
            for blocked in (905,496,266,759):
                assert blocked not in admitted,blocked
            for spec in os.environ.get('COMPANION_TRIGGER_ITEMS','280,55,127,5282,964,4672,42,273,65,1569,160,3368,162,113,2366,3852,3852/right').split(','):
                item,_,button=spec.partition('/')
                enemy=await scene('trigger-'+item)
                args={'entityRef':enemy['entityRef']}
                if button:args['button']=button
                launch=await manager.invoke('submit_task',{'operation':'combat_attack','arguments':args})
                if 'taskId' not in launch:
                    results[spec]=launch
                    continue
                reply=await manager.invoke('await_task',{'taskId':launch['taskId'],'milliseconds':8000})
                results[spec]=reply
                if not reply.get('completed'):
                    await manager.invoke('cancel_task',{'taskId':launch['taskId']})
                await asyncio.sleep(.25)
            for spec,reply in results.items():
                if spec=='42' and reply.get('result',{}).get('reason')=='combat_weapon_consumed':
                    hit=reply['result']['task']['progress']['lastHit']
                    assert hit['Projectile']['Weapon']==42 and hit['DamageDone']>0 and hit['LifeAfter']<hit['LifeBefore'],hit
                    assert reply['result']['endEvidence']['inputsReleased']
                    continue
                assert reply.get('completed'), (spec,reply)
                proof=completed(reply)
                assert proof['weapon']['TypeId']==int(spec.split('/')[0]), proof
                if spec.startswith('3852'):
                    assert proof['lastHit']['Projectile']['Type']==(704 if spec.endswith('/right') else 712),proof
            return
        if case == 'yoyo-defense':
            await scene('dual-setup')
            await asyncio.sleep(.8)
            for family in ('wood','rally'):
                enemy = await scene('yoyo-'+family)
                task = await manager.invoke('submit_task', {'operation':'combat_attack','arguments':{'entityRef':enemy['entityRef']}})
                assert 'taskId' in task,task
                for _ in range(80):
                    sample = await scene('yoyo-observe')
                    if sample['channel'] and sample['projectileHits'] and len(sample['flightTrace'])>=55:
                        break
                    await asyncio.sleep(.05)
                else:
                    raise AssertionError((sample,await observe()))
                hit = sample['projectileHits'][0]
                assert hit['Weapon']==enemy['weapon'] and hit['nativeDamage'] and hit['damage']>0 and hit['life']<1000, hit
                tracking = [f for f in sample['flightTrace'] if abs(f['x']-f['targetX'])<24]
                assert tracking and max(f['targetX'] for f in tracking)-min(f['targetX'] for f in tracking)>8, tracking
                results[family] = {'enemy':enemy,'flight':sample}
                if family=='wood':
                    stopped=await manager.invoke('cancel_task',{'taskId':task['taskId']})
                    assert stopped['status']=='cancelled', stopped
                else:
                    await scene('yoyo-switch')
                for _ in range(50):
                    after=await scene('yoyo-observe')
                    if not after['live'] and not after['channel'] and after['heldProj']<0:
                        break
                    await asyncio.sleep(.05)
                else:
                    raise AssertionError(after)
                if family=='rally':
                    assert after['selectedItem']==1,after
                results[family]['after']={k:after[k] for k in ('live','channel','heldProj','selectedItem','itemTime','itemAnimation')}
                assert after['itemTime']==0 and after['itemAnimation']==0, after
            return
        if case == 'auto-loadout':
            await scene('auto-loadout-setup')
            async def loadout():
                reply=await lua.execute("return api('query_combat_loadout')")
                return reply['value']['data']
            for _ in range(80):
                kit=await loadout()
                sample=await scene('summon-observe')
                if sample['flower'] and sample['held']==3507 and (kit.get('maintenance') or {}).get('confirmedCasts',0)>0:
                    break
                await asyncio.sleep(.1)
            else:
                raise AssertionError((kit,sample))
            assert kit['primary']['expectedTypeId']==3507 and kit['summon']['hotbarKey']==7, kit
            results['automatic']=kit
            dismissed=await lua.execute("return api('dismiss_summon',{itemType=5114})")
            assert dismissed['value']['completed'], dismissed
            await asyncio.sleep(1)
            kit=await loadout()
            assert kit['maintenance']['status']=='summon_paused' and not (await scene('summon-observe'))['flower'], kit
            results['paused']=kit
            manual=await lua.execute("local r=api('inspect_inventory',{mode='slots',slots={0}}).data.inventory[1]; return api('configure_combat_loadout',{primary={hotbarKey=r.hotbarKey,expectedTypeId=r.typeId,inventoryToken=r.inventoryToken}})")
            assert manual['value']['completed'], manual
            await asyncio.sleep(1)
            kit=await loadout()
            assert not kit.get('summon') and kit['primary']['expectedTypeId']==3507, kit
            results['manual']=kit
            return
        if case == 'shortsword-defense':
            await scene(case)
            await lua.execute("return api('select_hotbar_slot',{slot=7})")
            cast = await lua.execute("return api('use_item')")
            assert cast['value']['completed'], cast
            dismissed = await lua.execute("return api('dismiss_summon',{itemType=5114})")
            results['dismiss'] = dismissed
            assert dismissed['value']['completed'] and dismissed['value']['evidence']['wasActive'], dismissed
            selected = await lua.execute("return api('select_hotbar_slot',{slot=1})")
            assert selected['value']['completed'], selected
            audit = await scene('summon-observe')
            assert not audit['flower'] and audit['slotsMinions']==0 and not audit['projectiles'], audit
            await scene('flower-slime')
            for _ in range(120):
                action = await observe()
                progress = ((action or {}).get('task') or {}).get('progress') or {}
                hit = progress.get('lastHit') or {}
                if progress.get('source')=='reflex' and (hit.get('Projectile') or {}).get('Weapon')==3507:
                    assert hit['LifeAfter']<hit['LifeBefore'] and hit['DamageDone']>0, hit
                    results['defense'] = progress
                    return
                await asyncio.sleep(.08)
            raise AssertionError(progress)
        if case in ('flower-test', 'imp-test'):
            await scene(case)
            key = 7 if case == 'flower-test' else 9
            selection = await lua.execute(f"return api('select_hotbar_slot',{{slot={key}}})")
            assert selection['value']['completed'], selection
            for i in range(2):
                cast = await lua.execute("return api('use_item')")
                results[f'cast{i}'] = cast
                audit = await scene('summon-observe')
                results[f'audit{i}'] = audit
                assert (cast.get('value') or {}).get('reason') == 'native_summon_verified', (cast.get('lastAction'), audit)
                assert audit['slotsMinions'] == i+1 and audit['flower' if key==7 else 'imp'], audit
                if key == 7:
                    assert sum(q['type']==963 for q in audit['projectiles']) == 1, audit
            full = await lua.execute("return api('use_item')")
            assert full['lastAction']['reason']=='summon_capacity_unavailable', full
            if key == 7:
                await scene('flower-slime')
                for _ in range(80):
                    audit = await scene('summon-observe')
                    if any(hit['type']==963 and hit['damage']>0 and hit['life']<120 for hit in audit['summonDamage']):
                        break
                    await asyncio.sleep(.1)
                else:
                    raise AssertionError(audit)
                results['damage'] = audit['summonDamage']
            return
        if case == 'summon-maintenance':
            async def loadout_until(status, after=0):
                for _ in range(180):
                    reply = await asyncio.to_thread(bridge.command,'query_atomic',operation='query_combat_loadout',arguments={})
                    sample = reply['data']
                    if sample['data']['maintenance']['status']==status and sample['gameUpdate']>=after:
                        return sample
                    await asyncio.sleep(.06)
                raise AssertionError(sample)

            async def close_enemy(enemy=None):
                if enemy is None:
                    enemy = await scene('maint-threat')
                defense = await wait_for(lambda a:a['targetTask']['entityRef']==enemy['entityRef'])
                result = await manager.invoke('await_task',{'taskId':defense['actionId'],'milliseconds':30000})
                proof = completed(result)
                assert proof['source']=='reflex' and proof['weapon']['Slot']==0, proof
                return enemy['entityRef'],proof,await scene('audit')

            await scene('maint-capacity')
            configure_program = """
local rows=api('inspect_inventory',{mode='slots',slots={0,3}}).data.inventory
local function role(row)
    return {hotbarKey=row.hotbarKey,expectedTypeId=row.typeId,inventoryToken=row.inventoryToken}
end
return api('configure_combat_loadout',{primary=role(rows[1]),summon=role(rows[2])})
"""
            setup = await lua.execute(configure_program)
            assert setup['status']=='completed' and setup['value']['completed'], setup
            Draft202012Validator(schema_for('configure_combat_loadout')).validate(setup['value'])
            results['setup'] = setup['value']
            capacity = await loadout_until('summon_capacity_unavailable')
            assert capacity['data']['summon']['reason']=='summon_capacity_unavailable'
            results['capacity'] = capacity
            await scene('maint-mana')
            mana = await loadout_until('combat_mana_unavailable')
            assert mana['data']['summon']['reason']=='combat_mana_unavailable'
            results['mana'] = mana

            enemy,proof,without = await close_enemy()
            results['urgent'] = {'target':enemy,'proof':proof,'audit':without}
            assert without['hitTrace'] and not without['shots'] and not without['summons']
            await scene('maint-safe')
            active = await loadout_until('summon_already_active')
            later = await loadout_until('summon_already_active',active['gameUpdate']+120)
            assert later['data']['maintenance']['confirmedCasts']==1
            before = await scene('audit')
            results['safe'] = {'start':active,'later':later,'audit':before}
            assert len(before['summons'])==1 and before['summons'][0]['authorized'] and before['selectedItem']==0
            assert len(before['shots'])==1 and before['shots'][0]['nativeShoot'] and before['shots'][0]['PersistentRoot']
            assert sum(row['shots'] for row in before['manaTrace'] if row['debit']>0)==1

            enemy,proof,after = await close_enemy()
            results['combined'] = {'target':enemy,'proof':proof,'audit':after}
            melee,summon = proof['contributions']['Melee'],proof['contributions']['Summon']
            sword,staff = setup['value']['evidence']['primary']['actualTypeId'],setup['value']['evidence']['summon']['actualTypeId']
            assert melee['MeleeWeaponType']==sword and summon['Summon']['WeaponType']==staff
            assert summon['Summon']['ProjectileIdentity']==before['summons'][0]['identity']
            assert any(h['entityRef']==enemy and h['nativeDamage'] and h['weapon']==sword
                       and h['tick']==melee['GameUpdate'] for h in after['hitTrace'])
            assert any(h['target']==enemy and h['held']==sword and h['Weapon']==staff and h['nativeDamage']
                       and h['tick']==summon['GameUpdate'] for h in after['projectileHits'])
            assert len(after['shots'])==1 and after['inputScopeReleased'] and after['attackReleased']
            assert proof['encounters'][-1]['Contributions']==proof['contributions']

            await scene('maint-recast')
            arrival = await wait_file(profile/'recast-ambush.json')
            enemy,proof,after = await close_enemy(arrival)
            results['recast'] = {'arrival':arrival,'proof':proof,'audit':after}
            assert arrival['itemAnimation']>0 and arrival['selectedItem']==3
            second = after['shots'][-1]
            assert len(after['shots'])==2 and second['PersistentRoot'] and second['tick']>before['shots'][0]['tick']
            selection = proof['weapon']['Selection']
            assert second['tick']<=arrival['tick']<selection['SelectedUpdate']
            assert proof['contributions']['Melee']['MeleeWeaponType']==sword
            summon_hit = proof['contributions']['Summon']['Summon']
            assert summon_hit['ProjectileIdentity']==second['Identity'] and summon_hit['SpawnUpdate']==second['tick']

            await scene('maint-hydra')
            hydra_setup = await lua.execute(configure_program)
            assert hydra_setup['value']['completed'], hydra_setup
            await loadout_until('summon_already_active')
            enemy,proof,after = await close_enemy()
            results['hydra'] = {'target':enemy,'proof':proof,'audit':after}
            hydra = proof['contributions']['Summon']['Summon']
            assert hydra['WeaponType']==hydra_setup['value']['evidence']['summon']['actualTypeId']
            assert hydra['ParentIdentity']==after['summons'][0]['identity'] and after['summons'][0]['sentry']
            assert proof['contributions']['Melee']['MeleeWeaponType']==sword
            assert any(h['target']==enemy and h['identity']==hydra['ProjectileIdentity'] and h['held']==sword
                       and h['nativeDamage'] for h in after['projectileHits'])
            return
        if case == 'dual-weapons':
            await scene('dual-setup')
            configured = await lua.execute("""
local inventory=api('inspect_inventory',{mode='slots',slots={0,2}}).data.inventory
local function role(row)
    return {hotbarKey=row.hotbarKey,expectedTypeId=row.typeId,inventoryToken=row.inventoryToken}
end
return api('configure_combat_loadout',{primary=role(inventory[1]),secondary=role(inventory[2])})
""")
            config = configured['value']
            assert config['completed'], configured
            Draft202012Validator(schema_for('configure_combat_loadout')).validate(config)
            results['configuration'] = config
            sword = config['evidence']['primary']['expectedTypeId']
            bow = config['evidence']['secondary']['expectedTypeId']
            for mode in ('dual-flight','dual-empty','dual-active','dual-reflex','dual-range'):
                target = await scene(mode)
                ref = target['entityRef']
                if mode=='dual-reflex':
                    defense = await wait_for(lambda a:a['targetTask']['entityRef']==ref)
                    task_id = defense['actionId']
                else:
                    submitted = await manager.invoke('submit_task', {'operation':'combat_attack','arguments':{'entityRef':ref}})
                    task_id = submitted['taskId']
                result = await manager.invoke('await_task',{'taskId':task_id,'milliseconds':30000})
                sample = await scene('audit')
                results[mode] = {'target':target,'action':result,'audit':sample}
                proof = completed(result)
                assert proof['target']['EntityRef']==ref and proof['weapon']['TypeId']==sword, proof
                assert proof['source']==('reflex' if mode=='dual-reflex' else 'agent')
                assert sample['hitViolations']==0 and sample['inputScopeReleased']
                if mode=='dual-empty':
                    assert not sample['shots'] and any(h['weapon']==sword and h['nativeDamage'] for h in sample['hitTrace'])
                elif mode=='dual-flight':
                    hit = proof['lastHit']
                    flight = hit['Projectile']
                    selected = proof['weapon']['Selection']['SelectedUpdate']
                    assert flight['Weapon']==bow and flight['Launched']<selected<hit['GameUpdate'], (flight,selected,hit)
                    raw = sample['projectileHits'][-1]
                    assert raw['held']==sword and raw['target']==ref and raw['identity']==flight['Identity'] and raw['nativeDamage'], raw
                else:
                    assert sample['shots'][0]['Weapon']==bow and sample['shots'][0]['nativeShoot']
                    assert any(h['entityRef']==ref and h['weapon']==sword and h['nativeDamage'] for h in sample['hitTrace'])
                    edge = sample['edgeSamples']
                    assert len(edge)>=85 and len({r['distance'] for r in edge})>=2
                    assert all(r['selectedItem']==2 for r in edge), edge
                    if mode=='dual-range':
                        assert {r['clear'] for r in edge}=={False,True}, edge
            return
        if case == 'loadout-trip':
            await scene('loadout-trip')
            async def program(source):
                view = await lua.execute(source)
                assert view['status']=='completed', view
                return view['value']

            # Use observed inventory and live help only; the client gets no fixture slots or item IDs.
            chosen = await program("""
local q=api('inspect_inventory',{mode='full'})
local bar=api('inspect_inventory',{mode='hotbar'}).data.inventory
local free={}
for _,row in ipairs(bar) do if row.empty then table.insert(free,row.slot) end end
local candidates=q.data.combatCandidates
assert(#candidates==3 and #free>=3)
for i,c in ipairs(candidates) do
    local dest=free[i]
    local pair=api('inspect_inventory',{mode='slots',slots={c.slot,dest}}).data.inventory
    local source,target
    for _,row in ipairs(pair) do
        if row.slot==c.slot then source=row else target=row end
    end
    assert(target.empty)
    assert(api('swap_inventory_slots',{sourceSlot=c.slot,destinationSlot=dest,sourceToken=source.inventoryToken,
        destinationToken=target.inventoryToken}).completed)
end
return candidates
""")
            assert {r['family'] for r in chosen}=={'melee','ranged','summon'}
            example = await program("return api('help',{name='configure_combat_loadout'}).example")
            config = await program(example)
            assert config['completed'], config
            ready = await program("return api('query_combat_loadout',{})")
            roles = ready['data']
            assert roles['summon']['reason']=='combat_mana_unavailable' and roles['secondary']['available']
            results['setup'] = {'candidates':chosen,'configuration':config,'observed':ready}
            # Hand mining the configured sword slot: its tool is still in the backpack.
            assert (await program("return api('select_hotbar_slot',{slot="+str(roles['primary']['hotbarKey'])+"})"))['completed']
            tile = await scene('arm-mining-ambush')
            start = await program('return api("submit_task",{operation="mine_target",arguments={x=%d,y=%d}})' %
                                  (tile['x'],tile['y']))
            arrival = await wait_file(profile/'ambush.json')
            assert arrival['Swings']>0 and arrival['SourceSlot']>=10 and arrival['displaced']==roles['primary']['actualTypeId']
            defense = await wait_for(lambda a:a['targetTask']['entityRef']==arrival['entityRef'])
            interrupted = await asyncio.to_thread(bridge.task_request,'action',start['taskId'])
            assert interrupted['reason']=='task_interrupt_combat' and interrupted['endEvidence']['inputsReleased']
            done = await manager.invoke('await_task',{'taskId':defense['actionId'],'milliseconds':30000})
            proof = completed(done)
            results['fight'] = {'arrival':arrival,'interrupted':interrupted,'combat':done}
            assert proof['source']=='reflex' and proof['weapon']['TypeId']==roles['primary']['actualTypeId']
            for _ in range(220):
                after = await program("return api('query_combat_loadout',{})")
                if after['data']['maintenance']['status']=='summon_already_active':
                    break
                await asyncio.sleep(.03)
            sample = await scene('audit')
            results['after'] = {'query':after,'audit':sample}
            assert after['data']['maintenance']['confirmedCasts']==1 and len(sample['summons'])==1
            assert proof['weapon']['Selection']['InterruptedAction']==start['taskId']
            assert any(h['Weapon']==roles['secondary']['actualTypeId'] and h['nativeShoot'] for h in sample['shots'])
            assert any(h['entityRef']==arrival['entityRef'] and h['weapon']==roles['primary']['actualTypeId']
                       and h['nativeDamage'] for h in sample['hitTrace'])
            roots = [h for h in sample['shots'] if h['PersistentRoot']]
            assert len(roots)==1 and roots[0]['tick']>done['result']['endEvidence']['gameUpdate']
            return
        if case == 'mining-ambush':
            prepared = await lua.execute("""
local q=api('inspect_inventory',{mode='slots',slots={0,9}})
local a,b=q.data.inventory[1],q.data.inventory[2]
local moved=api('swap_inventory_slots',{sourceSlot=0,destinationSlot=9,sourceToken=a.inventoryToken,destinationToken=b.inventoryToken})
assert(moved.completed)
local sword=api('inspect_inventory',{mode='slots',slots={9}}).data.inventory[1]
local held=api('select_hotbar_slot',{slot=2})
assert(held.completed)
local configured=api('configure_combat_loadout',{primary={hotbarKey=0,expectedTypeId=sword.typeId,inventoryToken=sword.inventoryToken}})
return {configured,api('query_combat_loadout',{})}
""")
            results['prepared'] = prepared
            assert prepared['status'] == 'completed', prepared
            configured, observed = prepared['value']
            assert configured['completed'] and observed['data']['primary']['available'], prepared
            for name, receipt in (('configure_combat_loadout',configured),('query_combat_loadout',observed)):
                Draft202012Validator(schema_for(name)).validate(receipt)
            sword = observed['data']['primary']['actualTypeId']
            tile = await scene('arm-mining-ambush')
            mining = await manager.invoke('submit_task', {'operation':'mine_target','arguments':tile})
            results['mining'] = mining
            spawn = await wait_file(profile/'ambush.json', 20)
            results['spawn'] = spawn
            assert spawn['Swings'] > 0 and spawn['itemAnimation'] > 0 and spawn['selectedItem'] == 1, spawn
            reflex = await wait_for(lambda a:a['targetTask']['entityRef']==spawn['entityRef'])
            interrupted = await asyncio.to_thread(bridge.task_request,'action',mining['taskId'])
            results['interrupted'] = interrupted
            assert interrupted['reason']=='task_interrupt_combat' and interrupted['endEvidence']['inputsReleased'], interrupted
            result = await manager.invoke('await_task', {'taskId':reflex['actionId'],'milliseconds':30000})
            results['reflex'] = result
            proof = completed(result)
            sample = await audit(proof)
            results['audit'] = sample
            assert proof['source']=='reflex' and proof['weapon']['Slot']==9 and proof['weapon']['TypeId']==sword
            hit = sample['hitTrace'][0]
            assert hit['nativeDamage'] and hit['weapon']==sword and hit['selectedItem']==9 and hit['entityRef']==spawn['entityRef']
            assert spawn['spawn']<=interrupted['endEvidence']['gameUpdate']<=sample['firstPress']<=hit['tick']
            held = await lua.execute("return api('select_hotbar_slot',{slot=2})")
            assert held['value']['completed'], held
            enemy = await scene('agent')
            launch = await manager.invoke('submit_task',{'operation':'combat_attack','arguments':{'entityRef':enemy['entityRef']}})
            active = await manager.invoke('await_task',{'taskId':launch['taskId'],'milliseconds':30000})
            results['active'] = active
            direct = completed(active)
            assert direct['source']=='agent' and direct['weapon']['Slot']==9 and direct['weapon']['TypeId']==sword
            selection = proof['weapon']['Selection']
            assert selection['Role']=='primary' and selection['PreviousSlot']==1 and selection['InterruptedAction']==mining['taskId']
            assert spawn['spawn']<=selection['AdmittedUpdate']<=selection['SelectedUpdate']<=sample['firstPress']
            assert direct['weapon']['Selection']['PreviousSlot']==1
            (profile/'ambush.json').unlink()
            tile = await scene('arm-invalid-primary')
            mining = await manager.invoke('submit_task', {'operation':'mine_target','arguments':tile})
            invalid_spawn = await wait_file(profile/'ambush.json',20)
            finished = await manager.invoke('await_task',{'taskId':mining['taskId'],'milliseconds':30000})
            results['unarmedMining'] = finished
            assert finished['completed'] and finished['result']['inputsReleased'], finished
            query = await lua.execute("return api('query_combat_loadout',{})")
            unavailable = query['value']['data']
            results['unavailable'] = unavailable
            assert unavailable['primary']['reason']=='combat_primary_unavailable'
            assert unavailable['lastRefusal']['entityRef']==invalid_spawn['entityRef']
            assert (await observe())['actionId']==launch['taskId']
            cleared = await lua.execute("return api('configure_combat_loadout',{})")
            assert cleared['value']['completed'] and cleared['value']['evidence'].get('primary') is None, cleared
            return
        if case in ('summons', 'summon-death', 'summon-generation', 'summon-disconnect'):
            await verify_summons(case.split('-')[1] if '-' in case else None)
            return
        enemy = await scene('agent-multi')
        program = 'return api("submit_task",{operation="combat_attack",arguments={entityRef=' + json.dumps(enemy['entityRef']) + '}})'
        submitted = await lua.execute(program)
        assert submitted['status'] == 'completed' and submitted['value']['status'] == 'running', submitted
        task = submitted['value']['taskId']
        await wait_for(lambda a: a['actionId'] == task and a['status'] == 'running')
        observed = await lua.execute('return {api("get_player_state",{}),api("query_combat",{})}')
        assert observed['status'] == 'completed' and all(v['valid'] for v in observed['value']), observed
        combat_view = observed['value'][1]
        Draft202012Validator(schema_for('query_combat')).validate(combat_view)
        assert combat_view['data']['action']['status'] == 'running'
        bridge.invalidate_replies(1)
        await asyncio.to_thread(bridge.advance_reply_turn, 1)
        speech = await asyncio.to_thread(bridge.command, 'say', text='Combat verification', replyTurn=1)
        results['chat'] = speech
        agent = await manager.invoke('await_task', {'taskId': task, 'milliseconds': 30000})
        results['agent'] = agent
        proof = completed(agent)
        results['agentNative'] = await audit(proof)
        assert proof['combinedUpdates'] > 0, proof
        task_view = agent['result']['task']
        assert 0 < proof['itemChecks'] <= task_view['observedGameUpdate'] - (task_view['deadlineGameUpdate'] - 1800)
        assert speech['gameUpdate'] < agent['result']['endEvidence']['gameUpdate']

        # Own a normal native action before introducing a targeted threat.
        start = await asyncio.to_thread(bridge.command, 'start_atomic', operation='move_input',
            arguments={'horizontal': 'none', 'vertical': 'none', 'jumpHoldUpdates': 0, 'maxUpdates': 600},
            expectedGeneration=bridge.binding['generation'])
        ordinary = start['movement']['action']['actionId']
        for _ in range(50):
            state = await asyncio.to_thread(bridge.task_request, 'action', ordinary)
            if (state.get('evidence') or {}).get('updatesExecuted', 0) >= 2:
                break
            await asyncio.sleep(.03)
        else:
            raise AssertionError(state)
        enemy = await scene('reflex')
        assert enemy['aiStyle'] >= 0 and enemy['knockBackResist'] > 0, enemy
        results['reflexSpawn'] = enemy
        reflex = await wait_for(lambda a: a['targetTask']['entityRef'] == enemy['entityRef'])
        interrupted = await asyncio.to_thread(bridge.task_request, 'action', ordinary)
        assert interrupted['status'] == 'cancelled' and interrupted['reason'] == 'task_interrupt_combat', interrupted
        assert interrupted['endEvidence']['inputsReleased'], interrupted
        results['interrupted'] = interrupted
        reflex_id = reflex['actionId']
        results['reflex'] = await manager.invoke('await_task', {'taskId': reflex_id, 'milliseconds': 30000})
        reflex_proof = completed(results['reflex'])
        results['reflexNative'] = await audit(reflex_proof)
        assert reflex_proof['source'] == 'reflex' and reflex_proof['threat']
        assert interrupted['endEvidence']['gameUpdate'] < reflex_proof['lastHit']['GameUpdate']
        kills = [json.loads(line) for line in (profile / 'combat-deaths.jsonl').read_text().splitlines()]
        for hit in (proof['lastHit'], reflex_proof['lastHit']):
            call_stack = next(kill['stack'] for kill in kills if kill['gameUpdate'] == hit['GameUpdate'])
            assert 'StrikeWithEvidence' in call_stack and 'Terraria.Player::ItemCheckWrapped' in call_stack, call_stack
        assert (await asyncio.to_thread(bridge.task_request, 'action', ordinary))['status'] == 'cancelled'
        pair, action_id = await defense_start()
        joined = await scene('defense-join')
        first, second = (pair[key]['entityRef'] for key in ('first', 'second'))
        third, bystander = (joined[key]['entityRef'] for key in ('added', 'bystander'))
        results['continuousDefense'] = await defense_finish(action_id,
            dict.fromkeys((first, second, third), 'combat_target_defeated'), allowed_survivor=bystander)
        encounters = results['continuousDefense']['result']['result']['task']['result']['evidence']['encounters']
        assert encounters[0]['Target']['EntityRef'] == first, encounters
        assert await observe() is not None and (await observe())['actionId'] == action_id

        pair, action_id = await defense_start()
        replacement = await scene('defense-replace')
        first, second = (pair[key]['entityRef'] for key in ('first', 'second'))
        new_ref = replacement['entityRef']
        assert replacement['slot'] == pair['first']['slot'] and new_ref != first, (pair, replacement)
        results['defenseReuse'] = await defense_finish(action_id, {
            first: 'combat_target_replaced', second: 'combat_target_defeated', new_ref: 'combat_target_defeated',
        })

        enemy = await scene('agent')
        cancelled = await manager.invoke('submit_task', {'operation': 'combat_attack',
                                                         'arguments': {'entityRef': enemy['entityRef']}})
        active = cancelled['taskId']
        await wait_for(lambda a: a['actionId'] == active and (a['task'].get('progress') or {}).get('itemChecks', 0) > 0)
        results['cancelled'] = await manager.invoke('cancel_task', {'taskId': active})
        assert results['cancelled']['status'] == 'cancelled' and results['cancelled']['result']['inputsReleased']
        async def durable_fight(spawn):
            submitted = await manager.invoke('submit_task', {'operation': 'combat_attack',
                                                             'arguments': {'entityRef': spawn['entityRef']}})
            assert submitted['status'] == 'running', submitted
            fight = await manager.invoke('await_task', {'taskId': submitted['taskId'], 'milliseconds': 30000})
            receipt = completed(fight)
            assert receipt['target']['EntityRef'] == spawn['entityRef'] and receipt['target']['Slot'] == spawn['slot']
            sampled = await audit(receipt)
            trace = sampled['hitTrace']
            assert len(trace) == sampled['hits'] >= 2, sampled
            life, previous_tick, previous_press = spawn['life'], spawn['spawnGameUpdate'] - 1, 0
            for hit in trace:
                assert hit['entityRef'] == spawn['entityRef'] and hit['whoAmI'] == spawn['slot'], hit
                assert hit['nativeWrapper'] and hit['nativeDamage'] and hit['tick'] > previous_tick, hit
                assert hit['press'] > previous_press and hit['damageDone'] > 0, hit
                assert life - hit['life'] == hit['damageDone'], (life, hit)
                life, previous_tick, previous_press = hit['life'], hit['tick'], hit['press']
            assert all(hit['life'] > 0 for hit in trace[:-1])
            assert receipt['lastHit']['GameUpdate'] == trace[-1]['tick']
            assert receipt['lastHit']['LifeBefore'] == trace[-2]['life']
            assert receipt['lastHit']['LifeAfter'] == trace[-1]['life']
            assert receipt['lastHit']['DamageDone'] == trace[-1]['damageDone']
            return {'spawn': spawn, 'receipt': receipt, 'audit': sampled, 'taskId': submitted['taskId']}

        a = await scene('durable')
        results['multiHit'] = await durable_fight(a)
        b = await scene('reuse')
        assert b['slot'] == a['slot'] and b['entityRef'] != a['entityRef'] and b['cooldownAtSpawn'] > 0, (a, b)
        results['slotReuse'] = await durable_fight(b)
        audit_b = results['slotReuse']['audit']
        assert audit_b['firstCooldown'] > 0 and audit_b['hitTrace'][0]['cooldown'] <= 0, audit_b
        assert audit_b['hitTrace'][0]['tick'] > results['multiHit']['receipt']['lastHit']['GameUpdate']
        old = await manager.invoke('await_task', {'taskId': results['multiHit']['taskId'], 'milliseconds': 1000})
        assert completed(old) == results['multiHit']['receipt']

        for mode in ('bow', 'gun', 'bow-reflex', 'bow-empty'):
            enemy = await scene(mode)
            if mode == 'bow-reflex':
                action = await wait_for(lambda a: a['targetTask']['entityRef'] == enemy['entityRef'])
                task_id = action['actionId']
            else:
                launch = await manager.invoke('submit_task', {'operation': 'combat_attack',
                                                               'arguments': {'entityRef': enemy['entityRef']}})
                task_id = launch['taskId']
            result = await manager.invoke('await_task', {'taskId': task_id, 'milliseconds': 30000})
            sample = await scene('audit')
            results[mode] = {'result': result, 'audit': sample}
            if mode == 'bow-empty':
                assert result['status'] == 'failed' and result['result']['reason'] == 'combat_ammo_unavailable', result
                assert not sample['shots'] and not sample['projectileHits']
                continue
            proof = completed(result)
            assert proof['target']['EntityRef'] == enemy['entityRef']
            assert proof['source'] == ('reflex' if mode == 'bow-reflex' else 'agent')
            assert sample['inputScopeReleased'] and sample['attackReleased'] and sample['frames'] == proof['itemChecks']
            shots = {shot['tick']: shot for shot in sample['shots']}
            assert len(shots) == len(sample['shots']) >= 2, sample
            assert len({shot['tick'] for shot in shots.values()}) == len(shots), sample
            assert all(shot['Verified'] and shot['nativeShoot'] and shot['nativeWrapper']
                       and shot['Ammo'] == enemy['ammoType'] and shot['Weapon'] == enemy['weapon']
                       and shot['target'] == enemy['entityRef'] for shot in shots.values()), sample
            # Minishark may conserve every round in a short encounter.
            minimum_spent = 0 if mode == 'gun' else 1
            assert minimum_spent <= enemy['ammo'] - sample['ammo'] <= len(shots) and sample['spareAmmo'] == enemy['spareAmmo'], sample
            assert sample['spawnPackets'] == len(shots) and sample['ammoPackets'] >= enemy['ammo'] - sample['ammo'], sample
            if mode == 'bow':
                assert len(sample['survivors']) == 1 and sample['survivors'][0]['life'] == 40, sample
                first = next(iter(shots.values()))
                assert first['vy'] * abs((first['tx'] - first['x']) / first['vx']) < first['ty'] - first['y'], first
            if mode == 'gun':
                assert any(shot['targetVx'] or shot['targetVy'] for shot in shots.values()), sample
            assert all(hit['target'] == enemy['entityRef'] and hit['nativeDamage']
                       and hit['tick'] > hit['launched'] and hit['identity'] == shots[hit['launched']]['Identity'] and hit['damage'] > 0
                       for hit in sample['projectileHits']), sample
            final = sample['projectileHits'][-1]
            assert final['tick'] == proof['lastHit']['GameUpdate'] and final['life'] == proof['lastHit']['LifeAfter'], sample

        # Native magic: the held staff supplies the projectile; no ammo is
        # consumed. The launch record must carry zero ammo and the cast state.
        # magic-multi supplies the multi-cast regression: a durable NPC needs
        # several independent native casts in distinct frames.
        for mode in ('spark', 'amethyst', 'magic-reflex', 'no-mana', 'magic-multi'):
            enemy = await scene(mode)
            if mode == 'magic-reflex':
                action = await wait_for(lambda a: a['targetTask']['entityRef'] == enemy['entityRef'])
                task_id = action['actionId']
            else:
                launch = await manager.invoke('submit_task', {'operation': 'combat_attack',
                                                               'arguments': {'entityRef': enemy['entityRef']}})
                task_id = launch['taskId']
            result = await manager.invoke('await_task', {'taskId': task_id, 'milliseconds': 30000})
            sample = await scene('audit')
            results[mode] = {'result': result, 'audit': sample}
            if mode == 'no-mana':
                assert result['status'] == 'failed' and result['result']['reason'] == 'combat_mana_unavailable', result
                assert not sample['shots'] and not sample['projectileHits'] and sample['mana'] == 0, sample
                assert result['result']['inputsReleased']
                continue
            proof = completed(result)
            assert proof['target']['EntityRef'] == enemy['entityRef']
            assert sample['inputScopeReleased'] and sample['attackReleased']
            assert len(sample['shots']) >= 1
            for shot in sample['shots']:
                assert shot['Verified'] and shot['Magic'] and shot['nativeShoot'] and shot['nativeWrapper'], shot
                assert shot['Ammo'] == 0 and shot['Weapon'] == enemy['weapon'] and shot['target'] == enemy['entityRef'], shot
                # 'mana' is measured after ItemCheck_PayMana; the cast that
                # spends the last point legitimately leaves zero.
                assert shot['mana'] >= 0 and shot['damage'] > 0, shot
            # Every native debit in the run must own a projectile spawn in the
            # same frame, and the whole-frame ledger must agree.
            assert sample['manaPaidWithoutShot'] == 0, sample
            for row in sample['manaTrace']:
                if row['debit'] > 0:
                    assert row['shots'] >= 1, (row, sample)
            # Native mana regeneration runs between frames, so compare the
            # per-frame ledger rather than the final pool.
            charged = sum(row['debit'] for row in sample['manaTrace'])
            assert charged <= enemy['manaCost'] * len(sample['shots']) + 2, (enemy, sample)
            assert sample['manaPackets'] >= 1, sample
            assert all(hit['nativeDamage'] and hit['target'] == enemy['entityRef'] and hit['damage'] > 0
                       for hit in sample['projectileHits']), sample
            assert sample['projectileHits'][-1]['tick'] == proof['lastHit']['GameUpdate'], sample
            if mode == 'magic-multi':
                # A durable target needs several native casts in distinct frames,
                # all from the same task and the same target incarnation.
                assert len(sample['shots']) >= 2, sample
                assert len({shot['tick'] for shot in sample['shots']}) == len(sample['shots']), sample
                assert all(shot['Identity'] == sample['shots'][0]['Identity'] for shot in sample['shots']), sample
                assert sample['potionPackets'] == 0 and sample['ammoPackets'] == 0, sample
                assert charged >= enemy['manaCost'], (enemy, sample)
            if mode == 'amethyst':
                first = sample['shots'][0]
                assert first['vy'] * abs((first['tx'] - first['x']) / first['vx']) < first['ty'] - first['y'], first

        # Mana exactly equal to the native cast cost. ItemCheck_PayMana empties
        # the pool; a post-payment re-check would silently drop the shot, so a
        # debit with no projectile fails here.
        enemy = await scene('mana-exact')
        launch = await manager.invoke('submit_task', {'operation': 'combat_attack',
                                                       'arguments': {'entityRef': enemy['entityRef']}})
        exact = await manager.invoke('await_task', {'taskId': launch['taskId'], 'milliseconds': 30000})
        sample = await scene('audit')
        results['manaExact'] = {'spawn': enemy, 'result': exact, 'audit': sample}
        assert enemy['mana'] == enemy['manaCost'] > 0, enemy
        assert sample['manaPaidWithoutShot'] == 0, sample
        assert sample['shots'], sample
        first = sample['shots'][0]
        assert first['Verified'] and first['mana'] == 0 and first['damage'] > 0, first
        assert [(row['debit'], row['shots']) for row in sample['manaTrace']] == [(enemy['manaCost'], 1)], sample
        assert len(sample['shots']) == 1, sample
        assert completed(exact)['target']['EntityRef'] == enemy['entityRef'], exact
        assert exact['result']['inputsReleased'], exact

        # Every admitted staff must rally the native shot path with its own
        # projectile; a debit without a spawn is rejected above for each run.
        staff_modes = ('staff-spark', 'staff-amethyst', 'staff-topaz', 'staff-sapphire',
                       'staff-emerald', 'staff-ruby', 'staff-diamond', 'staff-amber')
        for mode in staff_modes:
            enemy = await scene(mode)
            launch = await manager.invoke('submit_task', {'operation': 'combat_attack',
                                                           'arguments': {'entityRef': enemy['entityRef']}})
            result = await manager.invoke('await_task', {'taskId': launch['taskId'], 'milliseconds': 30000})
            sample = await scene('audit')
            results[mode] = {'spawn': enemy, 'result': result, 'audit': sample}
            assert sample['manaPaidWithoutShot'] == 0, sample
            assert sample['shots'], (mode, sample)
            assert all(shot['Verified'] and shot['Magic'] and shot['Type'] == enemy['shoot']
                       and shot['Weapon'] == enemy['weapon'] and shot['Ammo'] == 0
                       for shot in sample['shots']), (mode, sample)
            assert any(row['debit'] > 0 and row['shots'] >= 1 for row in sample['manaTrace']), (mode, sample)
            assert sample['manaPackets'] >= 1 and sample['attackReleased'], (mode, sample)
            proof = completed(result)
            assert proof['target']['EntityRef'] == enemy['entityRef'], (mode, proof)

        # Native QuickMana: no mana for the cast, but Mana Flower plus a potion
        # must still fire. The AI pre-check may not refuse what the engine funds.
        enemy = await scene('mana-flower')
        assert enemy['mana'] < enemy['manaCost'] and enemy['potionDelay'] == 0, enemy
        launch = await manager.invoke('submit_task', {'operation': 'combat_attack',
                                                       'arguments': {'entityRef': enemy['entityRef']}})
        flower = await manager.invoke('await_task', {'taskId': launch['taskId'], 'milliseconds': 30000})
        sample = await scene('audit')
        results['manaFlower'] = {'spawn': enemy, 'result': flower, 'audit': sample}
        assert flower['status'] == 'completed' and flower['result']['reason'] == 'combat_target_defeated', flower
        assert sample['manaPaidWithoutShot'] == 0 and sample['shots'], sample
        assert sample['potions'] < enemy['potions'], sample
        assert sample['manaFlower'] and sample['manaPackets'] >= 1, sample
        potion_frames = [row for row in sample['manaTrace'] if row['potionsUsed'] > 0]
        assert sum(row['potionsUsed'] for row in potion_frames) == enemy['potions'] - sample['potions'], sample
        assert all(row['flower'] and row['shots'] >= 1 for row in potion_frames), sample
        assert sample['potionPackets'] >= len(potion_frames), sample

        # Mana Flower without any mana potion cannot finance QuickMana. The
        # cast must be refused rather than faked, with no potion or payment.
        enemy = await scene('mana-flower-dry')
        launch = await manager.invoke('submit_task', {'operation': 'combat_attack',
                                                       'arguments': {'entityRef': enemy['entityRef']}})
        blocked = await manager.invoke('await_task', {'taskId': launch['taskId'], 'milliseconds': 30000})
        sample = await scene('audit')
        results['manaFlowerDry'] = {'spawn': enemy, 'result': blocked, 'audit': sample}
        assert blocked['status'] == 'failed' and blocked['result']['reason'] == 'combat_mana_unavailable', blocked
        assert not sample['shots'] and not sample['projectileHits'], sample
        assert sample['potions'] == enemy['potions'], sample
        assert not any(row['flower'] for row in sample['manaTrace']), sample
        assert sample['manaPaidWithoutShot'] == 0, sample
        assert blocked['result']['inputsReleased'] and not blocked['result']['effectVerified'], blocked

        # Magical reflex defense starts with a threat and stays on the same
        # task while a second nearby threat is introduced.
        pair, action_id = await defense_start('magic-defense')
        first = pair['first']['entityRef']
        joined = await scene('magic-defense-join')
        second, bystander = joined['added']['entityRef'], joined['bystander']['entityRef']
        results['magicDefense'] = await defense_finish(action_id, {first: 'combat_target_defeated',
                                                                   second: 'combat_target_defeated'}, bystander, magic=True)

        # Magical old projectiles have no authority after the target changes.
        for mode in ('magic-replace', 'magic-cancel'):
            enemy = await scene('spark')
            launch = await manager.invoke('submit_task', {'operation': 'combat_attack',
                                                           'arguments': {'entityRef': enemy['entityRef']}})
            task_id = launch['taskId']
            for _ in range(40):
                sample = await scene('audit')
                if sample['shots']:
                    break
            assert sample['shots'] and sample['shots'][0]['Magic'], sample
            shot = sample['shots'][0]
            if mode == 'magic-replace':
                replacement = await scene(mode)
                assert replacement['live'] and replacement['slot'] == enemy['slot']
                assert replacement['entityRef'] != enemy['entityRef']
                watched = replacement['entityRef']
                result = await manager.invoke('await_task', {'taskId': task_id, 'milliseconds': 5000})
                assert result['status'] == 'failed' and result['result']['reason'] == 'combat_target_replaced'
            else:
                watched = enemy['entityRef']
                result = await manager.invoke('cancel_task', {'taskId': task_id})
                assert result['status'] == 'cancelled'
            assert result['result']['inputsReleased'] and not result['result']['effectVerified']
            for _ in range(60):
                sample = await scene('audit')
                if sample['gameUpdate'] >= shot['tick'] + 65:
                    break
                await asyncio.sleep(.03)
            assert not sample['projectileHits'], sample
            results[mode] = {'result': result, 'audit': sample}

        clearance = await scene('ranged-clearance')
        assert clearance['centered'] and not clearance['edge'] and clearance['legacy'] and clearance['restored'], clearance
        results['rangedClearance'] = clearance
        for mode in ('bow-wall', 'bow-high', 'bow-sealed', 'bow-opposite', 'bow-no-route'):
            enemy = await scene(mode)
            assert enemy['initialAim'] == (mode == 'bow-high'), enemy
            launch = await manager.invoke('submit_task', {'operation': 'combat_attack',
                                                           'arguments': {'entityRef': enemy['entityRef']}})
            result = await manager.invoke('await_task', {'taskId': launch['taskId'], 'milliseconds': 30000})
            sample = await scene('audit')
            results[mode] = {'result': result, 'audit': sample, 'spawn': enemy}
            if mode in ('bow-sealed', 'bow-no-route'):
                reason = 'combat_shot_blocked' if mode == 'bow-sealed' else 'combat_firing_navigation_no_route'
                assert result['status'] == 'failed' and result['result']['reason'] == reason, result
                assert result['result']['inputsReleased'] and not sample['shots'] and sample['ammo'] == enemy['ammo'], sample
                continue
            proof = completed(result)
            assert proof['target']['EntityRef'] == enemy['entityRef'] and sample['attackReleased'], sample
            first = sample['shots'][0]
            if mode == 'bow-wall':
                assert abs(first['x'] - enemy['startX']) > 16 and first['y'] < enemy['startY'] - 16, (first, enemy)
            elif mode == 'bow-opposite':
                assert first['x'] < first['tx'] < enemy['startX'] and first['vx'] > 0, (first, enemy)
                assert sample['projectileHits'], sample
            else:
                assert first['ty'] < first['y'] - 48 and first['vy'] < 0, first

        for mode in ('ranged-replace', 'ranged-cancel'):
            enemy = await scene('bow-one')
            launch = await manager.invoke('submit_task', {'operation': 'combat_attack',
                                                           'arguments': {'entityRef': enemy['entityRef']}})
            task_id = launch['taskId']
            for _ in range(30):
                sample = await scene('audit')
                if sample['shots']:
                    break
            assert len(sample['shots']) == 1 and not sample['projectileHits'], sample
            shot = sample['shots'][0]
            if mode == 'ranged-replace':
                replacement = await scene(mode)
                assert replacement['live'] and replacement['slot'] == enemy['slot'] and replacement['entityRef'] != enemy['entityRef']
                watched = replacement['entityRef']
                result = await manager.invoke('await_task', {'taskId': task_id, 'milliseconds': 5000})
                assert result['status'] == 'failed' and result['result']['reason'] == 'combat_target_replaced', result
            else:
                watched = enemy['entityRef']
                result = await manager.invoke('cancel_task', {'taskId': task_id})
                assert result['status'] == 'cancelled', result
            assert result['result']['inputsReleased'] and not result['result']['effectVerified'], result
            for _ in range(60):
                sample = await scene('audit')
                if sample['gameUpdate'] >= shot['tick'] + 65:
                    break
                await asyncio.sleep(.03)
            assert sample['gameUpdate'] >= shot['tick'] + 65 and not sample['projectileHits'], sample
            assert any(n['entityRef'] == watched and n['life'] == 32 for n in sample['survivors']), sample
            results[mode] = {'result': result, 'audit': sample}

        await verify_summons()

        for mode in ('external', 'despawn', 'replace', 'fault'):
            enemy = await scene('agent')
            submitted = await manager.invoke('submit_task', {'operation': 'combat_attack',
                                                             'arguments': {'entityRef': enemy['entityRef']}})
            active = submitted['taskId']
            await wait_for(lambda a: a['actionId'] == active and (a['task'].get('progress') or {}).get('itemChecks', 0) > 0)
            removed = await scene(mode)
            if mode == 'replace':
                assert removed['active'] and removed['slot'] == enemy['slot'] and removed['entityRef'] != enemy['entityRef'], removed
            elif mode != 'fault':
                assert not removed['active'], removed
            result = await manager.invoke('await_task', {'taskId': active, 'milliseconds': 5000})
            results[mode] = result
            expected = {'replace': 'combat_target_replaced', 'fault': 'task_exception'}.get(mode, 'combat_target_invalidated')
            assert result['status'] == ('unknown' if mode == 'fault' else 'failed') and result['result']['reason'] == expected, result
            if mode == 'fault':
                assert 'Intentional native combat input fault.' in result['result']['task']['result']['reasonDetail'], result
                results['faultNative'] = await scene('audit')
                assert results['faultNative']['inputScopeReleased'] and results['faultNative']['attackReleased']
            assert result['result']['inputsReleased'] and result['result']['effectVerified'] is False

    finally:
        (profile / 'combat-results.json').write_text(json.dumps(results, indent=2))
        await manager.close()
        bridge.close()


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--port', type=int, required=True)
    parser.add_argument('--profile', type=Path, required=True)
    asyncio.run(verify(parser.parse_args()))
