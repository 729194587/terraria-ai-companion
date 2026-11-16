"""Copied-world Dedicated Server combat through Lua, IPC and native Player updates."""
import argparse
import asyncio
import json
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

    async def audit(proof):
        sample = await scene('audit')
        assert sample['frames'] == proof['itemChecks'] and sample['hits'] > 0 and sample['hitViolations'] == 0, (sample, proof)
        assert sample['inputScopeReleased'] and sample['attackReleased'], sample
        return sample

    try:
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
