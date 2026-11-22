"""Deferred copied-world acceptance for server mine_wall and hammer_tile."""
import argparse
import asyncio
import json
from pathlib import Path

from native_direct_input import Limits, LuaRuntime, ServerAiBridge, wait_file


async def verify(port, profile):
    await wait_file(profile / 'action-fixture.json', 90)
    bridge = ServerAiBridge(port)
    receipts = []
    try:
        assert {'mine_wall', 'hammer_tile'} <= bridge.supported_atomic_operations
        request = profile / 'direct-input-request.tmp'
        request.write_text(json.dumps({'scene': 'hammer'}))
        request.replace(profile / 'direct-input-request.json')
        start = await wait_file(profile / 'direct-input-ready.json')
        left, floor = int(start['startX']) // 16, int(start['floorY']) // 16
        runtime = LuaRuntime(bridge, evidence_root=profile/'lua')

        async def call(name, args):
            fields = ','.join(f'{key}={value}' for key, value in args.items())
            recorded = []
            result = await runtime.execute(f'return api("{name}",{{{fields}}})',
                limits=Limits(seconds=20, calls=1, actions=1),
                on_result=lambda _, __, receipt: recorded.append(receipt))
            assert recorded, result
            receipts.append(dict(name=name, result=recorded[0]))
            return recorded[0]

        wall = dict(x=left+2, y=floor-1)
        covered = dict(x=left+3, y=floor)
        assert (await call('select_hotbar_slot', {'slot':5}))['completed']
        assert (await call('mine_wall', wall))['reason'] == 'selected_item_requires_pure_hammer'
        assert (await call('select_hotbar_slot', {'slot':4}))['completed']
        assert (await call('mine_wall', covered))['reason'] == 'unsupported_wall_target'
        assert (await call('hammer_tile', covered))['reason'] == 'unsupported_hammer_target'
        assert (await call('mine_wall', wall))['completed']
        assert (await call('inspect_tile', wall))['data']['cell']['wallType'] == 0
        assert (await call('inspect_tile', covered))['data']['cell']['wallType'] > 0
        for x in (left+2,left-2):
            target = dict(x=x,y=floor)
            before = (await call('inspect_tile',target))['data']['cell']
            assert (await call('hammer_tile',target))['completed']
            after = (await call('inspect_tile',target))['data']['cell']
            assert before['tileType'] == after['tileType'] and after['hasTile']
            assert (before['halfBlock'],before['slope']) != (after['halfBlock'],after['slope'])
    finally:
        bridge.close()
        (profile/'hammer-results.json').write_text(json.dumps(receipts,indent=2))


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--port', type=int, required=True)
    parser.add_argument('--profile', type=Path, required=True)
    args = parser.parse_args()
    asyncio.run(verify(args.port, args.profile))
