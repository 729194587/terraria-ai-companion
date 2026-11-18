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
        assert {n['entityRef'] for n in sample['survivors']} == ({allowed_survivor} if allowed_survivor else set()), sample
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
            await asyncio.sleep(1)
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

        if lifecycle:
            enemy = await scene('summon-hydra-cancel')
            task = await attack(enemy)
            await wait_for(lambda a: a['actionId'] == task and (a['task'].get('progress') or {}).get('itemChecks', 0) > 0)
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
            assert all(q['life'] == boundary['life'] for q in later['survivors']), later
            results[lifecycle] = {'boundary': boundary, 'audit': later}

    try:
        case = os.environ.get('COMPANION_COMBAT_CASE')
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
