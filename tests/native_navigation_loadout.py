"""Measure native navigate_to arrivals across matched terrain and loadouts."""
import argparse
import asyncio
import json
from pathlib import Path

from native_direct_input import Limits, LuaRuntime, ServerAiBridge, wait_file


TARGETS = {"short_gap": (12, 0), "solid_up_five": (8, -5), "wide_gap": (24, 0), "flat": (12, 0), "extra_up": (8, -8), "frontier_nohook": (0, 0)}
LOADOUTS = ("basic", "cloud", "cloud_angel", "angel_hook", "angel", "hook")


async def measure(profile, port, scene, loadout):
    name = f"{scene}-{loadout}"
    ready = profile / "terrain-ready.json"
    ready.unlink(missing_ok=True)
    request = profile / "terrain-request.tmp"
    shelf = int(scene[6:]) if scene.startswith("bottle") else 0
    request.write_text(json.dumps(dict(scene="flat" if shelf else scene, direction=1, loadout=loadout,shelfHeight=shelf)))
    request.replace(profile / "terrain-request.json")
    fixture = await wait_file(ready)
    offset, rise = (0,-shelf) if shelf else TARGETS[scene]
    x = (fixture["originTileX"] + offset) * 16
    y = (fixture["floor"] + rise) * 16
    bridge = ServerAiBridge(port)
    try:
        result = await LuaRuntime(bridge).execute(
            f"return api('navigate_to',{{x={x},y={y}}})", limits=Limits(seconds=90, calls=1, actions=1))
    finally:
        bridge.close()
    (profile / f"{name}-receipt.json").write_text(json.dumps(result, indent=2))
    action = result.get("value") or result.get("lastAction") or {}
    assert action.get("status") in ("failed", "completed", "stalled", "timeout", "cancelled"), (name, result)
    end = action["endEvidence"]
    assert end["inputsReleased"] and end["actorSlot"] == fixture["slot"]
    assert end["worldSessionId"] == fixture["world"] and end["actorGeneration"] == fixture["generation"]
    (profile / "loadout-finish").touch()
    frames = await wait_file(profile / f"{name}-motion.json")
    # The terrain fixture gives the actor 15 native updates to settle and apply accessories.
    samples = frames[14:]
    assert samples and all(f["Enabled"] == (loadout in ("cloud", "cloud_angel","cloud_balloon","bundle","bundle_angel","separate","duplicate_cloud","shaft_bundle"))
                           and f["wingsLogic"] == (2 if "angel" in loadout else 0)
                           and f["basicHook"] == ("hook" in loadout) for f in samples), name
    start = samples[0]
    assert abs(start["x"] - fixture["startX"]) <= .1 and abs(start["feetY"] - fixture["startY"] - 42) <= .1
    evidence = action["task"]["result"]["evidence"] or {}
    arrived = bool(action.get("completed"))
    if arrived:
        assert samples[-1]["grapCount"] == 0
        assert all(evidence.get(k) for k in ("arrived", "grounded", "settled", "safeSupport"))
        assert abs(evidence["feetY"] - y) <= 2 and abs(evidence["x"] + 10 - x) <= 24
    return dict(scene=scene, loadout=loadout, arrived=arrived, status=action["status"], reason=action["reason"],
                target=[x,y], start=[start["x"],start["feetY"]], end=[samples[-1]["x"],samples[-1]["feetY"]],
                horizontalInput=any(f["horizontalInput"] for f in samples),
                jumpPressed=any(f["jumpInput"] for f in samples),
                jumps=["cloud" if key=="Active" else key for i,frame in enumerate(samples) for key in ('sand','blizzard','Active')
                       if frame[key] and (i==0 or not samples[i-1][key])],
                extraJump=any(f["Active"] or f["blizzard"] or f["sand"] for f in samples),
                flight=any(f["wingsLogic"] and f["wingTime"] < f["wingTimeMax"]
                           and f["feetY"] < start["feetY"] for f in samples),
                dashCount=samples[-1]["dashCount"], attached=any(f["grapCount"] > 0 for f in samples),
                climbed=any(f["pulley"] for f in samples), platformsUsed=start["platforms"]-samples[-1]["platforms"])


