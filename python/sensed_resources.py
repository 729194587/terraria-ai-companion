"""Bounded session-local history of resource observations. Coordinates are in Tiles."""
from collections import OrderedDict


class ResourceHistory:
    def __init__(self, limit=128):
        self.records = OrderedDict()
        self.limit = limit

    @staticmethod
    def key(hit):
        anchor = hit['anchor']
        return hit['kind'], hit['typeId'], hit.get('entityRef') or (anchor['x'], anchor['y'])

    def pass_by(self, hits):
        for hit in hits:
            record = self.records.get(self.key(hit))
            if record is not None:
                record['passed'] = True

    def observe(self, scan):
        tick = scan['gameUpdate']
        current = []
        discoveries = []
        seen = set()
        for hit in scan['items']:
            key = self.key(hit)
            previous = self.records.get(key)
            if hit['kind'] == 'ore_component':
                tiles = {(p['x'],p['y']) for p in hit['tiles']}
                # A wider scan can connect two components clipped by earlier windows.
                for prior in list(self.records):
                    if prior == key or prior[:2] != key[:2] or prior in seen:
                        continue
                    record = self.records[prior]
                    if (record['state'] != 'absent_at_last_location'
                            and (tiles.intersection(record['completeShape'] or ())
                                 or any((p['x'],p['y']) in tiles for p in record['hit']['tiles']))):
                        previous = previous or record
                        del self.records[prior]
            freshness = 'new' if previous is None else (
                'updated' if previous['hit'] != hit or previous['state'] == 'absent_at_last_location' else 'known')
            # Clipping the same object at the edge of consecutive windows is not a new discovery.
            signature = previous.get('completeShape') if previous else None
            complete_shape = signature
            if hit['status'] == 'complete' and hit['kind'] != 'drop':
                complete_shape = sorted((p['x'],p['y']) for p in hit.get('tiles',()))
            passed = bool(previous and previous.get('passed') and previous['state'] != 'absent_at_last_location')
            # Resolving the clipped footprint of a passed object is not a world change.
            changed_shape = complete_shape != signature and (signature is not None or not passed)
            if previous is None or previous['state']=='absent_at_last_location' or changed_shape:
                passed = False
                discoveries.append(dict(hit, freshness=freshness))
            self.records[key] = dict(hit=dict(hit), lastSeen=tick, state='observed', completeShape=complete_shape, passed=passed)
            self.records.move_to_end(key)
            seen.add(key)
            item = dict(hit, freshness=freshness)
            if passed:
                item['decision'] = 'passed'
            current.append(item)
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
                changes.append(dict(record['hit'], state=record['state'], lastSeen=record['lastSeen']))
            elif not covered and record['state'] == 'observed':
                record['state'] = 'needs_revalidation'
        while len(self.records) > self.limit:
            self.records.popitem(last=False)
        historical = [dict(kind=r['hit']['kind'], contentKey=r['hit']['contentKey'], anchor=r['hit']['anchor'],
                           state=r['state'], lastSeen=r['lastSeen'])
                      for k,r in reversed(self.records.items()) if k not in seen][:12]
        return {**scan, 'items': current, 'historical': historical, 'changes': changes[:16], 'discoveries': discoveries}
