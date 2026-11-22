"""Isolated turnaround house, one naturally moving resident and a single talk_to call."""
import argparse
import asyncio
import json
from pathlib import Path

from native_direct_input import Limits, LuaRuntime, ServerAiBridge, wait_file


async def verify(port, profile, kind):
    assert (profile / "isolated-action-fixture").is_file()
    await wait_file(profile / "action-fixture.json", 90)
    request = profile / "terrain-request.tmp"
    request.write_text(json.dumps(dict(scene="flat", direction=1, recovery="turnaround")))
    request.replace(profile / "terrain-request.json")
    ready = await wait_file(profile / "g3-shop-ready.json")
    bridge = ServerAiBridge(port)
    runtime = LuaRuntime(bridge, evidence_root=profile / "dialogue-lua")
    calls, sightings = [], []
    key = "Terraria/" + kind

    def observe(name, args, result):
        calls.append(name)
        if name == "scan_entities":
            for row in result["data"]["items"]:
                if row["details"]["contentKey"] == key:
                    sightings.append(dict(ref=row["details"]["entityRef"], slot=row["slot"],
                                          pos=row["centerWorldPixels"], distance=row["distancePixels"]))
    try:
        result = await runtime.execute(f"return terraria.npc.talk_to({json.dumps(key)})",
                                       limits=Limits(seconds=60), on_result=observe)
        report = result["value"]
        evidence = dict(calls=calls, sightings=sightings, result=result)
        (profile / "talk-to-result.json").write_text(json.dumps(evidence, ensure_ascii=False), encoding="utf8")
        assert result["status"] == "completed" and report["status"] == "completed", (result["status"], report, result.get("error"), result.get("lastAction",{}))
        assert calls[-1] == "open_npc_dialogue" and set(calls) == {"navigate_to","scan_entities","open_npc_dialogue"}, calls
        assert sightings[0]["distance"] > 80 and sightings[-1]["distance"] <= 80
        assert len({row["pos"]["x"] for row in sightings}) > 1, sightings
        control = profile / "g3-shop-control.tmp"
        control.write_text(json.dumps(dict(id="dialogue", op="snapshot")))
        control.replace(profile / "g3-shop-control.json")
        actual = await wait_file(profile / "g3-shop-control-result.json")
        assert actual["id"] == "dialogue" and actual["talkNPC"] == ready["slot"]
        assert report["entityRef"] == actual["entityRef"] and all(
            row["ref"] == actual["entityRef"] and row["slot"] == ready["slot"] for row in sightings)
        assert result["lastAction"]["evidence"]["text"] == report["text"] and report["text"].strip()
        evidence["nativeDialogue"] = dict(slot=actual["talkNPC"], ref=actual["entityRef"])
        (profile / "talk-to-result.json").write_text(json.dumps(evidence, ensure_ascii=False), encoding="utf8")
        print(f"{kind}: {len(sightings)} observations, native dialogue verified: {profile}")
    finally:
        bridge.close()


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--port", type=int, required=True)
    parser.add_argument("--profile", type=Path, required=True)
    parser.add_argument("--npc", required=True, choices=["Merchant", "Nurse"])
    args = parser.parse_args()
    asyncio.run(verify(args.port, args.profile, args.npc))
