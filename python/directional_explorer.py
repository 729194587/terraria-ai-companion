"""Host-side short trips using native navigate_to and observation-scoped landing candidates."""
import asyncio
import time

import atomic_operations as atomic
from control import BridgeError

DIRECTIONS = ('left', 'right', 'up', 'down')
MAX_SEGMENTS = 6
SEGMENT_SECONDS = 12
TRIP_SECONDS = 45
SCAN_SECONDS = .5
STEP_PIXELS = 12 * 16


def position(actor):
    return dict(leftX=actor['boundsWorldPixels']['x'], feetY=actor['feetY'])


def destination(scene, direction):
    actor = scene['actor']
    left = actor['boundsWorldPixels']['x']
    width = actor['boundsWorldPixels']['width']
    feet = actor['feetY']
    options = []
    for row in scene['landingRegions']['items']:
        region = row['target']
        low, high = region['minPlayerLeftX'], region['maxPlayerLeftX']
        desired = left + {'right': STEP_PIXELS, 'left': -STEP_PIXELS}.get(direction, 0)
        landing = max(low, min(high, desired))
        dx, dy = landing - left, region['surfaceY'] - feet
        advance = {'right': dx, 'left': -dx, 'down': dy, 'up': -dy}[direction]
        if advance < 16:
            continue
        # AtDestination(x,y) accepts player-left in [x-width,x]. Keep the current
        # stance outside that interval even on the platform we already occupy.
        x = round(landing + width / 2)
        lateral = direction in ('left', 'right')
        if lateral and x-width <= left <= x:
            continue
        score = (abs(advance-STEP_PIXELS) + abs(dy) if lateral else abs(dx)+advance)
        options.append((score, x, round(region['surfaceY'])))
    if options:
        _, x, y = min(options)
        return dict(x=x, y=y)


async def run(mode, direction, version):
    bridge = mode.agent.bridge
    binding = dict(bridge.binding)
    deadline = time.monotonic() + TRIP_SECONDS
    segments, samples, findings = [], [], []
    start = finish = None
    reason = 'budget_reached'
    detail = 'exploration budget reached'
    last = {}
    result = dict(direction=direction, objective=mode.objective, coordinateUnit='world_pixels',
                  start=None, finish=None, segments=segments, findings=findings, coverage=samples,
                  progress=dict(completedSegments=0, dx=None, dy=None), lastActionResult={})
    # Publish in-flight evidence so that revocation can finish this same receipt.
    mode.progress['explorationResult'] = result

    def authorized():
        if version != mode.version or mode.agent.bridge is not bridge or bridge.binding != binding:
            raise asyncio.CancelledError

    async def sense():
        nonlocal finish
        scan = await mode.resource_scan(version)
        finish = result['finish'] = scan['position']
        if start:
            result['progress'].update(dx=finish['leftX']-start['leftX'], dy=finish['feetY']-start['feetY'])
        coverage = {k:v for k,v in scan.items() if k in (
            'source','gameUpdate','bounds','requestedTiles','scannedTiles','unknownCells','complete',
            'truncated','elapsedMilliseconds','observedAtUnixMs','position')}
        samples.append(coverage)
        if scan['discoveries'] or scan['changes']:
            findings.append(dict(**coverage, items=scan['discoveries'], changes=scan['changes']))
            return 'discovery'
        if not scan['complete']:
            return 'observation_incomplete'

    for _ in range(MAX_SEGMENTS):
        authorized()
        try:
            local = await atomic.dispatch(bridge, 'observe_scene', dict(layers=['landingRegions'], maxRegions=24))
            if not local.get('valid'):
                raise BridgeError('local landing observation unavailable')
            scene = local['data']
            finish = position(scene['actor'])
            if start is None:
                start = result['start'] = result['finish'] = finish
            # Recheck both departure and segment boundaries against the model's baseline.
            detected = await sense() if not segments else None
        except (BridgeError, OSError) as error:
            reason, detail = 'observation_incomplete', str(error)
            break
        if detected:
            reason = detail = detected
            break
        if time.monotonic() >= deadline:
            break
        target = destination(scene, direction)
        if target is None:
            reason = 'blocked'
            detail = 'no forward landing in current local candidates'
            break
        authorized()
        segment_deadline = min(deadline, time.monotonic()+SEGMENT_SECONDS)
        record, rejected = await mode.submit_owned('navigate_to', target, version)
        if record is None:
            last = rejected
            reason = 'blocked' if rejected.get('dispatchState')=='not_admitted' else 'outcome_unknown'
            detail = rejected.get('reason',reason)
            break
        task_id = record['view']['taskId']
        segment = dict(start=finish, target=target, taskId=task_id)
        segments.append(segment)
        planned = None
        try:
            while not record['done'].is_set():
                authorized()
                remaining = segment_deadline-time.monotonic()
                if remaining <= 0:
                    planned = 'budget_reached'
                    break
                try:
                    await asyncio.wait_for(record['done'].wait(), min(remaining, SCAN_SECONDS))
                except asyncio.TimeoutError:
                    if time.monotonic() >= segment_deadline:
                        continue
                    try:
                        planned = await sense()
                    except (BridgeError, OSError) as error:
                        planned, detail = 'observation_incomplete', str(error)
                    if planned:
                        break
        finally:
            if not record['done'].is_set():
                try:
                    await mode.mailbox.invoke('cancel_task', dict(taskId=task_id))
                except (BridgeError, OSError, ValueError) as error:
                    # A rejected stop does not imply the already admitted navigation was rejected.
                    raise BridgeError(f'Cannot confirm release of navigation {task_id}: {error}') from error
        last = await mode.terminal(record)
        segment['receipt'] = result['lastActionResult'] = mode.receipt_fields(last)
        if last.get('dispatchState') == 'not_admitted':
            reason, detail = 'blocked', last.get('reason','navigation rejected')
            break
        if (last.get('actionId')!=task_id or last.get('inputsReleased') is not True
                or last.get('endEvidenceStatus')!='known' or last.get('status') not in
                ('cancelled','completed','failed','timeout')):
            reason, detail = 'outcome_unknown', last.get('reason','missing terminal release')
            break
        if last['status']=='cancelled' and (not planned or last.get('reason')!='explicit_cancel'):
            reason, detail = 'interrupted', last.get('reason','navigation interrupted')
            break
        authorized()
        result['progress']['completedSegments'] += int(last['status']=='completed')
        try:
            detected = await sense()
        except (BridgeError, OSError) as error:
            detected, detail = 'observation_incomplete', str(error)
        segment['finish'] = finish
        if last['status'] in ('failed','timeout'):
            reason, detail = 'blocked', last.get('reason','navigation failed')
            break
        if findings or planned or detected:
            reason = 'discovery' if findings else planned or detected
            if reason != 'observation_incomplete' or detail=='exploration budget reached':
                detail = reason
            break
        if finish == segment['start']:
            reason, detail = 'blocked', 'navigation completed without observed displacement'
            break

    result.update(returnReason=reason, reason=detail, lastActionResult=mode.receipt_fields(last))
    normal = reason in ('discovery','budget_reached')
    status = 'completed' if normal else {'outcome_unknown':'unknown','interrupted':'cancelled'}.get(reason,'failed')
    return dict(status=status, completed=normal, inputsReleased=reason!='outcome_unknown',
                reason=detail, explorationResult=result)
