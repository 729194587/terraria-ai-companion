"""Single-destination journeys across disjoint strips of an unedited generated surface."""
import argparse
import asyncio
import json
import time
from itertools import groupby
from pathlib import Path

from native_direct_input import Limits, LuaRuntime, ServerAiBridge, wait_file
from program_tasks import coordinator


def landing(columns, near):
    for x in sorted(range(near-12, near+13), key=lambda x: abs(x-near)):
        left, right = columns.get(x), columns.get(x+1)
        if (left and right and left['clear'] and left['y'] == right['y']
                and all(not c['half'] and not c['slope'] for c in (left, right))):
            return x
    raise ValueError(f'No two-tile landing near column {near}')


def movement_metrics(frames):
    # Exclude waiting at spawn and braking at the final destination.
    moving=[i for i,f in enumerate(frames) if abs(f['vx'])>=.1]
    transit=frames[moving[0]:moving[-1]+1] if moving else []
    stops=sum(1 for still,group in groupby(transit,key=lambda f:abs(f['vx'])<.1)
              if still and sum(1 for _ in group)>=3)
    stances=None
    if all('platformStock' in f for f in frames):
        stances=len({round(b['x']) for a,b in zip(frames,frames[1:])
                     if b['platformStock']<a['platformStock']})
    return dict(transitStops=stops,placementStances=stances)


async def walk_reference(bridge, start, target):
    # Independent control, with no access to the route graph or the height survey.
    # Walk toward the real target and try a jump after a blocked stride.
    x = start['x']
    stuck = 0
    gained = 0
    for step in range(80):
        remaining = target['x'] - (x + 10)
        direction = 'right' if remaining >= 0 else 'left'
        updates = min(120, max(4, int(abs(remaining)/3)))
        hold = min(15, updates) if stuck else 0
        result = await LuaRuntime(bridge).execute(
            'return api("move_input",{horizontal="%s",vertical="none",jumpHoldUpdates=%d,maxUpdates=%d})'
            % (direction,hold,updates), limits=Limits(seconds=20,calls=1,actions=1))
        action = result.get('value') or result.get('lastAction') or {}
        if not action.get('completed'):
            return step+1, action.get('reason',result['status']), gained
        evidence = action['evidence']
        new_x = evidence['endPosition']['X']
        gained += evidence['healthDelta']
        if evidence['healthDelta'] < 0:
            return step+1, 'health_decreased', gained
        if new_x <= target['x'] <= new_x+20 and abs(evidence['endPosition']['Y']+42-target['y']) <= .5:
            return step+1, 'footprint_at_goal', gained
        stuck = stuck+1 if abs(new_x-x)<16 else 0
        if stuck >= 4:
            return step+1, 'walk_and_jump_stalled', gained
        x = new_x
    return 80, 'reference_budget', gained


async def wait_camera(profile, fixture=None):
    marker=profile/'viewer'/'watch-camera.json'
    async with asyncio.timeout(300):
        while True:
            try:
                camera=json.loads(marker.read_text())
                if (marker.stat().st_mtime > time.time()-3 and
                    (fixture is None or camera['world'].replace('-','')==fixture['fixture']['world'].replace('-','') and
                     abs(camera['x']-fixture['x']-10)<32 and abs(camera['y']-fixture['y']-21)<32)):
                    return
            except (OSError,json.JSONDecodeError):
                pass
            await asyncio.sleep(.25)


