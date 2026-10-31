"""Host-only, evidence-bound handoff from a failed navigation to one direct-input attempt."""
import math

OPERATIONS = {'navigate_to_x', 'navigate_to_landing_region'}
KINDS = {'no_route', 'no_progress', 'profile_refused'}


def finite(value):
    return type(value) in (int, float) and math.isfinite(value)


def confirmed_failure(result, lifecycle):
    if (not isinstance(result, dict) or result.get('valid') is not True
            or result.get('status') != 'failed' or result.get('operation') not in OPERATIONS
            or result.get('reason') not in ('native_navigation_failed', 'navigation_stalled')
            or result.get('supervisorError') or lifecycle.get('uncertain')
            or result.get('endEvidenceStatus') != 'known'):
        return False
    proof, end, action = result.get('evidence'), result.get('endEvidence'), lifecycle.get('action')
    if not all(isinstance(v, dict) for v in (proof, end, action)):
        return False
    if (proof.get('recoverableFailure') is not True or proof.get('failureKind') not in KINDS
            or any(proof.get(k) is not True for k in ('inputReleased', 'alive', 'grounded', 'safeSupport', 'settled'))
            or (proof.get('failureKind') == 'no_progress') != (result.get('reason') == 'navigation_stalled')
            or proof.get('effectStatus') != 'native_navigation_observed'
            or not all(finite(proof.get(k)) for k in ('x', 'y', 'vx', 'vy'))
            or abs(proof['vx']) > .1 or abs(proof['vy']) > .1
            or type(proof.get('observedGameUpdate')) is not int
            or type(end.get('gameUpdate')) is not int or not 0 <= proof['observedGameUpdate'] <= end['gameUpdate']):
        return False
    return (end.get('inputsReleased') is True and type(end.get('inputsAcquired')) is bool
            and end.get('effectVerified') is False and end.get('phase') == 'ServerAI_game_thread_inputs_released'
            and end.get('operation') == result['operation'] == action.get('operation')
            and action.get('reason') == result.get('reason')
            and action.get('status') == 'failed' and action.get('evidence') == proof
            and isinstance(result.get('actionId'), str) and bool(result['actionId'])
            and result['actionId'] == action.get('actionId')
            and isinstance(action.get('session'), str) and bool(action['session'])
            and result.get('session') == action['session']
            and isinstance(action.get('worldSessionId'), str) and bool(action['worldSessionId'])
            and end.get('worldSessionId') == action['worldSessionId']
            and isinstance(action.get('actorId'), str) and bool(action['actorId'])
            and end.get('actorId') == action['actorId']
            and type(action.get('generation')) is int and type(end.get('actorGeneration')) is int
            and end['actorGeneration'] == action['generation'] == result.get('actionGeneration') == result.get('controlGeneration')
            and type(action.get('actorSlot')) is int and type(end.get('actorSlot')) is int
            and end['actorSlot'] == action['actorSlot'])


class Handoff:
    def __init__(self, result, action, chat_id=None):
        self.chat_id = chat_id
        self.action_id = result['actionId']
        self.tick = result['endEvidence']['gameUpdate']
        self.binding = dict(actor=action['actorId'], world=action['worldSessionId'],
                            generation=action['generation'], slot=action['actorSlot'])
        self.observed = False

    def observe_scene(self, result):
        data = result.get('data')
        if not isinstance(data, dict):return False
        actor = data.get('actor')
        if not isinstance(actor, dict):return False
        tick = result.get('gameUpdate')
        bounds, velocity = actor.get('boundsWorldPixels'), actor.get('velocity')
        self.observed = (result.get('valid') is True and result.get('available') is True
            and result.get('binding') == self.binding
            and type(result['binding'].get('generation')) is int and type(result['binding'].get('slot')) is int
            and result.get('worldSessionId') == self.binding['world']
            and type(result.get('actorGeneration')) is int and result['actorGeneration'] == self.binding['generation']
            and type(result.get('actorSlot')) is int and result['actorSlot'] == self.binding['slot']
            and type(tick) is int and tick >= self.tick and data.get('gameUpdate') == tick
            and isinstance(data.get('sceneId'), str) and bool(data['sceneId'])
            and type(actor.get('slot')) is int and actor['slot'] == self.binding['slot'] and actor.get('dead') is False
            and isinstance(bounds, dict) and all(finite(bounds.get(k)) for k in ('x', 'y', 'width', 'height'))
            and bounds['width'] > 0 and bounds['height'] > 0
            and isinstance(velocity, dict) and all(finite(velocity.get(k)) for k in ('x', 'y')))
        return self.observed
