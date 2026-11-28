"""Shared exploration hosts and compatibility with directional-only servers."""
import asyncio
import time
import uuid

import program_tasks

import atomic_operations as atomic
from control import BridgeError

DIRECTIONS = ('left', 'right', 'up', 'down')
MAX_SEGMENTS = 6
SEGMENT_SECONDS = 12
TRIP_SECONDS = 45
STEP_PIXELS = 32 * 16


DIRECTIONAL_TOOL = atomic.tool('explore', 'Travel in short hops in one direction, observing along the way. Stops at the budget, an obstacle or an optional seek match. Ore findings identify an observed mining tile with targetTile.',
    {'direction': {'type':'string', 'enum':list(DIRECTIONS)},
     'objective': {'type':'string','minLength':1,'maxLength':240},
     'seek': {'type':'string','minLength':1,'maxLength':96,
              'description':('To find a treasure chest (宝箱), use "chest". After each hop, check for the exact '
                             'resource kind or contentKey (e.g. "Terraria/Iron"). Use "passage" for a passage '
                             'or "any" for any finding. Omit to keep exploring.')}}, ['direction'])


EXPLORE_TOOL = atomic.tool('explore',
    'Continue exploring unvisited landings and map frontiers. Direction is an optional preference. '
    'Resume after investigating a finding by calling explore again. Ore findings include targetTile.',
    DIRECTIONAL_TOOL['function']['parameters']['properties'])


def exploration_tool(bridge):
    return EXPLORE_TOOL if 'server_ai_exploration_v2' in bridge.hello['capabilities'] else DIRECTIONAL_TOOL


REQUIRED_CAPABILITIES = frozenset({
    'server_ai_atomic_v1', 'server_ai_actions_v1', 'server_ai_tasks_v1', 'server_ai_program_tasks_v1',
    'server_ai_navigation_v1', 'server_ai_perception_v1', 'server_ai_scene_v1',
    'server_ai_scene_region_v1', 'server_ai_resources_v1', 'server_ai_region_resources_v1',
    'server_ai_directional_landings_v1',
})


def supported(operations, capabilities):
    required_ops = {'observe_scene','navigate_to','mine_target'}
    if 'server_ai_exploration_v2' in capabilities:
        return ({'server_ai_passages_v1','server_ai_map_v1'} | (REQUIRED_CAPABILITIES -
                {'server_ai_directional_landings_v1'})) <= set(capabilities) and required_ops | {'query_map'} <= set(operations)
    return REQUIRED_CAPABILITIES.issubset(capabilities) and required_ops <= set(operations)


def available(bridge):
    return program_tasks.available(bridge) and supported(bridge.supported_atomic_operations, bridge.hello['capabilities'])


