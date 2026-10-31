"""Native placed torches + existing semantic coverage, real Lua/IPC, no model.

SCENE_LIGHT_BASELINE=1 explicitly captures the pre-classification package.
Run with verify_server_ai_direct_input_native.ps1 -Scene -TestScript native_scene_lights.py.
"""
import argparse
import asyncio
import json
import os
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'python'))
from server_ai import ServerAiBridge
from lua_runtime import LuaRuntime, Limits
from native_direct_input import wait_file


async def verify(args):
    fixture = await wait_file(args.profile / 'scene-fixture.json', 90)
    baseline = os.environ.get('SCENE_LIGHT_BASELINE') == '1'
    bridge = ServerAiBridge(args.port)
    calls = []
    source = """
local full=api('observe_scene',{})
local capped=api('observe_scene',{layers={'objects','map','hazards'},maxObjects=1,maxHazards=1})
local objects=api('observe_scene',{layers={'objects'},maxObjects=32})
local geometry=api('observe_scene',{layers={'geometry','landingRegions'}})
return {full=full,capped=capped,objects=objects,geometry=geometry}
"""
    try:
        result = await LuaRuntime(bridge, evidence_root=args.profile / 'scene-light-lua').execute(
            source, limits=Limits(seconds=20, calls=4, actions=0),
            on_result=lambda n, a, r: calls.append(dict(operation=n, arguments=a, result=r)))
        report = dict(baseline=baseline, fixture=fixture, source=source, result=result, calls=calls)
        (args.profile / 'scene-lights.json').write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
        assert result['status'] == 'completed', result
        value = result['value']
        full = value['full']['data']; capped = value['capped']['data']
        objects = value['objects']['data']; geometry = value['geometry']['data']
        kinds = full['objectCounts']['observed']
        for kind in ('tree_component', 'ore_component', 'chest', 'door', 'workbench', 'npc', 'drop'):
            assert kinds.get(kind, 0) > 0, (kind, full)
            assert full['objectCounts']['returned'].get(kind, 0) > 0, (kind, full)
        assert capped['objectCounts']['observed'] == kinds
        assert capped['objects']['truncated'] and capped['objects']['returnedCount'] == 1
        assert capped['objects']['omittedCount'] == capped['objects']['matchedCount'] - 1
        # Separate API calls are different updates: the native Guide may move.
        # Caps cannot alter static glyphs; compare cells without entity overlays.
        markers = set('N!$P@*')
        assert all(a == b for ra, rb in zip(full['map']['rows'], capped['map']['rows'])
                   for a, b in zip(ra, rb) if a not in markers and b not in markers)
        assert {'water', 'lava', 'honey', 'shimmer'} <= {
            s for h in full['hazards']['tiles']['items'] for s in h['sources']}
        assert capped['hazards']['tiles']['truncated'] and full['hazards']['entities']['matchedCount'] > 0
        assert full['geometry'] == geometry['geometry']
        assert full['landingRegions'] == geometry['landingRegions']
        assert len({r['target']['surfaceY'] for r in full['landingRegions']['items']}) >= 2
        assert 'map' not in objects and 'geometry' not in objects and 'hazards' not in objects
        lights = [o for o in objects['objects']['items'] if o['kind'] == 'light_source']
        if baseline:
            assert not lights and not kinds.get('light_source', 0)
        else:
            expected = {(t['x'], t['y']) for t in fixture['torches']}
            assert kinds['light_source'] == len(expected) > 16
            assert lights and {(o['observedBounds']['x'], o['observedBounds']['y']) for o in lights} <= expected
            assert len(lights) == objects['objectCounts']['returned']['light_source']
            assert objects['objects']['matchedCount'] == sum(kinds.values())
            for light in lights:
                assert light['typeId'] == 4 and light['contentKey'] == 'Terraria/Torches'
                assert light['complete'] and light['observedTileCount'] == 1
                assert light['observedBounds']['width'] == light['observedBounds']['height'] == 1
            assert full['objects']['truncated'] and full['objectCounts']['returned']['light_source'] > 0
            assert capped['objects']['items'][0]['kind'] != 'light_source'
            assert any('t' in row for row in full['map']['rows'])
        report['summary'] = dict(baseline=baseline, fullBytes=len(json.dumps(value['full'], ensure_ascii=False, separators=(',', ':')).encode()),
            objectsOnlyBytes=len(json.dumps(value['objects'], ensure_ascii=False, separators=(',', ':')).encode()),
            observed=kinds, returned=full['objectCounts']['returned'], lightsLocated=len(lights))
        (args.profile / 'scene-lights.json').write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
        print(json.dumps(report['summary']), flush=True)
    finally:
        bridge.close()


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--port', type=int, required=True)
    parser.add_argument('--profile', type=Path, required=True)
    asyncio.run(verify(parser.parse_args()))
