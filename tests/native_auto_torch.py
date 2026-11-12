"""Isolated Dedicated Server native lighting, autonomous placement and arbitration."""
import argparse
import json
from pathlib import Path
import sys
import time
import uuid

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "python"))
from server_ai import ServerAiBridge


def until(read, predicate=bool, seconds=15):
    end = time.monotonic() + seconds
    while time.monotonic() < end:
        value = read()
        if predicate(value):
            return value
        time.sleep(.04)
    raise AssertionError(f"Timed out. Last observation: {value}")


def verify(profile, port):
    hold = profile / "torch-hold"
    pause = profile / "torch-pause"
    hold.touch()
    until((profile / "action-fixture.json").exists, seconds=90)
    bridge = ServerAiBridge(port)
    report = []
    output = profile / "auto-torch-results.json"

    def state():
        return bridge._request("state")["data"]

    def sample():
        value = bridge._request("query", operation="observe_scene", arguments={"layers": ["geometry"]})
        lighting = value["data"]["data"]["lighting"]
        assert lighting["available"], lighting
        return lighting

    def log():
        path = profile / "torch-placement.jsonl"
        if not path.exists():
            return []
        return [json.loads(line) for line in path.read_text().splitlines() if line.endswith("}")]

    def fixture(mode):
        ready = profile / "torch-ready.json"
        ready.unlink(missing_ok=True)
        pending = profile / "torch-request.tmp"
        pending.write_text(mode)
        pending.replace(profile / "torch-request")
        return until(lambda: json.loads(ready.read_text()) if ready.exists() else None)

    def advance(updates):
        start = state()["tick"]
        return until(state, lambda s: s["tick"] >= start + updates)

    def record(name, **evidence):
        report.append(dict(case=name, **evidence))
        output.write_text(json.dumps(report, indent=2))
        print(f"{name} passed", flush=True)

    def placement(count, expected_stack=3, check_layout=True):
        detail = until(log, lambda items: len(items) > count)[-1]
        result = detail["result"]
        proof = result["Evidence"]
        assert result["Status"] == "completed" and result["InputsReleased"] and result["EffectVerified"], detail
        assert proof["typeId"] == 4 and proof["targetPresent"] and proof["placedByActor"], proof
        assert proof["nativePlacementEvents"] == proof["consumedCount"] == 1, proof
        assert detail["source"] == 15 and detail["hotbar"] == 2, detail
        if check_layout and expected_stack:
            assert detail["restoreStatus"] == "completed", detail
        elif not expected_stack:
            # Native consumption replaces the exhausted stack with an empty
            # item. Both slots are already empty; no reverse swap is required.
            assert detail["initialStack"] == 1 and detail["restoreStatus"] is None, detail
        after = state()
        assert after["selectedSlot"] == detail["selectedAfter"] == detail["selectedBefore"], detail
        if check_layout:
            assert after["inventory"][2]["type"] == 0, after["inventory"][2]
        assert after["inventory"][15]["stack"] == expected_stack, after["inventory"][15]
        assert after["inventory"][15]["type"] == (8 if expected_stack else 0), after["inventory"][15]
        return detail

    try:
        for mode in ("dark", "occluded", "single"):
            hold.touch()
            fixture(mode)
            before = sample()
            count = len(log())
            assert before["brightness"] < .45, before
            if mode == "dark":
                pause.touch()
                time.sleep(.1)
                hold.unlink()
                paused = state()
                time.sleep(.5)
                assert state()["tick"] == paused["tick"] and len(log()) == count
                pause.unlink()
            else:
                hold.unlink()
            detail = placement(count, expected_stack=0 if mode == "single" else 3)
            after = sample()
            assert after["brightness"] >= .45 and after["brightness"] > before["brightness"] + .15, (before, after)
            advance(250)
            assert len(log()) == count + 1
            record(mode, before=before, after=after, placement=detail)

        for mode in ("lit", "gemspark", "daylight", "blocked", "empty"):
            hold.touch()
            fixture(mode)
            before = sample()
            assert (before["brightness"] >= .45) == (mode in ("lit", "gemspark", "daylight")), (mode, before)
            count = len(log())
            hold.unlink()
            after = advance(250)
            assert len(log()) == count and after["inventory"][15]["stack"] == (0 if mode == "empty" else 4), mode
            record(mode, lighting=before, inventory=after["inventory"][15])

        for mode, operation in (("nav", "navigate_to"), ("mine", "mine_target")):
            hold.touch()
            scene = fixture(mode)
            arguments = (dict(x=(scene["x"] + 20) * 16, y=scene["y"] * 16) if mode == "nav"
                         else dict(x=scene["x"] + 11, y=scene["y"] - 2))
            action_id = uuid.uuid4().hex
            bridge._request("start", actionId=action_id, operation=operation, arguments=arguments,
                            expiresAtUnixMs=int(time.time() * 1000) + 1500)
            stocked = fixture("stock")
            assert stocked["activeAgent"], stocked
            count = len(log())
            hold.unlink()
            def poll():
                return bridge._request("action", actionId=action_id)["data"]["action"]
            end = until(poll, lambda a: a["status"] != "running", seconds=30)
            assert end["status"] == "completed" and end["endEvidence"]["inputsReleased"], (mode, end["reason"])
            # Mining drops may naturally enter the freed hotbar slot after
            # the torch is returned. The inventory cases verify both slots.
            detail = placement(count, check_layout=mode != "mine")
            assert detail["GameUpdate"] > end["endEvidence"]["gameUpdate"], detail
            record(mode, action=end, placement=detail)
    finally:
        bridge.close()


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--profile", type=Path, required=True)
    parser.add_argument("--port", type=int, required=True)
    args = parser.parse_args()
    verify(args.profile, args.port)
