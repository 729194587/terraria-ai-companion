"""Isolated L1 facts/costs and L0 navigation regression; no model or graphics client."""
import argparse
import asyncio
import json
from pathlib import Path
import sys
sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'python'))
import atomic_operations as atomic
from server_ai import ServerAiBridge
from lua_runtime import LuaRuntime, Limits
from native_direct_input import wait_file


def at(grid,x,y):
    row=grid['rows'][y];data=row['data']
    if row['encoding']=='indices':return grid['palette'][data[x]]
    if row['encoding']=='hex_indices':
        n=row['indexDigits'];return grid['palette'][int(data[x*n:(x+1)*n],16)]
    assert row['encoding']=='rle'
    end=0
    for count,index in zip(data[::2],data[1::2]):
        end+=count
        if x<end:return grid['palette'][index]
    raise AssertionError((x,y,grid))


async def verify(args):
    p=args.profile;await wait_file(p/'action-fixture.json',90)
    ready=p/'terrain-ready.json';ready.unlink(missing_ok=True)
    temp=p/'terrain-request.tmp';temp.write_text(json.dumps(dict(scene='flat',direction=1,mobility='basic',region=True,environment=args.environment)))
    temp.replace(p/'terrain-request.json');fixture=await wait_file(ready)
    bridge=ServerAiBridge(args.port);rows=[]
    try:
        assert 'server_ai_scene_region_v1' in bridge.hello['capabilities']
        x,y=fixture['originTileX']+32,fixture['floor']-1
        async def read(name,arguments):
            r=await atomic.dispatch(bridge,'observe_scene',arguments)
            (p/f'region-{name}.json').write_text(json.dumps(r,indent=2))
            assert r['valid'] and r['worldSessionId']==bridge.binding['world']
            assert r['actorGeneration']==bridge.binding['generation'] and r['actorSlot']==bridge.binding['slot']
            d=r['data'];assert d['gameUpdate']==r['gameUpdate'] and d['sample']['sameGameUpdate']
            return d
        local=await read('local',dict(layers=['geometry','objects','hazards']))
        assert local['schemaVersion']==2 and len(local['geometry']['rows'])==29
        if args.environment:
            probe=await wait_file(p/'environment-probe.json')
            assert probe['dedServ'] and probe['netMode']==2
            assert probe['before']==probe['after'],probe
            assert local['lighting']['available'] is False and 'mean' not in local['lighting']
            def projectile(scene):
                return next(h for h in scene['hazards']['entities']['items']
                    if h['kind']=='hostile_projectile' and h['slot']==probe['projectileSlot'])
            first=projectile(local)
            assert first['observedGameUpdate']==local['gameUpdate'] and first['velocity']['x']==-4,first
            assert first['threat']['status']=='no_projected_contact' and first['threat']['horizonTicks']==12,first
            repeat=await read('environment-repeat',dict(layers=['geometry','hazards']))
            assert projectile(repeat)['observationRef']!=first['observationRef']
            assert repeat['geometry']==local['geometry']
        assert not local['bounds']['minX']<=x<=local['bounds']['maxX']
        # Include cold and warm runs; never drop a slow observation from the evidence.
        for width,height,label in ((80,45,'1280x720'),(120,68,'1920x1080'),(128,64,'bounded_2048x1024')):
            for i in range(args.iterations):
                d=await read(f'{width}x{height}-{i}',dict(scale='region',width=width,height=height,maxObjects=32,maxHazards=1))
                assert d['schemaVersion']==3 and 'landingRegions' not in d and 'placementCandidates' not in d
                b=d['bounds'];cov=d['coverage'];sample=d['sample']
                assert b['maxX']-b['minX']+1==width and b['maxY']-b['minY']+1==height
                assert cov['requestedTiles']==width*height and cov['scannedTiles']<=width*height<=8192
                assert cov['samplingTruncated']==sample['samplingBudgetReached']
                if cov['samplingTruncated']:
                    assert cov['unknownByReason']['not_sampled']>0 and not cov['geometryComplete']
                else:
                    assert any(o['kind']=='ore_component' and o['contentKey']=='Terraria/Copper'
                        and o['observedBounds']['x']<=x<o['observedBounds']['x']+o['observedBounds']['width'] for o in d['objects']['items']),d
                    assert all('target' not in o for o in d['objects']['items'])
                    assert at(d['geometry']['grid'],x-b['minX'],y-b['minY'])=='#'
                    sx,sy=fixture['originTileX']+30,fixture['floor']-1
                    assert at(d['hazards']['tileMask'],sx-b['minX'],sy-b['minY'])!=0,d
                rows.append(dict(viewport=label,iteration=i,scanned=cov['scannedTiles'],partial=cov['samplingTruncated'],
                    sample=sample,dataJsonBytes=len(json.dumps(d,ensure_ascii=True,separators=(',',':')).encode()),
                    world=bridge.binding['world']))
            assert any(not r['partial'] for r in rows if r['viewport']==label),'No complete sample for '+label
        # Real Lua Worker receives structured L1 and returns a small task-relevant
        # summary; no client-side tile reconstruction or action is needed.
        lua="""local r=api('observe_scene',{scale='region',layers={'geometry','objects','hazards'},maxObjects=32})
return {version=r.data.schemaVersion,coverage=r.data.coverage.samplingTruncated,source=r.data.sample.source,
        count=r.data.objects.returnedCount,world=r.worldSessionId}"""
        worker=await LuaRuntime(bridge).execute(lua,limits=Limits(seconds=12,calls=1,actions=0))
        (p/'region-worker.json').write_text(json.dumps(dict(source=lua,result=worker),indent=2))
        assert worker['status']=='completed' and worker['calls']==1 and worker['actions']==0,worker
        assert worker['value']['version']==3 and worker['value']['source']=='server_world_tile_current',worker
        history=await atomic.dispatch(bridge,'query_map',dict(mode='region',centerX=x,centerY=y,width=4,height=4,cellSize=1))
        (p/'region-history.json').write_text(json.dumps(history,indent=2))
        assert history['valid'] and 'last_known' in history['data']['knowledge']
        target=fixture['startX']+192
        navigation=await LuaRuntime(bridge).execute(f"return api('navigate_to_x',{{targetX={target}}})",limits=Limits(seconds=30,calls=1,actions=1))
        (p/'region-navigation.json').write_text(json.dumps(navigation,indent=2))
        a=navigation.get('value') or navigation.get('lastAction') or {}
        assert a.get('completed') and a['endEvidenceStatus']=='known' and a['endEvidence']['inputsReleased'],navigation
        assert all(a['evidence'][k] for k in ('arrived','grounded','safeSupport','settled')),a
        summary=dict(fixture=fixture,binding=bridge.binding,costs=rows,worker=worker['value'],
            navigation=dict(status=a['status'],startX=fixture['startX'],endX=a['evidence']['x'],updates=a['evidence']['updates']),
            scope='dedicated native; geometry injected only in copied fixture; no graphics or real model; no global map writes from L1')
        (p/'region-results.json').write_text(json.dumps(summary,indent=2))
        print(json.dumps(summary),flush=True)
    finally:bridge.close()


if __name__=='__main__':
    a=argparse.ArgumentParser(description=__doc__)
    a.add_argument('--profile',type=Path,required=True);a.add_argument('--port',type=int,required=True)
    a.add_argument('--iterations',type=int,choices=range(1,21),default=8)
    a.add_argument('--environment',action='store_true')
    asyncio.run(verify(a.parse_args()))
