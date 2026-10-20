"""Continuous local traversal using the existing game-thread motion executor.

python python/continuous_walk.py --direction right --seconds 20 --max-actions 8
"""
import argparse
from collections import deque
import json
import math
from pathlib import Path
import time

from control import Bridge, BridgeError


class SafetyStop(Exception):
    pass


def vector(value):
    if not isinstance(value, dict) or any(type(value.get(k)) not in (int, float)
            or not math.isfinite(value[k]) for k in ("x", "y")):
        raise BridgeError("invalid position/velocity vector")
    return value


def observation(reply):
    nav, state = reply["navigation"], reply["state"]
    if not isinstance(nav, dict) or not isinstance(state, dict):
        raise BridgeError("missing navigation/player state")
    if nav.get("status") not in ("idle", "running", "landed", "failed"):
        raise BridgeError("invalid navigation status")
    if any(type(nav.get(k)) is not int or nav[k] < 0 for k in ("session", "generation")):
        raise BridgeError("invalid navigation identity")
    if state.get("inWorld") is not True or type(state.get("life")) is not int or state["life"] <= 0:
        raise SafetyStop("player unavailable or dead")
    vector(state["position"])
    vector(state["velocity"])
    return nav, state


def verify_landing(nav):
    if nav.get("action") != "jump":
        raise SafetyStop("landing feedback is not a jump")
    predicted, actual = vector(nav["predictedLanding"]), vector(nav["actualLanding"])
    observed, velocity = vector(nav["observedPosition"]), vector(nav["observedVelocity"])
    if (abs(actual["x"] - predicted["x"]) > 8 or abs(actual["y"] - predicted["y"]) > 4
            or max(abs(velocity[k]) for k in ("x", "y")) > 0.1
            or max(abs(actual[k] - observed[k]) for k in ("x", "y")) > 0.01):
        raise SafetyStop("jump landing verification failed")


