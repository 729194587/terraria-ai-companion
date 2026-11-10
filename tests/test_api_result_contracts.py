"""Validate curated native results and synthetic fixtures independently of the registry."""
from copy import deepcopy
import json
from pathlib import Path
import unittest

from jsonschema import Draft202012Validator

from test_server_ai_contract import FULL, bridge
from api_result_contracts import schema_for
from server_ai_contract import server_definitions


FIXTURES = Path(__file__).with_name('fixtures') / 'api_results'


def load(name):
    return json.loads((FIXTURES / f'{name}.json').read_text(encoding='utf-8'))


POINT = {'X': 16, 'Y': 32}
LANDING = {'minPlayerLeftX': 16, 'maxPlayerLeftX': 30, 'surfaceY': 64}
PLACEMENT = dict(
    x=1, y=2, typeId=0, itemType=2, InitialStack=5, remainingStack=4, Ticks=7, Starts=1, AnimationPeak=6,
    Attempts=1, nativePlacementEvents=1, uncertainNativeEffect=False, consumedCount=1, placedByActor=True,
    objectPlacement=False, footprint=None, fullFootprintVerified=False, style=0, requestedX=None, requestedY=None,
    extended=None, targetPresent=True, targetChanged=False, effectStatus='native_tile_placed',
)
NAVIGATION = dict(
    targetX=16, targetRegion=None, startX=12, x=16, y=22, vx=0, vy=0, updates=12, navigation=None,
    arrived=True, grounded=True, safeSupport=True, settled=True, velocityBeforeRelease=POINT, inputReleased=True,
    alive=True, health=100, recoverableFailure=False, failureKind=None, observedGameUpdate=12,
    effectStatus='native_navigation_observed',
)
MOVE = dict(
    actionId='fixture-action', requestedInputs=dict(horizontal='right', vertical='none', jumpHoldUpdates=0, maxUpdates=1),
    updatesRequested=1, updatesExecuted=1, startPosition=POINT, endPosition=POINT, endVelocity={'X': 0, 'Y': 0},
    grounded=True, airborne=False, healthStart=100, healthEnd=100, healthDelta=0, dead=False, inputReleased=True,
    settled=True, noProgress=False, blockedHorizontalUpdates=0, acceptedGameUpdate=1, firstInputGameUpdate=2,
    lastInputGameUpdate=2, observedGameUpdate=2, effectStatus='native_input_observed',
)


def receipt(operation, evidence, **targets):
    """Representative admitted receipt from the Python dispatcher projection."""
    return dict(
        valid=True, status='completed', reason='fixture_completed', reasonDetail=None, dispatchState='admitted',
        worldSessionId='fixture-world', actorId='fixture-actor', actorSlot=254, inputsAcquired=True,
        inputsReleased=True, effectVerified=True, stopRequest=None, operation=operation, actionId='fixture-action',
        session='fixture-session', actionGeneration=1, controlGeneration=1, task=None, evidence=evidence,
        endEvidence=dict(
            phase='ServerAI_game_thread_inputs_released', gameUpdate=12, worldSessionId='fixture-world',
            actorId='fixture-actor', actorGeneration=1, actorSlot=254, observationSequence=1, operation=operation,
            inputsAcquired=True, inputsReleased=True, effectVerified=True,
        ),
        endEvidenceStatus='known', completed=True, cancelled=False,
        **{**dict.fromkeys(('targetRegion', 'targetTile', 'targetRecipe', 'targetInventory', 'targetTask')), **targets},
    )


def extra_actions():
    tile = {'x': 1, 'y': 2}
    return {
        'move_input': receipt('move_input', MOVE),
        'navigate_to_x': receipt('navigate_to_x', NAVIGATION),
        'navigate_to_landing_region': receipt(
            'navigate_to_landing_region', {**NAVIGATION, 'targetX': None, 'targetRegion': LANDING}, targetRegion=LANDING),
        'use_item': receipt('use_item', dict(
            itemType=3507, Ticks=8, Starts=1, AnimationPeak=6, TimePeak=8, ToolPeak=0, UsesPeak=1,
            InitialStack=1, stack=1, consumptionObserved=False, cycleVerified=True, effectStatus='unconfirmed',
        )),
        'craft_recipe': receipt('craft_recipe', dict(
            recipeId=1, recipeToken='a'*64, requestedCrafts=1, completedCrafts=1, producedCount=3,
            outputTypeId=8, outputCountPerCraft=3, mutationAttempted=True,
            batches=[{'consumed': [{'typeId': 9, 'count': 1}]}], pendingOutput=None,
            inventoryVerified=True, effectStatus='native_crafted_to_inventory',
        ), targetRecipe={'recipeId': 1, 'recipeToken': 'a'*64, 'crafts': 1}),
        'place_tile': receipt('place_tile', PLACEMENT, targetTile=tile),
        'place_object': receipt('place_object', {
            **PLACEMENT, 'typeId': 18, 'itemType': 36, 'objectPlacement': True,
            'footprint': {**tile, 'width': 2, 'height': 1}, 'fullFootprintVerified': True,
            'effectStatus': 'native_object_placed',
        }, targetTile=tile),
        'place_rope': receipt('place_rope', {
            **PLACEMENT, 'typeId': 213, 'itemType': 965, 'requestedX': 1, 'requestedY': 1,
            'extended': True, 'effectStatus': 'native_rope_placed',
        }, targetTile={'x': 1, 'y': 1}),
        'move_on_rope': receipt('move_on_rope', dict(
            ropeX=1, targetFeetY=96, finish='hold', maxUpdates=15, updates=11, startFeetY=80, feetY=96,
            attached=True, attachmentObserved=True, grounded=False, safeSupport=False, settled=True, inputReleased=True,
        )),
        'stop_action': dict(valid=True, status='completed', reason='no_agent_action', noOp=True),
        'wait': dict(valid=True, status='completed', elapsedMilliseconds=8.5,
                     clock='host_monotonic', effectStatus='not_applicable'),
    }


