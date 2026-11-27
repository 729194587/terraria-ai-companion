"""Host-side short trips using native navigate_to and observation-scoped landing candidates."""
import asyncio
import time

import atomic_operations as atomic
from control import BridgeError
from passage_memory import PassageMemory, nearby

DIRECTIONS = ('left', 'right', 'up', 'down')
MAX_SEGMENTS = 6
SEGMENT_SECONDS = 12
TRIP_SECONDS = 45
STEP_PIXELS = 32 * 16


EXPLORE_TOOL = atomic.tool('explore', 'Explore in a direction until a discovery, local obstruction or budget.',
    {'direction': {'type':'string', 'enum':list(DIRECTIONS)},
     'objective': {'type':'string','minLength':1,'maxLength':240},
     'seek': {'type':'string','minLength':1,'maxLength':96,
              'description':'Optional discovery to stop for: passage, a resource kind (ore_component, chest, life_crystal), or an exact contentKey such as Terraria/Iron. Default: any.'}}, ['direction','objective'])


def valid_options(args):
    seek = args.get('seek', 'any')
    return (set(args) <= {'direction','seek'} and args.get('direction') in DIRECTIONS
            and isinstance(seek,str) and 0 < len(seek.strip()) <= 96)


class ExplorerContext:
    """Shared observation and receipt format for lease-specific navigation hosts."""
    @staticmethod
    def receipt_fields(receipt):
        return {key: receipt[key] for key in ('actionId','operation','status','reason','completed',
                'effectVerified','inputsReleased','endEvidenceStatus','dispatchState') if key in receipt}

    async def resource_scan(self, version):
        scan = await self._resource_window(version,120,68)
        if not scan['samplingBudgetReached']:
            return scan
        # Preserve positive sightings from the wider, unfinished window.
        local = await self._resource_window(version,60,34)
        local['widerSample'] = dict(**scan_coverage(scan), discoveries=scan['discoveries'], changes=scan['changes'])
        return local

    async def _resource_window(self, version, width, height):
        reply = await atomic.dispatch(self.agent.bridge, 'observe_scene',
            dict(scale='region', width=width, height=height, layers=['resources'], maxObjects=32))
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


def destinations(scene, direction):
    actor = scene['actor']
    left = actor['boundsWorldPixels']['x']
    width = actor['boundsWorldPixels']['width']
    feet = actor['feetY']
    options = []
    survey = scene.get('passages', {})
    connected = set(survey.get('connectedLandingIds', ()))
    def offer(x, y, priority):
        dx, dy = x-width/2-left, y-feet
        lateral = direction in ('left','right')
        advance = {'right':dx,'left':-dx,'down':dy,'up':-dy}[direction]
        if 16 <= advance <= STEP_PIXELS and not (lateral and x-width <= left <= x):
            score = abs(advance-STEP_PIXELS) + (abs(dy) if lateral else abs(dx))
            options.append((priority,score,round(x),round(y)))

    if direction == 'down' and survey.get('descentStep'):
        step = survey['descentStep']
        offer(step['x'], step['y'], -1)
    for e in survey.get('exits', ()):
        # Enter the observed passage by stages. Both upward and downward travel
        # need a real landing. An open shaft has no navigation target.
        if direction in ('up','down'):
            offer(e['target']['x'], e['target']['y'], 0)
    for route in survey.get('routes', ()):
        if route['direction']==direction and route['kind']=='lateral_landing':
            offer(route['target']['x'], route['target']['y'], 0)
    for row in scene['landingRegions']['items']:
        if survey and direction in ('up','down') and row['id'] not in connected:
            continue
        region = row['target']
        low, high = region['minPlayerLeftX'], region['maxPlayerLeftX']
        desired = left + {'right': STEP_PIXELS, 'left': -STEP_PIXELS}.get(direction, 0)
        landing = max(low, min(high, desired))
        offer(landing+width/2, region['surfaceY'], 1)
    return [dict(x=x,y=y) for x,y in dict.fromkeys((x,y) for _,_,x,y in sorted(options))]


def destination(scene, direction):
    return next(iter(destinations(scene,direction)), None)


def scan_coverage(scan):
    coverage = {k: scan[k] for k in (
        'source','gameUpdate','bounds','requestedTiles','scannedTiles','unknownCells','complete',
        'samplingBudgetReached','unknownByReason','objectGroupingTruncated','truncated',
        'elapsedMilliseconds','observedAtUnixMs','position') if k in scan}
    if 'widerSample' in scan:
        coverage['widerCoverage'] = scan_coverage(scan['widerSample'])
    return coverage


