"""Bounded horizontal actions over the existing Bridge; no model or pathfinding loop."""
import asyncio
import math

from control import BridgeError

MAX_DURATION_MS = 2000
POLL_SECONDS = 0.1


def position_effects(before, after, source):
    """Observed position difference only; neither causality nor arrival is implied."""
    def known(value):
        return isinstance(value, dict) and all(type(value.get(k)) in (int, float)
            and math.isfinite(value[k]) for k in ('x', 'y'))
    before = {k: before[k] for k in ('x', 'y')} if known(before) else None
    after = {k: after[k] for k in ('x', 'y')} if known(after) else None
    return {'knowledge': 'known' if before is not None and after is not None else 'unknown',
            'beforePositionWorldPixels': before, 'afterPositionWorldPixels': after,
            'displacementWorldPixels': {k: after[k]-before[k] for k in ('x', 'y')}
                if before is not None and after is not None else None,
            'source': source, 'attributedToAction': None}


def describe_movement_result(result):
    """Add Python-only semantics without changing legacy bounded-move fields."""
    result = dict(result)
    status = result.get('status', 'unknown')
    confirmed = result.get('serverConfirmed') is True
    end = movement_end_evidence(result)
    result['execution'] = {'status': status, 'confirmed': confirmed,
                          'inputReleased': True if end is not None else None}
    result['effects'] = position_effects(result.get('beforePositionWorldPixels'),
                                         result.get('afterPositionWorldPixels'), 'bridge_movement_feedback')
    # The primitive receives no goal. Only MovementLoop can verify an XY goal.
    result['goal'] = {'kind': None, 'status': 'not_applicable', 'evidence': None}
    result['observation'] = {'required': True, 'purpose': ['post_action_verification', 'pre_next_action'],
                             'endGameUpdate': end['gameUpdate'] if end else None,
                             'endEvidence': end,
                             'actionId': result.get('actionId'),
                             'reason': 'movement_feedback_is_not_fresh_terrain_observation'}
    return result


def movement_end_evidence(result):
    """Only an explicit game-thread release receipt is a usable end bound."""
    end = result.get('endEvidence')
    if (result.get('serverConfirmed') is not True or result.get('endEvidenceStatus') != 'known'
            or result.get('status') not in ('completed', 'cancelled')
            or not isinstance(end, dict) or type(end.get('gameUpdate')) is not int or end['gameUpdate'] < 0
            or not isinstance(end.get('worldSessionId'), str) or not end['worldSessionId']
            or end.get('phase') not in ('PostUpdateRunSpeeds_before_bridge_input', 'UpdateDead_after_revoke')):
        return None
    return {k: end[k] for k in ('worldSessionId', 'gameUpdate', 'phase')}


def _position(state):
    if not isinstance(state, dict) or state.get("inWorld") is not True:
        return None
    position = state.get("position")
    if not isinstance(position, dict) or any(type(position.get(axis)) not in (int, float)
                                            or not math.isfinite(position[axis]) for axis in ("x", "y")):
        return None
    return dict(position)


def _feedback(reply):
    feedback = reply.get("movement")
    if (not isinstance(feedback, dict) or type(feedback.get("generation")) is not int
            or feedback["generation"] < 0):
        raise BridgeError("Malformed movement feedback")
    action = feedback.get("action")
    if action is not None:
        if (not isinstance(action, dict) or type(action.get("actionId")) is not int or action["actionId"] <= 0
                or action.get("status") not in ("running", "completed", "cancelled")
                or type(action.get("completed")) is not bool or type(action.get("cancelled")) is not bool
                or action["completed"] != (action["status"] == "completed")
                or action["cancelled"] != (action["status"] == "cancelled")):
            raise BridgeError("Malformed movement action")
    return feedback


def _result(action, control_generation=None):
    return {"valid": True, "status": action["status"], "completed": action["completed"],
            "cancelled": action["cancelled"], "reason": action["reason"], "actionId": action["actionId"],
            "direction": "left" if action["direction"] == -1 else "right", "durationMs": action["durationMs"],
            "elapsedMilliseconds": action["elapsedMilliseconds"],
            "beforePositionWorldPixels": _position(action.get("before")),
            "afterPositionWorldPixels": _position(action.get("after")),
            "serverConfirmed": True,
            "controlGeneration": control_generation,
            "endEvidence": action.get('endEvidence'),
            "endEvidenceStatus": action.get('endEvidenceStatus', 'unknown'),
            "scope": "terminal action; only endEvidence confirms the input release boundary; completion does not prove arrival or zero velocity"}


def _unknown(reason, action=None, before=None):
    return {"valid": False, "status": "unknown", "completed": None, "cancelled": None,
            "reason": reason, "actionId": action.get("actionId") if action else None,
            "beforePositionWorldPixels": _position(action.get("before")) if action else before,
            "afterPositionWorldPixels": None, "serverConfirmed": False,
            "safety": "No further renewals. Game-thread absolute deadline and 750ms lease remain enforced; no automatic resume."}


async def _command(bridge, cmd, **parameters):
    return await asyncio.to_thread(bridge.command, cmd, **parameters)


async def _cancel_owned(bridge, action_id):
    reply = await _command(bridge, "cancel_move", actionId=action_id)
    feedback = _feedback(reply)
    action = feedback["action"]
    if action is None or action["actionId"] != action_id or action["status"] == "running":
        raise BridgeError("Movement cancellation not confirmed for owned action")
    return _result(action, feedback["generation"])


