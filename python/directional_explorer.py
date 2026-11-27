"""Host-side short trips using native navigate_to and observation-scoped landing candidates."""
import asyncio
from collections import deque
import time

import atomic_operations as atomic
from control import BridgeError

DIRECTIONS = ('left', 'right', 'up', 'down')
MAX_SEGMENTS = 6
SEGMENT_SECONDS = 12
TRIP_SECONDS = 45
STEP_PIXELS = 32 * 16


EXPLORE_TOOL = atomic.tool('explore', 'Survey in the preferred direction. After a confirmed dead end the daily explorer may branch to another observed landing.',
    {'direction': {'type':'string', 'enum':list(DIRECTIONS)},
     'objective': {'type':'string','minLength':1,'maxLength':240}}, ['direction','objective'])


class ExplorerContext:
    """Shared observation and receipt format for lease-specific navigation hosts."""
    @staticmethod
    def receipt_fields(receipt):
        return {key: receipt[key] for key in ('actionId','operation','status','reason','completed',
                'effectVerified','inputsReleased','endEvidenceStatus','dispatchState') if key in receipt}

    async def resource_scan(self, version):
        reply = await atomic.dispatch(self.agent.bridge, 'observe_scene',
            dict(scale='region', width=120, height=68, layers=['resources'], maxObjects=32))
        if version != self.version:
            raise asyncio.CancelledError
        if not reply.get('valid'):
            raise BridgeError('regional resource observation unavailable')
        data = reply['data']
        return dict(self.resources.observe(data['resources']), position=position(data['actor']),
                    observedAtUnixMs=reply.get('observedAtUnixMs'), requestedTiles=data['coverage']['requestedTiles'],
                    elapsedMilliseconds=data['sample']['elapsedMilliseconds'],
                    samplingBudgetReached=data['sample']['samplingBudgetReached'],
                    unknownByReason=data['coverage']['unknownByReason'],
                    objectGroupingTruncated=data['resources'].get('objectGroupingTruncated'))


def position(actor):
    return dict(leftX=actor['boundsWorldPixels']['x'], feetY=actor['feetY'])


class RecentRoute:
    """Recent *observed* travel and local failed approaches, in world pixels."""
    def __init__(self):
        self.legs = deque(maxlen=32)
        self.dead_ends = deque(maxlen=16)

    @staticmethod
    def near(a, b):
        return abs(a['leftX'] - b['leftX']) <= 32 and abs(a['feetY'] - b['feetY']) <= 24

    @staticmethod
    def along_retreat(point, leg):
        # A short retreat down the same slope can be split into different length hops
        # on the next scan. Compare against the corridor, allowing for uneven ground.
        a, b = leg['start'], leg['finish']
        dx, dy = b['leftX']-a['leftX'], b['feetY']-a['feetY']
        length2 = dx*dx + dy*dy
        if length2 < 32*32:
            return None
        t = ((point['leftX']-a['leftX'])*dx + (point['feetY']-a['feetY'])*dy) / length2
        nearest = max(0., min(1., t))
        if (abs(point['leftX']-a['leftX']-nearest*dx) <= 48
                and abs(point['feetY']-a['feetY']-nearest*dy) <= 48):
            return t

    def rank(self, start, end):
        visits = 0
        for leg in self.legs:
            # A failed approach can finish at the foot of the same obstruction.
            if leg['blocked'] and (self.near(start, leg['start']) or self.near(start, leg['finish'])):
                if self.near(end, leg['goal']):
                    return None
            if leg.get('retreat'):
                origin = self.along_retreat(start, leg)
                target = self.along_retreat(end, leg)
                middle = {key: (start[key]+end[key])/2 for key in ('leftX','feetY')}
                middle_t = self.along_retreat(middle, leg)
                within = [t for t in (origin, middle_t, target) if t is not None]
                if len(within) >= 2 and within[-1] < within[0]:
                    return None
            if self.near(end, leg['start']) or self.near(end, leg['finish']):
                visits += 1
            if self.near(start, leg['start']) and self.near(end, leg['finish']):
                # Allow a retreat, but do not enter the same directed leg twice.
                return None
        return visits

    def record(self, segment, direction, width, blocked):
        start, finish = segment['start'], segment['finish']
        target = segment['target']
        leg = dict(direction=direction, start=start, finish=finish,
                   goal=dict(leftX=target['x']-width/2, feetY=target['y']),
                   blocked=blocked, retreat=segment.get('rerouted', False), taskId=segment['taskId'])
        revisited = any(self.near(finish, previous[key]) for previous in self.legs
                        for key in ('start', 'finish'))
        segment['revisited'] = revisited
        self.legs.append(leg)


