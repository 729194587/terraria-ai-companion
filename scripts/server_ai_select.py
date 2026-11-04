"""Explicit one-action Lua acceptance entrypoint for server AI (not a new runtime)."""
import argparse
import asyncio
import json
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "python"))
from lua_runtime import LuaRuntime, Limits
from server_ai import ServerAiBridge


async def select(port, key):
    bridge = ServerAiBridge(port=port)
    try:
        source = "return api('select_hotbar_slot', {slot=" + str(key) + "})"
        return await LuaRuntime(bridge, evidence_root=Path(__file__).resolve().parents[1] / ".local/g82-lua-evidence").execute(
            source, limits=Limits(seconds=10, calls=1, actions=1))
    finally:
        bridge.close()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--port", type=int, default=18779)
    parser.add_argument("--key", type=int, choices=range(10), default=2, help="game key 1..9,0; 0 is the tenth slot")
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    result = asyncio.run(select(args.port, args.key))
    text = json.dumps(result, ensure_ascii=True, indent=2)
    if args.output:
        args.output.write_text(text + "\n", encoding="utf-8")
    print(text)
    return 0 if result["status"] == "completed" and result.get("lastAction", {}).get("completed") is True else 1


if __name__ == "__main__":
    raise SystemExit(main())
