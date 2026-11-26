"""Verify a bound scene and the loaded Mod item/recipe registries in the same world."""
import argparse
import asyncio
import json
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'python'))
from atomic_operations import dispatch
from server_ai import ServerAiBridge


async def verify(port, profile):
    bridge = ServerAiBridge(port)
    try:
        async def read(name, args):
            response = await dispatch(bridge, name, args)
            assert response['valid'] and response['binding'] == bridge.binding, name
            return response['data']

        scene = await read('observe_scene', {'layers': ['geometry']})
        assert scene['includedLayers'] == ['geometry'] and scene['geometry']
        results = {'binding': bridge.binding, 'sceneLayers': scene['includedLayers'], 'mods': {}}
        for mod in bridge.hello['compatibility']['loadedMods']:
            name = mod['name']
            if name not in ('CalamityMod', 'RecipeBrowser', 'ImproveGame'):
                continue
            matches = await read('resolve_content', {'kind': 'item', 'query': name+'/', 'mode': 'search', 'maxResults': 1})
            row = results['mods'][name] = {'itemMatches': matches['matchedCount']}
            if matches['candidates']:
                type_id = matches['candidates'][0]['typeId']
                item = await read('query_item_info', {'typeId': type_id})
                assert item['mod'] == name and item['contentKey'] == matches['candidates'][0]['contentKey']
                recipes = await read('query_recipes', {'resultType': type_id, 'availableOnly': False})
                row.update(item=item['contentKey'], typeId=type_id, recipes=recipes['matchedCount'])
            else:
                assert name == 'RecipeBrowser', name
        (profile/'mod-queries.json').write_text(json.dumps(results, indent=2), encoding='utf-8')
    finally:
        bridge.close()


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--port', type=int, required=True)
    parser.add_argument('--profile', type=Path, required=True)
    args = parser.parse_args()
    asyncio.run(verify(args.port, args.profile))