def destination(scene, direction, route=None):
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
        if not 16 <= advance <= STEP_PIXELS:
            continue
        # AtDestination(x,y) accepts player-left in [x-width,x]. Keep the current
        # stance outside that interval even on the platform we already occupy.
        x = round(landing + width / 2)
        lateral = direction in ('left', 'right')
        if lateral and x-width <= left <= x:
            continue
        score = abs(advance-STEP_PIXELS) + (abs(dy) if lateral else abs(dx))
        visits = route.rank(position(actor), dict(leftX=landing, feetY=region['surfaceY'])) if route else 0
        if visits is not None:
            options.append((visits, score, x, round(region['surfaceY'])))
    if options:
        _, _, x, y = min(options)
        return dict(x=x, y=y)


def scan_coverage(scan):
    return {k: scan[k] for k in (
        'source','gameUpdate','bounds','requestedTiles','scannedTiles','unknownCells','complete',
        'samplingBudgetReached','unknownByReason','objectGroupingTruncated','truncated',
        'elapsedMilliseconds','observedAtUnixMs','position') if k in scan}


def usable_survey(scan, route):
    # A regional resource scan proves presence even when clipped. Movement safety
    # is established separately by live landing observation and native navigation.
    return scan.get('complete') or (route is not None and scan.get('scannedTiles', 0) > 0
                                    and scan.get('requestedTiles', scan['scannedTiles']) > scan.get('unknownCells', 0))


async def landing_scene(bridge, direction, *, allow_partial=False):
    args = dict(layers=['landingRegions'])
    if 'server_ai_directional_landings_v1' in bridge.hello.get('capabilities', ()):
        args['landingDirection'] = direction
    for cap in (12, 24):
        reply = await atomic.dispatch(bridge, 'observe_scene', dict(args, maxRegions=cap))
        if not reply.get('valid'):
            raise BridgeError('local landing observation unavailable')
        scene = reply['data']
        if not scene['landingRegions']['truncated']:
            return scene
    if allow_partial and scene['landingRegions']['items']:
        return scene
    raise BridgeError('landingRegions.truncated after expanding maxRegions from 12 to 24')


async def alternate_landing(bridge, direction, route, deadline, authorized):
    options = []
    for alternative in DIRECTIONS:
        if time.monotonic() >= deadline:
            break
        if alternative == direction:
            continue
        authorized()
        try:
            scene = await landing_scene(bridge, alternative, allow_partial=True)
        except (BridgeError, OSError):
            continue
        other = destination(scene, alternative, route)
        if other:
            end = dict(leftX=other['x']-scene['actor']['boundsWorldPixels']['width']/2, feetY=other['y'])
            visits = route.rank(position(scene['actor']), end)
            if visits is not None:
                options.append((visits, alternative, other, scene))
    if options:
        return min(options, key=lambda option: option[:2])[1:]


