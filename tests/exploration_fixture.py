"""Loopback fixture with observed landings and native task receipts."""
from test_server_ai_agent import AiHost


class ExplorerGame(AiHost):
    extra_capabilities = ('server_ai_navigation_v1', 'server_ai_program_tasks_v1', 'server_ai_tasks_v1',
                          'server_ai_region_resources_v1', 'server_ai_scene_region_v1',
                          'server_ai_resources_v1', 'server_ai_directional_landings_v1')

    def __init__(self):
        super().__init__()
        self.actions = self.scene = self.perception = True
        self.complete = True
        self.hits = []
        self.x = 100
        self.action_transform = self.arrive

    def arrive(self, action):
        if action['operation'] != 'navigate_to' or action['status'] != 'completed':
            return
        target = action['targetTask']
        self.x = target['x'] - 10
        arrival = dict(target=target, arrived=True, grounded=True, safeSupport=True, settled=True,
                       x=self.x, feetY=target['y'])
        action['evidence']['task'] = dict(taskId=action['actionId'], taskName='navigate_to', status='completed',
            currentChild=None, result=dict(inputsReleased=True, effectVerified=True, evidence=arrival))

    def query(self, operation, args):
        if operation != 'observe_scene':
            return super().query(operation, args)
        data = dict(actor=dict(boundsWorldPixels=dict(x=self.x, width=20), feetY=200))
        if args.get('scale') == 'region':
            assert (args['width'], args['height'], args['layers']) == (120, 68, ['resources'])
            data.update(resources=dict(items=self.hits, gameUpdate=33, complete=self.complete, truncated=False,
                unknownCells=int(not self.complete), bounds=dict(minX=0, maxX=119, minY=0, maxY=67)),
                coverage=dict(requestedTiles=8160, unknownByReason={}),
                sample=dict(elapsedMilliseconds=1, samplingBudgetReached=False))
        else:
            data['coverage'] = dict(geometryComplete=True)
            data['landingRegions'] = dict(truncated=False, items=[dict(target=dict(
                minPlayerLeftX=self.x, maxPlayerLeftX=self.x+600, surfaceY=200))])
            if args.get('includePassages'):
                data.update(gameUpdate=33,hazards={},passages=dict(originResolved=True,junction=False,
                    returnTarget=dict(x=self.x+10,y=200),exits=[],routes=[dict(direction='right',kind='lateral_landing',
                        target=dict(x=self.x+330,y=200),visitState='no_recent_visit',unknownNearbyTiles=24)]))
        return dict(available=True, worldSessionId=self.binding['world'], actorGeneration=self.binding['generation'],
                    actorSlot=self.binding['slot'], gameUpdate=33, clock='server_utc_at_projection',
                    observedAtUnixMs=1793070000123, data=data)
