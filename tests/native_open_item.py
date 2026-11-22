"""Verify bag/crate consumption and source-bound loot on the dedicated server."""
import argparse
import asyncio
import json
from pathlib import Path

from native_direct_input import Limits, LuaRuntime, ServerAiBridge, wait_file
from api_result_contracts import schema_for
from jsonschema import Draft202012Validator


async def verify(port, profile):
    await wait_file(profile/'action-fixture.json', 90)
    bridge = ServerAiBridge(port)
    results = []
    async def row(slot):
        reply = bridge.command('query_atomic', operation='inspect_inventory', arguments={'mode':'slots','slots':[slot]})
        return reply['data']['data']['inventory'][0]

    async def open_slot(slot, token, expected):
        calls = []
        await LuaRuntime(bridge, evidence_root=profile/'lua').execute(
            f'return api("open_inventory_item",{{sourceSlot={slot},sourceToken="{token}"}})',
            limits=Limits(seconds=15,calls=1,actions=1), on_result=lambda _, __, r: calls.append(r))
        result, = calls
        results.append(result)
        assert result['reason']==expected, result
        Draft202012Validator(schema_for('open_inventory_item')).validate(result)
        return result

    try:
        assert 'open_inventory_item' in bridge.supported_atomic_operations
        tmp=profile/'direct-input-request.tmp'
        tmp.write_text(json.dumps({'scene':'open_items'}))
        tmp.replace(profile/'direct-input-request.json')
        await wait_file(profile/'direct-input-ready.json')
        for slot in (20,21):
            initial = await row(slot)
            result = await open_slot(slot, initial['inventoryToken'], 'container_item_opened')
            evidence=result['evidence']
            assert result['completed'] and evidence['typeId']==initial['typeId'] and evidence['drops']
            after=await row(slot)
            assert after['stack']==initial['stack']-1==evidence['afterStack'], after
            await open_slot(slot, initial['inventoryToken'], 'stale_inventory_token')
        material=await row(22)
        await open_slot(22,material['inventoryToken'],'unsupported_right_click_item')
        assert (await row(22))['inventoryToken']==material['inventoryToken']
    finally:
        bridge.close()
        (profile/'opening-results.json').write_text(json.dumps(results,indent=2))


if __name__=='__main__':
    parser=argparse.ArgumentParser()
    parser.add_argument('--port',type=int,required=True)
    parser.add_argument('--profile',type=Path,required=True)
    args=parser.parse_args()
    asyncio.run(verify(args.port,args.profile))