async def _await_end_evidence(bridge, action, generation, deadline):
    # Terminal ACK can precede the player hook in the same DoUpdate. Do not renew
    # a terminal action or extend the existing deadline while awaiting its receipt.
    while (action['status'] == 'completed' and action.get('endEvidenceStatus') == 'pending'
           and asyncio.get_running_loop().time() < deadline):
        await asyncio.sleep(POLL_SECONDS)
        feedback = _feedback(await _command(bridge, 'get_movement'))
        current = feedback['action']
        if current is None or current['actionId'] != action['actionId'] or current['status'] == 'running':
            return _unknown('Action ownership changed while awaiting input release evidence', action)
        action, generation = current, feedback['generation']
    return _result(action, generation)


async def move(bridge, direction, duration_ms, chat_id=None, expected_generation=None):
    return describe_movement_result(await _move(bridge, direction, duration_ms, chat_id, expected_generation))


async def _move(bridge, direction, duration_ms, chat_id=None, expected_generation=None):
    if direction not in ("left", "right") or type(duration_ms) is not int or not 50 <= duration_ms <= MAX_DURATION_MS:
        raise ValueError("move requires direction left/right and integer duration_ms in 50..2000")
    if "bounded_movement_v1" not in bridge.hello.get("capabilities", []):
        raise BridgeError("Movement requires bounded_movement_v1; no action started")
    action = None
    start_task = None
    before = None
    try:
        initial = await _command(bridge, "get_movement")
        generation = _feedback(initial)["generation"]
        if expected_generation is not None and generation != expected_generation:
            return {"valid":True,"status":"cancelled","completed":False,"cancelled":True,
                    "reason":"control_generation_changed_since_observation","controlGeneration":generation,
                    "beforePositionWorldPixels":_position(initial.get("state")),"afterPositionWorldPixels":None}
        before = _position(initial.get("state"))
        parameters = {"direction": -1 if direction == "left" else 1, "durationMs": duration_ms,
                      "expectedGeneration": generation}
        if chat_id is not None:
            parameters["chatId"] = chat_id
        # Cancellation waits for the short start ACK before stopping its exact ID. An uncancellable
        # to_thread start cannot arrive later and restart a cancelled task's movement.
        start_task = asyncio.create_task(_command(bridge, "move_for", **parameters))
        started = await asyncio.shield(start_task)
        action = _feedback(started)["action"]
        generation = _feedback(started)["generation"]
        if started.get("status") != "movement_started" or action is None:
            raise BridgeError("Missing movement start acknowledgement")
        action_id = action["actionId"]
        deadline = asyncio.get_running_loop().time() + duration_ms / 1000 + 1
        while action["status"] == "running":
            if asyncio.get_running_loop().time() >= deadline:
                return await _cancel_owned(bridge, action_id)
            await asyncio.sleep(POLL_SECONDS)
            reply = await _command(bridge, "get_movement")
            feedback = _feedback(reply)
            current = feedback["action"]
            generation = feedback["generation"]
            if current is None or current["actionId"] != action_id:
                return _unknown("Action ownership changed; no automatic retry", action)
            action = current
            if action["status"] == "running":
                await _command(bridge, "renew_move", actionId=action_id)
        return await _await_end_evidence(bridge, action, generation, deadline)
    except asyncio.CancelledError:
        try:
            if action is None and start_task is not None:
                started = await start_task
                action = _feedback(started)["action"]
            if action is not None:
                await _cancel_owned(bridge, action["actionId"])
        except (BridgeError, OSError, ValueError):
            pass  # Socket timeout closes the Bridge; the lease/deadline still stop injected input.
        raise
    except (BridgeError, OSError, ValueError) as error:
        if action is not None:
            try:
                reply = await _command(bridge, "get_movement")
                feedback = _feedback(reply)
                current = feedback["action"]
                if current is not None and current["actionId"] == action["actionId"]:
                    if current["status"] != "running":
                        return _result(current, feedback["generation"])  # Completion can race the last heartbeat.
                    return await _cancel_owned(bridge, action["actionId"])
            except (BridgeError, OSError, ValueError):
                pass
            return _unknown(str(error), action)
        if start_task is not None and getattr(error, "status", None) is None:
            return _unknown(str(error), before=before)  # Start ACK lost: cannot claim the server rejected it.
        return {"valid": False, "status": "rejected", "completed": False, "cancelled": False,
                "reason": str(error), "beforePositionWorldPixels": before, "afterPositionWorldPixels": None}


async def stop_movement(bridge):
    result = describe_movement_result(await _stop_movement(bridge))
    # Legacy status/completed still describe the queried movement, not this stop request.
    result['targetAction'] = {'actionId': result.get('actionId'),
                              'execution': dict(result['execution'])} if result.get('actionId') else None
    result['execution'] = {'status': 'unknown' if result['status'] == 'unknown' else 'completed',
                          'confirmed': result['status'] != 'unknown',
                          'inputReleased': result['execution']['inputReleased'],
                          'operation': 'stop_movement',
                          'noOp': None if result['status'] == 'unknown' else not result['cancelRequested']}
    return result


async def _stop_movement(bridge):
    if "bounded_movement_v1" not in bridge.hello.get("capabilities", []):
        raise BridgeError("Stopping movement requires bounded_movement_v1")
    action = None
    cancel_requested = False
    try:
        reply = await _command(bridge, "get_movement")
        action = _feedback(reply)["action"]
        if action is None:
            return {"valid": True, "status": "idle", "completed": False, "cancelled": False,
                    "cancelRequested": False,
                    "reason": "No bounded movement action", "beforePositionWorldPixels": None,
                    "afterPositionWorldPixels": _position(reply.get("state"))}
        if action["status"] != "running":
            return {**_result(action, _feedback(reply)["generation"]), 'cancelRequested': False}
        cancel_requested = True
        return {**await _cancel_owned(bridge, action["actionId"]), 'cancelRequested': True}
    except (BridgeError, OSError, ValueError) as error:
        return {**_unknown(str(error), action), 'cancelRequested': cancel_requested}
