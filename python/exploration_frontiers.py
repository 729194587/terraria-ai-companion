"""World-space survey targets. All internal coordinates are Tiles except target (foot pixels)."""
from base64 import b64decode
from itertools import groupby

SPAN = 12


class MapWindow:
    def __init__(self, data):
        self.left, self.top = data['originX'], data['originY']
        self.width, self.height = data['width'], data['height']
        self.rows = data['rows']
        self.bits = b64decode(data['visitBits'], validate=True)
        self.count = data['visitedCount']
        self.identity = data['worldUid'], data['characterId']
        if (data['cellSize'] != 1 or len(self.bits) != (self.width*self.height+7)//8
                or len(self.rows) != self.height or any(len(row) != self.width for row in self.rows)):
            raise ValueError('Incomplete exploration map window')

    def contains(self, x, y):
        return self.left <= x < self.left+self.width and self.top <= y < self.top+self.height

    def cell(self, x, y):
        if self.contains(x,y):
            return self.rows[y-self.top][x-self.left]

    def visited(self, x, y):
        if not self.contains(x,y):
            return False
        index = (y-self.top)*self.width+x-self.left
        return bool(self.bits[index//8] & 1 << (index%8))

    def revealed(self, point):
        return self.cell(*point) not in (None, '?')

    def boundary(self, x, y):
        # A sight line must start in remembered air and reach an actual unknown
        # cell inside the map. A clipped read or a wall terminates the ray.
        if self.cell(x,y-2) != ' ':
            return None
        for dx,dy in ((1,0),(-1,0),(0,-1),(0,1)):
            for step in range(1,9):
                point = (x+dx*step, y-2+dy*step)
                value = self.cell(*point)
                if value == '?':
                    return list(point)
                if value != ' ':
                    break


def candidates(scene, window, *, regional=False):
    # Store contiguous, supported foot positions. A connected landing is a
    # geometric preference only. The map fallback never asserts connectivity.
    stances = {}
    if regional:
        for row, line in enumerate(window.rows):
            y = row + window.top
            for col in range(1, len(line)):
                if line[col] not in '#=' or line[col-1] not in '#=':
                    continue
                x = col + window.left
                if all(window.cell(i,j) in (' ','=') for i in (x-1,x) for j in range(y-3,y)):
                    stances[y,x] = (x*16,False,'map')
    survey = scene.get('passages') or {}
    connected = set(survey.get('connectedLandingIds', ()))
    half = scene['actor']['boundsWorldPixels']['width']/2
    bounds = scene.get('bounds')
    if regional and bounds:
        # Inside a complete live survey, use the native flat-landing checks.
        if scene['coverage']['geometryComplete'] and not scene['landingRegions']['truncated']:
            stances = {(y,x): value for (y,x),value in stances.items() if not (
                bounds['minX'] < x < bounds['maxX'] and bounds['minY']+2 < y <= bounds['maxY'])}
    for landing in scene['landingRegions']['items']:
        region = landing['target']
        lo, hi = round(region['minPlayerLeftX']+half), round(region['maxPlayerLeftX']+half)
        y = round(region['surfaceY']/16)
        for x in range(lo//16, hi//16+1):
            px = min(hi,max(lo,x*16+8))
            stances[y,x] = (px,landing['id'] in connected,'landing')
    hints = {(r['target']['y']//16,r['target']['x']//16):r['kind'] for r in survey.get('routes',())}
    found = []
    for (y,bucket), group in groupby(sorted(stances), key=lambda p:(p[0],p[1]//SPAN)):
        columns = [x for _,x in group]
        for _, run in groupby(enumerate(columns),key=lambda pair:pair[1]-pair[0]):
            xs = [x for _,x in run]
            # Local connected and regional positions can meet at a survey edge.
            center = bucket*SPAN+SPAN//2
            x = min(xs,key=lambda i:(not stances[y,i][1],abs(i-center),i))
            px, natural, source = stances[y,x]
            hint = next((hints[y,i] for i in xs if (y,i) in hints and
                         hints[y,i] not in ('lateral_landing','open_landing')),None)
            base = dict(y=y, lo=xs[0], hi=xs[-1], target=dict(x=px,y=y*16),
                        natural=natural,source=source,hint=hint)
            if not any(window.visited(i,y) for i in xs):
                found.append(dict(base,kind='visit'))
            for i in sorted(xs,key=lambda i:abs(i-x)):
                witness = window.boundary(i,y)
                if witness:
                    found.append(dict(base,kind='map',witness=witness,
                                      target=dict(x=stances[y,i][0],y=y*16)))
                    break
    return found