def sufficient_scan(scan, seek):
    if scan['complete']:
        return True
    # Dense underground resource lists can exceed the output cap even when
    # every tile was sampled. Leave the list marked partial in the receipt.
    return (seek in ('any','passage') and scan.get('truncated') is True and scan.get('unknownCells') == 0
            and scan.get('scannedTiles',0) > 0 and scan['scannedTiles'] == scan.get('requestedTiles')
            and not scan.get('samplingBudgetReached') and not scan.get('objectGroupingTruncated')
            and scan.get('matchedCount',0) > len(scan.get('items',())))


async def landing_scene(bridge, direction):
    args = dict(layers=['landingRegions'])
    if 'server_ai_passages_v1' in bridge.hello.get('capabilities', ()):
        args['includePassages'] = True
    if 'server_ai_directional_landings_v1' in bridge.hello.get('capabilities', ()):
        args['landingDirection'] = direction
    for cap in (12, 24):
        reply = await atomic.dispatch(bridge, 'observe_scene', dict(args, maxRegions=cap))
        if not reply.get('valid'):
            raise BridgeError('local landing observation unavailable')
        scene = reply['data']
        if not scene['landingRegions']['truncated']:
            return scene
    if scene.get('passages',{}).get('originResolved'):
        return scene  # Exits were computed before limiting the landing list.
    raise BridgeError('landingRegions.truncated after expanding maxRegions from 12 to 24')