def run(bridge, direction, seconds, max_actions=8, no_progress=3, *,
        clock=time.monotonic, sleep=time.sleep, log=print):
    deadline = clock() + seconds
    recent = deque(maxlen=20)
    owned, usable = False, True
    generation = session = None
    actions = jumps = 0
    anchor = life = None
    progressed = last_cycle = clock()
    starting = True
    previous = None
    terrain_reply = None

    def finish(status, reason):
        return {"status": status, "reason": reason, "actions": actions,
                "completedJumps": jumps, "samples": list(recent), "lastTerrain": terrain_reply}

    def guarded(command):
        reply = bridge.command(command, expectedGeneration=generation,
                               **({"direction": direction} if command == "terrain_step" else {}))
        if reply.get("status") != "applied" or reply.get("mode") != ("none" if command == "stop" else "external"):
            raise BridgeError("unexpected control acknowledgement")

    try:
        if "guarded_control" not in bridge.hello.get("capabilities", []):
            raise SafetyStop("requires CompanionBridge 0.1.10+ guarded_control; no motion started")
        while clock() < deadline:
            if clock() - last_cycle > 0.25:
                raise SafetyStop("control loop stalled; no automatic resume")
            started = clock()
            reply = bridge.get_navigation()
            nav, state = observation(reply)
            recent.append(reply)
            if generation is None:
                generation, session = nav["generation"], nav["session"]
            expected_mode = "none" if starting else "external"
            if (reply.get("mode") != expected_mode or nav["generation"] != generation
                    or nav["session"] != session):
                owned = False
                raise SafetyStop("control interrupted: mode/session/generation changed; no automatic resume")
            if life is not None and state["life"] < life:
                raise SafetyStop("life decreased")
            life = state["life"]
            x = direction * state["position"]["x"]
            if anchor is None or x >= anchor + 8:
                anchor, progressed = x, clock()
            if clock() - progressed >= no_progress:
                raise SafetyStop("no forward progress within time limit")
            if clock() - started > 0.25:
                raise SafetyStop("observation exceeded 250ms")

            if starting:
                if nav["status"] != "idle":
                    raise SafetyStop("previous action has not been released")
                if actions >= max_actions:
                    return finish("completed", "action limit reached")
                # Explicitly reobserve at every action boundary. Native C# prediction
                # still reads current tiles/physics every frame; no Python tile thresholds.
                terrain_reply = bridge.get_terrain()
                terrain = terrain_reply["terrain"]
                fresh = terrain_reply["state"]
                if not isinstance(terrain, dict) or not isinstance(fresh, dict):
                    raise BridgeError("missing terrain/player state")
                if (terrain_reply.get("mode") != "none" or fresh.get("inWorld") is not True
                        or terrain.get("supported") is not True or terrain.get("grounded") is not True
                        or max(abs(vector(fresh["velocity"])[k]) for k in ("x", "y")) > 0.1):
                    raise SafetyStop("reobservation requires supported, grounded, settled player")
                if fresh["life"] < life:
                    raise SafetyStop("life decreased during reobservation")
                if clock() >= deadline:
                    break
                if clock() - started > 0.25:
                    raise SafetyStop("reobservation exceeded 250ms")
                guarded("terrain_step")
                generation += 1  # None -> External, atomically guarded by previous generation.
                owned, starting = True, False
                actions += 1
                log(json.dumps({"event": "action_started", "actionNumber": actions,
                                "generation": generation, "position": fresh["position"]}))
            else:
                marker = (actions, nav["status"], nav.get("action"), nav.get("reason"))
                if marker != previous:
                    log(json.dumps({"event": "feedback", "actionNumber": actions, **nav}, ensure_ascii=False))
                    previous = marker
                if nav["status"] == "failed":
                    return finish("failed", f"motion failed: {nav['reason']}")
                if nav["status"] == "landed":
                    verify_landing(nav)
                    guarded("stop")
                    owned = False
                    generation += 1
                    jumps += 1
                    starting = True
                elif nav["status"] in ("idle", "running"):
                    guarded("terrain_step")  # Renewal only; same C# action/generation.
            if clock() - started > 0.25:
                raise SafetyStop("control/observation exceeded 250ms")
            last_cycle = clock()
            sleep(max(0, min(0.1 - (clock() - started), deadline - clock())))
        return finish("completed", "runtime limit reached")
    except SafetyStop as error:
        return finish("stopped", str(error))
    except (BridgeError, OSError, ValueError, KeyError, TypeError) as error:
        usable = False
        return finish("interrupted", f"Bridge failure: {error}")
    finally:
        try:
            if owned and usable:
                try:
                    guarded("stop")
                except (BridgeError, OSError, ValueError):
                    pass
        finally:
            bridge.close()


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--direction", choices=("left", "right"), default="right")
    parser.add_argument("--seconds", type=float, default=20)
    parser.add_argument("--max-actions", type=int, default=8)
    parser.add_argument("--no-progress", type=float, default=3)
    parser.add_argument("--port", type=int, default=18777)
    parser.add_argument("--output", type=Path, default=Path(".local/continuous-walk.json"))
    args = parser.parse_args(argv)
    if (not math.isfinite(args.seconds) or not 0 < args.seconds <= 120
            or not 1 <= args.max_actions <= 100 or not math.isfinite(args.no_progress)
            or not 1 <= args.no_progress <= 10 or not 1 <= args.port <= 65535):
        parser.error("seconds in (0,120]; max-actions in [1,100]; no-progress in [1,10]; port in [1,65535]")
    try:
        result = run(Bridge(args.port, timeout=0.25), -1 if args.direction == "left" else 1,
                     args.seconds, args.max_actions, args.no_progress)
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
        print(f"Stopped: {result['status']}: {result['reason']}; jumps={result['completedJumps']}; report={args.output}")
        return 0 if result["status"] == "completed" else 1
    except (BridgeError, OSError, ValueError) as error:
        print(f"Stopped: {error}")
        return 1
    except KeyboardInterrupt:
        print("Interrupted; control released.")
        return 130


if __name__ == "__main__":
    raise SystemExit(main())
