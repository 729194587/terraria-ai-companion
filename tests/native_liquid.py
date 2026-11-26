"""Native collision and root navigation checks in an isolated copied world."""
import argparse
import asyncio
import json
import sys
from pathlib import Path

from native_direct_input import wait_file

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "python"))
from lua_runtime import Limits, LuaRuntime
from server_ai import ServerAiBridge


async def verify_puddle(args):
    await wait_file(args.profile / "action-fixture.json", 90)
    (args.profile / "liquid-request").write_text("puddle")
    proof = await wait_file(args.profile / "liquid-proof.json")
    assert all(proof["checks"].values()), proof
    print(f"Native liquid collision: {len(proof['checks'])} checks passed", flush=True)
    bridge = ServerAiBridge(args.port)
    try:
        observation = await asyncio.to_thread(bridge.command, "query_atomic", operation="observe_terrain", arguments={})
        terrain = observation["data"]["data"]["terrain"]
        sample_x, sample_y = proof["x"] // 16 - 4, proof["y"] // 16
        assert terrain["rows"][sample_y - terrain["originY"]][sample_x - terrain["originX"]] == ".", terrain
        assert any(c["x"] == sample_x and c["y"] == sample_y and c["type"] == "water" and c["amount"] > 0
                   for c in terrain["liquids"]), terrain
        source = "return api('navigate_to',{x=%s,y=%s})" % (proof["x"], proof["y"])
        result = await LuaRuntime(bridge).execute(source, limits=Limits(seconds=60, calls=1, actions=1))
        (args.profile / "liquid-puddle-receipt.json").write_text(json.dumps(result, indent=2))
        action = result.get("value") or result.get("lastAction") or {}
        assert action.get("completed") and action["endEvidence"]["inputsReleased"], action
        evidence = action["task"]["lastResult"]["evidence"]
        assert all(evidence[k] for k in ("arrived", "grounded", "safeSupport", "settled")), evidence
        print("Native navigate_to crossed the puddle and verified arrival", flush=True)

        (args.profile / "liquid-request").write_text("wet")
        for _ in range(100):
            if (args.profile / "liquid-wet-ready").exists():
                break
            await asyncio.sleep(.02)
        else:
            raise AssertionError("Wet start fixture did not run")
        await asyncio.sleep(.2)
        result = await LuaRuntime(bridge).execute(source, limits=Limits(seconds=30, calls=1, actions=1))
        (args.profile / "liquid-wet-receipt.json").write_text(json.dumps(result, indent=2))
        action = result.get("value") or result.get("lastAction") or {}
        assert action.get("completed") and action["endEvidence"]["effectVerified"], action
        assert action["endEvidence"]["inputsReleased"], action
        print("Native wet starting pose reached the dry destination", flush=True)
    finally:
        bridge.close()


async def verify_edits(args, mode):
    ready = args.profile / "liquid-edit-ready.json"
    ready.unlink(missing_ok=True)
    request = args.profile / "liquid-request"
    temporary = request.with_suffix(".tmp")
    temporary.write_text(mode)
    temporary.replace(request)
    scene = await wait_file(ready)
    bridge = ServerAiBridge(args.port)
    try:
        source = "return api('navigate_to',{x=%s,y=%s})" % (scene["x"], scene["y"])
        result = await LuaRuntime(bridge).execute(source, limits=Limits(seconds=150 if mode=="bridge_long" else 60, calls=1, actions=1))
    finally:
        bridge.close()
    (args.profile / f"liquid-edit-{mode}-receipt.json").write_text(json.dumps(result, indent=2))
    await asyncio.sleep(.15)
    lines = (args.profile / f"liquid-edit-{mode}.jsonl").read_text().splitlines()
    rows = [json.loads(line) for line in lines if line.endswith("}")]
    action = result.get("value") or result.get("lastAction") or {}
    progress = action["task"]["progress"]
    names = [c["taskName"] for c in progress["children"]]
    print(f"Native liquid {mode}: {action['status']} {action['reason']}, "
          f"dug={progress['dugTiles']} placed={progress['placedTiles']}, trace={args.profile}", flush=True)
    assert action["endEvidence"]["inputsReleased"], action
    assert not any(r["lavaWet"] or (mode != "flood" and r["wet"]) for r in rows), rows[-3:]
    if mode == "hazard":
        assert rows and len({r["statLife"] for r in rows}) == 1, rows[-3:]
    if mode in ("bridge", "bridge_long", "detour", "mine"):
        assert action["completed"], action
        if mode in ("bridge", "bridge_long"):
            if mode == "bridge_long":
                assert progress["placedTiles"] >= 18, progress
            assert progress["placedTiles"] >= 2 and rows[-1]["waterPlatforms"] >= 2, (progress, rows[-3:])
            assert progress["dugTiles"] == 0 and "navigate_to_landing_region" in names, names
        elif mode == "detour":
            assert not progress["placedTiles"] and not progress["dugTiles"], progress
            assert rows[-1]["minY"] <= scene["startY"] - 64, rows[-3:]
        else:
            assert progress["dugTiles"] == 3 and not progress["placedTiles"], progress
    elif mode == "hazard":
        assert action["status"] == "failed" and action["reason"] == "navigation_liquid_local_frontier_exhausted", action
        assert not progress["placedTiles"] and not progress["dugTiles"], progress
    else:
        assert action["status"] in ("failed", "unknown") and action["reason"] in (
            "navigation_liquid_local_frontier_exhausted", "native_navigation_landing_unconfirmed",
            "unsupported_navigation_profile", "navigation_edit_settle_timeout"), action
        assert progress["dugTiles"] == 1 and not progress["placedTiles"], progress
        assert any(r["gateOpened"] and r["inflow"] and r["wetCorridor"] for r in rows), rows[-3:]
        assert all(abs(r["x"] - scene["startX"]) < 1 for r in rows), rows[-3:]
        assert any(d["choice"] == "local_edits" for d in progress["planning"]["history"]), progress


