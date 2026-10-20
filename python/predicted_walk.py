"""Lease-bound local motion experiment; native C# prediction and per-frame execution.

python python/predicted_walk.py --direction right --seconds 10
"""
import argparse
from collections import deque
import json
import math
from pathlib import Path
import time

from control import Bridge, BridgeError


def run(bridge, direction, seconds, *, clock=time.monotonic, sleep=time.sleep, log=print):
    deadline = clock() + seconds
    owned, usable = False, True
    recent = deque(maxlen=20)
    previous = None
    last_renewal = None
    try:
        # Feature check before taking control; old packages fail without starting motion.
        reply = bridge.get_navigation()
        while clock() < deadline:
            if last_renewal is not None and clock() - last_renewal > 0.25:
                return {"status": "interrupted", "reason": "control loop stalled; no automatic resume", "samples": list(recent)}
            if reply.get("mode") == "manual":
                owned = False
                return {"status": "interrupted", "reason": "manual control active", "samples": list(recent)}
            started = clock()
            bridge.terrain_step(direction)
            last_renewal = clock()
            owned = True
            reply = bridge.get_navigation()
            recent.append(reply)
            if reply.get("mode") == "manual":
                owned = False
                return {"status": "interrupted", "reason": "manual control active", "samples": list(recent)}
            if clock() - started > 0.25:
                return {"status": "interrupted", "reason": "control/observation exceeded 250ms", "samples": list(recent)}
            nav = reply["navigation"]
            if nav.get("status") not in ("idle", "running", "landed", "failed"):
                raise BridgeError("Invalid navigation feedback")
            marker = (nav["status"], nav["action"], nav["holdFrames"], nav["reason"])
            if marker != previous:
                log(json.dumps(nav, ensure_ascii=False))
                previous = marker
            if nav["status"] in ("landed", "failed"):
                return {"status": nav["status"], "reason": nav["reason"], "samples": list(recent)}
            sleep(max(0, min(0.1 - (clock() - started), deadline - clock())))
        return {"status": "interrupted", "reason": "runtime limit reached", "samples": list(recent)}
    except (BridgeError, OSError, ValueError, KeyError, TypeError) as error:
        usable = False
        return {"status": "interrupted", "reason": f"Bridge failure: {error}", "samples": list(recent)}
    finally:
        try:
            if owned and usable:
                try:
                    bridge.command("stop")
                except (BridgeError, OSError, ValueError):
                    pass
        finally:
            bridge.close()


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--direction", choices=("left", "right"), default="right")
    parser.add_argument("--seconds", type=float, default=10)
    parser.add_argument("--port", type=int, default=18777)
    parser.add_argument("--output", type=Path, default=Path(".local/predicted-walk.json"))
    args = parser.parse_args(argv)
    if not math.isfinite(args.seconds) or not 0 < args.seconds <= 30 or not 1 <= args.port <= 65535:
        parser.error("seconds must be finite in (0,30]; port in [1,65535]")
    try:
        result = run(Bridge(args.port, timeout=0.25), -1 if args.direction == "left" else 1, args.seconds)
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
        print(f"Stopped: {result['status']}: {result['reason']}; report={args.output}")
        return 0 if result["status"] == "landed" or result["reason"] == "runtime limit reached" else 1
    except (BridgeError, OSError, ValueError) as error:
        print(f"Stopped: {error}")
        return 1
    except KeyboardInterrupt:
        print("Interrupted; control released.")
        return 130


if __name__ == "__main__":
    raise SystemExit(main())