async def run(mode, direction, version, baseline, *, report_initial_findings=False, seek='any'):
    bridge = mode.agent.bridge
    binding = dict(bridge.binding)
    deadline = time.monotonic() + TRIP_SECONDS
    segments, samples, findings = [], [], []
    start = finish = baseline['position']
    reason = 'budget_reached'
    detail = 'exploration budget reached'
    last = {}
    memory = mode.passage_memory
    retries = 0
    next_scene = None
    result = dict(direction=direction, objective=mode.objective, coordinateUnit='world_pixels',
                  start=start, finish=finish, baseline=scan_coverage(baseline), segments=segments, findings=findings, coverage=samples,
                  progress=dict(completedSegments=0, newStances=0, dx=0., dy=0.), lastActionResult={})
    # Publish in-flight evidence so that revocation can finish this same receipt.
    mode.progress['explorationResult'] = result
    result['seek'] = seek
    result['baselineSufficient'] = bool(sufficient_scan(baseline,seek))

    def record_findings(scan):
        stop = False
        for sample in (scan.get('widerSample', {}),scan):
            items, changes = sample.get('discoveries', []), sample.get('changes', [])
            if items or changes:
                findings.append(dict(**scan_coverage(sample), items=items, changes=changes))
                stop |= any(seek in ('any', h.get('kind'), h.get('contentKey')) for h in (*items,*changes))
        return stop

    def authorized():
        if version != mode.version or mode.agent.bridge is not bridge or bridge.binding != binding:
            raise asyncio.CancelledError

    def observe_passages(scene):
        survey = scene.get('passages', {})
        if survey:
            tick = scene['gameUpdate']
            result['passages'] = dict(gameUpdate=tick, **{k:v for k,v in survey.items() if k!='connectedLandingIds'})
            if memory.observe(survey,tick):
                result['passageDiscovery'] = dict(gameUpdate=tick, returnTarget=survey['returnTarget'],
                                                  junction=survey['junction'], exits=survey['exits'],
                                                  openShaft=survey.get('openShaft'))
                return True
        return False

    async def sense():
        nonlocal finish
        scan = await mode.resource_scan(version)
        finish = result['finish'] = scan['position']
        result['progress'].update(dx=finish['leftX']-start['leftX'], dy=finish['feetY']-start['feetY'])
        coverage = scan_coverage(scan)
        samples.append(coverage)
        if record_findings(scan):
            return 'discovery'
        if not sufficient_scan(scan,seek):
            return 'observation_incomplete'

    for _ in range(MAX_SEGMENTS):
        authorized()
        if not segments and report_initial_findings and record_findings(baseline):
            if result['baselineSufficient']:
                reason = detail = 'discovery'
                break
        if not result['baselineSufficient']:
            reason, detail = 'observation_incomplete', 'initial resource baseline incomplete'
            break
        try:
            if next_scene is None:
                scene = await landing_scene(bridge, direction)
                new_passage = observe_passages(scene)
            else:
                scene, next_scene = next_scene, None
                new_passage = False
        except (BridgeError, OSError) as error:
            reason, detail = 'observation_incomplete', str(error)
            break
        finish = position(scene['actor'])
        if not segments:
            start = result['start'] = result['finish'] = finish
        if time.monotonic() >= deadline:
            break
        survey = scene.get('passages', {})
        tick = scene.get('gameUpdate', 0)
        origin = dict(x=round(finish['leftX']+scene['actor']['boundsWorldPixels']['width']/2), y=round(finish['feetY']))
        if new_passage and seek in ('any','passage'):
            reason, detail = 'discovery', 'new passage to investigate'
            break
        choices = [p for p in destinations(scene,direction) if not memory.failed(origin,p,tick)]
        step = survey.get('descentStep')
        if direction == 'down' and step in choices:
            target = step
        else:
            walked = [r['target'] for r in survey.get('routes', ()) if r['visitState']=='walked_nearby']
            target = next((p for p in choices if not any(nearby(p,old,2) for old in (*walked,*memory.arrivals))),
                          next(iter(choices), None))
        if target is None:
            if last.get('status')=='failed':
                reason, detail = 'blocked', last['reason']
            elif direction == 'down' and survey.get('openShaft'):
                reason, detail = 'observation_incomplete', 'shaft continues below the observed window; no confirmed lower landing'
            elif (scene['landingRegions']['truncated'] or survey and not survey['originResolved']):
                reason, detail = 'observation_incomplete', 'landing survey incomplete'
            elif scene['coverage']['geometryComplete']:
                reason, detail = 'blocked', 'no forward landing in current local candidates'
            else:
                reason, detail = 'observation_incomplete', 'no forward landing in partially known geometry'
            break
        authorized()
        segment_deadline = time.monotonic() + SEGMENT_SECONDS
        record, rejected = await mode.submit_owned('navigate_to', target, version)
        if record is None:
            last = rejected
            reason = 'blocked' if rejected.get('dispatchState')=='not_admitted' else 'outcome_unknown'
            detail = rejected.get('reason',reason)
            break
        task_id = record['view']['taskId']
        segment = dict(start=finish, target=target, taskId=task_id)
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
        if last['status']=='completed' and 'observationError' not in segment and finish != segment['start']:
            point = dict(x=round(finish['leftX']+scene['actor']['boundsWorldPixels']['width']/2), y=round(finish['feetY']))
            result['progress']['newStances'] += int(memory.arrived(point))
            if survey and not expired:
                try:
                    next_scene = await landing_scene(bridge, direction)
                    authorized()
                    if observe_passages(next_scene) and seek in ('any','passage') and detected is None:
                        detected = 'discovery'
                except (BridgeError, OSError) as error:
                    detected, detail = 'observation_incomplete', str(error)
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
            local_failure = detail in ('collision','navigation_local_frontier_exhausted','navigation_stalled',
                                       'navigation_liquid_local_frontier_exhausted','navigation_repeated_edit_checkpoint')
            if last['status']=='failed' and local_failure:
                failed_at = samples[-1].get('gameUpdate',tick) if samples and 'observationError' not in segment else tick
                memory.reject(origin,target,failed_at)
                if not detected and retries < 2 and len(segments)<MAX_SEGMENTS:
                    retries += 1
                    continue
            break
        if detected:
            reason = detected
            if detail == 'exploration budget reached':
                detail = 'segment-end resource scan incomplete'
            break
        if finish == segment['start']:
            reason, detail = 'blocked', 'navigation completed without observed displacement'
            break
        reason, detail = 'budget_reached', 'exploration budget reached'

    result.update(returnReason=reason, reason=detail, lastActionResult=mode.receipt_fields(last),
                  passageMemory=memory.summary())
    if reason not in ('outcome_unknown','interrupted'):
        result['resumeOptions'] = memory.finish_trip(result)
    normal = reason in ('discovery','budget_reached')
    status = 'completed' if normal else {'outcome_unknown':'unknown','interrupted':'cancelled',
                                         'segment_time_limit':'timeout'}.get(reason,'failed')
    return dict(status=status, completed=normal, inputsReleased=reason!='outcome_unknown',
                reason=detail, explorationResult=result)
