"""Native door interaction and passage using a copied world and the public Lua API."""
import argparse
import asyncio
import json
from pathlib import Path

from native_direct_input import Limits, LuaRuntime, ServerAiBridge, wait_file


async def verify(port, profile):
    await wait_file(profile / 'action-fixture.json', 90)
    bridge = ServerAiBridge(port)
    results = []

    async def scene(name):
        ready = profile / 'direct-input-ready.json'
        ready.unlink(missing_ok=True)
        tmp = profile / 'direct-input-request.tmp'
        tmp.write_text(json.dumps({'scene': name}))
        tmp.replace(profile / 'direct-input-request.json')
        return await wait_file(ready)

    async def action(name, args):
        calls = []
        argument = '{' + ','.join(f'{key}={value}' for key, value in args.items()) + '}'
        response = await LuaRuntime(bridge, evidence_root=profile/'lua').execute(
            f'return api("{name}",{argument})', limits=Limits(seconds=40, calls=1, actions=1),
            on_result=lambda _, __, r: calls.append(r))
        assert calls, response
        result = calls[-1]
        results.append(result)
        return result

    def observe_door(left, top, opened):
        view = bridge.command('query_atomic', operation='observe_scene', arguments={'layers': ['objects']})
        matches = [o for o in view['data']['data']['objects']['items'] if o['kind'] == 'door'
                   and o['details']['anchorTile'] == dict(x=left, y=top)]
        assert len(matches) == 1, matches
        door = matches[0]
        results.append(door)
        shape = door['furniture']
        width = 2 if opened else 1
        assert door['complete'] and door['observedTileCount'] == width * 3, door
        assert shape['category'] == 'door' and shape['doorState'] == ('open' if opened else 'closed'), door
        assert shape['footprint'] == dict(x=left, y=top, width=width, height=3), door
        assert shape['placementAnchor'] == dict(x=left+shape['origin']['x'], y=top+shape['origin']['y']), door

    try:
        start = await scene('door')
        x = int(start['startX']) // 16 + 3
        y = int(start['floorY']) // 16 - 2
        door = dict(x=x, y=y)
        for op, changed in [('open_door', True), ('open_door', False), ('close_door', True)]:
            r = await action(op, door)
            assert r['completed'] and r['evidence']['changed'] is changed, r
            observe_door(x, y-1, op == 'open_door')
        goal = int(start['startX']) + 7*16
        r = await action('navigate_to_x', {'targetX':goal})
        assert r['completed'] and r['evidence']['x'] > (x+1)*16 and r['evidence']['settled'], r
        r = await action('close_door', door)
        assert r['completed'] and r['evidence']['beforeOpen'], r
        # From inside, the door swings left. Address the free leaf, not the hinge.
        assert (await action('open_door', door))['completed']
        observe_door(x-1, y-1, True)
        r = await action('close_door', dict(x=x-1,y=y))
        assert r['completed'] and r['evidence']['hinge']==x, r
        r = await action('open_door', dict(x=x+30,y=y))
        assert r['reason']=='door_out_of_reach', r
        await scene('door')
        assert (await action('navigate_to_x', {'targetX': int(start['startX'])-160}))['completed']
        r = await action('navigate_to_x', {'targetX':goal})
        assert r['completed'] and r['evidence']['x'] > (x+1)*16, r
        await scene('door')
        destination=dict(x=goal,y=int(start['floorY']))
        r = await action('navigate_to',destination)
        assert r['completed'] and r['inputsReleased'],r
        assert (await action('close_door', door))['evidence']['changed']
        await scene('blocked_door')
        for op, args in [('open_door',door), ('navigate_to_x',{'targetX':goal}), ('navigate_to',destination)]:
            r = await action(op, args)
            assert not r['completed'] and r['reason']=='door_blocked', r
    finally:
        bridge.close()
        (profile/'door-results.json').write_text(json.dumps(results, indent=2))


if __name__=='__main__':
    parser=argparse.ArgumentParser()
    parser.add_argument('--port',type=int,required=True)
    parser.add_argument('--profile',type=Path,required=True)
    args=parser.parse_args()
    asyncio.run(verify(args.port,args.profile))
