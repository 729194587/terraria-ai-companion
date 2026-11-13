"""Draft 2020-12 documentation for final Server AI Lua results.

The executor does not use these schemas for admission or outcome decisions.
C# query payloads use SceneRegion.Json; default action serialization preserves
record member casing. Python-specific projections are defined at the boundary.
"""
from copy import deepcopy

import atomic_operations as atomic
from server_ai import operations_for_capabilities


DRAFT = 'https://json-schema.org/draft/2020-12/schema'
S = {'type': 'string'}
I = {'type': 'integer'}
N = {'type': 'number'}
B = {'type': 'boolean'}
NULL = {'type': 'null'}


def desc(schema, description):
    return {**schema, 'description': description}


def enum(*values):
    return {'enum': list(values)}


def const(value):
    return {'const': value}


def ref(name):
    return {'$ref': '#/$defs/' + name}


def array(items):
    return {'type': 'array', 'items': items}


def dictionary(values):
    return {'type': 'object', 'additionalProperties': values}


def any_of(*schemas):
    return {'anyOf': list(schemas)}


def nullable(schema):
    return any_of(schema, NULL)


def obj(properties, *, optional=(), closed=True):
    schema = {'type': 'object', 'properties': properties,
              'required': [name for name in properties if name not in optional]}
    if closed:
        schema['additionalProperties'] = False
    return schema


def extend(base, properties):
    return {**obj(properties, closed=False), 'allOf': [base], 'unevaluatedProperties': False}


def matches(**properties):
    """Require evidence fields in a completion predicate without closing the object."""
    return {'type': 'object', 'properties': properties, 'required': list(properties)}


def fields(**groups):
    """Name groups for C# records whose fields share a primitive type."""
    types = {'strings': S, 'integers': I, 'numbers': N, 'booleans': B}
    return {key: types[kind] for kind, names in groups.items() for key in names.split()}


TILE_COORD = desc(I, 'Absolute Tile coordinate. One Tile is 16 world pixels.')
PIXEL = desc(N, 'World pixel coordinate. X increases rightward and Y downward.')
SPEED = desc(N, 'World pixels per game update. Positive X is rightward and positive Y is downward.')
TICK = desc(I, 'Game update counter, independent of wall-clock time.')
AIR_KIND = desc(enum(0, 1, 2), 'AirKind: 0 None, 1 Wings, 2 RocketBoots.')
AIR_PHASE = desc(enum(0, 1, 2, 3, 4), 'AirPhase: 0 None, 1 Flying, 2 Gliding, 3 Descending, 4 RocketPulse.')
INVENTORY_TOKEN = desc(S, 'Opaque guard bound to world, actor generation, slot, item instance and basic fields. Reobserve before a mutation. Custom Mod state is not covered.')
HOTBAR = desc(nullable(I), 'Game hotbar key 1–9,0; null when the slot has no key.')
COUNT = {'type': 'integer', 'minimum': 0}
IDENTITY = {'worldSessionId': desc(S, 'Bound game world session.'),
            'actorGeneration': desc(I, 'Generation of the bound Server AI actor.'),
            'actorSlot': desc(I, 'Server slot of the bound AI Player.')}
OBSERVED = {**IDENTITY, 'gameUpdate': TICK,
            'observedAtUnixMs': desc(I, 'UTC Unix milliseconds at projection.'),
            'clock': const('server_utc_at_projection'), 'binding': ref('Binding')}
QUERY_METADATA = {'source': const('server_ai_player'), 'customState': S, 'inventoryTokenStatus': S}
COLLECTION = {'matchedCount': COUNT, 'returnedCount': COUNT,
              'truncated': desc(B, 'Some matches were omitted by the collection limit.')}
TILE_XY = {'x': TILE_COORD, 'y': TILE_COORD}
SLOT_FIELDS = {'slot': desc(I, 'Internal zero-based inventory or equipment slot.'), 'hotbarKey': HOTBAR}
ITEM_ID = {'typeId': I, 'contentKey': S, 'name': S}
LANDING_FIELDS = {'minPlayerLeftX': PIXEL, 'maxPlayerLeftX': PIXEL, 'surfaceY': PIXEL}

DEFS = {
    'Binding': desc(obj({'actor': S, 'world': S, 'generation': I, 'slot': I}),
                    'Connection identity attached by ServerAiBridge, outside the native observation envelope.'),
    'WorldPoint': obj({'x': PIXEL, 'y': PIXEL}),
    'Velocity': obj(dict.fromkeys(('x', 'y'), SPEED)),
    'WirePoint': obj(dict.fromkeys(('X', 'Y'), PIXEL)),
    'WireVelocity': obj(dict.fromkeys(('X', 'Y'), SPEED)),
    'WorldRect': obj({'x': PIXEL, 'y': PIXEL, 'width': N, 'height': N}),
    'TilePoint': obj(TILE_XY),
    'TileRect': obj({**TILE_XY, 'width': I, 'height': I}),
    'TileBounds': obj(dict.fromkeys(('minX', 'minY', 'maxX', 'maxY'), TILE_COORD)),
    'LandingRegion': obj(LANDING_FIELDS),
    'LandingRecord': obj({'MinPlayerLeftX': PIXEL, 'MaxPlayerLeftX': PIXEL, 'SurfaceY': PIXEL}),
    'MineToken': obj({**TILE_XY, **IDENTITY, 'kind': const('tile'), 'typeId': I,
                      'observedGameUpdate': TICK, 'observedAtUnixMs': OBSERVED['observedAtUnixMs'],
                      'targetToken': desc(S, 'Observation-bound token; pass unchanged to mine_target.')}),
    'ObservationUnavailable': obj({'valid': const(False), 'available': const(False),
                                    'reason': S, 'binding': ref('Binding')}, optional=('binding', 'reason')),
    'ObservationEnvelope': obj({'valid': const(True), 'available': const(True), **OBSERVED},
                               optional=('binding',), closed=False),
}

PLAYER_FIELDS = {
    **fields(strings='name equipmentScope hotbarNumbering',
             integers='id life lifeMax mana manaMax breath breathMax immuneTime direction selectedSlot',
             booleans='immune dead ghost'),
    'source': const('server_ai_player'), 'selectedHotbarKey': HOTBAR,
    'statDefense': obj({'positive': I, 'negative': I}),
    'positionWorldPixels': ref('WorldPoint'), 'boundsWorldPixels': ref('WorldRect'),
    'velocityPixelsPerTick': ref('Velocity'),
    'hotbarInventorySlots': array(I), 'hotbarKeyBindings': array(obj({'key': I, 'inventorySlot': I})),
    'mount': obj({'active': B, 'typeId': nullable(I)}),
    'movement': obj(fields(strings='contactStatus', integers='grapCount jump',
                           booleans='sloping stairFall wet lavaWet honeyWet shimmerWet pulley controlDown controlJump frozen webbed stoned',
                           numbers='gravDir runAcceleration maxRunSpeed gravity maxFallSpeed')),
}
DEFS.update({
    'Player': obj({**PLAYER_FIELDS, 'inWorld': const(True), 'active': B,
                   'position': ref('WorldPoint'), 'velocity': ref('Velocity')}),
    'Motion': obj({'grounded': desc(B, 'Observed ground contact.'),
                   'supported': desc(B, 'Motion profile supported; independent of ground contact.'),
                   'playerWidth': I, 'playerHeight': I, 'knowledge': S}),
    'FullItem': obj({**SLOT_FIELDS, **ITEM_ID, 'empty': B, 'stack': I, 'prefix': I, 'favorited': B,
                    'use': obj(fields(integers='damage pick axe hammer createTile createWall useStyle useTime useAnimation ammo shoot',
                                      booleans='consumable')), 'customState': S}),
    'CompactItem': obj({**SLOT_FIELDS, **ITEM_ID, 'empty': B, 'stack': I, 'prefix': I, 'maxStack': I,
                       'favorited': B, 'inventoryToken': desc(nullable(INVENTORY_TOKEN),
                           'Pass the fresh slot token unchanged for guarded inventory actions. Null means no usable token.'),
                       'use': obj(fields(integers='pick axe hammer createTile createWall'))}),
    'EquipmentSlot': obj({'slotId': S, 'kind': enum('head', 'body', 'legs', 'accessory'),
                          'source': enum('vanilla', 'mod_accessory_slot'), 'displayName': S, 'enabled': B,
                          'occupied': B, 'item': nullable(ref('FullItem')),
                          'targetToken': desc(INVENTORY_TOKEN, 'Functional equipment slot guard, including empty slots. Pass unchanged to equip_item or unequip_item.')}),
})

FULL_SLOT = any_of(obj({'slot': I, 'empty': const(True)}), ref('FullItem'))


def inventory_scope(*modes):
    return obj({'mode': enum(*modes), 'arrays': dictionary(array(I)), 'slotNumbering': S, 'excluded': array(S)})


