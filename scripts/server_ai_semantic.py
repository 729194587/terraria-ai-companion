"""L1 semantic acceptance in the explicit copied-world native fixture only."""
import argparse
import asyncio
import json
from pathlib import Path
import sys
ROOT=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(ROOT/'python'))
from lua_runtime import LuaRuntime, Limits
from server_ai import ServerAiBridge
SOURCE='''
local before=sense_world({maxObjects=8,radius=640,oreNames={'Copper'}})
local tree=nil
local pair=false
local single=false
for _,o in ipairs(before.objects) do
    if o.kind=='tree_trunk' and (tree==nil or o.observedTileCount>tree.observedTileCount) then tree=o end
    if o.contentKey=='Terraria/Copper' then
        if o.observedTileCount==2 then pair=true end
        if o.observedTileCount==1 then single=true end
    end
end
assert(tree and pair and single, 'native fixture tree / separated copper components missing')
assert(tree.candidate.status=='observed_present' and not tree.complete)
assert(#before.drops>0, 'native world drops missing')
local selected=api('select_hotbar_slot',{slot=4})
assert(selected.completed)
local mined=api('mine_tile',{x=tree.candidate.x,y=tree.candidate.y})
assert(mined.completed and mined.evidence.targetAbsent and mined.evidence.destroyedByActor and mined.endEvidence.inputsReleased, 'semantic candidate did not fell native tree')
local after=sense_world({maxObjects=8,ores=false,drops=false})
local drops=sense_world({trees=false,ores=false,radius=640,dropTypes={before.drops[1].typeId}})
for _,d in ipairs(drops.drops) do assert(d.typeId==before.drops[1].typeId, 'drop filter not applied') end
assert(not drops.requested.trees and not drops.requested.ores and #drops.objects==0)
local ranks={}
for _,d in ipairs(drops.drops) do
    table.insert(ranks,{distance=math.sqrt(d.distancePixels^2),slot=d.observationSlot})
end
table.sort(ranks,function(a,b) return a.distance<b.distance end)
for _,o in ipairs(after.objects) do
    assert(o.kind~='tree_trunk' or o.bounds.minX~=tree.bounds.minX, 'felled trunk retained')
end
assert(after.sources[1].gameUpdate>before.sources[1].gameUpdate)
return {before=before,selected=selected,mined=mined,after=after,drops=drops,ranks=json.array(ranks)}
'''
async def verify(port):
    bridge=ServerAiBridge(port=port)
    try:
        return await LuaRuntime(bridge,evidence_root=ROOT/'.local/semantic-evidence').execute(SOURCE,limits=Limits(seconds=45,calls=64,actions=2))
    finally: bridge.close()
def main():
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('--port',type=int,required=True);p.add_argument('--output',type=Path,required=True);a=p.parse_args()
    result=asyncio.run(verify(a.port));a.output.write_text(json.dumps(result,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    print(json.dumps(dict(status=result['status'],calls=result['calls'],error=result.get('error'),failure=result.get('failure'),output=str(a.output))))
    resolves=[r for r in result['trace'] if r['operation']=='resolve_content']
    return 0 if result['status']=='completed' and result['actions']==2 and len(resolves)==3 and result['calls']<=28 else 1
if __name__=='__main__': raise SystemExit(main())
