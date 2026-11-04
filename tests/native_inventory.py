"""Copied-world native inventory matrix plus real Lua/IPC receipt verification; no model."""
import argparse
import asyncio
import json
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'python'))
from lua_runtime import LuaRuntime, Limits
from server_ai import ServerAiBridge


async def verify(args):
    profile = args.profile.resolve()
    assert (profile / 'isolated-action-fixture').is_file()
    ready = profile / 'inventory-selftest.json'
    ready.unlink(missing_ok=True)
    (profile / 'inventory-request').write_text('explicit isolated inventory diagnostics')
    for _ in range(600):
        if ready.exists():
            break
        await asyncio.sleep(.05)
    matrix = json.loads(ready.read_text())
    assert matrix['passed'], matrix
    rows = {row['kind']: row for row in matrix['rows']}
    for case in ('baseline_missing', 'baseline_empty', 'baseline_partial'):
        action = rows[case]['result']
        assert action['Status'] == 'unknown' and not action['Evidence']['mutationAttempted'], action
        for name in ('source', 'destination'):
            slot = action['Evidence']['after'][name]
            known = case == 'baseline_empty' or case == 'baseline_partial' and name == 'source'
            assert slot['available'] == known, slot
            if known:
                assert slot['instanceChanged'] == (case == 'baseline_empty' and name == 'source'), slot
            else:
                assert slot['reason'] == 'baseline_not_established' and 'instanceChanged' not in slot, slot
    for case in ('save_throw', 'save_mutate', 'global_save_throw', 'global_save_mutate'):
        assert rows[case]['saveDataCalls'] == rows[case]['globalSaveDataCalls'] == 0, rows[case]
        assert rows[case]['result']['Status'] == 'completed', rows[case]
    for case in ('permission_throw', 'stack_throw', 'split_throw'):
        action = rows[case]['result']
        assert action['Status'] == 'unknown' and not action['EffectVerified'], action
        after = action['Evidence']['after']
        assert after['available'] and after['kind'] == 'basic_fields_only', after
        for slot in ('source', 'destination'):
            assert all(key in after[slot] for key in ('typeId', 'stack', 'prefix', 'instanceId', 'instanceChanged')), after
    assert rows['stack_throw']['result']['Evidence']['after']['source']['instanceChanged']
    assert rows['mod_custom_state_untracked']['result']['Status'] == 'completed'
    assert rows['stale_instance']['result']['Status'] == 'failed'
    for case, row in rows.items():
        if case.startswith('stale'):
            assert not row['result']['Evidence']['mutationAttempted'], row
    bridge = ServerAiBridge(args.port)
    try:
        result = await LuaRuntime(bridge, evidence_root=profile/'inventory-lua').execute("""
local records={}
local function change(op,from,to,count,key)
    local q=api('inspect_inventory',{mode='slots',slots=from==to and {from} or {from,to}})
    assert(q.valid)
    local a=q.data.inventory[1]; local b=from==to and a or q.data.inventory[2]
    assert(#a.inventoryToken==64 and #b.inventoryToken==64)
    local args={sourceSlot=from,sourceToken=a.inventoryToken,destinationToken=b.inventoryToken}
    if key~=nil then args.hotbarKey=key else args.destinationSlot=to end
    if count then args.count=count end
    local r=api(op,args);assert(r.valid and r.completed and r.evidence.inventoryVerified)
    assert(r.evidence.confirmedAtGameUpdate>r.evidence.mutationGameUpdate)
    table.insert(records,r)
end
change('transfer_inventory_item',2,10,1)
change('transfer_inventory_item',10,2,1)
change('swap_inventory_slots',0,10)
change('hold_inventory_item',10,0,nil,1)
change('hold_inventory_item',0,0,nil,1)
change('transfer_inventory_item',2,10,2)
change('transfer_inventory_item',10,2,2)
return records
""", limits=Limits(actions=7, calls=20, seconds=40))
        (profile/'inventory-results.json').write_text(json.dumps(dict(native=matrix, lua=result), ensure_ascii=False, indent=2), encoding='utf-8')
        assert result['status'] == 'completed', result
        assert len(result['value']) == 7
    finally:
        bridge.close()
    print(f"Inventory: {len(matrix['rows'])} native cases and 7 Lua/IPC actions passed", flush=True)


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--port', type=int, required=True)
    parser.add_argument('--profile', type=Path, required=True)
    asyncio.run(verify(parser.parse_args()))