INVENTORY_FIELDS = {**QUERY_METADATA, 'inspectedSlots': array(I)}
INVENTORY = any_of(
    obj({**INVENTORY_FIELDS, **PLAYER_FIELDS, 'inventoryScope': inventory_scope('full'),
         **dict.fromkeys(('inventory', 'equipment', 'dyes', 'miscEquipment', 'miscDyes'), array(FULL_SLOT)),
         'buffs': array(obj({'slot': I, 'typeId': I, 'timeTicks': I, 'contentKey': nullable(S)})),
         'buffScope': S, 'scope': S, 'functionalEquipmentSlots': array(ref('EquipmentSlot'))}),
    obj({**INVENTORY_FIELDS, **ITEM_ID, 'status': const('ok'), 'inventoryScope': inventory_scope('item'),
         'count': COUNT, 'slots': array(obj({**SLOT_FIELDS, 'stack': I})), 'scope': S}),
    obj({**INVENTORY_FIELDS, 'status': enum('unknown_item', 'ambiguous_item'), 'inventoryScope': inventory_scope('item'),
         'candidates': array(obj(ITEM_ID)), 'truncated': B}),
    obj({**INVENTORY_FIELDS, 'status': const('ok'), 'inventoryScope': inventory_scope('selected', 'hotbar', 'slots'),
         'selectedSlot': I, 'selectedHotbarKey': HOTBAR, 'inventory': array(ref('CompactItem'))}),
    obj({**INVENTORY_FIELDS, 'status': const('inventory_slot_out_of_range'), 'inventoryScope': inventory_scope('slots'),
         'inventory': array(ref('CompactItem'))}),
)

PHYSICS_FIELDS = {**dict.fromkeys(('px', 'py'), PIXEL), **dict.fromkeys(('vx', 'vy'), SPEED), 'airPhase': AIR_PHASE,
                  **fields(integers='jumpFramesLeft',
                           booleans='grounded releaseJump extraAvailable extraActive extraUsed'),
                  'air': nullable(ref('AirResources'))}
DEFS['PhysicsPose'] = obj(PHYSICS_FIELDS)
DEFS['TileCell'] = obj({
    **fields(integers='frameX frameY slope wireMask liquidType liquidAmount tilePaint wallPaint touchDamage',
             booleans='hasTile halfBlock solid solidTop actuated actuator tileInvisible wallInvisible tileFullbright wallFullbright hot bleeding destroyOnTouch boulder'),
    'knowledge': enum('read', 'out_of_world', 'unloaded'), 'collisionKnowledge': S,
    'tileType': nullable(I), 'wallType': nullable(I),
})
SUPPORT_ASSESSMENT = obj({'supportPixels': nullable(N), 'rejection': nullable(S)})
DEFS['MiningSupportImpact'] = obj({
    'available': B, 'targetX': TILE_COORD, 'targetY': TILE_COORD, 'pose': ref('PhysicsPose'),
    'current': nullable(SUPPORT_ASSESSMENT), 'afterRemoval': nullable(SUPPORT_ASSESSMENT),
    'affectsCurrentStance': nullable(B), 'supportDeltaPixels': nullable(N), 'minimumSupportPixels': N,
    'reasons': array(S), 'scope': S,
})
PREFLIGHT = obj({
    'distancePixels': N, 'reachPixels': N, 'reachScope': S, 'withinConservativeReach': B,
    'normalGravity': B, 'selectedItem': ref('CompactItem'), 'limitations': S,
    'checks': obj(fields(booleans='tilePresent wallPresent pickSelected pureHammerSelected consumableTileSelected consumableWallSelected supportedInteractionType vanillaNonFrameImportantTile')),
})
INSPECT_TILE = {**QUERY_METADATA, **TILE_XY, 'cell': ref('TileCell'), 'miningSupportImpact': ref('MiningSupportImpact')}
TERRAIN = obj({
    **QUERY_METADATA, 'bounds': ref('TileBounds'), 'scannedCount': COUNT,
    'unknownCount': desc(COUNT, 'Unread cells; they are not air.'),
    'complete': desc(B, 'True if this local terrain sample contains no unknown cells.'),
    **fields(strings='coordinates legend liquidCoordinates scope'),
    'terrain': obj({**fields(integers='originX originY playerWidth playerHeight', booleans='grounded supported'),
                    'tileSize': const(16), 'rows': array(S),
                    'liquids': nullable(array(obj({**TILE_XY, 'type': S, 'amount': I})))}),
    'state': obj({**fields(integers='id direction', booleans='dead'), 'positionWorldPixels': ref('WorldPoint'),
                  'centerWorldPixels': ref('WorldPoint'), 'velocityPixelsPerTick': ref('Velocity')}),
})

NPC_CLASS = enum('friendly_npc', 'critter', 'hostile_creature', 'unknown')
ENTITY_FIELDS = {'slot': I, 'name': S, 'classificationBasis': S, 'centerWorldPixels': ref('WorldPoint'),
                 'velocityPixelsPerTick': ref('Velocity'), 'distancePixels': N}
SCANNED_ENTITY = any_of(
    obj({**ENTITY_FIELDS, 'kind': const('npc'), 'typeId': I, 'classification': NPC_CLASS,
         'details': obj({'entityRef': S, **fields(integers='type life lifeMax', booleans='friendly dontTakeDamage chaseable'),
                         'evidence': obj(fields(integers='damage', booleans='friendly townNpc critter chaseable modded projectileNpc'))})}),
    obj({**ENTITY_FIELDS, 'kind': const('player'), 'typeId': NULL, 'classification': const('other_player'),
         'details': obj({'dead': B, 'statLife': I, 'team': I})}),
    obj({**ENTITY_FIELDS, 'kind': const('item'), 'typeId': I, 'classification': const('dropped_item'),
         'details': obj({'type': I, 'stack': I, 'contentKey': S})}),
)
DEFS['RecipeItem'] = obj({**ITEM_ID, 'count': I})
RECIPE = obj({
    'recipeId': I, 'recipeToken': S, 'output': ref('RecipeItem'), 'supported': B, 'available': B,
    'reason': nullable(S), 'hasOutputSpace': B,
    'ingredients': array(obj({'item': ref('RecipeItem'),
                              'acceptedGroups': array(obj({'id': I, 'name': S, 'typeIds': array(I)}))})),
    'stations': array(obj({'typeId': I, 'contentKey': S, 'adjacent': B})),
    'conditions': array(obj({'description': S, 'supported': B, 'met': nullable(B)})),
})


def observation(data):
    return any_of(extend(ref('ObservationEnvelope'), {'data': data}), ref('ObservationUnavailable'))


OBSERVATIONS = {
    'get_player_state': any_of(extend(ref('ObservationEnvelope'), {
        **QUERY_METADATA, 'player': ref('Player'), 'motion': ref('Motion'),
        'functionalEquipmentSlots': array(ref('EquipmentSlot')),
    }), ref('ObservationUnavailable')),
    'inspect_inventory': observation(INVENTORY),
    'inspect_container': observation(any_of(
        obj({'status': const('unavailable'), 'reason': S, 'container': NULL}),
        obj({'status': const('open'), 'reason': NULL, 'container': obj({
            **TILE_XY, 'typeId': enum(21, 467), 'chestIndex': COUNT, 'name': S,
            'containerToken': atomic.INVENTORY_TOKEN,
            'slots': {'type': 'array', 'minItems': 40, 'maxItems': 40, 'items': obj({
                **fields(integers='slot typeId stack prefix maxStack', strings='name instanceId', booleans='favorited'),
                'slotToken': atomic.INVENTORY_TOKEN,
            })},
        })}))),
    'observe_terrain': observation(TERRAIN),
    'inspect_tile': observation(any_of(
        obj({**INSPECT_TILE, 'preflight': NULL, 'preflightStatus': const('unknown_target')}),
        obj({**INSPECT_TILE, 'preflight': PREFLIGHT, 'preflightStatus': const('partial_checks_only'),
             'content': nullable(S), 'wallContent': nullable(S)}))),
    'scan_tiles': observation(obj({
        **QUERY_METADATA, **COLLECTION,
        'items': array(obj({**TILE_XY, 'typeId': I, 'contentKey': S, 'distancePixels': N})),
        'unknownCount': COUNT, 'complete': desc(B, 'All requested cells sampled; independent of truncation.'),
        'status': enum('partial_unloaded', 'not_found', 'ok'), 'bounds': ref('TileBounds'),
        'scannedCount': COUNT, 'sort': S, 'coordinates': S,
    })),
    'scan_entities': observation(obj({
        **QUERY_METADATA, **COLLECTION, 'items': array(SCANNED_ENTITY), 'radiusPixels': N, 'sort': S,
        'complete': B, 'status': enum('not_found', 'ok'), 'originWorldPixels': ref('WorldPoint'), 'scope': S,
    })),
    'resolve_content': observation(obj({
        **QUERY_METADATA, **COLLECTION, 'status': enum('not_found', 'ambiguous', 'ok'),
        'kind': enum('tile', 'wall', 'item'), 'mode': enum('search', 'exact'), 'scope': S,
        'candidates': array(obj({**ITEM_ID, 'nameSource': enum('localized_item_name', 'content_symbol'),
                                 'creates': nullable(obj({'tileType': I, 'wallType': I}))})),
    })),
    'query_recipes': observation(obj({
        **QUERY_METADATA, **COLLECTION, 'items': array(RECIPE), 'offset': COUNT, 'unsupportedCount': COUNT,
        'registryRecipeCount': COUNT, 'availableOnly': B, 'storageReason': nullable(S), 'scope': S, 'quantities': S,
    })),
    'query_map': observation(obj({
        **QUERY_METADATA, **fields(integers='schemaVersion originX originY cellSize width height',
                                   strings='worldUid characterId legend knowledge visibility'),
        'mode': enum('overview', 'region'), 'rows': array(S),
        'exploredRatio': desc(N, 'Proportion of known map cells in this last-known memory, not live observation.'),
    })),
}

# SceneRegion.Json uses camelCase even for record members. A requested layer
# controls both its collection and its associated material/count metadata.
DEFS['AirResources'] = obj(fields(numbers='wingTime', integers='wingMax rocketTime rocketMax delay boots',
                                  booleans='canRocket rocketRelease rocketFrame'))
