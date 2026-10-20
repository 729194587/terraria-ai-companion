"""Lease and observe a game-thread navigation task; no Python movement decisions."""
import argparse
from collections import deque
import json
import math
from pathlib import Path
import time

from control import Bridge, BridgeError


def run(bridge, direction, tiles, seconds, *, clock=time.monotonic, sleep=time.sleep, log=print):
    samples = deque(maxlen=30)
    events = []
    owned = False
    generation = target = None
    result = {"status": "stopped", "reason": "runtime limit reached"}
    try:
        if not {"local_navigation", "guarded_control"}.issubset(bridge.hello.get("capabilities", [])):
            raise BridgeError("requires CompanionBridge 0.1.11+ local_navigation")
        first = bridge.get_navigation()
        nav, state = first["navigation"], first["state"]
        if first["mode"] != "none" or nav["status"] != "idle" or not state["inWorld"]:
            raise BridgeError("navigation requires idle local player")
        generation, session = nav["generation"], nav["session"]
        target = state["position"]["x"] + direction * tiles * 16
        if not math.isfinite(target) or not 0 <= target <= 1000000:
            raise BridgeError("target outside protocol bounds")
        life = state["life"]
        deadline, previous = clock() + seconds, None
        anchor, progressed, last_cycle = direction * state["position"]["x"], clock(), clock()
        ack = bridge.command("navigate", targetX=target, expectedGeneration=generation)
        if ack.get("status") != "applied" or ack.get("mode") != "external":
            raise BridgeError("unexpected navigation acknowledgement")
        generation += 1
        owned = True
        while clock() < deadline:
            if clock() - last_cycle > 0.3:
                raise BridgeError("supervisor stalled; no automatic resume")
            reply = bridge.get_navigation()
            samples.append(reply)
            nav, state = reply["navigation"], reply["state"]
            if (reply["mode"] != "external" or nav["session"] != session
                    or nav["generation"] != generation):
                owned = False
                raise BridgeError("control interrupted; no automatic resume")
            if not state["inWorld"] or state["life"] <= 0 or state["life"] < life:
                raise BridgeError("player unavailable/life decreased")
            life = state["life"]
            status = nav["status"]
            if status not in ("idle", "running", "arrived", "failed"):
                raise BridgeError("invalid navigation status")
            # System.Text.Json emits the shortest decimal for a C# Single; Python
            # computes the requested goal as a double. Allow only float roundoff.
            if status != "idle" and not math.isclose(nav["targetX"], target, rel_tol=1e-7, abs_tol=0.01):
                raise BridgeError("navigation target changed")
            marker = (status, nav.get("completedActions", 0))
            if marker != previous:
                event = {"event": "navigation", **nav}
                events.append(event)
                log(json.dumps(event, ensure_ascii=False))
                previous = marker
            if status in ("arrived", "failed"):
                result = {"status": status, "reason": nav["reason"]}
                break
            x = direction * state["position"]["x"]
            if x >= anchor + 4:
                anchor, progressed = x, clock()
            if clock() - progressed >= 5:
                raise BridgeError("no forward progress for 5 seconds")
            if clock() - last_cycle > 0.3:
                raise BridgeError("observation stalled; no automatic resume")
            ack = bridge.command("navigate", targetX=target, expectedGeneration=generation)
            if ack.get("status") != "applied" or ack.get("mode") != "external":
                raise BridgeError("unexpected renewal acknowledgement")
            last_cycle = clock()
            sleep(min(0.1, max(0, deadline - clock())))
    except (BridgeError, OSError, ValueError, KeyError, TypeError) as error:
        result = {"status": "interrupted", "reason": str(error)}
    finally:
        try:
            if owned:
                try:
                    bridge.command("stop", expectedGeneration=generation)
                except (BridgeError, OSError, ValueError):
                    pass
        finally:
            bridge.close()
    return {**result, "targetX": target, "events": events, "samples": list(samples)}


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--direction", choices=("left", "right"), default="right")
    parser.add_argument("--tiles", type=float, default=40)
    parser.add_argument("--seconds", type=float, default=40)
    parser.add_argument("--port", type=int, default=18777)
    parser.add_argument("--output", type=Path, default=Path(".local/navigation.json"))
    args = parser.parse_args(argv)
    if (not math.isfinite(args.tiles) or not 1 <= args.tiles <= 200
            or not math.isfinite(args.seconds) or not 0 < args.seconds <= 120
            or not 1 <= args.port <= 65535):
        parser.error("tiles in [1,200], seconds in (0,120], port in [1,65535]")
    try:
        report = run(Bridge(args.port, timeout=0.25), -1 if args.direction == "left" else 1,
                     args.tiles, args.seconds)
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
        print(f"{report['status']}: {report['reason']}; report={args.output}")
        return 0 if report["status"] == "arrived" else 1
    except (BridgeError, OSError, ValueError) as error:
        print(f"Navigation failed: {error}")
        return 1
    except KeyboardInterrupt:
        print("Interrupted; control released.")
        return 130


if __name__ == "__main__":
    raise SystemExit(main())