def program_views(mined):
    accepted_task = dict(taskId='fixture-task', taskName='mine_target', phase='accepted', status='running',
                         currentChild=None, progress={'completedChildren': 0}, lastResult=None, reason='accepted')
    action = dict(
        actionId='fixture-task', operation='mine_target', session='fixture-session', generation=1,
        worldSessionId='fixture-world', actorId='fixture-actor', actorSlot=254, durationMs=55000,
        targetTile=None, targetInventory=None, targetTask={'x': 1, 'y': 2}, targetRecipe=None,
        requestedInputs=None, targetRegion=None, task=accepted_task, status='running', executionState='accepted',
        reason='accepted', reasonDetail=None, stopRequest=None, evidence=None, endEvidenceStatus='not_ended', endEvidence=None,
    )
    running = dict(taskId='fixture-task', operation='mine_target', status='running', valid=True, completed=False,
                   dispatchState='admitted', origin={'chatId': None, 'programId': 'fixture-program', 'runId': None},
                   retryAllowed=False, action=action)
    finished = {
        **running, 'status': 'completed', 'completed': True, 'result': mined, 'resultSequence': 1,
        'action': {**action, 'status': 'completed', 'executionState': 'terminal', 'task': mined['task'],
                   'evidence': mined['evidence'], 'endEvidenceStatus': 'known', 'endEvidence': mined['endEvidence']},
    }
    return {
        'submit_task': running, 'query_task': finished, 'await_task': {**running, 'waitStatus': 'timeout'},
        'cancel_task': {'status': 'unknown', 'taskId': 'gone', 'reason': 'unknown_task', 'retryAllowed': False},
        'resume_task': {**running, 'resumedFrom': 'previous'},
    }


class TypedResultTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.observations = load('observations')
        cls.actions = {**load('actions'), **extra_actions()}
        cls.programs = program_views(cls.actions['mine_target'])

    def valid(self, name, result, capabilities=FULL):
        Draft202012Validator(schema_for(name, capabilities=capabilities)).validate(result)

    def invalid(self, name, result, capabilities=FULL):
        self.assertFalse(Draft202012Validator(schema_for(name, capabilities=capabilities)).is_valid(result), name)

    def test_every_exposed_api_has_a_representative_value(self):
        fixtures = {**self.observations, **self.actions, **self.programs,
                    'inspect_inventory': self.observations['inspect_inventory_selected']}
        names = {row['function']['name'] for row in server_definitions(bridge(FULL))}
        self.assertEqual(35, len(names))
        for name in sorted(names):
            with self.subTest(name=name):
                self.valid(name, fixtures[name])

    def test_observation_variants_and_field_types(self):
        self.valid('get_player_state', {'valid': False, 'available': False, 'reason': 'unavailable'})
        state = deepcopy(self.observations['get_player_state'])
        del state['binding']
        self.valid('get_player_state', state)
        state['player']['life'] = None
        self.invalid('get_player_state', state)

        for key in ('full', 'selected', 'item', 'item_missing'):
            self.valid('inspect_inventory', self.observations[f'inspect_inventory_{key}'])
        for mode in ('hotbar', 'slots'):
            row = deepcopy(self.observations['inspect_inventory_selected'])
            row['data']['inventoryScope']['mode'] = mode
            self.valid('inspect_inventory', row)
            row['data']['inventory'][0]['inventoryToken'] = 7
            self.invalid('inspect_inventory', row)

        unknown = deepcopy(self.observations['inspect_tile_unknown'])
        self.valid('inspect_tile', unknown)
        unknown['data']['preflight'] = {}
        self.invalid('inspect_tile', unknown)
        tiles = deepcopy(self.observations['scan_tiles'])
        tiles['data'].update(complete=False, truncated=True, unknownCount=2, status='partial_unloaded')
        self.valid('scan_tiles', tiles)
        del tiles['data']['unknownCount']
        self.invalid('scan_tiles', tiles)

        scan = deepcopy(self.observations['scan_entities'])
        item = scan['data']['items'][0]
        scan['data']['items'] += [
            {**item, 'kind': 'player', 'typeId': None, 'classification': 'other_player',
             'details': {'dead': False, 'statLife': 100, 'team': 0}},
            {**item, 'kind': 'item', 'typeId': 2, 'classification': 'dropped_item',
             'details': {'type': 2, 'stack': 1, 'contentKey': 'Terraria/DirtBlock'}},
        ]
        self.valid('scan_entities', scan)
        scan['data']['items'][-1]['typeId'] = None
        self.invalid('scan_entities', scan)

    def test_scene_capability_layers_and_regional_encoding(self):
        local = deepcopy(self.observations['observe_scene'])
        regional = deepcopy(self.observations['observe_scene_region'])
        self.valid('observe_scene', local, FULL - {'server_ai_scene_region_v1'})
        self.valid('observe_scene', regional)
        self.invalid('observe_scene', regional, FULL - {'server_ai_scene_region_v1'})
        layer_fields = {'map', 'geometry', 'hazards', 'objects', 'objectCounts', 'landingRegions',
                        'placementCandidates', 'placementMaterial', 'objectPlacementCandidates', 'objectPlacementMaterial'}
        local['data'] = {k: v for k, v in local['data'].items() if k not in layer_fields - {'map'}}
        local['data']['includedLayers'] = ['map']
        self.valid('observe_scene', local)
        local['data']['includedLayers'].append('hazards')
        self.invalid('observe_scene', local)
        row = regional['data']['coverage']['knowledge']['rows'][0]
        row['encoding'] = 'hex_indices'
        self.invalid('observe_scene', regional)

    def test_action_states_and_native_casing(self):
        self.valid('mine_tile', self.actions['mine_tile_invalid'])
        self.valid('mine_target', self.actions['mine_target_unknown'])
        self.valid('stop_action', self.actions['mine_tile'])
        unknown = dict(valid=False, status='unknown', completed=False, dispatchState='unknown',
                       reason='connection lost', operation='mine_tile')
        self.valid('mine_tile', unknown)
        self.invalid('mine_tile', {**unknown, 'completed': True})
        preflight = {
            **self.actions['mine_tile'], 'status': 'failed', 'completed': False,
            'inputsAcquired': False, 'effectVerified': False,
            'evidence': {'operation': 'mine_tile', 'mutationAttempted': False, 'effectStatus': 'not_started'},
        }
        self.valid('mine_tile', preflight)
        self.invalid('mine_tile', {**preflight, 'evidence': {**preflight['evidence'], 'operation': 'use_item'}})
        mined = deepcopy(self.actions['mine_tile'])
        mined['evidence']['x'] = mined['evidence'].pop('X')
        self.invalid('mine_tile', mined)
        selected = deepcopy(self.actions['select_hotbar_slot'])
        selected['endEvidenceStatus'] = 'unknown'
        self.invalid('select_hotbar_slot', selected)
        placed = deepcopy(self.actions['place_object'])
        placed['evidence']['footprint']['width'] = 'two'
        self.invalid('place_object', placed)
        self.invalid('stop_action', {**self.actions['stop_action'], 'noOp': False})

    def test_navigation_records_and_omitted_blocking_tiles(self):
        landing = deepcopy(self.actions['navigate_to_landing_region'])
        snapshot = load('navigation_snapshot')
        landing['evidence']['navigation'] = snapshot
        self.valid('navigate_to_landing_region', landing)
        del snapshot['BlockingTiles']
        self.valid('navigate_to_landing_region', landing)
        snapshot['BlockingTiles'] = None
        self.invalid('navigate_to_landing_region', landing)
        del snapshot['BlockingTiles']
        snapshot['Mobility']['Movement']['Rockets']['Delay'] = 'unknown'
        self.invalid('navigate_to_landing_region', landing)

    def test_program_task_layers_and_timeout(self):
        self.valid('query_task', self.programs['cancel_task'])
        self.valid('cancel_task', self.programs['query_task'])
        self.valid('await_task', self.programs['query_task'])
        self.invalid('query_task', self.programs['await_task'])
        self.invalid('await_task', {**self.programs['await_task'], 'waitStatus': 'cancelled'})
        self.invalid('resume_task', {**self.programs['resume_task'], 'retryAllowed': True})
        finished = deepcopy(self.programs['query_task'])
        child = finished['result']['evidence']['task']['progress']['children'][0]
        del child['parentTaskId']
        self.invalid('query_task', finished)


if __name__ == '__main__':
    unittest.main()