DEFS['SceneRow'] = any_of(
    obj({'encoding': const('rle'), 'data': desc(array(COUNT), 'Alternating run length and palette index.')}),
    obj({'encoding': const('hex_indices'), 'data': S, 'indexDigits': I}),
)


def grid(palette):
    return obj({'encodingVersion': const(2), 'palette': array(palette), 'rows': array(ref('SceneRow'))})


def scene_list(item, extra=None, *, nullable_counts=False, optional=()):
    count = nullable(COUNT) if nullable_counts else COUNT
    return obj({**COLLECTION, 'matchedCount': count, 'omittedCount': count, 'items': array(item),
                **(extra or {})}, optional=optional)


SCENE_OBJECT_FIELDS = {
    'id': S, 'typeId': nullable(I), 'contentKey': nullable(S), 'observedBounds': ref('TileRect'),
    'observedTileCount': COUNT, 'complete': desc(B, 'Completeness of this observed object grouping.'),
}
ENTITY_OBJECT = {
    'npc': obj({**fields(integers='slot life damage', strings='entityRef basis'), 'classification': NPC_CLASS}),
    'player': obj(fields(integers='slot', booleans='dead ghost')),
    'drop': obj(fields(integers='slot stack', strings='pickup')),
}


def scene_object(regional):
    targets = {} if regional else {'target': ref('MineToken')}
    tile_details = obj({'anchorTile': ref('TilePoint'), 'observedTargetTile': ref('TilePoint')})
    return any_of(
        obj({**SCENE_OBJECT_FIELDS, **targets, 'kind': const('ore_component'), 'details': tile_details},
            optional=('target',)),
        obj({**SCENE_OBJECT_FIELDS, 'kind': enum('tree_component', 'chest', 'door', 'workbench'),
             'details': tile_details}),
        obj({**SCENE_OBJECT_FIELDS, 'kind': const('light_source'),
             'details': obj({'anchorTile': ref('TilePoint'), 'frameX': I, 'frameY': I, 'actuated': B})}),
        *(obj({**SCENE_OBJECT_FIELDS, 'kind': const(kind),
               'details': obj({'boundsWorldPixels': ref('WorldRect'), 'velocity': ref('Velocity'), 'detail': detail})})
          for kind, detail in ENTITY_OBJECT.items()),
    )


DEFS.update({
    'SceneObjectLocal': scene_object(False),
    'SceneObjectRegional': scene_object(True),
    'SceneTileHazard': obj({
        **fields(strings='id', integers='minLiquidAmount maxLiquidAmount touchDamage'),
        'sources': array(S), 'minX': TILE_COORD, 'maxX': TILE_COORD, 'y': TILE_COORD,
        'tileType': nullable(I), 'contentKey': nullable(S),
    }),
    'SceneEntityHazard': obj({
        **fields(strings='kind classification observationRef knowledge', integers='slot typeId damage extraUpdates'),
        'entityRef': nullable(S), 'observedGameUpdate': TICK, 'boundsWorldPixels': ref('WorldRect'),
        'velocity': nullable(ref('Velocity')),
        'threat': obj({'status': enum('unavailable', 'projected_contact', 'no_projected_contact'),
                       'contactAfterTicks': nullable(N), 'horizonTicks': I,
                       'basis': desc(S, 'Constant-velocity projection, not damage or path verification.')}),
    }),
    'ScenePlacementDiagnostic': obj({
        **fields(integers='anchorCount inBoundsAnchorCount checkedCount rejectedCount unknownCount notCheckedCount',
                 booleans='coveredScopeComplete', strings='classification'),
        'rejectedByReason': dictionary(COUNT), 'notCheckedByReason': dictionary(COUNT),
        'firstExamples': dictionary(obj({
            'target': ref('TilePoint'), 'footprint': ref('TileRect'), 'support': ref('TileRect'),
            'detail': any_of(NULL,
                             obj({**TILE_XY, **fields(strings='knowledge collisionKnowledge navigation',
                                                      integers='liquidAmount'), 'tileType': nullable(I)}),
                             obj({'distancePixels': N, 'limitPixels': N}),
                             obj({'playerBoundsWorldPixels': ref('WorldRect')})),
        })),
    }),
})
SCENE_BASE = {
    **QUERY_METADATA, 'sceneId': S, 'gameUpdate': TICK, 'bounds': ref('TileBounds'), 'tileSize': const(16),
    'lighting': any_of(obj({'available': const(False), 'source': S, 'reason': S}),
                      obj({'available': const(True), 'source': S, 'brightness': N, 'gameUpdate': TICK})),
    'actor': obj({'slot': I, 'boundsWorldPixels': ref('WorldRect'), 'feetY': PIXEL,
                  'velocity': ref('Velocity'), 'dead': B, 'navigationProfileSupported': B,
                  'mapTile': ref('TilePoint'), 'bodyTileBounds': ref('TileBounds')}),
    'limits': S,
}
SCENE_COVERAGE = {
    'scannedTiles': COUNT, 'unknownCells': desc(COUNT, 'Unread cells are not air.'),
    'uninterpretedCollisionCells': COUNT, 'unknownNavigationCells': COUNT,
    'geometryComplete': desc(B, 'No unknown or uninterpreted geometry or navigation cells.'),
}
SCENE_SAMPLE = fields(strings='phase', booleans='sameGameUpdate', numbers='elapsedMilliseconds')
ORIGIN = {'originX': TILE_COORD, 'originY': TILE_COORD}
SCENE_HAZARDS = {'tiles': scene_list(ref('SceneTileHazard')), 'entities': scene_list(ref('SceneEntityHazard'))}
SCENE_COUNTS = {'observed': dictionary(COUNT), 'returned': dictionary(COUNT)}
CANDIDATE_VALIDATION = {'validation': S}


def scene_variant(regional):
    properties = dict(SCENE_BASE)
    if regional:
        properties.update(
            schemaVersion=const(3), scale=const('region'),
            coverage=obj({**SCENE_COVERAGE, 'requestedTiles': COUNT, 'unknownByReason': dictionary(COUNT),
                          'samplingTruncated': B,
                          'knowledge': grid(obj(fields(strings='tile collision', booleans='navigationKnown')))}),
            sample=obj({**SCENE_SAMPLE, **fields(strings='source', booleans='samplingBudgetReached',
                        integers='allocatedBytes payloadBytesBeforeMetrics maxTiles projectionAllocatedBytes',
                        numbers='samplingBudgetMilliseconds projectionElapsedMilliseconds')}),
        )
        layers = {
            'map': obj({**ORIGIN, 'terrain': grid(S), 'actor': ref('TilePoint'),
                        'markers': scene_list(obj({**TILE_XY, 'glyph': S}))}),
            'geometry': obj({**ORIGIN, 'grid': grid(S)}),
            'objects': scene_list(ref('SceneObjectRegional'), {
                'matchedCountLowerBound': COUNT, 'groupingTruncated': B, 'maxGroups': COUNT,
                'kindGrid': grid(obj({'kind': S, 'contentKey': nullable(S)})),
            }, nullable_counts=True),
            'hazards': obj({**SCENE_HAZARDS, 'tileMask': grid(I), 'bitSources': array(S)}),
        }
        companions = {'objects': {'objectCounts': obj({**SCENE_COUNTS, 'observedCountsComplete': B})}}
    else:
        properties.update(schemaVersion=const(2), coverage=obj(SCENE_COVERAGE), sample=obj(SCENE_SAMPLE))
        rows = obj({**ORIGIN, 'rows': array(S)})
        layers = {
            'map': rows, 'geometry': rows, 'objects': scene_list(ref('SceneObjectLocal')),
            'hazards': obj(SCENE_HAZARDS),
            'landingRegions': scene_list(obj({'id': S, 'target': ref('LandingRegion'), 'feetDeltaY': N}),
                                         CANDIDATE_VALIDATION, optional=('validation',)),
            'placementCandidates': scene_list(obj({'id': S, 'target': ref('TilePoint'), 'solidNeighbor': ref('TilePoint')}),
                                              CANDIDATE_VALIDATION, optional=('validation',)),
            'objectPlacementCandidates': scene_list(obj({
                'id': S, 'target': ref('TilePoint'), 'footprint': ref('TileRect'), 'support': ref('TileRect'),
                'distancePixels': N, 'contentKey': S, 'style': I,
            }), {'diagnostics': ref('ScenePlacementDiagnostic'), **CANDIDATE_VALIDATION}, optional=('validation',)),
        }
        companions = {
            'objects': {'objectCounts': obj(SCENE_COUNTS)},
            'placementCandidates': {'placementMaterial': obj({
                **fields(integers='itemType stack createTile', booleans='supported'), 'hotbarKey': HOTBAR,
            })},
            'objectPlacementCandidates': {'objectPlacementMaterial': obj(fields(
                integers='itemType stack', booleans='supported definitionSupported', strings='anchorMeaning'))},
        }
    properties['includedLayers'] = {**array(enum(*layers)), 'minItems': 1, 'uniqueItems': True}
    extra = {name: schema for companion in companions.values() for name, schema in companion.items()}
    schema = obj({**properties, **layers, **extra}, optional=(*layers, *extra))
    schema['allOf'] = []
    for layer in layers:
        required = {'required': [layer, *companions.get(layer, ())]}
        schema['allOf'].append({
            'if': {'properties': {'includedLayers': {'contains': const(layer)}}},
            'then': required,
            'else': {'not': any_of(*({'required': [name]} for name in required['required']))},
        })
    return schema