def check_result(row):
    scene, loadout = row["scene"], row["loadout"]
    expected_failures = {
        "extra_up": {"basic", "hook"},
        "frontier_nohook": {"basic", "limited_angel"},
        "wide_gap": {"basic", "cooling_dash"},
    }
    assert row["arrived"] == (loadout not in expected_failures.get(scene, ())), row
    if scene == "frontier_nohook":
        assert row["attached"] == (loadout in ("limited_angel_hook", "hook")), row
    if scene in ("extra_up", "frontier_nohook") and loadout == "cloud":
        assert row["extraJump"], row
    if scene in ("extra_up", "frontier_nohook") and loadout in ("angel", "angel_hook"):
        assert row["flight"], row
    if scene in ("extra_up", "frontier_nohook") and loadout == "cloud_angel":
        assert row["extraJump"] or row["flight"], row
    assert row["dashCount"] == (1 if scene == "wide_gap" and loadout == "dash" else 0), row


async def verify(args):
    await wait_file(args.profile / "action-fixture.json", 90)
    rows = []
    for scene in args.scenes or TARGETS:
        if scene == "shaft_routes":
            for loadout in ("shaft_rope", "shaft_platform", "bundle", "shaft_blocked", "shaft_bundle"):
                row = await measure(args.profile, args.port, "extra_up", loadout)
                rows.append(row)
                print(json.dumps(row), flush=True)
                (args.profile / "loadout-results.json").write_text(json.dumps(rows, indent=2))
                assert row["arrived"] and row["climbed"] == (loadout == "shaft_rope"), row
                assert (row["platformsUsed"] > 0) == (loadout in ("shaft_platform", "shaft_blocked")), row
                if loadout in ("bundle", "shaft_bundle"):
                    assert row["jumpPressed"], row
            continue
        if scene == "bottles":
            first_bottle = len(rows)
            for height, loadout in ((5,"basic"),(8,"balloon"),(12,"cloud_balloon"),(12,"blizzard"),
                                    (14,"sand"),(18,"sand_balloon"),(18,"blizzard_balloon"),(5,"bundle"),(18,"bundle"),
                                    (0,"sand"),(0,"blizzard"),(0,"bundle_angel"),(12,"duplicate_cloud"),(50,"separate"),(50,"bundle")):
                row=await measure(args.profile,args.port,f"bottle{height}" if height else "extra_up",loadout)
                rows.append(row)
                (args.profile / "loadout-results.json").write_text(json.dumps(rows,indent=2))
                print(json.dumps(row),flush=True)
            assert all(r["arrived"] for r in rows if r["scene"].startswith("bottle")), rows
            assert rows[-1]["jumps"]==["sand","blizzard","cloud"],rows[-1]
            for r in rows[first_bottle:]:
                if r["loadout"]=="bundle" and r["scene"]=="bottle5": assert not r["jumps"],r
                if r["loadout"]=="bundle" and r["scene"]=="bottle18": assert r["jumps"]==["sand"],r
                if r["loadout"]=="separate": assert r["jumps"]==["sand","blizzard","cloud"],r
                if r["loadout"]=="duplicate_cloud": assert r["jumps"]==["cloud"],r
                if r["scene"]=="extra_up": assert r["arrived"] and (r["jumps"] or r["flight"] or r["jumpPressed"]),r
            continue
        loadouts = {"short_gap": ("basic","boost_run"), "solid_up_five": ("basic","boost_jump"),
                    "wide_gap": ("basic", "boost_run", "dash", "cooling_dash"),
                    "extra_up": LOADOUTS + ("boost_jump",),
                    "frontier_nohook": LOADOUTS + ("limited_angel","limited_angel_hook")}.get(scene, LOADOUTS)
        for loadout in loadouts:
            row = await measure(args.profile, args.port, scene, loadout)
            rows.append(row)
            (args.profile / "loadout-results.json").write_text(json.dumps(rows, indent=2))
            print(json.dumps(row), flush=True)
            check_result(row)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--profile", type=Path, required=True)
    parser.add_argument("--port", type=int, required=True)
    parser.add_argument("--scenes", nargs="+", choices=(*TARGETS,'bottles','shaft_routes'))
    asyncio.run(verify(parser.parse_args()))