async def verify(profile, port, reference=False, watch=False, shore_starts=False, west_descent=False, east_wall=False, journey_case=None, travel_contact=False, death_feedback=False, broadcast_feedback=False):
    survey = await wait_file(profile/'natural-catalog.json')
    if watch:
        await wait_camera(profile)
    columns = {row['x']: row for row in survey['columns']}
    rows = []
    routes=[('west', -165, -485), ('central', -145, 145), ('east', 165, 485)]
    if travel_contact:
        routes=[('contact-light',-145,-109),('contact-heavy',-145,-109)]
    if death_feedback:
        routes=[('death-feedback',-145,-109),('death-stop',-109,-145)]
    if broadcast_feedback:
        routes=[('news-receive',-145,-109),('news-change',-145,-109)]
    for name, a, b in routes:
        if journey_case and name!=journey_case:
            continue
        if east_wall and name!='east':
            continue
        if west_descent and name != 'west':
            continue
        if (reference or watch or shore_starts) and name == 'central':
            continue
        start, goal = (landing(columns, survey['spawnX'] + dx) for dx in (a, b))
        if east_wall:
            assert survey['seed']=='20261028'
            start,goal=2390,2402
            name='east-wall'
        if west_descent:
            assert survey['seed']=='20261028'
            # Start above the photographed ledge and land at the bottom of
            # the descent verified by the independent left-input control.
            start, goal = 1758, 1734
        heights = [columns[x]['y'] for x in range(min(start,goal), max(start,goal)+1) if x in columns]
        target = dict(x=goal*16+10, y=columns[goal]['y']*16)
        ready = profile/'natural-ready.json'
        ready.unlink(missing_ok=True)
        request = profile/'natural-request.tmp'
        placement=dict(caseId=name, offsetTiles=start-survey['spawnX'], surfaceY=columns[start]['y'])
        if east_wall:
            placement.update(startPixelX=38252,surfaceY=261)
        if shore_starts:
            assert survey['seed']=='20261028'
            if name=='west':
                placement.update(startPixelX=29638.29,surfaceY=255)
                start=int(placement['startPixelX']//16)
                placement['offsetTiles']=start-survey['spawnX']
                heights=[columns[x]['y'] for x in range(min(start,goal),max(start,goal)+1) if x in columns]
        request.write_text(json.dumps(placement))
        request.replace(profile/'natural-request.json')
        fixture = await wait_file(ready)
        assert fixture['Settled'] and fixture['SafeSupport'], fixture
        if watch:
            await wait_camera(profile,fixture)
            await asyncio.sleep(2)
        bridge = ServerAiBridge(port)
        if broadcast_feedback:
            from native_broadcast_feedback import exercise
            rows.append(await exercise(profile,port,bridge,name,target))
            (profile/'natural-finish').write_text(name)
            await wait_file(profile/f'natural-motion-{name}.json')
            (profile/'natural-results.json').write_text(json.dumps(dict(scenarios=rows),ensure_ascii=False))
            continue
        if death_feedback:
            from native_death_feedback import exercise
            rows.append(await exercise(profile,port,bridge,name))
            (profile/'natural-finish').write_text(name)
            await wait_file(profile/f'natural-motion-{name}.json')
            (profile/'natural-results.json').write_text(json.dumps(dict(scenarios=rows),ensure_ascii=False))
            continue
        if reference:
            try:
                count, stop, life_delta = await walk_reference(bridge, fixture, target)
            finally:
                bridge.close()
            await asyncio.sleep(1)
            (profile/'natural-finish').write_text(name)
            frames=await wait_file(profile/f'natural-motion-{name}.json')
            last=frames[-1]
            arrived=(last['Settled'] and last['SafeSupport'] and last['x']<=target['x']<=last['x']+20
                     and abs(last['y']+42-target['y'])<=.5)
            row=dict(case=name, target=target, arrived=arrived, stop=stop, inputs=count,
                     netTiles=round((last['x']-fixture['x'])*(1 if goal>start else -1)/16,2),
                     endX=last['x'], endFeetY=last['y']+42, healthDelta=life_delta)
            rows.append(row)
            (profile/'natural-results.json').write_text(json.dumps(dict(scenarios=rows),indent=2))
            print(json.dumps(row),flush=True)
            continue
        manager = coordinator(bridge)
        try:
            submitted = await LuaRuntime(bridge).execute(
                "return api('submit_task',{operation='navigate_to',arguments={x=%s,y=%s}})" %
                (target['x'], target['y']), limits=Limits(seconds=12,calls=1,actions=1))
            assert submitted['status'] == 'completed' and submitted['actions'] == 1, submitted
            task = submitted['value']['taskId']
            async with asyncio.timeout(175):
                while True:
                    view = await manager.invoke('await_task', dict(taskId=task,milliseconds=10000))
                    if view['status'] != 'running':
                        break
            result = view['result']
            (profile/f'{name}-receipt.json').write_text(json.dumps(result))
            assert result['endEvidence']['inputsReleased'] and view['taskId'] == task, result
            if result['completed']:
                arrival = result['task']['result']['evidence']
                assert (arrival['settled'] and arrival['safeSupport'] and arrival['arrived']
                        and arrival['x'] <= target['x'] <= arrival['x']+20
                        and abs(arrival['feetY']-target['y']) <= .5), arrival
        finally:
            await manager.close()
            bridge.close()
        (profile/'natural-finish').write_text(name)
        frames = await wait_file(profile/f'natural-motion-{name}.json', 30)
        dx = [(f['x']-fixture['x'])/16 * (1 if goal>start else -1) for f in frames]
        progress = result.get('task',{}).get('progress',{})
        row = dict(case=name, seed=survey['seed'], startColumn=start, target=target, distanceTiles=abs(goal-start),
                   surfaceRelief=max(heights)-min(heights), surfaceChanges=sum(a!=b for a,b in zip(heights,heights[1:])),
                   status=result['status'], reason=result['reason'], detail=result.get('reasonDetail'),
                   netTiles=round(dx[-1],2), furthestTiles=round(max(dx),2), endX=frames[-1]['x'],
                   endFeetY=frames[-1]['y']+42, ticks=frames[-1]['tick']-frames[0]['tick'],
                   dug=progress.get('dugTiles'), placed=progress.get('placedTiles'),
                   hookAttached=any(f['hookCount']>0 for f in frames))
        first=next((i for i,f in enumerate(frames) if f.get("platformStock",60)<frames[0].get("platformStock",60)),None)
        row["firstPlatform"]=None if first is None else dict(x=frames[first]["x"],feet=frames[first]["y"]+42,
            everWet=any(f["wet"] for f in frames[:first+1]),ticks=frames[first]["tick"]-frames[0]["tick"])
        row.update(movement_metrics(frames))
        leaf = (result.get('task',{}).get('lastResult') or {}).get('evidence') or {}
        nav = leaf.get('navigation') or {}
        graph = nav.get('SurfaceGraph') or {}
        row['lastPlanner'] = dict(reason=nav.get('Reason'), searchExit=nav.get('SearchExit'),
                                  waitUpdates=nav.get('SearchWaitUpdates'), jumps=nav.get('CompletedJumps'),
                                  radius=graph.get('Radius'), candidateEdges=graph.get('CandidateEdges'),
                                  verifiedEdges=graph.get('VerifiedEdges'))
        rows.append(row)
        (profile/'natural-results.json').write_text(json.dumps(dict(scenarios=rows),indent=2))
        print(json.dumps(row), flush=True)
        if travel_contact:
            loss=frames[0]['life']-min(f['life'] for f in frames)
            assert loss>0, row
            if name=='contact-light':
                assert result['completed'], row
            else:
                assert result['status']=='cancelled' and result['reason']=='task_interrupt_health_decreased',row
        if east_wall:
            assert result['completed'] and 0<row['placed']<=4 and first is not None and not frames[first]['Grounded'], row
            placements=[c['result']['evidence'] for c in result['task']['progress']['children']
                        if c['reason']=='native_tile_placed_and_consumed']
            assert placements and all(e['placedByActor'] and e['consumedCount']==1 for e in placements), placements
            ledge=placements[0]
            assert any(f['Settled'] and abs(f['y']+42-ledge['y']*16)<=1
                       and f['x']<(ledge['x']+1)*16 and f['x']+20>ledge['x']*16 for f in frames[first:]), ledge
        if west_descent:
            assert result['completed'], row
        if shore_starts:
            assert row["placed"]>0 and first is not None and not row["firstPlatform"]["everWet"], row
            if name=="west":
                assert abs(row["firstPlatform"]["x"]-fixture["x"])<2, row
        if watch:
            await asyncio.sleep(12)


async def replay_stops(profile, port, baseline):
    survey = await wait_file(profile/'natural-catalog.json')
    rows = []
    for original in json.loads(baseline.read_text())['scenarios']:
        if original['status'] == 'completed':
            continue
        assert survey['seed'] == original['seed']
        x, feet = original['endX'], original['endFeetY']
        name = original['case'] + '-replay'
        (profile/'natural-probe-direction').write_text('-1' if original['target']['x']<x else '1')
        ready = profile/'natural-ready.json'
        ready.unlink(missing_ok=True)
        request = profile/'natural-request.tmp'
        request.write_text(json.dumps(dict(caseId=name, offsetTiles=int(x//16)-survey['spawnX'],
                                           startPixelX=x, surfaceY=int(feet//16))))
        request.replace(profile/'natural-request.json')
        start = await wait_file(ready)
        assert start['Settled'] and abs(start['x']-x)<.02 and abs(start['y']+42-feet)<.02, start
        bridge = ServerAiBridge(port)
        direction = 'left' if original['target']['x'] < x else 'right'
        sign = -1 if direction == 'left' else 1
        try:
            for hold in (0, 15):
                source = ('return api("move_input",{horizontal="%s",vertical="none",'
                          'jumpHoldUpdates=%d,maxUpdates=100})' % (direction, hold))
                result = await LuaRuntime(bridge).execute(source, limits=Limits(seconds=20,calls=1,actions=1))
                action = result.get('value') or result.get('lastAction')
                assert action['completed'] and action['endEvidence']['inputsReleased'], action
                if sign*(action['evidence']['endPosition']['X']-x)>16*8:
                    break
        finally:
            bridge.close()
        await asyncio.sleep(1)
        (profile/'natural-finish').write_text(name)
        frames = await wait_file(profile/f'natural-motion-{name}.json')
        final = frames[-1]
        row = dict(case=name, startX=x, endX=final['x'], startFeetY=feet, endFeetY=final['y']+42,
                   netTiles=sign*(final['x']-x)/16, settled=final['Settled'], jumpUsed=bool(hold),
                   healthDelta=action['evidence'].get('healthDelta'))
        rows.append(row)
        print(json.dumps(row),flush=True)
        (profile/'natural-results.json').write_text(json.dumps(dict(scenarios=rows),indent=2))


async def watch_west_stop(profile, results):
    survey = await wait_file(profile/'natural-catalog.json')
    stop = next(row for row in json.loads(results.read_text())['scenarios'] if row['case']=='west')
    assert survey['seed']==stop['seed']
    x = stop['endX']
    column = int(x//16)
    ground = next(row['y'] for row in survey['columns'] if row['x']==column)
    request = profile/'natural-request.tmp'
    request.write_text(json.dumps(dict(caseId='west-photo', offsetTiles=column-survey['spawnX'],
                                       startPixelX=x, surfaceY=ground)))
    request.replace(profile/'natural-request.json')
    fixture = await wait_file(profile/'natural-ready.json')
    await wait_camera(profile,fixture)
    print('West stop camera ready', fixture['x'], fixture['y'], flush=True)
    (profile/'natural-finish').write_text('west-photo')
    marker = profile/'viewer'/'watch-camera.json'
    while time.time()-marker.stat().st_mtime < 60 and not (profile/'finish-photo').exists():
        await asyncio.sleep(1)
    (profile/'natural-results.json').write_text(json.dumps(dict(scenarios=[],photo=fixture)))


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--profile',type=Path,required=True)
    parser.add_argument('--port',type=int,required=True)
    parser.add_argument('--replay-stops',type=Path)
    parser.add_argument('--walk-reference',action='store_true')
    parser.add_argument('--watch',action='store_true')
    parser.add_argument('--journey-case',choices=('east','west','central'))
    parser.add_argument('--broadcast-feedback',action='store_true')
    parser.add_argument('--death-feedback',action='store_true')
    parser.add_argument('--travel-contact',action='store_true')
    parser.add_argument('--east-wall',action='store_true')
    parser.add_argument('--west-descent',action='store_true')
    parser.add_argument('--shore-starts',action='store_true')
    parser.add_argument('--watch-west-stop',type=Path)
    args = parser.parse_args()
    asyncio.run(watch_west_stop(args.profile,args.watch_west_stop) if args.watch_west_stop else replay_stops(args.profile,args.port,args.replay_stops) if args.replay_stops else verify(args.profile,args.port,args.walk_reference,args.watch,args.shore_starts,args.west_descent,args.east_wall,args.journey_case,args.travel_contact,args.death_feedback,args.broadcast_feedback))