DEFS['SceneLocal'] = scene_variant(False)
DEFS['SceneRegional'] = scene_variant(True)

# These action records use the default C# serializer (PascalCase), unlike
# inspect_tile and observe_scene. Anonymous object member names stay as written.
def wire_pose():
    return obj({name[0].upper() + name[1:]: schema for name, schema in PHYSICS_FIELDS.items()}
               | {'Air': nullable(ref('WireAirResources')), 'Wet': B}, optional=('Wet',))


DEFS.update({
    'WireAirResources': obj(fields(numbers='WingTime', integers='WingMax RocketTime RocketMax Delay Boots',
                                    booleans='CanRocket RocketRelease RocketFrame')),
    'WirePose': wire_pose(),
    'PhysicsParameters': obj({
        **fields(numbers='AccRun MaxRun AccRunSpeed RunSlowdown Gravity MaxFall JumpSpeed', integers='JumpHeight'),
        'ExtraJump': nullable(obj(fields(strings='FullName', numbers='DurationMultiplier'))),
        'Air': nullable(obj({'Kind': AIR_KIND})),
        'Fall': obj(fields(integers='ExtraFall', booleans='Immune')),
    }, optional=('Fall',)),
    'MovementSnapshot': obj({
        **fields(integers='GrappleCount', booleans='BlockExtraJumps GrappleListComplete', strings='Scope'),
        'ExtraJumps': array(obj(fields(strings='FullName', integers='Order', booleans='Enabled Available Active'))),
        'Wings': obj({
            **fields(integers='FunctionalSlot TimeMax RegisteredSlots', numbers='Time'),
            'BaseStats': nullable(obj(fields(
                integers='FlyTime', numbers='AccRunSpeedOverride AccRunAccelerationMult DownHoverSpeedOverride DownHoverAccelerationMult',
                booleans='HasDownHoverStats'))),
        }),
        'Rockets': obj(fields(integers='Boots Time TimeMax Delay Delay2', booleans='CanRocket Release Frame')),
        'Mount': obj({**fields(integers='Type RegisteredTypes', booleans='Active'), 'ModFullName': nullable(S)}),
        'Grapples': array(obj(fields(integers='Order ProjectileIndex Type Identity Owner',
                                    booleans='Valid Active RegisteredHook'))),
        'Environment': obj(fields(integers='PulleyDir', booleans='Pulley Wet HoneyWet LavaWet ShimmerWet')),
    }),
    'MobilityProfile': obj({
        **fields(integers='Revision SourceTick Session Generation Width Height', strings='Variant Source',
                 booleans='SwiftnessExpiryTail Supported'),
        'EffectivePhysics': nullable(ref('PhysicsParameters')), 'ObservedPhysics': nullable(ref('PhysicsParameters')),
        'AvailableModes': array(S), 'UnsupportedEffects': array(S), 'Movement': nullable(ref('MovementSnapshot')),
    }),
    'SurfaceGraph': obj({
        **fields(integers='TileReads Nodes CandidateEdges VerifiedEdges Expansions SliceWork ChangedTiles RetainedNodes RangeExpansions'),
        'Exit': desc(S, 'Search exit reason such as budget, unsupported_model or surface_node_budget. It does not establish global reachability.'),
        'Radius': desc(I, 'Current search radius in Tiles.'),
        'SliceMilliseconds': desc(N, 'Elapsed wall-clock milliseconds of the current search slice.'),
        'Waypoint': nullable(ref('LandingRecord')),
        'EstimatedRouteUpdates': desc(nullable(N), 'Estimated route cost in game updates, not a confirmed duration.'),
        'CandidateReachedGoal': desc(B, 'A graph candidate reached the goal in search. A candidate alone cannot issue inputs or establish arrival.'),
    }),
    'LandingFeedback': obj({
        **fields(integers='ActionNumber', booleans='Jump Settled', numbers='MaxError'),
        **dict.fromkeys(('Predicted', 'Actual'), nullable(ref('WirePoint'))),
        **dict.fromkeys(('PredictedVelocity', 'ActualVelocity'), nullable(ref('WireVelocity'))),
    }),
    'AirFeedback': obj({
        **fields(integers='Frame', booleans='Confirmed', strings='Motion'),
        'Kind': AIR_KIND, 'Phase': AIR_PHASE,
        'Expected': nullable(ref('WireAirResources')), 'Observed': nullable(ref('WireAirResources')),
    }),
    'NavigationFailure': desc(obj({
        **fields(strings='Motion Reason', integers='ExtraJumpFrame AirStartFrame AirHoldFrames'),
        'Direction': desc(I, 'Signed horizontal input: -1 left, 0 neutral, 1 right.'),
        'Hold': desc(I, 'Count of predicted frames with jump input held.'),
        'Stride': desc(I, 'Count of predicted frames with horizontal input for a non-jump edge; zero for a jump.'),
        'Terrain': desc(I, 'Opaque terrain signature used to invalidate failure memory when terrain changes.'),
        'Expires': desc(I, 'Expiry on the navigation update counter. Scoped to one navigation task.'),
        'Start': ref('WirePose'), 'ExpectedLanding': ref('WirePose'), 'ActualLanding': ref('WirePose'),
        'Physics': nullable(ref('PhysicsParameters')),
    }), 'Recent failed edge excluded from this navigation search. It is not a permanent obstacle assertion.'),
    'NavigationSnapshot': obj({
        **fields(integers='Session Frame HoldFrames Generation CompletedActions CompletedJumps Expansions PlannedActions PlanNumber LandingConfirmationFrames SearchNumber FastWalkPlans DominatedStates RecoveryCount RecoveryReplans FailedEdgeCount TripUpdates ContinuedEdges InvalidatedRoutes DetourCheckpoints VisitedCheckpoints RepeatRejections MobilityRevision PlanMobilityRevision DeferredSearches SearchCacheHits SearchWaitUpdates NoProgressUpdates ActualMovingUpdates SearchExpansionHits',
                 numbers='MaxError PlanMilliseconds TripPlanningMilliseconds ActualTravelPixels'),
        **dict.fromkeys('Reason SearchFailureReason PlanKind SearchExit RecoveryReason'.split(), nullable(S)),
        'Status': desc(nullable(S), 'Motion controller diagnostic such as idle, running, landed, arrived or failed. Use the outer action receipt for terminal status.'),
        'Action': desc(nullable(S), 'Current motion phase such as plan, walk, jump, or release before replanning.'),
        'ProgressActivity': desc(nullable(S), 'Watchdog activity: Search, Movement, Recovery, VerifiedCheckpoint or VerifiedEffect.'),
        'Frame': desc(I, 'Frame of the current motion edge, in game updates.'),
        'MaxError': desc(N, 'Maximum position prediction error in world pixels. Zero is not evidence of arrival.'),
        'PlanMilliseconds': desc(N, 'Wall-clock planning time in milliseconds.'),
        'TripPlanningMilliseconds': desc(N, 'Accumulated planning time in milliseconds for this trip.'),
        'ActualTravelPixels': desc(N, 'Accumulated observed travel in world pixels, distinct from displacement to the target.'),
        **dict.fromkeys('ReachedGoal TimeBudgetReached ObservedGrounded SafeSupport RecoveryInputsReleased'.split(), nullable(B)),
        **dict.fromkeys('PredictedLanding ActualLanding ExpectedPosition ObservedPosition'.split(), nullable(ref('WirePoint'))),
        **dict.fromkeys(('ExpectedVelocity', 'ObservedVelocity'), nullable(ref('WireVelocity'))),
        'BlockingTiles': array(obj({'X': TILE_COORD, 'Y': TILE_COORD, 'ContentKey': nullable(S), 'Basis': S})),
        'TargetX': nullable(PIXEL), 'TargetRegion': nullable(ref('LandingRecord')),
        'LastLanding': nullable(ref('LandingFeedback')), 'PlanStart': nullable(ref('WirePose')),
        'RecoveryPosition': nullable(ref('WirePose')), 'SearchRejections': nullable(dictionary(I)),
        'Mobility': nullable(ref('MobilityProfile')), 'FailureMemory': nullable(array(ref('NavigationFailure'))),
        'SurfaceGraph': nullable(ref('SurfaceGraph')), 'AirMotion': nullable(ref('AirFeedback')),
    }, optional=('BlockingTiles',)),
})