async def run(mode, direction, version, baseline, *, report_initial_findings=False, route=None):
    bridge = mode.agent.bridge
    binding = dict(bridge.binding)
    deadline = time.monotonic() + TRIP_SECONDS
    segments, samples, findings = [], [], []
    start = finish = baseline['position']
    reason = 'budget_reached'
    detail = 'exploration budget reached'
    last = {}
    result = dict(direction=direction, objective=mode.objective, coordinateUnit='world_pixels',
                  start=start, finish=finish, baseline=scan_coverage(baseline), segments=segments, findings=findings, coverage=samples,
                  progress=dict(completedSegments=0, dx=0., dy=0.), lastActionResult={})
    # Publish in-flight evidence so that revocation can finish this same receipt.
    mode.progress['explorationResult'] = result

    def authorized():
        if version != mode.version or mode.agent.bridge is not bridge or bridge.binding != binding:
            raise asyncio.CancelledError

    async def sense():
        nonlocal finish
        scan = await mode.resource_scan(version)
        finish = result['finish'] = scan['position']
        result['progress'].update(dx=finish['leftX']-start['leftX'], dy=finish['feetY']-start['feetY'])
        coverage = scan_coverage(scan)
        samples.append(coverage)
        if scan['discoveries'] or scan['changes']:
            findings.append(dict(**coverage, items=scan['discoveries'], changes=scan['changes']))
            return 'discovery'
        if not usable_survey(scan, route):
            return 'observation_incomplete'

    requested_direction = direction
    for _ in range(MAX_SEGMENTS):
        authorized()
        if not segments and report_initial_findings and (baseline['discoveries'] or baseline['changes']):
            findings.append(dict(**scan_coverage(baseline), items=baseline['discoveries'], changes=baseline['changes']))
            if baseline['complete']:
                reason = detail = 'discovery'
                break
        if not usable_survey(baseline, route):
            reason, detail = 'observation_incomplete', 'initial resource baseline incomplete'
            break
        try:
            scene = await landing_scene(bridge, direction, allow_partial=route is not None)
        except (BridgeError, OSError) as error:
            reason, detail = 'observation_incomplete', str(error)
            break
        finish = position(scene['actor'])
        if not segments:
            start = result['start'] = result['finish'] = finish
        if time.monotonic() >= deadline:
            break
        target = destination(scene, direction, route)
        if target is None and route is not None:
            known_dead_end = any(d == direction and route.near(p, finish) for d,p in route.dead_ends)
            if known_dead_end or destination(scene, direction):
                branch = await alternate_landing(bridge, direction, route, deadline, authorized)
                if branch:
                    direction, target, scene = branch
                    finish = position(scene['actor'])
                    result.update(requestedDirection=requested_direction, direction=direction,
                                  reroutedAt=finish)
            elif scene['coverage']['geometryComplete']:
                route.dead_ends.append((direction, finish))
        if target is None:
            if route and destination(scene, direction):
                reason, detail = 'blocked', 'recently failed or repeated approach; choose another direction'
            elif scene['coverage']['geometryComplete']:
                reason, detail = 'blocked', 'no forward landing in current local candidates'
            else:
                reason, detail = 'observation_incomplete', 'no forward landing in partially known geometry'
            break
        authorized()
        if time.monotonic() >= deadline:
            break
        segment_deadline = time.monotonic() + SEGMENT_SECONDS
        record, rejected = await mode.submit_owned('navigate_to', target, version)
        if record is None:
            last = rejected
            reason = 'blocked' if rejected.get('dispatchState')=='not_admitted' else 'outcome_unknown'
            detail = rejected.get('reason',reason)
            break
        task_id = record['view']['taskId']
        segment = dict(start=finish, direction=direction, target=target, taskId=task_id)
        if finish == result.get('reroutedAt'):
            segment['rerouted'] = True
        segments.append(segment)
        expired = False
        try:
            authorized()
            if not record['done'].is_set():
                remaining = max(0., min(segment_deadline, deadline) - time.monotonic())
                try:
                    await asyncio.wait_for(record['done'].wait(), remaining)
                except asyncio.TimeoutError:
                    expired = True
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
        if last['status']=='cancelled' and (not expired or last.get('reason')!='explicit_cancel'):
            reason, detail = 'interrupted', last.get('reason','navigation interrupted')
            break
        authorized()
        result['progress']['completedSegments'] += int(last['status']=='completed')
        try:
            detected = await sense()
        except (BridgeError, OSError) as error:
            detected, detail = 'observation_incomplete', str(error)
            segment['observationError'] = detail
        segment['finish'] = finish
        if route is not None and 'observationError' not in segment:
            route.record(segment, direction, scene['actor']['boundsWorldPixels']['width'],
                         expired or last['status'] in ('failed','timeout') or finish == segment['start'])
        if expired and last['status'] in ('cancelled','completed'):
            # Release and observation may finish after both deadlines. Classify by
            # the deadline that ended the wait, retaining any findings from sense().
            if segment_deadline < deadline:
                reason, detail = 'segment_time_limit', 'navigation segment time limit reached'
            else:
                reason, detail = 'budget_reached', 'exploration time budget reached'
            break
        if detected == 'discovery':
            reason = detail = detected
            break
        if last['status'] in ('failed','timeout'):
            reason, detail = 'blocked', last.get('reason','navigation failed')
            break
        if detected:
            reason = detected
            if detail == 'exploration budget reached':
                detail = 'segment-end resource scan incomplete'
            break
        if finish == segment['start']:
            reason, detail = 'blocked', 'navigation completed without observed displacement'
            break

    result.update(returnReason=reason, reason=detail, lastActionResult=mode.receipt_fields(last))
    normal = reason in ('discovery','budget_reached')
    status = 'completed' if normal else {'outcome_unknown':'unknown','interrupted':'cancelled',
                                         'segment_time_limit':'timeout'}.get(reason,'failed')
    return dict(status=status, completed=normal, inputsReleased=reason!='outcome_unknown',
                reason=detail, explorationResult=result)
