"""Verify a housing-sized blueprint through the Lua worker on an isolated server."""
import argparse
import asyncio
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "python"))
from lua_runtime import Limits, LuaRuntime
from server_ai import ServerAiBridge


async def verify(port, profile):
    source = """
local player = api("get_player_state", {}).player
local origin = {x=math.floor(player.positionWorldPixels.x/16), y=math.floor(player.positionWorldPixels.y/16)}
local blueprint = {origin=origin, tiles={}, walls={}, objects={{x=3,y=6,contentKey="Terraria/WorkBench"}}}
for x=0,7 do
  for y=0,7 do
    if x==0 or y==0 or x==7 or y==7 then
      table.insert(blueprint.tiles,{x=x,y=y,itemType=9})
    else
      table.insert(blueprint.walls,{x=x,y=y,itemType=93})
    end
  end
end
local result=api("build_diff",{blueprint=blueprint})
assert(result.valid)
local diff=result.data
assert(#diff.entries==65 and diff.matched+diff.pending==65)
return {entries=#diff.entries,matched=diff.matched,pending=diff.pending}
"""
    bridge = ServerAiBridge(port=port)
    try:
        runtime = LuaRuntime(bridge, evidence_root=profile / "build-diff-lua")
        result = await runtime.execute(source, limits=Limits(seconds=15, calls=2, actions=0))
    finally:
        bridge.close()
    (profile / "build-diff-lua.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
    if result["status"] != "completed" or result["actions"] != 0:
        raise RuntimeError(f"Lua query failed. See {profile / 'build-diff-lua.json'}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--port", type=int, required=True)
    parser.add_argument("--profile", type=Path, required=True)
    args = parser.parse_args()
    asyncio.run(verify(args.port, args.profile))
