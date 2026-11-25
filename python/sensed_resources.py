"""Bounded session-local history of resource observations. Coordinates are in Tiles."""
from collections import OrderedDict


class ResourceHistory:
    def __init__(self, limit=128):
        self.records = OrderedDict()
        self.limit = limit

    def observe(self, scan):
        tick = scan['gameUpdate']
        current = []
        seen = set()
        for hit in scan['items']:
            anchor = hit['anchor']
            key = (hit['kind'], hit['typeId'], hit.get('entityRef') or (anchor['x'], anchor['y']))
            previous = self.records.get(key)
            if hit['kind'] == 'ore_component':
                tiles = {(p['x'],p['y']) for p in hit['tiles']}
                # A wider scan can connect two components clipped by earlier windows.
                for prior in list(self.records):
                    if prior == key or prior[:2] != key[:2] or prior in seen:
                        continue
                    record = self.records[prior]
                    if (record['state'] != 'absent_at_last_location'
                            and any((p['x'],p['y']) in tiles for p in record['hit']['tiles'])):
                        previous = previous or record
                        del self.records[prior]
            freshness = 'new' if previous is None else (
                'updated' if previous['hit'] != hit or previous['state'] == 'absent_at_last_location' else 'known')
            self.records[key] = dict(hit=dict(hit), lastSeen=tick, state='observed')
            self.records.move_to_end(key)
            seen.add(key)
            current.append(dict(hit, freshness=freshness))
        changes = []
        bounds = scan['bounds']
        for key, record in self.records.items():
            if key in seen:
                continue
            footprint = record['hit']['bounds']
            covered = (scan['complete'] and not scan['truncated'] and scan['unknownCells'] == 0
                       and bounds['minX'] <= footprint['x'] and bounds['minY'] <= footprint['y']
                       and footprint['x'] + footprint['width'] - 1 <= bounds['maxX']
                       and footprint['y'] + footprint['height'] - 1 <= bounds['maxY'])
            if covered and record['state'] != 'absent_at_last_location':
                record.update(state='absent_at_last_location', checkedAt=tick)
                changes.append(dict(kind=key[0], anchor=record['hit']['anchor'], state=record['state']))
            elif not covered and record['state'] == 'observed':
                record['state'] = 'needs_revalidation'
        while len(self.records) > self.limit:
            self.records.popitem(last=False)
        historical = [dict(kind=r['hit']['kind'], contentKey=r['hit']['contentKey'], anchor=r['hit']['anchor'],
                           state=r['state'], lastSeen=r['lastSeen'])
                      for k,r in reversed(self.records.items()) if k not in seen][:12]
        return {**scan, 'items': current, 'historical': historical, 'changes': changes[:16]}
