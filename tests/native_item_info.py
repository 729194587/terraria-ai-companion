"""Read loaded item definitions through the bound Server AI dispatcher."""
import argparse
import asyncio
import json
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'python'))
from atomic_operations import dispatch
from control import Bridge
from server_ai import ServerAiBridge


async def verify(port, profile, calamity, client):
    bridge = Bridge(port) if client else ServerAiBridge(port)
    results = {}
    try:
        keys = ['Terraria/ObsidianSkull']
        if calamity:
            keys.extend(['CalamityMod/BladecrestOathsword', 'CalamityMod/LoreKingSlime'])
        for key in keys:
            resolved = await dispatch(bridge, 'resolve_content', {'kind': 'item', 'query': key})
            candidate, = resolved['data']['candidates']
            result = await dispatch(bridge, 'query_item_info', {'typeId': candidate['typeId']})
            results[key] = result
            (profile / 'item-info.json').write_text(json.dumps(results, indent=2, ensure_ascii=False), encoding='utf-8')
            data = result['data']
            assert result['valid'] and data['contentKey'] == key and data['name'] == candidate['name']
            assert data['mod'] == key.split('/')[0] and any(data['tooltip']), data
            if data['mod'] == 'Terraria':
                assert data['stats']['defense'] >= 1 and 'extraTooltip' not in data
            else:
                assert data['extraTooltip'].strip() and data['extraTooltip'] not in data['tooltip']
        missing = await dispatch(bridge, 'query_item_info', {'typeId': 2147483647})
        assert not missing['valid'] and missing['reason'] == 'unknown_item_type'
    finally:
        bridge.close()


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--port', type=int, required=True)
    parser.add_argument('--profile', type=Path, required=True)
    parser.add_argument('--calamity', action='store_true')
    parser.add_argument('--client', action='store_true', help='Use the local player bridge in a loaded world')
    args = parser.parse_args()
    asyncio.run(verify(args.port, args.profile, args.calamity, args.client))
