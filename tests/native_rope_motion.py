"""Verify native rope movement through the isolated Server AI IPC and Lua path."""
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
    bridge = ServerAiBridge(args.port)
    rows = []
    assert "move_on_rope" in bridge.supported_atomic_operations

    def record(label, action):
        rows.append(dict(label=label, action=action))
        (profile / "rope-motion-results.json").write_text(json.dumps(rows, indent=2))
        print(f"Native rope {label}: {action.get('status')} {action.get('reason')}", flush=True)

    async def setup(mode):
        nonlocal bridge
        ready = profile / "rope-motion-ready.json"
        ready.unlink(missing_ok=True)
        temp = profile / "rope-motion-request.tmp"
        temp.write_text(mode)
        temp.replace(profile / "rope-motion-request")
        scene = await wait_file(ready)
        # The fixture replaces terrain outside the agent session. A failed
        # move from the previous layout has no bearing on this layout.
        bridge.close()
        bridge = ServerAiBridge(args.port)
        return scene

    async def move(label, feet, finish="hold", status="completed", reason=None, updates=250, column=None):
        source = "return api('move_on_rope',{ropeX=%d,targetFeetY=%s,finish='%s',maxUpdates=%d})" % (
            x if column is None else column, feet, finish, updates)
        result = await LuaRuntime(bridge).execute(source, limits=Limits(seconds=20, calls=1, actions=1))
        action = result.get("value") or result.get("lastAction") or {}
        record(label, action)
        assert action.get("status") == status, (label, action, result, profile)
        if reason is not None:
            assert action["reason"] == reason, (label, action)
        if status != "unknown":
            assert action["endEvidence"]["inputsReleased"], action
        if status == "completed":
            evidence = action["evidence"]
            assert evidence["attachmentObserved"] and evidence["settled"] and evidence["inputReleased"], evidence
            assert abs(evidence["feetY"] - feet) <= (4 if finish == "hold" else .5), evidence
            if finish == "hold":
                assert evidence["attached"], evidence
            else:
                assert not evidence["attached"] and evidence["grounded"] and evidence["safeSupport"], evidence
        return action

    try:
        scene = await setup("shaft")
        x, floor = scene["x"], scene["floor"]
        await move("misaligned", floor - 112, status="failed", reason="rope_not_in_attach_range", column=x + 1)
        await move("ascent", floor - 112)
        await move("continued_ascent", floor - 176)
        await asyncio.sleep(.3)
        held = await move("hold_after_neutral_ticks", floor - 176)
        assert abs(held["evidence"]["startFeetY"] - (floor - 176)) <= 4, held
        await move("descent", floor - 80.1234567)
        await move("no_exit", floor, "land", "failed", "rope_landing_exit_unavailable")
        await setup("exit")
        await move("detach_and_land", floor, "land")

        await setup("blocked")
        await move("obstructed", floor - 128, status="failed", reason="rope_corridor_blocked")
        await setup("shaft")
        await move("timeout", floor - 150, status="timeout", reason="rope_update_budget", updates=2)

        await setup("shaft")
        await asyncio.to_thread(bridge.command, "start_atomic", operation="move_on_rope",
                                arguments=dict(ropeX=x, targetFeetY=floor - 200, finish="hold", maxUpdates=250),
                                expectedGeneration=bridge.binding["generation"])
        action_id = bridge._action_id
        await asyncio.sleep(.25)
        cancelled = (await asyncio.to_thread(bridge.command, "cancel_move", actionId=action_id))["movement"]["action"]
        record("cancel", cancelled)
        assert cancelled["status"] == "cancelled" and cancelled["endEvidence"]["inputsReleased"], cancelled
        assert cancelled["evidence"]["attachmentObserved"], cancelled

        await setup("shaft")
        await setup("break")
        interrupted = await move("rope_break", floor - 150, status="failed", reason="rope_interrupted")
        assert interrupted["evidence"]["attached"], interrupted
        broken = await wait_file(profile / "rope-motion-break.json")
        assert broken["pulley"] and broken["inputsReleased"], broken

        await setup("shaft")
        await setup("generation")
        await move("generation_change", floor - 150, status="unknown")
        stale = await wait_file(profile / "rope-motion-generation.json")
        assert stale["pulley"] and stale["inputsReleased"] and stale["myPlayer"] != stale["slot"], stale
    finally:
        bridge.close()


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--profile", type=Path, required=True)
    parser.add_argument("--port", type=int, required=True)
    asyncio.run(verify(parser.parse_args()))