DEFS.update({
    'NavigationEvidence': obj({
        **dict.fromkeys(('startX', 'x', 'y'), PIXEL), **dict.fromkeys(('vx', 'vy'), SPEED),
        **fields(integers='updates health',
            booleans='arrived grounded safeSupport settled inputReleased alive recoverableFailure'),
        'targetX': nullable(PIXEL), 'targetRegion': nullable(ref('LandingRegion')),
        'velocityBeforeRelease': nullable(ref('WireVelocity')), 'failureKind': nullable(S),
        'observedGameUpdate': TICK, 'navigation': nullable(ref('NavigationSnapshot')),
        'effectStatus': const('native_navigation_observed'),
    }),
    'MiningEvidence': obj({
        'nativeActionId': S, 'X': TILE_COORD, 'Y': TILE_COORD, 'TileType': nullable(I),
        'invalidTarget': nullable(obj({**TILE_XY, 'inWorld': B, 'hasTile': nullable(B),
                                       'tileType': nullable(I), 'withinMiningBounds': B})),
        **fields(integers='ExpectedType Ticks Swings Strikes PeakDamage Transforms pick axe furnitureCells',
                 booleans='nativeDestroyed targetAbsent furnitureAbsent targetChanged targetUnchanged'),
        'destroyedByActor': desc(B, 'Target removal attributed to this actor. This does not verify pickup or inventory gain.'),
        'toolKind': enum('axe', 'pickaxe'), 'effectStatus': enum('native_tile_destroyed', 'not_started', 'not_confirmed'),
    }),
    'PlacementEvidence': obj({
        **TILE_XY, **fields(integers='typeId itemType InitialStack remainingStack Ticks Starts AnimationPeak Attempts nativePlacementEvents consumedCount style',
                            booleans='uncertainNativeEffect placedByActor objectPlacement fullFootprintVerified targetPresent targetChanged'),
        'footprint': nullable(ref('TileRect')), 'requestedX': nullable(TILE_COORD), 'requestedY': nullable(TILE_COORD),
        'extended': nullable(B), 'effectStatus': enum('native_rope_placed', 'native_object_placed', 'native_tile_placed', 'not_confirmed'),
    }),
    'UseEvidence': obj({
        **fields(integers='itemType Ticks Starts AnimationPeak TimePeak ToolPeak UsesPeak InitialStack stack',
                 booleans='consumptionObserved cycleVerified'),
        'effectStatus': desc(enum('unconfirmed', 'not_started'), 'A verified use cycle does not prove a hit.'),
    }),
    'SelectionEvidence': obj(fields(integers='requestedHotbarKey targetSlot beforeSlot',
                                    booleans='mutationAttempted selectionApplied')),
    'RopeEvidence': obj({
        **fields(integers='ropeX maxUpdates updates', numbers='targetFeetY startFeetY feetY',
                 booleans='attached attachmentObserved grounded safeSupport settled inputReleased'),
        'finish': enum('land', 'hold'),
    }),
})

INVENTORY_RECEIPT_SLOT = obj({
    **SLOT_FIELDS, **fields(integers='typeId stack prefix maxStack', booleans='favorited'),
    'instanceId': nullable(S), 'inventoryToken': INVENTORY_TOKEN,
})
BASIC_SLOT = any_of(
    obj({'slot': I, 'available': const(False), 'reason': const('baseline_not_established')}),
    obj({'slot': I, 'available': const(True), 'present': B, 'instanceId': nullable(S), 'instanceChanged': B,
         **dict.fromkeys(('typeId', 'stack', 'prefix', 'maxStack'), nullable(I)), 'favorited': nullable(B)}),
)
DEFS['InventoryConfirmedSnapshot'] = obj({'source': INVENTORY_RECEIPT_SLOT, 'destination': INVENTORY_RECEIPT_SLOT, 'selectedSlot': I})
DEFS['InventorySnapshot'] = any_of(
    ref('InventoryConfirmedSnapshot'),
    obj({'available': const(False), 'reason': S}),
    obj({'available': const(True), 'kind': const('basic_fields_only'), 'gameUpdate': TICK, 'selectedSlot': I,
         'source': BASIC_SLOT, 'destination': BASIC_SLOT, 'customState': const('unknown')}),
)
EQUIPMENT_RECEIPT_ITEM = obj({
    **fields(integers='typeId stack prefix', booleans='favorited', strings='token'), 'instanceId': nullable(S),
})
DEFS.update({
    'EquipmentSnapshot': obj(dict.fromkeys(('backpack', 'equipment'), EQUIPMENT_RECEIPT_ITEM)),
    'InventoryEvidence': obj({
        **fields(integers='sourceSlot destinationSlot selectedSlot', booleans='mutationAttempted inventoryVerified',
                 strings='effectStatus'),
        'before': nullable(ref('InventorySnapshot')), 'after': nullable(ref('InventorySnapshot')),
        'mutationGameUpdate': TICK, 'confirmedAtGameUpdate': TICK, 'nativeError': nullable(S),
    }),
    'SortEvidence': obj({
        **fields(booleans='mutationAttempted sortVerified'),
        'mutationGameUpdate': TICK, 'observedGameUpdate': TICK, 'chestIndex': nullable(COUNT),
        'changedSlots': array(obj(fields(integers='slot typeId stack prefix', booleans='favorited', strings='instanceId'))),
    }),
    'ContainerBatchEvidence': obj({
        'mutationAttempted': B, 'mutationGameUpdate': TICK, 'observedGameUpdate': TICK,
        'effectStatus': enum('complete', 'partial', 'none', 'unknown'), 'movedCount': nullable(COUNT),
        'moves': array(obj(fields(integers='inventorySlot chestIndex containerSlot typeId prefix count'))),
        'blocked': array(obj(fields(integers='slot remaining', strings='reason'))),
    }),
    'ContainerTransferSlot': obj({**fields(integers='typeId stack prefix maxStack', booleans='favorited')}),
    'ContainerTransferPair': obj({'source': ref('ContainerTransferSlot'), 'destination': ref('ContainerTransferSlot')}),
    'ContainerTransferEvidence': obj({
        **fields(integers='inventorySlot containerSlot requestedCount', booleans='mutationAttempted'),
        **dict.fromkeys(('movedCount', 'remainingCount', 'blockedSlot', 'chestIndex'), nullable(COUNT)),
        'before': nullable(ref('ContainerTransferPair')), 'after': nullable(ref('ContainerTransferPair')),
        'effectStatus': enum('complete', 'partial', 'none', 'unknown'),
        'mutationGameUpdate': TICK, 'observedGameUpdate': TICK,
    }),
    'ContainerEvidence': obj({
        **fields(integers='chestIndex', booleans='mutationAttempted containerVerified'),
        **dict.fromkeys(('x', 'y', 'typeId'), nullable(I)),
        'mutationGameUpdate': TICK, 'observedGameUpdate': TICK,
    }),
    'EquipmentEvidence': obj({
        **fields(integers='backpackSlot', booleans='mutationAttempted equipmentVerified'),
        'targetSlotId': S, 'before': nullable(ref('EquipmentSnapshot')), 'after': nullable(ref('EquipmentSnapshot')),
        'mutationGameUpdate': TICK, 'confirmedAtGameUpdate': TICK, 'nativeError': nullable(S),
    }),
    'CraftEvidence': obj({
        **fields(integers='recipeId requestedCrafts completedCrafts producedCount outputTypeId outputCountPerCraft',
                 booleans='mutationAttempted inventoryVerified', strings='recipeToken effectStatus'),
        'batches': array(obj({'consumed': array(obj({'typeId': I, 'count': I}))})),
        'pendingOutput': nullable(ref('RecipeItem')),
    }),
    'MoveEvidence': obj({
        **fields(strings='actionId', integers='updatesRequested updatesExecuted healthStart healthEnd healthDelta blockedHorizontalUpdates',
                 booleans='grounded airborne dead inputReleased settled noProgress'),
        'requestedInputs': ref('MoveArguments'),
        **dict.fromkeys(('startPosition', 'endPosition'), ref('WirePoint')),
        'endVelocity': ref('WireVelocity'),
        'acceptedGameUpdate': TICK, 'firstInputGameUpdate': nullable(TICK),
        'lastInputGameUpdate': nullable(TICK), 'observedGameUpdate': TICK,
        'effectStatus': const('native_input_observed'),
    }),
})

# Echoed arguments use the existing input contract, while receipts and their
# result evidence are independently typed.
DEFS['MoveArguments'] = atomic.SCHEMAS['move_input']
TARGETS = {
    'targetRegion': ('navigate_to_landing_region',),
    'targetTile': ('place_tile', 'place_object', 'place_rope'),
    'targetContainer': tuple(sorted(atomic.CONTAINER_OPERATIONS)),
    'targetRecipe': ('craft_recipe',),
    'targetInventory': ('transfer_inventory_item', 'swap_inventory_slots', 'hold_inventory_item',
                        'equip_item', 'unequip_item'),
    'targetTask': ('navigate_to', 'mine_target', 'combat_attack'),
}
MINE_ARGUMENTS = any_of(obj({**TILE_XY, 'cluster': B}, optional=('cluster',)),
                        obj({'target': ref('MineToken'), 'cluster': B}, optional=('cluster',)))
COMBAT_ARGUMENTS = obj({'entityRef': S, 'threat': S}, optional=('threat',))


def target_schema(key, names):
    variants = [MINE_ARGUMENTS if name == 'mine_target' else COMBAT_ARGUMENTS if name == 'combat_attack' else atomic.SCHEMAS[name]
                for name in TARGETS[key] if name in names]
    return any_of(*variants, NULL) if variants else NULL


ACTION_STATUS = enum('running', 'completed', 'failed', 'cancelled', 'timeout', 'unknown')
TERMINAL_STATUS = enum('completed', 'failed', 'cancelled', 'timeout', 'unknown')
END_IDENTITY = {**IDENTITY, 'actorId': S, 'gameUpdate': TICK, 'observationSequence': I}
DEFS['EndEvidence'] = any_of(
    obj({**END_IDENTITY, 'phase': const('ServerAI_game_thread_no_input'), 'inputsAcquired': const(False),
         'selectedSlot': I, 'selectedHotbarKey': HOTBAR, 'selectionVerified': B}),
    obj({**END_IDENTITY, 'phase': const('ServerAI_game_thread_inputs_released'), 'operation': S,
         **fields(booleans='inputsAcquired inputsReleased effectVerified')}),
)
DEFS['StopRequest'] = obj({'status': TERMINAL_STATUS, 'reason': S})

