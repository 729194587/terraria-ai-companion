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
from program_tasks import coordinator


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
        if mode=="breath_explore":
            await verify_breath_explore(args)
        elif mode.startswith("breath_"):
            await verify_breath(args, mode)
        elif mode == "puddle":
            await verify_puddle(args)
        elif mode.startswith("wade_"):
            await verify_wading(args, mode)
        else:
            await verify_edits(args, mode)


async def verify_breath_explore(args):
    ready=args.profile/"liquid-edit-ready.json"
    ready.unlink(missing_ok=True)
    request=args.profile/"liquid-request"
    temp=request.with_suffix(".tmp")
    temp.write_text("breath_explore")
    temp.replace(request)
    await wait_file(ready)
    bridge=ServerAiBridge(args.port)
    manager=coordinator(bridge)
    phases=[]
    try:
        # The liquid fixture floods only after the explorer's native movement starts.
        run=asyncio.create_task(LuaRuntime(bridge,evidence_root=args.profile/"lua").execute(
            "return explore()",limits=Limits(actions=1,seconds=120)))
        while not run.done():
            for task_id,record in manager.records.items():
                view=record["view"]
                action=view.get("action") or {}
                phase=[task_id,action.get("executionState"),action.get("reason")]
                if not phases or phases[-1]!=phase:
                    phases.append(phase)
            await asyncio.sleep(.02)
        lua=await run
        proof=lua.get("lastAction") or {}
        result=dict(phases=phases,receipt=proof,trip=lua.get("value"),status=lua["status"])
        (args.profile/"breath-explore.json").write_text(json.dumps(result),encoding="utf-8")
        diagnostic=(phases,proof.get("reason"),str(args.profile/"breath-explore.json"))
        assert len(manager.records)==1 and [proof["actionId"],"paused","task_interrupt_breath"] in phases,diagnostic
        assert proof["inputsReleased"] and proof["completed"] and result["trip"]["progress"]["surveyedGoals"]==1,diagnostic
        print("Native breath_explore passed",flush=True)
    finally:
        await manager.close()
        bridge.close()


async def verify_breath(args, mode):
    ready = args.profile / "liquid-edit-ready.json"
    ready.unlink(missing_ok=True)
    bridge = ServerAiBridge(args.port)
    try:
        assert "server_ai_breath_reflex_v1" in bridge.hello["capabilities"]
        with_work = mode in ("breath_work", "breath_cluster")
        request = args.profile / "liquid-request"
        temp = request.with_suffix(".tmp")
        temp.write_text(mode)
        temp.replace(request)
        scene = await wait_file(ready)
        if with_work:
            manager = coordinator(bridge)
            target = {"x":scene["x"]+3,"y":scene["floor"]-1}
            if mode=="breath_cluster":
                target["cluster"] = True
            mining = await manager.invoke("submit_task", {"operation":"mine_target", "arguments":target})
            assert mining["status"]=="running", mining
        trace = args.profile / f"liquid-edit-{mode}.jsonl"
        notices = []
        paused = False
        for _ in range(250):
            await asyncio.sleep(.12)
            state = await asyncio.to_thread(bridge.life_status)
            notices.extend(i["text"] for i in (state.get("messages") or {}).get("items", []))
            rows = [json.loads(line) for line in trace.read_text().splitlines() if line.endswith("}")] if trace.exists() else []
            if with_work:
                work = await asyncio.to_thread(bridge.task_request, "action", mining["taskId"])
                paused |= work["executionState"]=="paused" and work["reason"]=="task_interrupt_breath"
            if mode in ("breath_gills", "breath_full"):
                if len(rows)>=14:
                    assert not any("换气" in n for n in notices), notices
                    break
            elif mode=="breath_sealed" and any("没找到能到达的透气口" in n for n in notices) and rows:
                assert "已恢复呼吸。" not in notices and all(r["roofRemaining"]==3 for r in rows), notices
                break
            elif "已恢复呼吸。" in notices:
                if with_work:
                    done = await manager.invoke("await_task", {"taskId":mining["taskId"],"milliseconds":15000})
                    assert paused and done["completed"], (paused, done)
                    if mode=="breath_cluster":
                        cells = done["result"]["task"]["result"]["evidence"]["targets"]
                        assert len(cells)==3 and all(c["status"]=="verified" for c in cells), cells
                    else:
                        assert done["result"]["reason"]=="mine_target_verified", done
                assert all(r["statLife"]>0 for r in rows), rows[-3:]
                assert any(not r["headWet"] and r["breath"]>=r["breathMax"]-8 for r in rows), rows[-3:]
                if mode in ("breath_build", "breath_existing", "breath_nearby", "breath_timing"):
                    assert any(r["pulley"] for r in rows), rows[-3:]
                if mode in ("breath_build", "breath_timing"):
                    assert rows[-1]["ropeCount"]<26, rows[-1]
                if mode=="breath_nearby":
                    assert rows[-1]["x"]>rows[0]["x"]+32, rows[-1]
                if mode=="breath_timing":
                    assert rows[0]["breath"]>=90 and rows[0]["ropeCount"]==30, rows[0]
                    first_placement = next(r for r in rows if r["ropeCount"]<30)
                    assert 0 < first_placement["breath"] < rows[0]["breath"], first_placement
                if mode=="breath_roof":
                    assert rows[-1]["roofRemaining"]<3, rows[-1]
                if mode=="breath_side":
                    assert rows[-1]["x"]>rows[0]["x"]+32 and rows[-1]["roofRemaining"]==3, rows[-1]
                break
            if mode!="breath_sealed":
                assert not any("行动结束" in n for n in notices), (mode,notices,rows[-3:])
        else:
            raise AssertionError((mode,notices,rows[-3:]))
        print(f"Native {mode} passed",flush=True)
    finally:
        bridge.close()


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--profile", type=Path, required=True)
    parser.add_argument("--port", type=int, required=True)
    parser.add_argument("--scenes", nargs="+", choices=("puddle", "bridge", "detour", "hazard", "mine", "flood", "wade_one", "wade_deep", "wade_long", "wade_pool", "bridge_long", "breath_shallow", "breath_roof", "breath_side", "breath_existing", "breath_build", "breath_gills", "breath_full", "breath_nearby", "breath_timing", "breath_sealed", "breath_work", "breath_cluster", "breath_explore"))
    asyncio.run(verify(parser.parse_args()))
