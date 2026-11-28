"""Exercise the public Lua follow module against a moving multiplayer session."""
import asyncio
import json
import struct
import time

from jsonschema import Draft202012Validator
from api_result_contracts import schema_for
from atomic_operations import dispatch
from lua_runtime import LuaRuntime
from native_direct_input import wait_file
from program_tasks import coordinator


async def verify_follow(profile, bridge, leader):
    ready = json.loads((profile / 'container-ready.json').read_text())
    origin = ready['x'] * 16
    top = (ready['y'] + 2) * 16 - 42
    runtime = LuaRuntime(bridge)
    tasks = coordinator(bridge)
    cases = {}

    def move(offset, elevation=0):
        leader.send(13, struct.pack('<6Bff', leader.slot, 0, 0, 0, 0, 0, origin + offset, top - elevation))

    async def fixture(command):
        state = profile / 'container-state.json'
        state.unlink(missing_ok=True)
        tmp = profile / 'follow.tmp'
        tmp.write_text(command)
        tmp.replace(profile / 'container-request')
        assert (await wait_file(state, 5))['command'] == command

    async def start(seconds=None, chat_id=1):
        options = '' if seconds is None else f', {{durationSeconds={seconds}}}'
        result = await runtime.execute(f"return terraria.move.follow('{ref}'{options})", chat_id=chat_id)
        assert result['status'] == 'completed', result
        ack = result['value']
        assert ack['dispatchState'] == 'admitted', ack
        return ack['taskId']

    async def until(task, predicate, seconds=10):
        deadline = time.monotonic() + seconds
        while True:
            view = await tasks.invoke('query_task', {'taskId': task})
            state = view['action']['task'] or {}
            if predicate(view, state):
                return view, state
            assert view['status'] == 'running' and time.monotonic() < deadline, (view['status'], state)
            await asyncio.sleep(.08)

    async def position():
        result = await dispatch(bridge, 'get_player_state', {})
        return result['player']['position']['x']

    try:
        assert (await dispatch(bridge, 'set_team', {'team': 1}))['completed']
        leader.send(45, bytes([leader.slot, 1]))
        move(0)
        async with asyncio.timeout(5):
            while True:
                team = (await dispatch(bridge, 'list_teammates', {}))['data']['players']
                member = next((p for p in team if p['name'] == 'ChestObserverA' and not p['reason']), None)
                if member and member['handoff']['inRange']:
                    break
                await asyncio.sleep(.05)
        ref = member['teammateRef']
        task = await start()
        running, nearby = await until(task, lambda v, t: t.get('phase') == 'nearby')
        assert nearby['currentChild'] is None
        assert running['action']['durationMs'] is None and nearby['deadlineGameUpdate'] is None
        assert nearby['progress']['durationUpdates'] is None
        contract = Draft202012Validator(schema_for('query_task', capabilities=bridge.hello['capabilities']))
        contract.validate(running)
        read = await runtime.execute("return api('get_player_state', {}).valid", chat_id=2)
        assert read['value'] is True, read
        await asyncio.sleep(60)
        _, still_following = await until(task, lambda v, t: t.get('phase') == 'nearby'
                                         and t['progress']['elapsedUpdates'] > 3600, seconds=25)
        cases['untimed_after_minute'] = still_following['progress']
        move(240)
        await until(task, lambda v, t: t.get('phase') == 'following')
        async with asyncio.timeout(10):
            while (right := await position()) <= origin + 48:
                await asyncio.sleep(.08)
        move(-144)
        _, returned = await until(task, lambda v, t: t.get('phase') == 'nearby' and t['progress']['legs'] >= 2)
        left = await position()
        assert left < origin - 64 and returned['currentChild'] is None
        cases['turned_and_stopped'] = dict(right=right, left=left, task=returned['progress'])

        await fixture('follow-ambush')
        _, defending = await until(task, lambda v, t: t.get('phase') == 'defending')
        await fixture('follow-clear')
        move(128)
        _, resumed = await until(task, lambda v, t: t.get('phase') == 'nearby' and t['progress']['legs'] > returned['progress']['legs'])
        assert (await position()) > origin + 48
        cases['resumed_after_defense'] = dict(defending=defending['progress'], resumed=resumed['progress'])
        move(-128)
        await until(task, lambda v, t: t.get('phase') == 'following')
        cancelled = await tasks.invoke('cancel_task', {'taskId': task})
        assert cancelled['status'] == 'cancelled' and cancelled['result']['endEvidence']['inputsReleased'], cancelled
        cases['cancelled'] = cancelled['result']['reason']

        move((await position()) - origin)
        previous = await start(chat_id=3)
        await until(previous, lambda v, t: t.get('phase') == 'nearby')
        action = await runtime.execute("return api('select_hotbar_slot', {slot=2}).completed", chat_id=4)
        assert action['value'] is True, action
        old = await tasks.invoke('query_task', {'taskId':previous})
        assert old['status'] == 'cancelled' and old['result']['inputsReleased'], old
        cases['replaced_by_action'] = old['result']['reason']
        previous = await start(chat_id=5)
        task = await start(2, chat_id=6)
        old = await tasks.invoke('query_task', {'taskId':previous})
        assert old['status'] == 'cancelled' and old['result']['inputsReleased'], old
        cases['replaced_by_task'] = old['result']['reason']
        done, state = await until(task, lambda v, t: v['status'] != 'running')
        assert done['completed'] and done['result']['effectVerified'], done
        cases['duration'] = state['result']['evidence']
        contract.validate(done)

        # A connected player also falls between position packets. Send flight updates while hovering.
        hover_x = (await position()) - origin
        async def hover():
            while True:
                move(hover_x, elevation=96)
                await asyncio.sleep(.04)
        flight = asyncio.create_task(hover())
        try:
            task = await start(2)
            await until(task, lambda v, t: t.get('phase') == 'waiting_for_landing')
            timed_out, state = await until(task, lambda v, t: v['status'] != 'running')
        finally:
            flight.cancel()
            await asyncio.gather(flight, return_exceptions=True)
        assert timed_out['status'] == 'timeout' and state['reason'] == 'follow_target_out_of_range', timed_out
        airborne = state['result']['evidence']
        assert not airborne['withinRange'] and airborne['legs'] == 0
        cases['airborne_out_of_range'] = airborne

        await fixture('follow-ledge')
        move(240, elevation=48)
        task = await start(30)
        _, climbed = await until(task, lambda v, t: t.get('phase') == 'nearby' and t['progress']['legs'] > 0)
        pose = (await dispatch(bridge, 'get_player_state', {}))['player']['position']
        assert abs(pose['y'] - (top - 48)) <= 1 and pose['x'] >= origin + 192, pose
        cases['climbed_ledge'] = dict(position=pose, progress=climbed['progress'])
        leader.close()
        ended, state = await until(task, lambda v, t: v['status'] != 'running')
        assert ended['status'] == 'failed' and state['reason'] == 'teammate_unavailable', ended
        assert ended['result']['endEvidence']['inputsReleased']
        cases['disconnected'] = state['reason']
        return {'cases': cases}
    finally:
        await tasks.close()