TASK_NAMES = ('navigate_to', 'mine_target', 'combat_attack')
TASK_CHILDREN = ('navigate_to_landing_region', 'mine_tile', 'place_tile', 'excavate_tiles')
TASK_EVIDENCE = {
    'navigate_to': obj({'target': ref('WorldPoint'), 'x': PIXEL, 'y': PIXEL, 'feetY': PIXEL,
                        **fields(booleans='arrived grounded safeSupport settled')}),
    'mine_target': ref('MiningEvidence'),
}
DEFS['CombatProgress'] = obj({
    'source': enum('agent', 'reflex'), 'threat': nullable(S), 'life': nullable(I),
    'target': nullable(obj(fields(integers='Slot TypeId', strings='EntityRef'))),
    'weapon': nullable(obj(fields(integers='Slot TypeId', strings='Name'))),
    'lastHit': nullable(obj({**fields(integers='LifeBefore LifeAfter DamageDone', booleans='Defeated'), 'GameUpdate': TICK})),
    'itemChecks': COUNT, 'combinedUpdates': COUNT,
})
DEFS['LocalPlanCost'] = obj(fields(numbers='Movement Mining Placement Materials Alteration Remaining'))
DEFS['LocalEditPlan'] = obj({
    'Actions': array(obj({
        **fields(integers='Slot Type WaterDirection', strings='Shape'),
        'Kind': desc(enum(0, 1, 2), 'LocalEditKind: 0 Move, 1 Mine, 2 Place. This is a candidate plan, not native execution.'),
        'X': TILE_COORD, 'Y': TILE_COORD,
        'Start': ref('WirePose'), 'Expected': ref('WirePose'),
    })),
    'Cost': ref('LocalPlanCost'), 'OpenExit': B,
})
PLANNING_DECISION = {
    'choice': enum('movement', 'local_edits'), 'movementCost': nullable(N), 'movementVerifiedComplete': B,
    'candidateCount': COUNT, 'selected': nullable(ref('LocalEditPlan')),
    'liquidBoundary': B, 'virtualNativeVerified': const(False),
}
DEFS['TaskPlanning'] = any_of(
    obj({'status': const('evaluation_budget'), 'work': I}),
    obj({'status': const('movement_budget'), 'candidates': COUNT, 'graph': nullable(ref('SurfaceGraph'))}),
    obj({'status': const('movement_evaluation_incomplete'), 'choice': const('movement'),
         'graph': nullable(ref('SurfaceGraph'))}),
    obj({'status': const('complete'), **PLANNING_DECISION, 'graph': nullable(ref('SurfaceGraph')),
         'history': array(obj({**PLANNING_DECISION, 'gameUpdate': TICK})),
         'candidates': array(obj({'Cost': ref('LocalPlanCost'), 'OpenExit': B, 'changes': COUNT, 'steps': COUNT}))}),
)
DEFS['TaskProgress'] = obj({
    **fields(strings='activity', integers='clearedTiles dugTiles placedTiles openingMoves',
             booleans='childrenTruncated openingMode'),
    'stage': desc(S, 'Current task strategy stage such as observe, navigating, opening_tile, settling, mining or verify. See task status for the outcome.'),
    'completedChildren': desc(COUNT, 'Child actions that completed successfully. This count does not establish root completion.'),
    'settledChildren': desc(COUNT, 'Child actions that ended, including those that did not complete.'),
    'omittedChildren': desc(COUNT, 'Ended child views omitted from the bounded history.'),
    'verifiedTerrainActions': desc(COUNT, 'Terrain edits verified by the task watchdog.'),
    'children': desc(array(ref('GameTaskView')), 'Bounded history of ended child tasks. The active child is reported separately.'),
    'target': any_of(ref('WorldPoint'), obj({**TILE_XY, 'typeId': I, 'toolSlot': I, 'scope': S})),
    'search': nullable(S), 'planning': nullable(ref('TaskPlanning')),
    'opening': nullable(obj({'drops': array(obj({'fromFeetY': PIXEL, 'toFeetY': PIXEL,
                                                 'playerLeftX': PIXEL, 'gameUpdate': TICK}))})),
})
DEFS['ExcavationProof'] = obj({'X': TILE_COORD, 'Y': TILE_COORD, **fields(integers='Type Update Strikes')})
DEFS['MiningRunEvidence'] = obj({**fields(integers='requested actualUpdates strikes toolType', numbers='estimatedUpdates'),
                                 'verified': array(ref('ExcavationProof'))})
DEFS['ExcavationEvidence'] = obj({
    'outcome': enum('running', 'completed', 'partial', 'interrupted', 'failed', 'unknown'),
    'seedX': TILE_COORD, 'seedY': TILE_COORD, 'typeId': I,
    'targets': array(obj({**TILE_XY, 'typeId': I, 'status': enum('pending', 'verified', 'changed'),
                          'proof': nullable(ref('ExcavationProof'))})),
})
DEFS['ExcavationProgress'] = obj({
    'stage': S, **fields(integers='completedChildren settledChildren omittedChildren', booleans='childrenTruncated'),
    'children': array(ref('GameTaskView')), 'excavation': ref('ExcavationEvidence'),
})
TASK_LEAF_EVIDENCE = (ref('MiningEvidence'), ref('NavigationEvidence'), ref('PlacementEvidence'), ref('MiningRunEvidence'))


def native_preflight(*operations):
    return obj({'operation': enum(*operations), 'mutationAttempted': const(False),
                'effectStatus': const('not_started')})


DEFS['TaskReceipt'] = obj({
    'status': TERMINAL_STATUS, 'reason': S, 'reasonDetail': nullable(S),
    'evidence': any_of(*TASK_LEAF_EVIDENCE, TASK_EVIDENCE['navigate_to'], ref('ExcavationEvidence'), ref('CombatProgress'),
                       native_preflight(*TASK_CHILDREN), NULL),
    **fields(booleans='inputsReleased effectVerified inputsAcquired'),
})
GAME_TASK_BASE = {
    'taskId': S, 'phase': desc(S, 'Task lifecycle or strategy stage. Terminal means ended; check status for the outcome.'),
    'status': ACTION_STATUS, 'reason': S,
}
DEFS['GameTaskView'] = obj({
    **GAME_TASK_BASE, 'taskName': enum(*TASK_NAMES, *TASK_CHILDREN), **IDENTITY,
    'parentTaskId': nullable(S), 'observedGameUpdate': TICK, 'deadlineGameUpdate': TICK,
    'currentChild': nullable(ref('GameTaskView')),
    'progress': nullable(any_of(ref('TaskProgress'), ref('ExcavationProgress'), ref('MiningRunEvidence'), ref('CombatProgress'))),
    'lastResult': nullable(ref('TaskReceipt')), 'result': nullable(ref('TaskReceipt')),
})
DEFS['AcceptedGameTask'] = obj({
    **GAME_TASK_BASE, 'taskName': enum(*TASK_NAMES), 'currentChild': NULL,
    'progress': obj({'completedChildren': const(0)}),
    'lastResult': nullable(obj({'status': ACTION_STATUS, 'reason': S,
                                'evidence': nullable(native_preflight(*TASK_NAMES))})),
})
DEFS['GameTaskEvidence'] = obj({'task': ref('GameTaskView')})
GAME_TASK = any_of(ref('GameTaskView'), ref('AcceptedGameTask'), NULL)

ACTION_EVIDENCE = {
    'craft_recipe': 'CraftEvidence',
    'transfer_inventory_item': 'InventoryEvidence',
    'swap_inventory_slots': 'InventoryEvidence',
    'hold_inventory_item': 'InventoryEvidence',
    **dict.fromkeys(sorted(atomic.SORT_OPERATIONS), 'SortEvidence'),
    **dict.fromkeys(sorted(atomic.CONTAINER_BATCHES), 'ContainerBatchEvidence'),
    **dict.fromkeys(sorted(atomic.CONTAINER_TRANSFERS), 'ContainerTransferEvidence'),
    'open_container': 'ContainerEvidence',
    'close_container': 'ContainerEvidence',
    'equip_item': 'EquipmentEvidence',
    'unequip_item': 'EquipmentEvidence',
    'move_input': 'MoveEvidence',
    'navigate_to_x': 'NavigationEvidence',
    'navigate_to_landing_region': 'NavigationEvidence',
    'select_hotbar_slot': 'SelectionEvidence',
    'use_item': 'UseEvidence',
    'navigate_to': 'GameTaskEvidence',
    'mine_target': 'GameTaskEvidence',
    'combat_attack': 'GameTaskEvidence',
    'mine_tile': 'MiningEvidence',
    'place_tile': 'PlacementEvidence',
    'place_object': 'PlacementEvidence',
    'place_rope': 'PlacementEvidence',
    'move_on_rope': 'RopeEvidence',
}
ACTION_CORE = {
    **dict.fromkeys(('worldSessionId', 'actorId', 'session', 'reasonDetail'), nullable(S)),
    'actorSlot': nullable(I), 'actionId': S, 'reason': S,
    'stopRequest': nullable(ref('StopRequest')),
    'endEvidenceStatus': desc(enum('not_ended', 'unknown', 'known'), 'Confidence in the end evidence, independent of the effect.'),
    'endEvidence': nullable(ref('EndEvidence')),
}
CONTINUATION = {
    'continuationAllowed': desc(B, 'Explicit host permission for the Lua program to continue.'),
    'continuationScope': const('observe_scene_then_move_input'),
}
DEFS['ActionRecovery'] = obj({
    'callId': I, 'operation': S, 'kind': S, 'reason': S, 'nextStep': S,
    'detail': S, 'unchangedAttemptSuppressed': const(True),
}, optional=('detail', 'unchangedAttemptSuppressed'))
RECEIPT_FLAGS = {
    'terminalActionStatus': desc(TERMINAL_STATUS, 'Native status before host release or effect verification downgraded the receipt.'),
    'supervisorError': desc(S, 'Host supervision failed. A stop receipt may still contain a native terminal result.'),
    'supervisorStopRequest': desc(obj({'status': const('timeout'), 'reason': const('supervisor_deadline')}),
                                  'The host requested a stop at its deadline. The action status reports what the stop actually observed.'),
    **CONTINUATION,
    'recovery': ref('ActionRecovery'),
}
DEFS['ActionReceipt'] = obj({
    **ACTION_CORE, **RECEIPT_FLAGS,
    'actionGeneration': nullable(I), 'controlGeneration': nullable(I),
    'status': desc(TERMINAL_STATUS, 'Unknown preserves uncertain release, identity or world effects.'),
    'dispatchState': desc(enum('admitted', 'unknown'), 'Whether the host can confirm admission.'),
    'valid': desc(B, 'Host verification of the receipt, independent of whether the action succeeded.'),
    'completed': B, 'cancelled': B, 'inputsAcquired': nullable(B),
    'inputsReleased': desc(nullable(B), 'Release of native inputs does not prove the world effect.'),
    'effectVerified': nullable(B),
}, optional=RECEIPT_FLAGS, closed=False)
RAW_TASK_ACTION = {
    **ACTION_CORE, 'operation': enum('mine_target', 'combat_attack'), 'status': ACTION_STATUS, 'generation': I, 'durationMs': I,
    'requestedInputs': NULL, 'executionState': S, 'task': GAME_TASK,
    'evidence': any_of(ref('GameTaskEvidence'), native_preflight('mine_target', 'combat_attack'), NULL),
    **{key: target_schema(key, ('mine_target', 'combat_attack')) for key in TARGETS if key != 'targetContainer'},
}
DEFS['RawProgramTaskAction'] = obj(RAW_TASK_ACTION)


