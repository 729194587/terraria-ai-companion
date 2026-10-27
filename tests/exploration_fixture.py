"""Socket fixture with synthetic terrain/kinematics. Not native Terraria physics."""
import math
from test_server_ai_agent import AiHost


class ExplorationHost(AiHost):
    def __init__(self):
        super().__init__()
        self.perception = self.actions = self.chat = True
        self.x = 1600.0
        self.y = 3158.0
        self.life = 100
        self.terrain_mode = 'flat'
        self.frozen = self.damage = self.threat = False
        self.applied = set()
        self.tick = 1

    def query(self, operation, args):
        self.tick += 1
        if self.action and self.action['status'] == 'completed' and self.action['actionId'] not in self.applied:
            self.applied.add(self.action['actionId'])
            request = next(r for r in self.sent if r['op'] == 'start' and r['actionId'] == self.action['actionId'])
            if not self.frozen:
                self.x = request['arguments']['targetX']
            if self.damage:
                self.life -= 1
        base = super().query(operation, args)
        base.update(gameUpdate=self.tick, observedAtUnixMs=1793080000000+self.tick, clock='server_utc_at_projection')
        if operation == 'get_player_state':
            base['data']['player'].update(position=dict(x=self.x, y=self.y), life=self.life, dead=False)
        elif operation == 'observe_terrain':
            left = math.floor((self.x+10)/16)-22
            top = math.floor((self.y+42)/16)-17
            def cell(tx, ty):
                if self.terrain_mode == 'both_walls' and (tx in (99, 102)) and ty < 200:
                    return '#'
                if self.terrain_mode == 'corridor' and tx in (98,110) and ty < 200:
                    return '#'
                if self.terrain_mode == 'right_soon_wall' and tx == 106 and ty < 200:
                    return '#'
                if self.terrain_mode == 'right_wall' and tx == 102 and ty < 200:
                    return '#'
                if self.terrain_mode == 'obstacle' and 104 <= tx <= 105 and 198 <= ty < 200:
                    return '#'
                if self.terrain_mode == 'right_unknown' and tx >= 102:
                    return '?'
                if self.terrain_mode == 'right_hazard' and tx >= 102:
                    return '!'
                if self.terrain_mode == 'right_gap' and tx >= 102:
                    return '.'
                return '#' if ty >= 200 else '.'
            rows = [''.join(cell(left+x, top+y) for x in range(45)) for y in range(29)]
            base['data'] = dict(terrain=dict(originX=left, originY=top, rows=rows, tileSize=16,
                playerWidth=20, playerHeight=42, grounded=True, supported=True),
                bounds=dict(minX=left,minY=top,maxX=left+44,maxY=top+28))
        elif operation == 'resolve_content':
            base['data'] = dict(status='ok', candidates=[dict(typeId=5,contentKey='Terraria/Trees')])
        elif operation == 'scan_entities':
            base['data'] = dict(matchedCount=1 if self.threat else 0, items=[])
        elif operation == 'scan_tiles':
            base['data'] = dict(items=[dict(x=args['minX']+4,y=198,typeId=5,contentKey='Terraria/Trees')],
                matchedCount=1,returnedCount=1,truncated=False,unknownCount=0,complete=True,
                bounds={key:args[key] for key in ('minX','minY','maxX','maxY')})
        return base
