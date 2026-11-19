"""Exercise build_place over the isolated placement fixture's native IPC connection."""
import argparse
import asyncio
import json
from pathlib import Path
import socket
import sys
import time
import uuid

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "python"))
from jsonschema import Draft202012Validator
from api_result_contracts import schema_for
from program_tasks import coordinator
from server_ai import ServerAiBridge
from atomic_operations import verified_build_entries


async def model_submit(port, plan, log):
    bridge = ServerAiBridge(port)
    manager = coordinator(bridge)
    try:
        args = dict(blueprint=plan)
        launch = await manager.invoke("submit_task", dict(operation="build_place", arguments=args))
        result = await manager.invoke("await_task", dict(taskId=launch["taskId"], milliseconds=30000))
        print(json.dumps(dict(launch=launch, result=result)), file=log, flush=True)
        Draft202012Validator(schema_for("submit_task")).validate(launch)
        Draft202012Validator(schema_for("await_task")).validate(result)
        assert result["valid"] and result["completed"], result
        assert result["result"]["endEvidence"]["inputsReleased"], result
        proof = result["result"]["task"]["result"]["evidence"]
        assert verified_build_entries(args, proof), proof
        return [entry["status"] for entry in proof["entries"]]
    finally:
        await manager.close()
        bridge.close()


def verify(port, profile):
    assert (profile / "isolated-action-fixture").is_file()
    ready = profile / "action-fixture.json"
    deadline = time.monotonic() + 10
    while not ready.exists() and time.monotonic() < deadline:
        time.sleep(.05)
    fixture = json.loads(ready.read_text(encoding="utf-8-sig"))
    left = fixture["mineX"] - 8
    floor = fixture["mineY"] + 2
    binding = {}
    with socket.create_connection(("127.0.0.1", port), timeout=8) as conn, conn.makefile("rb") as reader, (
        profile / "build-place-trace.jsonl"
    ).open("w", encoding="utf-8") as log:
        def request(op, **params):
            msg = dict(v=1, id=uuid.uuid4().hex, op=op, **params)
            if binding:
                msg["binding"] = binding
            conn.sendall((json.dumps(msg, separators=(",", ":")) + "\n").encode())
            raw = reader.readline(1048578)
            assert raw.endswith(b"\n") and len(raw.rstrip(b"\r\n")) <= 1048576, "IPC frame exceeds 1 MiB"
            reply = json.loads(raw)
            print(json.dumps(dict(request=msg, reply=reply, bytes=len(raw))), file=log, flush=True)
            assert reply["ok"], reply
            return reply

        binding.update(request("bind")["binding"])

        def snapshot(plan, item):
            diff = request("query", operation="build_diff", arguments=dict(blueprint=plan))["data"]["data"]
            bag = request("state")["data"]["inventory"]
            return diff, sum(row["stack"] for row in bag if row["type"] == item)

        def run(plan, cancel=False):
            aid = uuid.uuid4().hex
            request("start", actionId=aid, operation="build_place", arguments=dict(blueprint=plan),
                    expiresAtUnixMs=int(time.time() * 1000) + 1800)
            deadline = time.monotonic() + 55
            while time.monotonic() < deadline:
                action = request("action", actionId=aid)["data"]["action"]
                if action["status"] != "running":
                    assert action["endEvidence"]["inputsReleased"], action
                    return action
                if cancel:
                    diff = request("query", operation="build_diff", arguments=dict(blueprint=plan))["data"]["data"]
                    if 0 < diff["matched"] < len(plan["tiles"]):
                        request("cancel", actionId=aid)
                        cancel = False
                time.sleep(.02)
            raise TimeoutError(aid)

        origin = dict(x=left, y=floor-1)
        partial = dict(origin=origin, tiles=[dict(x=3, y=0, itemType=9), dict(x=3, y=1, itemType=9)])
        before, stock = snapshot(partial, 9)
        assert before["matched"] == 0 and before["placeable"] == 1, before
        action = run(partial)
        assert action["status"] == "failed" and action["reason"] == "build_partial", action
        assert action["task"]["result"]["evidence"]["outcome"] == "partial", action
        after, remaining = snapshot(partial, 9)
        assert [e["status"] for e in after["entries"]] == ["already_matches", "occupied_requires_dig"], after
        assert stock - remaining == 1
        ground = request("query", operation="inspect_tile", arguments=dict(x=left+3, y=floor))["data"]["data"]["cell"]
        assert ground["hasTile"] and ground["tileType"] == 0, ground

        plan = dict(origin=origin, tiles=[dict(x=x, y=0, itemType=3) for x in (-2, -3, -4, -5)])
        before, stock = snapshot(plan, 3)
        assert before["matched"] == 0 and stock >= 4, before
        assert run(plan, cancel=True)["status"] == "cancelled"
        stopped, rest = snapshot(plan, 3)
        assert 0 < stopped["matched"] < 4 and stock-rest == stopped["matched"], stopped
        resumed = asyncio.run(model_submit(port, plan, log))
        assert resumed.count("matched") == stopped["matched"] and resumed.count("placed") == 4-stopped["matched"]
        done, rest = snapshot(plan, 3)
        assert done["matched"] == 4 and stock-rest == 4, done
        assert asyncio.run(model_submit(port, plan, log)) == ["matched"] * 4
        repeated, final = snapshot(plan, 3)
        assert repeated["matched"] == 4 and final == rest, repeated
    print(f"Native build_place passed: {profile / 'build-place-trace.jsonl'}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--port", required=True, type=int)
    parser.add_argument("--profile", required=True, type=Path)
    args = parser.parse_args()
    verify(args.port, args.profile)
