"""Check bed-foot interactions and detached native character save/load on a copied world."""
import argparse
import asyncio
import json
from pathlib import Path

from native_direct_input import Limits, LuaRuntime, ServerAiBridge, wait_file


async def verify(port, profile):
    await wait_file(profile / 'action-fixture.json', 90)
    bridge = ServerAiBridge(port)
    async def scene(name):
        ready = profile / 'direct-input-ready.json'
        ready.unlink(missing_ok=True)
        request = profile / 'direct-input-request.tmp'
        request.write_text(json.dumps({'scene':name}))
        request.replace(profile / 'direct-input-request.json')
        return await wait_file(ready)

    async def run(source, expected_status):
        result = await LuaRuntime(bridge).execute(source, limits=Limits(seconds=30,calls=12,actions=1))
        assert result['status']==expected_status, result
        return result['value'] or result['lastAction']

    results=[]
    try:
        assert 'server_ai_bed_spawn_v1' in bridge.hello['capabilities']
        for name in ('bed','bed-reversed','bed-invalid'):
            setup=await scene(name)
            left,y=int(setup['startX'])//16,int(setup['floorY'])//16-1
            scan=f"""local feet, heads = {{}},0
for x={left-1},{left+5} do
    local tile=api("inspect_tile",{{x=x,y={y}}}).data
    if tile.interaction and tile.interaction.kind=="bed" then
        table.insert(feet,tile.interaction)
    elseif tile.cell.hasTile and tile.cell.tileType==79 then heads=heads+1 end
end
assert(#feet==2 and heads==2)
"""
            for active in ((False,) if name=='bed-invalid' else (False,True)):
                result=await run(scan+f"assert(feet[1].active=={str(active).lower()})\n"+
                                 'return api("interact_tile",{target=feet[1].target})',
                                 'failed' if name=='bed-invalid' else 'completed')
                if name=='bed-invalid':
                    assert result['reason']=='invalid_bed_spawn',result
                    assert await scene('bed-readback')=={'x':-1,'y':-1}
                else:
                    assert result['completed'],result
                    proof=result['evidence']
                    saved=await scene('bed-readback')
                    assert (saved['x'],saved['y'])==(proof['afterSpawnX'],proof['afterSpawnY'])
                    results.append(dict(scene=name,active=proof['afterActive'],readback=saved))
        (profile/'bed-results.json').write_text(json.dumps(results,indent=2))
    finally:
        bridge.close()


if __name__=='__main__':
    parser=argparse.ArgumentParser()
    parser.add_argument('--port',type=int,required=True)
    parser.add_argument('--profile',type=Path,required=True)
    args=parser.parse_args()
    asyncio.run(verify(args.port,args.profile))
