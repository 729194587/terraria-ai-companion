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


async def verify(args):
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
        assert evidence["navigation"]["CompletedJumps"] >= 1, evidence
        print("Native navigate_to jumped over the puddle and verified arrival", flush=True)

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
        assert action.get("status") == "failed" and action.get("reason") == "unsupported_navigation_profile", action
        assert action["endEvidence"]["inputsReleased"], action
        print("Native wet starting pose refused by dry navigation", flush=True)
    finally:
        bridge.close()


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--profile", type=Path, required=True)
    parser.add_argument("--port", type=int, required=True)
    asyncio.run(verify(parser.parse_args()))
