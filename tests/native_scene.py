"""Deterministic old/new perception recipes + native candidate execution, no LLM."""
import argparse
import asyncio
import json
from pathlib import Path
import sys
sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'python'))
from lua_runtime import LuaRuntime, Limits
from server_ai import ServerAiBridge


async def run(args):
    profile=args.profile.resolve();assert (profile/'isolated-action-fixture').is_file()
    bridge=ServerAiBridge(args.port)
    records=[]
    async def program(name, source, actions=0):
        (profile/f'scene-{name}.lua').write_text(source,encoding='utf-8')
        r=await LuaRuntime(bridge,evidence_root=profile/'scene-lua').execute(source,limits=Limits(seconds=50,calls=48,actions=actions))
        records.append(dict(name=name,result=r))
        (profile/f'scene-{args.phase}.json').write_text(json.dumps(dict(records=records),ensure_ascii=False,indent=2),encoding='utf-8')
        assert r['status']=='completed',r
        return r
    try:
        if args.phase=='perception':
            r=await program('new',"return api('observe_scene',{})")
            s=r['value']['data']
            assert len(s['map']['rows'])==29 and all(len(row)==45 for row in s['map']['rows'])
            assert s['sample']['sameGameUpdate'] and s['gameUpdate']==r['value']['gameUpdate']
            resources=(await program('resources',"return api('observe_scene',{layers={'resources'},maxObjects=32})"))['value']['data']
            assert not {'map','geometry','objects'} & resources.keys()
            hits=resources['resources']['items']
            chest=next(h for h in hits if h['kind']=='chest' and h['status']=='complete')
            assert len(chest['tiles'])==4 and chest['bounds']==dict(**chest['anchor'],width=2,height=2)
            assert any(h['kind']=='ore_component' and h['tiles'] for h in hits)
            assert any(h['kind']=='drop' and h.get('entityRef') for h in hits)
            kinds=s['objectCounts']['observed']
            for kind in ('tree_component','ore_component','chest','door','workbench','npc','drop'):assert kinds.get(kind,0)>0,(kind,s)
            assert all(not o['complete'] for o in s['objects']['items'] if o['kind']=='tree_component')
            source_types={source for h in s['hazards']['tiles']['items'] for source in h['sources']}
            assert {'water','lava','honey','shimmer'} <= source_types,source_types
            assert s['hazards']['entities']['matchedCount']>0
            low=await program('truncated',"return api('observe_scene',{maxObjects=1,maxRegions=1,maxPlacements=1,maxHazards=1})")
            limited=low['value']['data']
            assert limited['objects']['truncated'] and limited['objects']['omittedCount']>0
            assert limited['objectCounts']['observed']==kinds
            assert limited['hazards']['tiles']['truncated']
            assert limited['map']['rows']==s['map']['rows'],'static scene map must not drop objects/hazards with list caps'
            old_objects=await program('old-objects',"return sense_world({trees=true,ores=true,oreNames={'Copper'},drops=false,maxObjects=8})")
            assert any(o['kind']=='tree_trunk' for o in old_objects['value']['objects'])
            assert any(o['kind']=='ore_vein' for o in old_objects['value']['objects'])
            old_grid=await program('old-grid',"return api('observe_terrain',{})")
            grid=old_grid['value']['data']['terrain']
            positions=[dict(x=grid['originX']+x,y=grid['originY']+y) for y,row in enumerate(grid['rows']) for x,ch in enumerate(row) if ch=='!']
            assert positions
            points='{'+','.join('{x=%d,y=%d}'%(p['x'],p['y']) for p in positions)+'}'
            old_hazards=await program('old-hazards',"local points="+points+"; local result={}; for _,p in ipairs(points) do result[#result+1]=api('inspect_tile',p) end; return result")
            assert {v['data']['cell']['liquidType'] for v in old_hazards['value'] if v['data']['cell']['liquidAmount']>0} >= {0,1,2,3}
            old_place=await program('old-placement',"""
local s=api('get_player_state',{}); local p=s.player.position
local cx=math.floor(p.x/16); local y=math.floor((p.y+s.motion.playerHeight)/16)-1
for _,x in ipairs({cx,cx-1,cx+2}) do
  local target={x=x,y=y}; local t=api('inspect_tile',target)
  local overlaps=x*16<p.x+s.motion.playerWidth and (x+1)*16>p.x
  if not t.data.cell.hasTile and t.data.cell.liquidAmount==0 and not overlaps then
    local support=api('inspect_tile',{x=x,y=y+1})
    if support.data.cell.hasTile and support.data.cell.solid and not support.data.cell.actuated then return target end
  end
end
error('no baseline placement target')
""")
            assert any(c['target']==old_place['value'] for c in s['placementCandidates']['items'])
            # Old grid already costs one query; count actual host coordinate
            # projection work separately rather than inventing extra API calls.
            landing_columns=[];coordinate_ops=0
            for y,row in enumerate(grid['rows']):
                for x in range(len(row)-1):
                    if row[x:x+2]=='==' and y>=3 and all(grid['rows'][yy][x:x+2]=='..' for yy in range(y-3,y)):
                        landing_columns.append(dict(minPlayerLeftX=(grid['originX']+x)*16,maxPlayerLeftX=(grid['originX']+x)*16+12,surfaceY=(grid['originY']+y)*16))
                        coordinate_ops+=4
            assert landing_columns
            benchmark=dict(kind='fixed deterministic read-only recipes; NOT real model efficiency or optimal old strategy',
                objects=dict(oldApiCalls=old_objects['calls'],newApiCalls=1),
                hazardSources=dict(oldApiCalls=old_grid['calls']+old_hazards['calls'],newApiCalls=1,oldCoordinateProjections=len(positions)*2),
                placement=dict(oldApiCalls=old_place['calls'],newApiCalls=1),
                landing=dict(oldApiCalls=old_grid['calls'],newApiCalls=1,oldHostCoordinateArithmetic=coordinate_ops,newCallerCoordinateArithmetic=0),
                bytes=dict(newScene=len(json.dumps(r['value'],ensure_ascii=False).encode()),oldObjectReport=len(json.dumps(old_objects['value'],ensure_ascii=False).encode())),
                sceneElapsedMilliseconds=s['sample']['elapsedMilliseconds'],sameSnapshotForNew=True,
                scope='old recipes are separate samples; same constructed static terrain, NPC pose isolated; new single query serves all four tasks')
            (profile/'scene-benchmark.json').write_text(json.dumps(benchmark,indent=2),encoding='utf-8')
            print(json.dumps(benchmark),flush=True)
        else:
            r=await program('candidate-actions',"""
local before=api('observe_scene',{}); local s=before.data
assert(before.valid and s.actor.navigationProfileSupported)
local bag=api('inspect_inventory',{mode='hotbar'}); local material=nil
for _,i in ipairs(bag.data.inventory) do if i.use.createTile==30 and i.stack>0 then material=i; break end end
assert(material and #s.placementCandidates.items>0)
api('select_hotbar_slot',{slot=material.hotbarKey})
local site=s.placementCandidates.items[1]
local placed=api('place_tile',site.target)
assert(placed.valid and placed.completed and placed.evidence.consumedCount==1)
local check=api('inspect_tile',site.target);assert(check.data.cell.hasTile and check.data.cell.tileType==30)
local goal=nil
for _,r in ipairs(s.landingRegions.items) do
  if r.feetDeltaY<=-64 and r.target.minPlayerLeftX>s.actor.boundsWorldPixels.x+64 then goal=r;break end
end
assert(goal)
local up=api('navigate_to_landing_region',goal.target)
assert(up.valid and up.completed and up.evidence.settled)
local fresh=api('observe_scene',{}); local back=nil
for _,r in ipairs(fresh.data.landingRegions.items) do if r.target.surfaceY==s.actor.feetY then back=r;break end end
assert(back)
local down=api('navigate_to_landing_region',back.target)
assert(down.valid and down.completed and down.evidence.settled)
return {placed=placed,up=up,down=down,goal=goal,back=back,sceneBefore=s.sceneId,sceneAfter=fresh.data.sceneId}
""",actions=4)
            assert r['actions']==4
            print(json.dumps(dict(result='passed',calls=r['calls'],actions=r['actions'],up=r['value']['goal']['target'],down=r['value']['back']['target'])),flush=True)
    finally:bridge.close()


if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('--port',type=int,required=True);p.add_argument('--profile',type=Path,required=True);p.add_argument('--phase',choices=('perception','actions'),required=True)
    asyncio.run(run(p.parse_args()))