def valid_options(args, bridge=None):
    seek = args.get('seek', 'any')
    return (set(args) <= {'direction','seek'} and (args.get('direction') in DIRECTIONS or
            'direction' not in args and bridge is not None and exploration_tool(bridge) is EXPLORE_TOOL)
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

    async def read_map(self, args):
        return await atomic.dispatch(self.agent.bridge, 'query_map', args)

    async def read_scene(self, args):
        return await atomic.dispatch(self.agent.bridge, 'observe_scene', args)

    async def _resource_window(self, version, width, height):
        reply = await self.read_scene(
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


class OwnedExplorer(ExplorerContext):
    """A trip owns one identified child at a time, including during admission."""

    async def submit_owned(self, operation, arguments, version):
        task = program_tasks.validate('submit_task', dict(operation=operation, arguments=arguments))
        # submit registers this ID before its first cancellable wait for native acknowledgement.
        self.task_id = uuid.uuid4().hex
        segments = self.progress['explorationResult']['segments']
        number = len(segments) if segments and 'taskId' not in segments[-1] else len(segments)+1
        origin = dict(self.origin, segment=number)
        def authorized():
            return self.version == version
        view = await self.mailbox.submit(task, origin, task_id=self.task_id,
                                         authorize=authorized, dispatch_guard=authorized)
        if not view.get('taskId'):
            self.task_id = None
            return None, view
        return self.mailbox.records[self.task_id], None

    async def stop_child(self):
        record = self.mailbox.records.get(self.task_id)
        if record is None:
            return None
        if not record['done'].is_set():
            await self.mailbox.invoke('cancel_task', dict(taskId=self.task_id))
        return await self.terminal(record)


def position(actor):
    return dict(leftX=actor['boundsWorldPixels']['x'], feetY=actor['feetY'])


def destination(scene, direction):
    """Project one hop onto a real landing; native navigation tests traversability."""
    actor = scene['actor']
    left, width = actor['boundsWorldPixels']['x'], actor['boundsWorldPixels']['width']
    feet = actor['feetY']
    survey = scene.get('passages', {})
    step = survey.get('descentStep')
    if direction == 'down' and step and 0 < step['y']-feet <= STEP_PIXELS:
        return step
    horizontal = direction in ('left', 'right')
    sign = 1 if direction in ('right', 'down') else -1
    desired = left + sign * STEP_PIXELS if horizontal else left
    connected = set(survey.get('connectedLandingIds', ()))
    candidates = []
    for row in scene['landingRegions']['items']:
        if survey and not horizontal and row['id'] not in connected:
            continue
        region = row['target']
        x = max(region['minPlayerLeftX'], min(region['maxPlayerLeftX'], desired))
        y = region['surfaceY']
        dx, dy = x-left, y-feet
        advance = sign * (dx if horizontal else dy)
        if 16 <= advance <= STEP_PIXELS:
            cost = STEP_PIXELS-advance + abs(dy if horizontal else dx)
            candidates.append((cost, round(x+width/2), round(y)))
    if candidates:
        _, x, y = min(candidates)
        return dict(x=x, y=y)


def scan_coverage(scan):
    coverage = {k: scan[k] for k in (
        'source','gameUpdate','bounds','requestedTiles','scannedTiles','unknownCells','complete',
        'samplingBudgetReached','unknownByReason','objectGroupingTruncated','truncated',
        'elapsedMilliseconds','observedAtUnixMs','position') if k in scan}
    if 'widerSample' in scan:
        coverage['widerCoverage'] = scan_coverage(scan['widerSample'])
    return coverage


async def landing_scene(bridge, direction, read_scene=None):
    args = dict(layers=['landingRegions'])
    if 'server_ai_passages_v1' in bridge.hello.get('capabilities', ()):
        args['includePassages'] = True
    if direction and 'server_ai_directional_landings_v1' in bridge.hello.get('capabilities', ()):
        args['landingDirection'] = direction
    args['maxRegions'] = 24
    reply = await read_scene(args) if read_scene else await atomic.dispatch(bridge, 'observe_scene', args)
    if not reply.get('valid'):
        raise BridgeError('local landing observation unavailable')
    return reply['data']


def route_hints(scene):
    return [{k:r[k] for k in ('direction','kind','target','visitState') if k in r}
            for r in scene.get('passages',{}).get('routes',())]


def brief(result):
    """Small decision context; the host retains the original samples and receipts."""
    by_location = {}
    for event in result['findings']:
        for hit in (*event['items'], *event.get('changes', ())):
            item = {k:hit[k] for k in ('kind','contentKey','anchor','state') if k in hit}
            anchor = item.get('anchor', {})
            location = (item.get('kind'), item.get('contentKey'), anchor.get('x'), anchor.get('y'))
            if item.get('kind') == 'ore_component':
                if item.get('state') == 'absent_at_last_location':
                    by_location.get((location, None), {}).pop('targetTile', None)
                elif not item.get('state') and hit.get('tiles'):
                    item['targetTile'] = hit['tiles'][0]
            by_location[location, item.get('state')] = item
    finds = list(by_location.values())
    preview = finds
    if len(finds) > 8:
        first_by_kind = {}
        first_by_content = {}
        for index, item in enumerate(finds):
            kind = item.get('kind')
            first_by_kind.setdefault(kind, index)
            first_by_content.setdefault((kind, item.get('contentKey')), index)
        # Cover broad kinds, then distinct materials, then other sightings in observation order.
        order = dict.fromkeys([*first_by_kind.values(), *first_by_content.values(), *range(len(finds))])
        preview = [finds[index] for index in list(order)[:8]]
    passages = [e for segment in result.get('segments',()) for e in segment.get('passages',())]
    samples = [result.get('baseline',{}), *result.get('coverage',())]
    return dict(direction=result['direction'], finish=result.get('finish'), progress=result['progress'],
                returnReason=result['returnReason'], reason=result.get('reason'), findings=preview,
                findingCount=len(finds), routes=result.get('routes',[]),
                passageSightings=[{k:e[k] for k in ('kind','direction','target')} for e in passages[-3:]],
                partialResourceScan=any(s.get('complete') is False or 'widerCoverage' in s for s in samples))


async def run(mode, direction, version, baseline, *, seek=None):
    if 'server_ai_exploration_v2' in mode.agent.bridge.hello['capabilities']:
        from spatial_explorer import run_spatial
        return await run_spatial(mode, direction, version, baseline, seek)
    if direction not in DIRECTIONS:
        raise ValueError('This server requires a direction')
    return await run_directional(mode, direction, version, baseline, seek=seek)


async def run_directional(mode, direction, version, baseline, *, seek=None):
    bridge = mode.agent.bridge
    read_scene = getattr(mode, 'read_scene', None)
    binding = dict(bridge.binding)
    deadline = time.monotonic() + TRIP_SECONDS
    segments, samples, findings = [], [], []
    start = finish = baseline['position']
    reason, detail = 'budget_reached', 'segment budget reached'
    last = {}
    result = dict(direction=direction, objective=mode.objective, coordinateUnit='world_pixels',
                  start=start, finish=finish, baseline=scan_coverage(baseline), segments=segments,
                  findings=findings, coverage=samples, progress=dict(completedSegments=0, dx=0., dy=0.),
                  lastActionResult={}, seek=seek)
    # Revocation finalizes the same in-flight receipt.
    mode.progress['explorationResult'] = result

    def record_findings(scan):
        matched = False
        for sample in (scan.get('widerSample',{}), scan):
            items, changes = sample.get('discoveries',[]), sample.get('changes',[])
            if items or changes:
                findings.append(dict(**scan_coverage(sample), items=items, changes=changes))
                matched |= seek=='any' or any(seek and seek in (h.get('kind'),h.get('contentKey')) for h in items)
        return matched

    def authorized():
        if version != mode.version or mode.agent.bridge is not bridge or bridge.binding != binding:
            raise asyncio.CancelledError

    record_findings(baseline)
    scene = None
    for _ in range(MAX_SEGMENTS):
        authorized()
        try:
            if scene is None:
                scene = await landing_scene(bridge, direction, read_scene)
        except (BridgeError, OSError) as error:
            reason, detail = 'observation_incomplete', str(error)
            break
        authorized()
        finish = position(scene['actor'])
        if not segments:
            start = result['start'] = result['finish'] = finish
        result['routes'] = route_hints(scene)
        if time.monotonic() >= deadline:
            detail = 'travel time budget reached'
            break
        target = destination(scene,direction)
        if target is None:
            survey = scene.get('passages',{})
            complete = (scene['coverage']['geometryComplete'] and not scene['landingRegions']['truncated']
                        and survey.get('originResolved',True) and not (direction=='down' and survey.get('openShaft')))
            reason = 'blocked' if complete else 'observation_incomplete'
            detail = 'no forward landing observed' if complete else 'no confirmed landing in partially observed terrain'
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
        result['routes'] = []
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
            scan = await mode.resource_scan(version)
            authorized()
            finish = segment['finish'] = result['finish'] = scan['position']
            samples.append(scan_coverage(scan))
            result['progress'].update(dx=finish['leftX']-start['leftX'], dy=finish['feetY']-start['feetY'])
            matched = record_findings(scan)
            if seek and seek not in ('any','passage'):
                hit = next((h for h in scan.get('items',()) if seek in (h.get('kind'),h.get('contentKey'))),None)
                if hit and not matched:
                    findings.append(dict(**scan_coverage(scan),items=[hit],changes=[]))
                    matched = True
        except (BridgeError, OSError) as error:
            reason, detail = 'observation_incomplete', str(error)
            break
        if expired:
            reason = 'segment_time_limit' if segment_deadline < deadline else 'budget_reached'
            detail = 'navigation time budget reached'
            break
        if last['status'] != 'completed':
            reason = 'segment_time_limit' if last['status']=='timeout' else 'blocked'
            detail = last.get('reason','navigation failed')
            break
        axis = 'leftX' if direction in ('left','right') else 'feetY'
        advance = (finish[axis]-segment['start'][axis]) * (1 if direction in ('right','down') else -1)
        if advance < 1:
            reason, detail = 'blocked', 'no observed forward displacement'
            break
        try:
            scene = await landing_scene(bridge, direction, read_scene)
            authorized()
        except (BridgeError, OSError) as error:
            reason, detail = 'observation_incomplete', str(error)
            break
        result['routes'] = route_hints(scene)
        passages = [e for e in scene.get('passages',{}).get('exits',()) if e['kind']!='open_landing']
        if passages:
            segment['passages'] = passages
        if matched or (seek in ('passage','any') and passages):
            reason, detail = 'discovery', 'requested observation found'
            break

    result.update(returnReason=reason, reason=detail, lastActionResult=mode.receipt_fields(last))
    normal = reason in ('discovery','budget_reached')
    status = 'completed' if normal else {'outcome_unknown':'unknown','interrupted':'cancelled',
                                         'segment_time_limit':'timeout'}.get(reason,'failed')
    return dict(status=status, completed=normal, inputsReleased=reason!='outcome_unknown',
                reason=detail, explorationResult=result)