async def verify_wading(args, mode):
    ready = args.profile / "liquid-edit-ready.json"
    ready.unlink(missing_ok=True)
    request = args.profile / "liquid-request"
    temporary = request.with_suffix(".tmp")
    temporary.write_text(mode)
    temporary.replace(request)
    scene = await wait_file(ready)
    bridge = ServerAiBridge(args.port)
    try:
        before = await asyncio.to_thread(bridge._request, "state")
        result = await LuaRuntime(bridge).execute(
            "return api('navigate_to',{x=%s,y=%s})" % (scene["x"], scene["y"]),
            limits=Limits(seconds=60, calls=1, actions=1))
        after = await asyncio.to_thread(bridge._request, "state")
    finally:
        bridge.close()
    (args.profile / f"liquid-edit-{mode}-receipt.json").write_text(json.dumps(
        dict(scene=scene, before=before, after=after, result=result), indent=2))
    await asyncio.sleep(.15)
    rows = [json.loads(line) for line in (args.profile / f"liquid-edit-{mode}.jsonl").read_text().splitlines()
            if line.endswith("}")]
    wet = [row for row in rows if row["wet"]]
    action = result.get("value") or result.get("lastAction") or {}
    print(f"Native {mode}: {action['status']} {action['reason']}, wet samples={len(wet)}, "
          f"descent={max(row['y'] for row in rows)-scene['startY']:.1f}px, trace={args.profile}", flush=True)
    assert action["completed"] and action["endEvidence"]["inputsReleased"] and action["endEvidence"]["effectVerified"], action
    final = action["task"]["result"]["evidence"]
    assert final["settled"] and final["grounded"] and final["safeSupport"] and abs(final["feetY"] - scene["y"]) <= .5, final
    assert abs(after["data"]["position"]["x"] + 10 - scene["x"]) <= 16, after
    assert wet and not rows[0]["wet"] and not rows[-1]["wet"], rows[-3:]
    assert len({row["statLife"] for row in rows}) == 1 and not any(
        row["lavaWet"] or row["honeyWet"] or row["shimmerWet"] for row in rows), rows[-3:]
    progress = action["task"]["progress"]
    assert not progress["dugTiles"] and not progress["placedTiles"], (progress["dugTiles"], progress["placedTiles"])
    assert all(child["status"] == "completed" for child in progress["children"]), [
        (child["status"], child["reason"]) for child in progress["children"]]
    if mode == "wade_pool":
        assert progress["planning"]["choice"] == "pool_crossing", progress["planning"]
        assert any(row["breath"] < row["breathMax"] for row in wet), "expected actual native oxygen consumption"
    if mode == "wade_deep":
        assert max(row["y"] for row in wet) >= scene["startY"] + 31 and any(row["vy"] < -1 for row in wet), wet
    if mode == "wade_long":
        assert wet[-1]["x"] - wet[0]["x"] >= 16 * 15, wet
        assert all(child["result"]["evidence"]["navigation"]["RecoveryCount"] == 0
                   for child in progress["children"])


async def verify(args):
    await wait_file(args.profile / "action-fixture.json", 90)
    for mode in args.scenes or ("puddle", "bridge", "detour", "mine", "hazard", "flood", "wade_one", "wade_deep", "wade_long", "wade_pool", "bridge_long"):
        if mode == "puddle":
            await verify_puddle(args)
        elif mode.startswith("wade_"):
            await verify_wading(args, mode)
        else:
            await verify_edits(args, mode)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--profile", type=Path, required=True)
    parser.add_argument("--port", type=int, required=True)
    parser.add_argument("--scenes", nargs="+", choices=("puddle", "bridge", "detour", "mine", "hazard", "flood", "wade_one", "wade_deep", "wade_long", "wade_pool", "bridge_long"))
    asyncio.run(verify(parser.parse_args()))
