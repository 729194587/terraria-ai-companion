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

    try:
        enemy = await scene('agent')
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
        reflex = await wait_for(lambda a: a['targetTask']['entityRef'] == enemy['entityRef'])
        interrupted = await asyncio.to_thread(bridge.task_request, 'action', ordinary)
        assert interrupted['status'] == 'cancelled' and interrupted['reason'] == 'task_interrupt_combat', interrupted
        assert interrupted['endEvidence']['inputsReleased'], interrupted
        results['interrupted'] = interrupted
        reflex_id = reflex['actionId']
        reflex = await wait_for(lambda a: a['actionId'] == reflex_id and a['status'] == 'running'
                                and (a['task'].get('progress') or {}).get('itemChecks', 0) > 0)
        assert reflex['task']['progress']['source'] == 'reflex'
        results['reflex'] = await manager.invoke('cancel_task', {'taskId': reflex_id})
        assert results['reflex']['status'] == 'cancelled' and results['reflex']['result']['inputsReleased']
        assert (await asyncio.to_thread(bridge.task_request, 'action', ordinary))['status'] == 'cancelled'
        enemy = await scene('agent')
        cancelled = await manager.invoke('submit_task', {'operation': 'combat_attack',
                                                         'arguments': {'entityRef': enemy['entityRef']}})
        active = cancelled['taskId']
        await wait_for(lambda a: a['actionId'] == active and (a['task'].get('progress') or {}).get('itemChecks', 0) > 0)
        results['cancelled'] = await manager.invoke('cancel_task', {'taskId': active})
        assert results['cancelled']['status'] == 'cancelled' and results['cancelled']['result']['inputsReleased']
    finally:
        (profile / 'combat-results.json').write_text(json.dumps(results, indent=2))
        await manager.close()
        bridge.close()


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--port', type=int, required=True)
    parser.add_argument('--profile', type=Path, required=True)
    asyncio.run(verify(parser.parse_args()))
