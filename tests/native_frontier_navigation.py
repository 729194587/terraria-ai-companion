"""Exercise the bounded frontier task on the same copied-world terrain as navigation."""
import argparse
import asyncio
import json
import time
import uuid
from pathlib import Path

from native_direct_input import Limits, LuaRuntime, ServerAiBridge, wait_file


async def verify(args):
    profile = args.profile
    await wait_file(profile / "action-fixture.json", 90)
    for scene in args.scenes or ("runup", "frontier_gap", "frontier_shaft", "frontier_changed", "high_basic", "high_cloud", "frontier_nohook", "frontier_hook"):
        ready = profile / "terrain-ready.json"
        ready.unlink(missing_ok=True)
        request = profile / "terrain-request.tmp"
        terrain = dict(scene="frontier_gap" if scene == "runup" else scene, direction=1)
        if scene.startswith("high_"):
            terrain.update(scene="frontier_high", mobility=scene.removeprefix("high_"))
        request.write_text(json.dumps(terrain))
        request.replace(profile / "terrain-request.json")
        fixture = await wait_file(ready)
        bridge = ServerAiBridge(args.port)
        try:
            if scene == "runup":
                x = (fixture["originTileX"] + 15) * 16
                y = fixture["floor"] * 16
                result = await LuaRuntime(bridge).execute(
                    f"return api('navigate_to',{{x={x},y={y}}})", limits=Limits(seconds=60, calls=1, actions=1))
                action = result.get("value") or result.get("lastAction") or {}
                (profile / "runup-result.json").write_text(json.dumps(action))
                assert action.get("completed") and action["endEvidence"]["inputsReleased"], (scene, action.get("reason"))
                trace = await wait_file(profile / "terrain-motion-frontier_gap-1.json")
                assert any(f["vy"] < 0 and f["vx"] > 1 for f in trace), "no running launch"
                continue
            task = uuid.uuid4().hex
            action = bridge.task_request("start", task, operation="explore_frontier",
                                         arguments=dict(radiusTiles=18, maxSegments=3, allowDig=False),
                                         expiresAtUnixMs=int(time.time()*1000)+1500)
            deadline = time.monotonic() + 110
            while action["status"] in ("accepted", "running"):
                assert time.monotonic() < deadline, action
                await asyncio.sleep(.15)
                action = bridge.task_request("action", task)
            (profile / f"{scene}-result.json").write_text(json.dumps(action, indent=2))
            evidence = action["task"]["result"]["evidence"]
            print(scene, action["status"], action["reason"], evidence, flush=True)
            assert action["endEvidence"]["inputsReleased"], action
            if scene in ("frontier_gap", "high_cloud", "frontier_hook"):
                assert action["status"] == "completed" and evidence["newTiles"] == 1 and evidence["returned"]
                assert abs(evidence["actualLeft"] - fixture["startX"]) <= 2
                if scene == "frontier_hook":
                    trace = await wait_file(profile / "terrain-motion-frontier_hook-1.json")
                    assert any(f["vy"] < 0 and f["extraObservation"]["movement"]["GrappleCount"] > 0 for f in trace)
                    assert trace[-1]["extraObservation"]["movement"]["GrappleCount"] == 0
            elif scene == "high_basic":
                assert action["status"] in ("unknown", "failed") and evidence["segments"] == 0
            elif scene in ("frontier_shaft", "frontier_nohook"):
                assert action["status"] in ("unknown", "failed") and evidence["segments"] == 0
                assert evidence["outboundReached"] > 0 and evidence["returnsReached"] == 0
            else:
                removed = await wait_file(profile / "frontier-bank-removed.json")
                lip = (fixture["originTileX"] + 5) * 16
                assert removed["X"] < lip and evidence["actualLeft"] < lip
                assert action["status"] in ("failed", "unknown") and evidence["newTiles"] == 0
        finally:
            bridge.close()


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--profile", type=Path, required=True)
    parser.add_argument("--port", type=int, required=True)
    parser.add_argument("--scenes", nargs="+")
    asyncio.run(verify(parser.parse_args()))