def refusal(*names, resumed=False):
    return obj({
        'operation': enum(*names), 'valid': const(False), 'status': const('failed'), 'completed': const(False),
        'reason': S, 'dispatchState': const('not_admitted'), 'actionId': NULL, 'inputsAcquired': const(False),
        'inputsReleased': NULL, 'effectVerified': const(False), 'endEvidence': NULL,
        'endEvidenceStatus': const('not_started'), **CONTINUATION, 'recovery': ref('ActionRecovery'),
        **({'resumedFrom': S} if resumed else {}),
    }, optional=(*CONTINUATION, 'recovery', *(('resumedFrom',) if resumed else ())))


# stop() and terminal() can report loss of ownership without a native receipt.
# _dispatch can forward the short result, annotate it, or invalidate a bad
# start receipt and replace the reason while preserving any known action ID.
STOP_FAILURES = (
    {'status': const('cancelled'), 'reason': const('ownership_changed')},
    {'status': const('unknown'), 'reason': const('action_missing'), 'completed': const(False), 'actionId': S},
    {'status': const('unknown'), 'reason': const('cancellation_unconfirmed'), 'completed': const(False)},
    {'status': const('unknown'), 'reason': const('action_changed_before_release_evidence')},
)
STOP_CONTINUATION = {'continuationAllowed': const(False)}
DEFS['ShortStopResult'] = any_of(*(
    obj({'valid': const(False), **fields, **STOP_CONTINUATION}, optional=STOP_CONTINUATION)
    for fields in STOP_FAILURES
))
DEFS['SupervisorStopResult'] = any_of(*(
    obj({'valid': const(False), **fields, **STOP_CONTINUATION,
         'supervisorError': RECEIPT_FLAGS['supervisorError']}, optional=STOP_CONTINUATION)
    for fields in STOP_FAILURES
), *(
    obj({'valid': const(False), **fields, **STOP_CONTINUATION, 'status': const('unknown'), 'completed': const(False),
         'supervisorStopRequest': RECEIPT_FLAGS['supervisorStopRequest']}, optional=STOP_CONTINUATION)
    for fields in STOP_FAILURES
), obj({'valid': const(False), 'status': const('unknown'), 'completed': const(False), 'reason': S, 'actionId': S,
        **STOP_CONTINUATION}, optional=('actionId', *STOP_CONTINUATION)))


TRUE = const(True)
FALSE = const(False)
MINED = matches(destroyedByActor=TRUE, targetAbsent=TRUE, targetChanged=FALSE)
ARRIVED = matches(arrived=TRUE, grounded=TRUE, safeSupport=TRUE, settled=TRUE)
PLACED = dict(placedByActor=TRUE, targetPresent=TRUE, targetChanged=FALSE, consumedCount=const(1))
NATIVE_PLACEMENT = dict(nativePlacementEvents=const(1), uncertainNativeEffect=FALSE)
TASK_COMPLETION_PROOF = {
    'combat_attack': matches(taskName=const('combat_attack'), status=const('completed'), phase=const('terminal'),
        result=matches(inputsReleased=TRUE, effectVerified=TRUE, evidence=matches(
            lastHit=matches(Defeated=TRUE, LifeBefore={'minimum': 1}, LifeAfter={'maximum': 0}, DamageDone={'minimum': 1})))),
    'navigate_to': matches(taskName=const('navigate_to'), status=const('completed'), currentChild=NULL,
                           result=matches(inputsReleased=TRUE, effectVerified=TRUE, evidence=ARRIVED)),
    'mine_target': {
        **matches(taskName=const('mine_target'), status=const('completed'), phase=const('terminal')),
        'anyOf': [
            matches(lastResult=matches(status=const('completed'), inputsReleased=TRUE, effectVerified=TRUE, evidence=MINED)),
            matches(currentChild=NULL, result=matches(status=const('completed'), inputsReleased=TRUE, effectVerified=TRUE,
                evidence=matches(outcome=const('completed'), targets={
                    'type': 'array', 'minItems': 1, 'maxItems': 18,
                    'items': matches(status=const('verified'), proof=ref('ExcavationProof')),
                }))),
        ],
    },
}
SUCCESSFUL_PROOF = {
    'select_hotbar_slot': matches(selectionApplied=TRUE),
    'move_input': matches(inputReleased=TRUE),
    'navigate_to_x': matches(arrived=TRUE),
    'navigate_to_landing_region': {
        **ARRIVED, 'allOf': [matches(targetRegion=ref('LandingRegion'),
                                     vx={'minimum': -0.1, 'maximum': 0.1}, vy={'minimum': -0.1, 'maximum': 0.1})],
    },
    'use_item': matches(cycleVerified=TRUE),
    'mine_tile': MINED,
    'place_tile': matches(**PLACED),
    'place_object': matches(**PLACED, **NATIVE_PLACEMENT, objectPlacement=TRUE, fullFootprintVerified=TRUE,
                            typeId=const(18), itemType=const(36), style=const(0),
                            footprint=matches(width=const(2), height=const(1))),
    'place_rope': matches(**PLACED, **NATIVE_PLACEMENT, requestedX=I, requestedY=I, extended=B,
                          remainingStack=COUNT),
    'move_on_rope': {
        **matches(settled=TRUE, attachmentObserved=TRUE, inputReleased=TRUE,
                  updates={'minimum': 2, 'maximum': 360}),
        'if': matches(finish=const('hold')),
        'then': matches(attached=TRUE),
        'else': matches(attached=FALSE, grounded=TRUE, safeSupport=TRUE),
    },
    **dict.fromkeys(sorted(atomic.SORT_OPERATIONS), matches(mutationAttempted=TRUE, sortVerified=TRUE)),
    **dict.fromkeys(sorted(atomic.CONTAINER_BATCHES), matches(
        mutationAttempted=TRUE, effectStatus=enum('complete', 'none'), movedCount=COUNT,
        blocked={'maxItems': 0})),
    **dict.fromkeys(sorted(atomic.CONTAINER_TRANSFERS), matches(
        mutationAttempted=TRUE, effectStatus=const('complete'), movedCount={'minimum': 1},
        remainingCount=const(0), blockedSlot=NULL, before=ref('ContainerTransferPair'), after=ref('ContainerTransferPair'))),
    'open_container': matches(mutationAttempted=TRUE, containerVerified=TRUE, chestIndex=COUNT,
                              x=TILE_COORD, y=TILE_COORD, typeId=enum(21, 467)),
    'close_container': matches(mutationAttempted=TRUE, containerVerified=TRUE, chestIndex=const(-1)),
    'craft_recipe': matches(mutationAttempted=TRUE, inventoryVerified=TRUE,
                            producedCount={'minimum': 1}, outputTypeId={'minimum': 1},
                            outputCountPerCraft={'minimum': 1}),
    **{name: matches(task=proof) for name, proof in TASK_COMPLETION_PROOF.items()},
    **{name: matches(inventoryVerified=TRUE, mutationAttempted=TRUE,
                     before=ref('InventoryConfirmedSnapshot'), after=ref('InventoryConfirmedSnapshot'))
       for name in atomic.INVENTORY_OPERATIONS},
    **{name: matches(equipmentVerified=TRUE, mutationAttempted=TRUE,
                     before=ref('EquipmentSnapshot'), after=ref('EquipmentSnapshot'))
       for name in atomic.EQUIPMENT_OPERATIONS},
}


