"""Isolated Server AI rope placement and lifecycle through the normal IPC."""
import argparse
import asyncio
import json
import sys
from pathlib import Path

from native_direct_input import wait_file

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "python"))
from lua_runtime import Limits, LuaRuntime
from server_ai import ServerAiBridge


async def verify(args):
    profile = args.profile
    await wait_file(profile / "action-fixture.json", 90)
    rows = []
    bridge = ServerAiBridge(args.port)
    assert "place_rope" in bridge.supported_atomic_operations

    async def setup(mode):
        ready = profile / "rope-ready.json"
        ready.unlink(missing_ok=True)
        temp = profile / "rope-request.tmp"
        temp.write_text(mode)
        temp.replace(profile / "rope-request")
        return await wait_file(ready)

    async def action_for(label, source, status, reason=None, *, require_release=True):
        result = await LuaRuntime(bridge).execute(
            source,
            limits=Limits(seconds=18, calls=1, actions=1))
        action = result.get("value") or result.get("lastAction") or {}
        rows.append(dict(label=label, action=action))
        (profile / "rope-results.json").write_text(json.dumps(rows, indent=2))
        assert action.get("status") == status, (label, action, profile)
        if reason:
            assert action.get("reason") == reason, (label, action, profile)
        if require_release and status != "unknown":
            assert action["endEvidence"]["inputsReleased"], action
        print(f"Native rope {label}: {action['status']} {action['reason']}", flush=True)
        return action

    async def place(label, x, y, status, reason=None, operation="place_rope"):
        return await action_for(label, "return api('%s',{x=%d,y=%d})" % (operation, x, y), status, reason)

    async def observed_tile(x, y):
        reply = await asyncio.to_thread(bridge.command, "query_atomic", operation="inspect_tile", arguments=dict(x=x, y=y))
        cell = reply["data"]["data"]["cell"]
        assert cell["knowledge"] == "read", (x, y, cell)
        return cell

    async def observed_type(x, y):
        return (await observed_tile(x, y))["tileType"]

    async def rope_stack():
        reply = await asyncio.to_thread(bridge.command, "query_atomic", operation="inspect_inventory",
                                        arguments=dict(mode="slots", slots=[4]))
        item, = reply["data"]["data"]["inventory"]
        assert item["typeId"] == 965, item
        return item["stack"]

    try:
        scene = await setup("plain")
        x, y = scene["x"], scene["y"]
        for index in range(3):
            action = await place(f"segment-{index}", x, y, "completed", "native_rope_placed_and_consumed")
            evidence = action["evidence"]
            assert evidence["y"] == y + index and evidence["extended"] is (index > 0), action
            assert evidence["remainingStack"] == 4 - index
            assert await observed_type(x, y + index) == 213
        await place("blocked_extension", x, y, "failed", "rope_extension_blocked")
        await place("occupied", x, y - 1, "failed", "rope_target_occupied")
        await place("requested_out_of_range", x + 35, y, "failed", "rope_out_of_range")
        await setup("end_range")
        await place("endpoint_out_of_range", x, y, "failed", "rope_out_of_range")
        await setup("empty")
        await place("empty_hotbar", x, y, "failed", "rope_material_unavailable")
        await setup("refuse")
        refused = await place("native_hook_refusal", x, y, "failed", "native_rope_placement_refused")
        assert refused["evidence"]["consumedCount"] == 0 and not refused["evidence"]["targetPresent"]
        for mode, kind in (("vine", 353), ("silk", 365), ("web", 366)):
            await setup(mode)
            action = await place(mode, x, y, "completed", "native_rope_placed_and_consumed")
            assert action["evidence"]["remainingStack"] == 0 and await observed_type(x, y) == kind, action

        upward = await setup("upward")
        top, bottom_gap, min_y = upward["y"], upward["floor"], upward["minReachY"]
        targets = list(range(top - 1, min_y - 1, -1))
        initial = await rope_stack()
        assert 2 <= len(targets) < initial and await observed_type(x, top) == 213, upward
        assert not (await observed_tile(x, bottom_gap))["hasTile"], upward
        for index, target_y in enumerate(targets, start=1):
            assert not (await observed_tile(x, target_y))["hasTile"], target_y
            action = await place(f"upward-{index}", x, target_y, "completed", "native_rope_placed_and_consumed")
            evidence = action["evidence"]
            assert (evidence["x"], evidence["y"]) == (x, target_y) and evidence["extended"] is False, evidence
            assert evidence["consumedCount"] == 1 and evidence["remainingStack"] == initial - index, evidence
            assert await observed_type(x, target_y) == 213 and await rope_stack() == initial - index
            assert not (await observed_tile(x, bottom_gap))["hasTile"], evidence
        rejected = await place("upward_out_of_range", x, min_y - 1, "failed", "rope_out_of_range")
        assert rejected["completed"] is False and rejected["endEvidence"]["inputsAcquired"] is False, rejected
        assert not (await observed_tile(x, min_y - 1))["hasTile"], rejected
        assert not (await observed_tile(x, bottom_gap))["hasTile"] and await rope_stack() == initial - len(targets)

        shaft = await setup("build_and_climb")
        x, floor = shaft["x"], shaft["floor"]
        assert await rope_stack() == 10
        await action_for("select_rope", "return api('select_hotbar_slot',{slot=5})", "completed",
                         require_release=False)
        refused = await action_for("cursorless_rope_use", "return api('use_item',{})",
                                   "failed", "unsupported_selected_item")
        assert refused["endEvidence"]["inputsAcquired"] is False
        assert "place_tile" in refused["reasonDetail"] and "place_rope" in refused["reasonDetail"]
        assert await rope_stack() == 10 and not (await observed_tile(x, floor - 1))["hasTile"]
        for index, row in enumerate(range(floor - 1, floor - 7, -1), start=1):
            assert not (await observed_tile(x, row))["hasTile"], row
            action = await place(f"place_tile-rope-{index}", x, row, "completed",
                                 "native_rope_placed_and_consumed", operation="place_tile")
            evidence = action["evidence"]
            assert (evidence["x"], evidence["y"], evidence["extended"]) == (x, row, False), evidence
            assert evidence["nativePlacementEvents"] == evidence["consumedCount"] == 1, evidence
            assert await observed_type(x, row) == 213 and await rope_stack() == 10 - index
        occupied = await place("place_tile-existing-rope", x, floor - 6, "failed", "target_occupied",
                               operation="place_tile")
        assert occupied["endEvidence"]["inputsAcquired"] is False and await rope_stack() == 4
        assert not (await observed_tile(x, floor - 7))["hasTile"]
        target_feet = floor * 16 - 38
        climb = await action_for("climb_new_rope",
                                 "return api('move_on_rope',{ropeX=%d,targetFeetY=%d,finish='hold',maxUpdates=180})"
                                 % (x, target_feet), "completed", "native_rope_hold_verified")
        proof = climb["evidence"]
        assert proof["attached"] and proof["attachmentObserved"] and proof["settled"], proof
        assert proof["startFeetY"] == floor * 16 and abs(proof["feetY"] - target_feet) <= 4, proof
        assert await rope_stack() == 4

        await setup("cancel")
        await asyncio.to_thread(bridge.command, "start_atomic", operation="place_rope",
                                arguments=dict(x=x, y=y), expectedGeneration=bridge.binding["generation"])
        cancelled = (await asyncio.to_thread(bridge.command, "cancel_move", actionId=bridge._action_id))["movement"]["action"]
        assert cancelled["status"] == "cancelled" and cancelled["endEvidence"]["inputsReleased"], cancelled
        rows.append(dict(label="cancel", action=cancelled))

        await setup("timeout")
        timed_out = await place("timeout", x, y, "timeout", "native_placement_deadline")
        assert timed_out["evidence"]["consumedCount"] == 0 and not timed_out["evidence"]["targetPresent"]
        before = await wait_file(profile / "rope-lifecycle-timeout.json")
        assert before["empty"] and before["stack"] == 5 and before["inputReleased"], before

        scope = [json.loads(line) for line in (profile / "rope-scope.jsonl").read_text().splitlines()]
        assert scope and all(r["unchanged"] and r["unscoped"] for r in scope), scope
        assert {(r["x"], r["y"]) for r in scope if r["mode"] == "plain"} == {(x, y + i) for i in range(3)}, scope
        assert [(r["x"], r["y"]) for r in scope if r["mode"] == "upward"] == [(x, row) for row in targets], scope

        await setup("generation")
        await place("generation", x, y, "unknown")
        before = await wait_file(profile / "rope-lifecycle-generation.json")
        assert before["empty"] and before["stack"] == 5 and before["inputReleased"], before
    finally:
        bridge.close()


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--profile", type=Path, required=True)
    parser.add_argument("--port", type=int, required=True)
    asyncio.run(verify(parser.parse_args()))
