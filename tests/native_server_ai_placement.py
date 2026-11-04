"""One observed placement through actual IPC/Lua/native Player; isolated fixture only."""
import argparse
import asyncio
import json
from pathlib import Path
import sys
sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'python'))
from lua_runtime import LuaRuntime, Limits
from server_ai import ServerAiBridge


async def verify(args):
    profile=args.profile.resolve()
    assert (profile/'isolated-action-fixture').is_file()
    fixture=json.loads((profile/'action-fixture.json').read_text(encoding='utf-8-sig'))
    # A model can derive these candidates from its observation; this diagnostic
    # supplies known fixture coordinates, then Lua inspects/selects before acting.
    source="""
local p=api('get_player_state',{})
local terrain=api('observe_terrain',{})
local bag=api('inspect_inventory',{mode='hotbar'})
local materials={}
for _,i in ipairs(bag.data.inventory) do
  if not i.empty and i.stack>0 and i.use.createTile>=0 then materials[#materials+1]=i end
end
assert(#materials>0)
table.sort(materials,function(a,b) return a.hotbarKey<b.hotbarKey end)
local material=materials[1]
local candidates={{x=TARGETX+1,y=TARGETY},{x=TARGETX,y=TARGETY}}
local target=nil
for _,c in ipairs(candidates) do
  local tile=api('inspect_tile',c)
  if tile.valid and tile.data.cell.knowledge=='read' and not tile.data.cell.hasTile then target=c; break end
end
assert(target)
local selected=api('select_hotbar_slot',{slot=material.hotbarKey})
assert(selected.valid and selected.completed)
local action=api('place_tile',target)
assert(action.valid and action.completed and action.evidence.consumedCount==1)
local after=api('inspect_tile',target)
local inventory=api('inspect_inventory',{mode='selected'})
assert(after.data.cell.hasTile and after.data.cell.tileType==material.use.createTile)
assert(inventory.data.inventory[1].stack==material.stack-1)
return {before=p,terrain=terrain,material=material,target=target,action=action,after=after,inventory=inventory}
""".replace('TARGETX',str(fixture['mineX'])).replace('TARGETY',str(fixture['mineY']))
    (profile/'placement-program.lua').write_text(source,encoding='utf-8')
    bridge=ServerAiBridge(args.port)
    try:
        r=await LuaRuntime(bridge,evidence_root=profile/'placement-lua').execute(source,limits=Limits(seconds=15,calls=16,actions=2))
    finally:bridge.close()
    (profile/'placement-lua-result.json').write_text(json.dumps(r,ensure_ascii=False,indent=2),encoding='utf-8')
    assert r['status']=='completed' and r['value']['action']['completed'],r
    print(json.dumps(dict(result='passed',target=r['value']['target'],calls=r['calls'],actions=r['actions'])),flush=True)


if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('--profile',type=Path,required=True);p.add_argument('--port',type=int,required=True)
    asyncio.run(verify(p.parse_args()))