def completed_receipt(name):
    proof = {
        'completed': TRUE, 'valid': TRUE, 'cancelled': FALSE, 'dispatchState': const('admitted'),
        'endEvidenceStatus': const('known'), 'evidence': SUCCESSFUL_PROOF[name],
        'worldSessionId': {'minLength': 1}, 'actorId': {'minLength': 1},
        'actionGeneration': I, 'actorSlot': I,
        **{key: {'not': NULL} for key, names in TARGETS.items() if name in names},
    }
    if name == 'select_hotbar_slot':
        proof.update(inputsAcquired=FALSE, inputsReleased=NULL, effectVerified=NULL,
                     endEvidence=matches(phase=const('ServerAI_game_thread_no_input'),
                                         selectionVerified=TRUE, gameUpdate=COUNT))
    else:
        proof.update(inputsAcquired=B, inputsReleased=TRUE, effectVerified=TRUE,
                     endEvidence=matches(phase=const('ServerAI_game_thread_inputs_released'), operation=const(name),
                                         inputsReleased=TRUE, effectVerified=TRUE, gameUpdate=COUNT))
    if name in TASK_COMPLETION_PROOF:
        proof['task'] = TASK_COMPLETION_PROOF[name]
    return {**matches(**proof), 'properties': {**proof, **STOP_CONTINUATION},
            'not': {'required': ['continuationScope']}}


def action_variants(name):
    evidence = ref(ACTION_EVIDENCE[name])
    operation_fields = {
        **{key: target_schema(key, (name,)) for key in TARGETS
           if key != 'targetContainer' or name in atomic.CONTAINER_OPERATIONS},
        'task': GAME_TASK if name in TASK_NAMES else NULL,
        'operation': const(name),
        'evidence': nullable(evidence) if name == 'select_hotbar_slot' else any_of(evidence, native_preflight(name), NULL),
    }
    admitted = extend(ref('ActionReceipt'), operation_fields)
    admitted['allOf'].append({
        'if': {'properties': {'status': const('completed')}, 'required': ['status']},
        'then': completed_receipt(name),
        'else': {'properties': {'completed': const(False)}},
    })
    admitted['allOf'].append({
        'if': matches(status=const('cancelled')),
        'then': matches(cancelled=TRUE),
    })
    admitted['allOf'].append({
        'if': matches(status=enum('failed', 'timeout')),
        'then': matches(cancelled=FALSE),
    })
    if name in atomic.CONTAINER_BATCHES:
        admitted['allOf'].append({
            'if': matches(evidence=matches(effectStatus=const('partial'))),
            'then': matches(status=const('failed'), valid=TRUE, completed=FALSE, effectVerified=TRUE,
                            endEvidenceStatus=const('known'),
                            endEvidence=matches(effectVerified=TRUE, inputsReleased=TRUE),
                            evidence=matches(movedCount={'minimum': 1}, moves={'minItems': 1},
                                             blocked={'minItems': 1})),
        })
    if name in atomic.CONTAINER_TRANSFERS:
        admitted['allOf'].append({
            'if': matches(evidence=matches(effectStatus=const('partial'))),
            'then': matches(status=const('failed'), valid=TRUE, completed=FALSE, effectVerified=TRUE,
                            endEvidenceStatus=const('known'),
                            endEvidence=matches(effectVerified=TRUE, inputsReleased=TRUE),
                            evidence=matches(mutationAttempted=TRUE, movedCount={'minimum': 1},
                                             remainingCount={'minimum': 1}, blockedSlot=COUNT,
                                             before=ref('ContainerTransferPair'), after=ref('ContainerTransferPair'))),
        })
    minimal = obj({
        'operation': const(name), 'valid': const(False), 'status': enum('unknown', 'failed'),
        'completed': const(False), 'reason': S, 'dispatchState': enum('unknown', 'not_admitted'),
        'actionId': nullable(S), **RECEIPT_FLAGS,
    }, optional=('operation', 'actionId', *RECEIPT_FLAGS))
    return any_of(admitted, refusal(name), minimal, ref('ShortStopResult'), ref('SupervisorStopResult'))


def action_ref(name):
    return ref('Action_' + name)


DEFS.update({'Action_' + name: action_variants(name) for name in ACTION_EVIDENCE})
DEFS['ProgramTaskUnknownOutcome'] = obj({
    'valid': const(False), 'status': const('unknown'), 'completed': const(False),
    'operation': enum('mine_target', 'combat_attack'), 'actionId': S, 'reason': S, 'retryAllowed': const(False), **STOP_CONTINUATION,
}, optional=STOP_CONTINUATION)
DEFS['ProgramTaskOrigin'] = obj({
    'chatId': nullable(any_of(I, S)), 'programId': S, 'runId': nullable(S),
    'mcp_session_id': S, 'mcp_request_id': any_of(I, S), 'mcp_tool_name': S,
}, optional=('mcp_session_id', 'mcp_request_id', 'mcp_tool_name'))
PROGRAM_TASK_BASE = {
    'taskId': S, 'operation': enum('mine_target', 'combat_attack'),
    'status': enum('submitting', 'running', 'completed', 'failed', 'cancelled', 'timeout', 'unknown'),
    'valid': B, 'completed': B, 'dispatchState': enum('unknown', 'admitted', 'not_admitted'),
    'origin': nullable(ref('ProgramTaskOrigin')), 'retryAllowed': const(False),
    'action': ref('RawProgramTaskAction'),
    'result': any_of(action_ref('mine_target'), action_ref('combat_attack'), ref('ProgramTaskUnknownOutcome')), 'resultSequence': I,
}
PROGRAM_TASK_OPTIONAL = ('action', 'result', 'resultSequence')
DEFS['ProgramTaskView'] = obj(PROGRAM_TASK_BASE, optional=PROGRAM_TASK_OPTIONAL)
DEFS['UnknownTask'] = obj({
    'status': const('unknown'), 'taskId': S, 'reason': const('unknown_task'), 'retryAllowed': const(False),
})

PRE_DISPATCH = obj({
    'valid': const(False), 'status': const('failed'), 'completed': const(False),
    'reason': S, 'dispatchState': const('not_admitted'),
})


def stop_schema(operations, program_tasks=True):
    branches = [
        obj({'valid': const(True), 'status': const('completed'), 'reason': const('no_agent_action'),
             'noOp': const(True), **STOP_CONTINUATION}, optional=STOP_CONTINUATION),
        ref('ShortStopResult'),
        obj({**PRE_DISPATCH['properties'], **CONTINUATION, 'recovery': ref('ActionRecovery')},
            optional=(*CONTINUATION, 'recovery')),
    ]
    if program_tasks:
        branches.extend((ref('ProgramTaskUnknownOutcome'), refusal('mine_target', 'combat_attack')))
    return any_of(*branches, *(action_ref(name) for name in ACTION_EVIDENCE if name in operations))


REGISTRY = {
    **{name: any_of(schema, PRE_DISPATCH) for name, schema in OBSERVATIONS.items()},
    'observe_scene': any_of(observation(ref('SceneLocal')), PRE_DISPATCH),
    'query_combat': any_of(observation(obj({'action': nullable(ref('RawProgramTaskAction'))})), PRE_DISPATCH),
    'wait': any_of(obj({
        'valid': const(True), 'status': const('completed'),
        'elapsedMilliseconds': desc(N, 'Elapsed milliseconds on the host monotonic clock.'),
        'clock': const('host_monotonic'), 'effectStatus': const('not_applicable'),
    }), PRE_DISPATCH),
    **{name: action_ref(name) for name in ACTION_EVIDENCE},
    'mine_target': any_of(action_ref('mine_target'), ref('ProgramTaskUnknownOutcome')),
    'stop_action': stop_schema(ACTION_EVIDENCE),
    'submit_task': any_of(ref('ProgramTaskView'), refusal('submit_task')),
    'query_task': any_of(ref('ProgramTaskView'), ref('UnknownTask')),
    'cancel_task': any_of(ref('ProgramTaskView'), ref('UnknownTask')),
    'await_task': any_of(ref('ProgramTaskView'), ref('UnknownTask'), obj({
        **PROGRAM_TASK_BASE,
        'waitStatus': desc(const('timeout'), 'The wait expired; the game task was not cancelled.'),
    }, optional=PROGRAM_TASK_OPTIONAL)),
    'resume_task': any_of(obj({**PROGRAM_TASK_BASE, 'resumedFrom': S}, optional=PROGRAM_TASK_OPTIONAL),
                          ref('UnknownTask'), refusal('resume_task', 'submit_task', resumed=True)),
}


def registered_names():
    return set(REGISTRY)


def missing_for(exposed_names):
    return set(exposed_names) - registered_names()


def schema_for(name, *, capabilities=()):
    """Return one independent, self-contained result schema; unknown names raise KeyError."""
    schema = REGISTRY[name]
    if name == 'observe_scene' and 'server_ai_scene_region_v1' in capabilities:
        schema = any_of(observation(any_of(ref('SceneLocal'), ref('SceneRegional'))), PRE_DISPATCH)
    if name == 'stop_action' and capabilities:
        operations = operations_for_capabilities(capabilities)
        schema = stop_schema(operations, 'mine_target' in operations and 'server_ai_program_tasks_v1' in capabilities)
    definitions = {}

    def include_dependencies(node):
        if isinstance(node, dict):
            if '$ref' in node:
                prefix = '#/$defs/'
                key = node['$ref'].removeprefix(prefix)
                if key not in definitions:
                    definitions[key] = DEFS[key]
                    include_dependencies(DEFS[key])
            for child in node.values():
                include_dependencies(child)
        elif isinstance(node, list):
            for child in node:
                include_dependencies(child)

    include_dependencies(schema)
    return deepcopy({'$schema': DRAFT, **schema, **({'$defs': definitions} if definitions else {})})
