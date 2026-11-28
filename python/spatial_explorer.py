"""Spatial exploration on the existing body's single owned navigate_to task."""
import asyncio
import sqlite3
import time

from control import BridgeError
from directional_explorer import landing_scene, position, route_hints, scan_coverage, TRIP_SECONDS
from exploration_frontiers import MapWindow, candidates
from exploration_session import ExplorationSession

STALL_LIMIT = 4
# Native navigation renews its progress lease and suspends it during reflexes.
# This outer guard also covers a frozen game clock or an unresponsive monitor.
TASK_GUARD_SECONDS = 180


async def map_window(mode, center=None):
    args = dict(mode='region',width=128,height=96,cellSize=1,includeVisits=True)
    if center:
        args.update(centerX=center[0],centerY=center[1])
    reply = await mode.read_map(args)
    if not reply.get('valid'):
        raise BridgeError('Exploration map unavailable')
    try:
        return MapWindow(reply['data'])
    except (KeyError,TypeError,ValueError) as error:
        raise BridgeError('Incomplete exploration map response') from error


def foot(scene):
    actor = scene['actor']
    rect = actor['boundsWorldPixels']
    return int((rect['x']+rect['width']/2)//16), int(actor['feetY']//16)


async def select(mode, session, scene, window, here, direction, local):
    choose = lambda goals, view: session.choose(goals,here,view,direction)
    goal = choose(local,window)
    if goal and goal['natural'] and not goal['history']:
        return goal
    # A remembered arm outside this live survey may still be in the map window.
    # Its original entrance was connected; Navigation rechecks the landing.
    bounds = scene.get('bounds',{})
    saved_goals = session.saved(here)
    recalls = []
    for saved in saved_goals:
        x,y = saved['target']['x']//16,saved['y']
        if (not saved.get('knownAccess') or saved['history'] or not window.contains(x,y)
                or bounds.get('minX',x)<=x<=bounds.get('maxX',x) and
                   bounds.get('minY',y)<=y<=bounds.get('maxY',y)):
            continue
        if window.cell(x,y) in ('?','#','=') and window.cell(x,y-1) in ('?',' ','='):
            recalls.append(saved)
    recall = choose(recalls,window)
    if recall:
        return dict(recall,natural=False,source='saved_landing')
    if goal and not goal['history']:
        return goal
    regional = session.refresh(candidates(scene,window,regional=True),window)
    goal = choose(regional,window)
    if goal and not goal['history']:
        return goal
    # Reread saved, out-of-window branches before handing them to Navigation.
    # A wider recall remains bounded even on a large world.
    centers = set()
    for saved in saved_goals:
        x,y = saved['target']['x']//16,saved['y']
        if window.contains(x,y) or any(abs(x-a)<48 and abs(y-b)<32 for a,b in centers):
            continue
        centers.add((x,y))
        remote = await map_window(mode,(x,y))
        if remote.identity!=window.identity:
            raise BridgeError('Exploration map identity changed')
        empty = dict(actor=scene['actor'],landingRegions=dict(items=[]))
        options = session.refresh(candidates(empty,remote,regional=True),remote)
        fallback = choose(options,remote)
        if fallback and not fallback['history']:
            return fallback
        if len(centers)>=3:
            break
    return goal


async def navigate(mode, goal, version, segment, authorized):
    record, rejected = await mode.submit_owned('navigate_to',goal['target'],version)
    if record is None:
        return rejected
    task_id = segment['taskId'] = record['view']['taskId']
    deadline = time.monotonic()+TASK_GUARD_SECONDS
    try:
        while not record['done'].is_set():
            authorized()
            remaining = deadline-time.monotonic()
            if remaining<=0:
                segment['guardExpired'] = True
                break
            action = record['view'].get('action',{})
            phase = (action.get('task') or {}).get('phase')
            if action.get('executionState')=='paused':
                phase = action.get('reason') or 'paused'
            if phase and phase!=segment.get('phases',[None])[-1]:
                segment.setdefault('phases',[]).append(phase)
            try:
                await asyncio.wait_for(record['done'].wait(),min(1,remaining))
            except asyncio.TimeoutError:
                pass
    finally:
        if not record['done'].is_set():
            await mode.mailbox.invoke('cancel_task',dict(taskId=task_id))
    return await asyncio.wait_for(mode.terminal(record),mode.agent.bridge.timeout+3)


def record_scan(session, result, scan, seek):
    matched = False
    for sample in (scan.get('widerSample',{}),scan):
        hits = []
        for item in sample.get('discoveries',[]):
            key = [item['kind'],item.get('contentKey'),item.get('entityRef'),item['anchor']]
            fresh = session.discovery(['observed',*key],item)
            wanted = seek=='any' or seek and seek in (item['kind'],item.get('contentKey'))
            if wanted and session.discovery(['seek',seek,*key],item):
                matched = True
                fresh = True
            if fresh:
                hits.append(item)
        # Explicit resource requests also work if another tool already saw it.
        if seek and seek not in ('any','passage'):
            for item in sample.get('items',()):
                if seek in (item['kind'],item.get('contentKey')):
                    key = [item['kind'],item.get('contentKey'),item.get('entityRef'),item['anchor']]
                    if session.discovery(['seek',seek,*key],item):
                        matched = True
                        hits.append(item)
        if hits or sample.get('changes'):
            result['findings'].append(dict(**scan_coverage(sample),items=hits,changes=sample.get('changes',[])))
    return matched


async def run_spatial(mode, direction, version, baseline, seek):
    bridge = mode.agent.bridge
    binding = dict(bridge.binding)
    def authorized():
        if mode.version!=version or mode.agent.bridge is not bridge or binding!=bridge.binding:
            raise asyncio.CancelledError

    deadline = time.monotonic()+TRIP_SECONDS
    start = baseline['position']
    progress = dict(completedSegments=0,surveyedGoals=0,newFootTiles=0,dx=0.,dy=0.)
    result = dict(direction=direction,objective=mode.objective,seek=seek,coordinateUnit='world_pixels',
                  start=start,finish=start,baseline=scan_coverage(baseline),coverage=[],segments=[],
                  findings=[],routes=[],progress=progress,lastActionResult={})
    mode.progress['explorationResult'] = result
    session = None
    previous = None
    stalled = 0
    scan = baseline
    reason, detail = 'budget_reached','exploration checkpoint'
    try:
        while True:
            authorized()
            scene = await landing_scene(bridge,None,mode.read_scene)
            window = await map_window(mode)
            authorized()
            if session is None:
                identity = window.identity
                session = ExplorationSession(identity)
                first_count = window.count
            elif window.identity!=identity:
                raise BridgeError('Exploration map identity changed')
            here = foot(scene)
            finish = result['finish'] = position(scene['actor'])
            progress.update(newFootTiles=window.count-first_count,
                            dx=finish['leftX']-start['leftX'],dy=finish['feetY']-start['feetY'])
            result['routes'] = route_hints(scene)
            if previous:
                goal, origin, segment = previous
                segment['finish'] = finish
                proof = segment['receipt']
                surveyed = session.finish(goal,window,origin,proof.get('reason') or 'no_target_evidence')
                segment.update(surveyed=surveyed, visitedCount=window.count)
                progress['surveyedGoals'] += int(surveyed)
                stalled = 0 if surveyed else stalled+1
                previous = None
                scan = await mode.resource_scan(version)
                authorized()
                result['coverage'].append(scan_coverage(scan))
            matched = record_scan(session,result,scan,seek)
            options = session.refresh(candidates(scene,window),window)
            if seek in ('any','passage'):
                for option in options:
                    if option['hint'] and option['hint']!='lower_landing':
                        if session.discovery(['passage',option['id']],option['target']):
                            result['findings'].append(dict(items=[dict(kind='passage',anchor=dict(
                                x=option['target']['x']//16,y=option['y']),passage=option['hint'])]))
                            matched = True
                            break
            if matched:
                reason,detail='discovery','requested observation found'
                break
            if time.monotonic()>=deadline:
                break
            if stalled>=STALL_LIMIT:
                reason,detail='no_progress','four released attempts without target evidence'
                break
            goal = await select(mode,session,scene,window,here,direction,options)
            authorized()
            if time.monotonic()>=deadline:
                break
            if not goal:
                partial = (not scene['coverage']['geometryComplete'] or scene['landingRegions']['truncated']
                           or (scene.get('passages') or {}).get('originResolved') is False)
                reason = 'observation_incomplete' if partial else 'no_candidate'
                detail = 'no eligible supported target in the surveyed windows'
                break
            if hasattr(mode,'can_start_segment') and not mode.can_start_segment():
                detail='Lua exploration action or observation budget reached'
                break
            session.activate(goal)
            segment = dict(start=finish,target=goal['target'],goalId=goal['id'],kind=goal['kind'],
                           natural=goal['natural'],source=goal['source'],area=[goal['lo'],goal['hi'],goal['y']])
            if goal['kind']=='map':
                segment['witness']=goal['witness']
            result['segments'].append(segment)
            try:
                receipt = await navigate(mode,goal,version,segment,authorized)
            except (OSError,BridgeError,asyncio.TimeoutError) as error:
                reason,detail='outcome_unknown',str(error)
                break
            segment['receipt'] = result['lastActionResult'] = mode.receipt_fields(receipt)
            if receipt.get('dispatchState')=='not_admitted' and receipt.get('inputsAcquired') is False:
                reason,detail='not_admitted',receipt.get('reason','navigation refused')
                break
            if (not segment.get('taskId') or receipt.get('actionId')!=segment['taskId']
                    or receipt.get('inputsReleased') is not True or receipt.get('endEvidenceStatus')!='known'
                    or receipt.get('status') not in ('completed','failed','timeout','cancelled')):
                reason,detail='outcome_unknown','navigation terminal or input release unconfirmed'
                break
            authorized()
            progress['completedSegments'] += int(receipt['status']=='completed')
            if receipt['status']=='cancelled' or receipt.get('reason') in ('actor_dead','actor_unavailable','player_dead'):
                reason,detail='interrupted',receipt.get('reason','navigation interrupted')
                # A long segment may end far from its entrance. Keep the saved goal
                # active, and report the actor's post-release position when available.
                try:
                    stopped = await landing_scene(bridge,None,mode.read_scene)
                    authorized()
                    finish = segment['finish'] = result['finish'] = position(stopped['actor'])
                    progress.update(dx=finish['leftX']-start['leftX'],dy=finish['feetY']-start['feetY'])
                except (OSError,BridgeError):
                    segment['finishObservation']='unavailable'
                else:
                    try:
                        stopped_map = await map_window(mode)
                        authorized()
                        if stopped_map.identity==identity:
                            progress['newFootTiles']=stopped_map.count-first_count
                    except (OSError,BridgeError):
                        segment['visitObservation']='unavailable'
                break
            previous = goal,here,segment
    except (OSError,BridgeError,sqlite3.Error) as error:
        reason,detail='observation_incomplete',str(error)
    result.update(returnReason=reason,reason=detail)
    normal = reason in ('budget_reached','discovery')
    status = 'completed' if normal else {'outcome_unknown':'unknown','interrupted':'cancelled'}.get(reason,'failed')
    return dict(status=status,completed=normal,inputsReleased=reason!='outcome_unknown',
                reason=detail,explorationResult=result)
