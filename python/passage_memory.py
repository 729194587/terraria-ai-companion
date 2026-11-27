"""Bounded exploration notes for one bound actor and driving session. Coordinates are world pixels."""
from collections import deque
from copy import deepcopy


def nearby(a, b, tiles):
    return abs(a['x']-b['x']) <= tiles*16 and abs(a['y']-b['y']) <= tiles*16


class PassageMemory:
    def __init__(self):
        self.sites = deque(maxlen=24)
        self.failures = deque(maxlen=32)
        self.arrivals = deque(maxlen=48)
        self.resumptions = deque(maxlen=6)

    def finish_trip(self, result):
        direction = result['direction']
        start, end = result['start'], result['finish']
        axis = 'leftX' if direction in ('left','right') else 'feetY'
        advance = (end[axis]-start[axis]) * (-1 if direction in ('left','up') else 1)
        def near_start(note):
            point = note['position']
            return abs(point['leftX']-start['leftX']) <= 48 and abs(point['feetY']-start['feetY']) <= 32
        if advance >= 16:
            for note in list(self.resumptions):
                if note['direction'] == direction and near_start(note):
                    self.resumptions.remove(note)
        if result['returnReason'] == 'discovery':
            note = dict(direction=direction, objective=result['objective'], position=dict(end))
            # Only expose a navigation target when this very stance was observed as supported.
            stance = result.get('passages',{}).get('returnTarget')
            if stance and abs(stance['x']-(end['leftX']+10)) <= 1 and abs(stance['y']-end['feetY']) <= 1:
                note['returnTarget'] = dict(stance)
            if note not in self.resumptions:
                self.resumptions.appendleft(note)
        return deepcopy(list(self.resumptions))

    def failed(self, start, target, tick):
        return any(0 <= tick-at < 300 and nearby(start, origin, 3) and nearby(target, end, 2)
                   for origin, end, at in self.failures)

    def arrived(self, point):
        fresh = not any(nearby(point,old,2) for old in self.arrivals)
        self.arrivals.append(point)
        for site in self.sites:
            for branch in site['exits']:
                if nearby(point, branch['target'], 2):
                    branch['state'] = 'arrived'
        return fresh

    def reject(self, start, target, tick):
        self.failures.append((start, target, tick))
        for site in self.sites:
            for branch in site['exits']:
                if nearby(branch['target'], target, 2) and branch['state'] != 'arrived':
                    branch.update(state='recently_blocked', retryAfterUpdate=tick+300)

    def observe(self, survey, tick):
        for site in self.sites:
            for branch in site['exits']:
                if branch.get('retryAfterUpdate', tick+1) <= tick:
                    branch.pop('retryAfterUpdate')
                    if branch['state'] == 'recently_blocked':
                        branch['state'] = 'untried'
        origin = survey.get('returnTarget')
        if not origin:
            return False
        walked = [r['target'] for r in survey.get('routes',()) if r['visitState']=='walked_nearby']
        # Include samples during ordinary navigation or an investigation.
        for site in self.sites:
            for branch in site['exits']:
                if nearby(branch['target'], origin, 2) or branch['target'] in walked:
                    branch['state'] = 'arrived'
        exits = survey['exits']
        kinds = {e['kind'] for e in exits}
        if survey['junction']:
            kind = 'junction'
        elif 'possible_cave_entrance' in kinds:
            kind = 'possible_cave_entrance'
        elif survey.get('openShaft'):
            kind = 'shaft_floor_unobserved'
        elif 'open_descent' in kinds:
            kind = 'open_descent'
        else:
            return False
        site = next((s for s in reversed(self.sites) if s['kind']==kind and nearby(origin,s['returnTarget'],6)
                     and abs(origin['y']-s['returnTarget']['y']) <= 32), None)
        fresh = site is None
        if fresh:
            site = dict(kind=kind, returnTarget=dict(origin), exits=[])
            self.sites.append(site)
        site['lastSeenUpdate'] = tick
        if survey.get('openShaft'):
            site['openShaft'] = dict(survey['openShaft'])
        for e in exits:
            previous = next((b for b in site['exits'] if b['direction']==e['direction']
                             and nearby(b['target'],e['target'],5)), None)
            if previous is None and len(site['exits']) < 8:
                state = 'arrived' if e['target'] in walked or any(nearby(p,e['target'],2) for p in self.arrivals) else 'untried'
                site['exits'].append(dict(kind=e['kind'],direction=e['direction'],target=dict(e['target']),state=state))
                fresh = True
        return fresh

    def summary(self):
        return deepcopy(sorted(self.sites, key=lambda s:s['lastSeenUpdate'], reverse=True)[:8])
