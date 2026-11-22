"""NPC assignment assertions for a house built in a copied dedicated-server world."""
import asyncio
import json
import time
import sys
from pathlib import Path

from jsonschema import Draft202012Validator

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "python"))
from api_result_contracts import schema_for
from atomic_operations import dispatch
from server_ai import ServerAiBridge


async def verify_assignment(port, profile, guide, housing):
    (profile / "housing-refs-request").touch()
    deadline = time.monotonic() + 8
    while not (profile / "housing-refs.json").exists():
        if time.monotonic() >= deadline:
            raise TimeoutError("Town NPC fixture did not produce its references")
        await asyncio.sleep(.05)
    refs = json.loads((profile / "housing-refs.json").read_text(encoding="utf-8"))
    merchant, stale = refs["merchant"], refs["stale"]
    assert stale != merchant and stale.rsplit(":", 1)[0] == merchant.rsplit(":", 1)[0]
    bridge = ServerAiBridge(port)
    with (profile / "npc-home.jsonl").open("w", encoding="utf-8") as log:
        async def call(name, args):
            result = await dispatch(bridge, name, args)
            print(json.dumps(dict(name=name, args=args, result=result)), file=log, flush=True)
            Draft202012Validator(schema_for(name)).validate(result)
            return result

        async def states():
            snapshots = []
            for ref in (guide, merchant):
                result = await call("inspect_npc_housing", {"entityRef": ref})
                assert result["valid"]
                snapshots.append(result["data"])
            return snapshots

        async def rejected(ref, tile, reason):
            before = await states()
            result = await call("assign_npc_home", dict(entityRef=ref, **tile))
            assert result["status"] == "failed" and result["reason"] == reason, result
            assert result["evidence"]["mutationAttempted"] is False
            assert await states() == before, reason

        try:
            query_tile = housing["queryTile"]
            initial = await states()
            assert initial[0]["type"] == 22 and initial[1]["type"] == 17
            result = await call("assign_npc_home", dict(entityRef=guide, **query_tile))
            assert result["valid"] and result["completed"], result
            moved, other = await states()
            assert moved["entityRef"] == guide and moved["homeless"] is False and other == initial[1]
            assert moved["managerHasRoom"] and moved["managerHomeTile"] == housing["homeTile"]
            assert (moved["homeTileX"], moved["homeTileY"]) == tuple(housing["homeTile"][a] for a in ("x", "y"))
            # Reject even when the Guide already has a verified prior home to lose.
            await rejected(guide, dict(x=query_tile["x"]-3, y=query_tile["y"]-3), "invalid_room")
            await rejected(merchant, query_tile, "room_occupied")
            await rejected(stale, query_tile, "stale_entity_ref")
        finally:
            bridge.close()
