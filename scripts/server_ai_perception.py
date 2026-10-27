"""G8.5 isolated copied-world perception acceptance through the existing Lua runtime."""
import argparse
import asyncio
import json
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'python'))
from lua_runtime import LuaRuntime, Limits
from server_ai import ServerAiBridge


async def verify(port):
    bridge = ServerAiBridge(port=port)
    try:
        source = (ROOT / 'scripts/lua/server_ai_perception.lua').read_text(encoding='utf-8')
        result = await LuaRuntime(bridge, evidence_root=ROOT / '.local/g85-lua-evidence').execute(
            source, limits=Limits(seconds=20, calls=12, actions=0))
        value = result.get('value') or {}
        for name in ('terrain', 'tiles', 'inspected', 'unknown', 'outside', 'players', 'npcs', 'drops', 'content'):
            if result['status'] != 'completed':
                break
            observation = value[name]
            assert observation['binding'] == bridge.binding
            assert observation['actorSlot'] == bridge.binding['slot']
            assert observation['actorGeneration'] == bridge.binding['generation']
            assert observation['worldSessionId'] == bridge.binding['world']
            assert observation['observedAtUnixMs'] > 0
            assert observation['clock'] == 'server_utc_at_projection'
        return result
    finally:
        bridge.close()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--port', type=int, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    result = asyncio.run(verify(args.port))
    args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2)+'\n', encoding='utf-8')
    print(json.dumps(dict(status=result['status'], actions=result['actions'], failure=result.get('failure'), output=str(args.output))))
    return 0 if result['status'] == 'completed' and result['actions'] == 0 else 1


if __name__ == '__main__':
    raise SystemExit(main())
