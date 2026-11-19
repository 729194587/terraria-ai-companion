"""Build and inspect a native 8x8 residence in a fresh action fixture."""
import argparse
import json
import socket
import time
import uuid
from pathlib import Path


def verify(port, profile):
    assert (profile / "isolated-action-fixture").exists()
    ready = profile / "action-fixture.json"
    deadline = time.monotonic() + 15
    while not ready.exists() and time.monotonic() < deadline:
        time.sleep(.05)
    fixture = json.loads(ready.read_text(encoding="utf-8-sig"))
    ox, oy = fixture["mineX"] - 8, fixture["mineY"] - 6
    origin = dict(x=ox, y=oy)
    with socket.create_connection(("127.0.0.1", port), timeout=10) as sock, sock.makefile("rb") as reader, (
        profile / "housing.jsonl"
    ).open("w", encoding="utf-8") as log:
        binding = {}

        def request(op, **kwargs):
            msg = dict(v=1, id=uuid.uuid4().hex, op=op, **kwargs)
            if binding:
                msg["binding"] = binding
            sock.sendall((json.dumps(msg, separators=(",", ":")) + "\n").encode())
            reply = json.loads(reader.readline(1048577))
            if op != "action" or reply.get("data", {}).get("action", {}).get("status") != "running":
                print(json.dumps(dict(request=msg, reply=reply)), file=log, flush=True)
            assert reply["ok"], reply
            return reply

        binding.update(request("bind")["binding"])

        def query(name, **args):
            return request("query", operation=name, arguments=args)["data"]["data"]

        def put(**layers):
            bp = dict(origin=origin, **layers)
            aid = uuid.uuid4().hex
            request("start", actionId=aid, operation="build_place", arguments=dict(blueprint=bp),
                    expiresAtUnixMs=int(time.time()*1000)+1800)
            deadline = time.monotonic()+180
            while time.monotonic() < deadline:
                a = request("action", actionId=aid)["data"]["action"]
                if a["status"] != "running":
                    evidence = a["task"]["result"]["evidence"]
                    failures = [e for e in evidence["entries"] if e["status"] != "placed"]
                    assert a["status"] == "completed" and not failures, (a["reason"], failures)
                    assert a["endEvidence"]["inputsReleased"]
                    return
                time.sleep(.15)
            raise TimeoutError(aid)

        def item(x, y, kind):
            return dict(x=x, y=y, itemType=kind)

        def housing():
            return query("inspect_housing", x=ox+3, y=oy+3)

        tiles = [item(x,y,9) for x in range(8) for y in range(8)
                 if (x in (0,7) or y in (0,7)) and not (x==0 and 4<=y<=6)]
        # Resolve placement origins from the loaded definitions, including the door.
        def object_at(left, top, kind):
            trial = item(left, top, kind)
            e = query("build_diff", blueprint=dict(origin=origin, objects=[trial]))["entries"][0]
            return item(left+ox+left-e["left"], top+oy+top-e["top"], kind)

        door = object_at(0,4,25)
        bench = object_at(5,6,36)
        chair = object_at(1,5,34)
        put(tiles=tiles, objects=[door, bench])
        missing = housing()
        assert (missing["status"], missing["reason"]) == ("invalid", "wall_gap_too_big"), missing
        walls = [item(x,y,93) for x in range(1,7) for y in range(1,7)]
        put(walls=walls)
        # Torch uses the side wall as its native attachment.
        torch = item(6,3,8)
        trial = query("build_diff", blueprint=dict(origin=origin, tiles=[torch]))["entries"][0]
        put(**{("objects" if trial["status"]=="unsupported_content" else "tiles"): [torch]})
        incomplete = housing()
        assert incomplete["status"]=="invalid" and incomplete["failureStage"]=="furniture", incomplete
        assert incomplete["furniture"] == dict(light=True,door=True,table=True,chair=False), incomplete
        put(objects=[chair])
        final = housing()
        assert final["status"]=="valid" and all(final["furniture"].values()), final
        # Run after a successful score to detect leakage of the preceding room's static fields.
        solid = query("inspect_housing", x=ox, y=oy)
        assert solid["status"] == "invalid" and solid["reason"] == "solid_query_tile", solid
        assert all(solid[key] is None for key in (
            "bounds", "furnitureValid", "furniture", "score", "occupied", "evil", "homeTile")), solid
        scene = query("observe_scene", layers=["objects"])
        expected = {"door":(0,4,1,3), "torch":(6,3,1,1), "workbench":(5,6,2,1), "chair":(1,5,1,2)}
        for category, (x,y,w,h) in expected.items():
            matches = [o for o in scene["objects"]["items"] if (o.get("furniture") or {}).get("category")==category
                       and o["furniture"]["footprint"]==dict(x=ox+x,y=oy+y,width=w,height=h)]
            assert len(matches)==1, (category,scene)
    print(f"Housing scenarios passed in {profile}")


if __name__=="__main__":
    parser=argparse.ArgumentParser()
    parser.add_argument("--port",type=int,required=True)
    parser.add_argument("--profile",type=Path,required=True)
    args=parser.parse_args()
    verify(args.port,args.profile)
