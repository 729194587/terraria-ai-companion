"""G8.2 I/II acceptance entrypoint; uses the existing worker, dispatcher and budgets."""
import argparse
import asyncio
import json
from pathlib import Path
import sys
import uuid

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'python'))
from lua_runtime import LuaRuntime, Limits
from server_ai import ServerAiBridge


async def verify(port, key):
    bridge = ServerAiBridge(port=port)
    try:
        source = 'local testKey = ' + ('nil' if key is None else str(key)) + '\n'
        source += (ROOT / 'scripts/lua/server_ai_reads.lua').read_text(encoding='utf-8')
        return await LuaRuntime(bridge, evidence_root=ROOT / '.local/g82-lua-evidence').execute(
            source, limits=Limits(seconds=15, calls=12, actions=0 if key is None else 1))
    finally:
        bridge.close()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--port', type=int, default=18779)
    parser.add_argument('--key', type=int, choices=range(10), help='omit for read-only; otherwise explicitly select game key 1..9,0')
    parser.add_argument('--output', type=Path)
    args = parser.parse_args()
    result = asyncio.run(verify(args.port, args.key))
    output = args.output or ROOT / ('.local/g82-read-acceptance-' + uuid.uuid4().hex + '.json')
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(result, ensure_ascii=True, indent=2) + '\n', encoding='utf-8')
    value = result.get('value') or {}
    player = value.get('after') or {}
    summary = dict(status=result['status'], actions=result['actions'], actionState=result['actionState'],
                   binding=value.get('binding'), life=player.get('life'), selectedSlot=player.get('selectedSlot'),
                   selectedHotbarKey=player.get('selectedHotbarKey'), fullInventorySlots=value.get('fullInventorySlots'),
                   occupiedSlots=value.get('occupiedInventorySlots'),
                   inventory=value.get('inventoryAtFullQuery') if value.get('occupiedInventorySlots') else [],
                   output=str(output), failure=result.get('failure'))
    print(json.dumps(summary, ensure_ascii=True, indent=2))
    return 0 if result['status'] == 'completed' and result['actionState'] != 'unknown' else 1


if __name__ == '__main__':
    raise SystemExit(main())
