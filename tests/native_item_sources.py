"""Verify native NPC loot reports through the bound Server AI dispatcher."""
import argparse
import asyncio
import json
import math
from pathlib import Path
import sys

from jsonschema import Draft202012Validator

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'python'))
from api_result_contracts import schema_for
from atomic_operations import dispatch
from server_ai import ServerAiBridge


async def verify(port, profile, calamity):
    bridge = ServerAiBridge(port)
    validator = Draft202012Validator(schema_for('query_item_sources'))
    cases = {
        'Terraria/Lens': 'Terraria/DemonEye',
        'Terraria/KingSlimeBossBag': 'Terraria/KingSlime',
        'Terraria/ObsidianSkull': None,
    }
    if calamity:
        cases['CalamityMod/PearlShard'] = 'CalamityMod/DesertScourgeHead'
        cases['CalamityMod/DesertScourgeBag'] = 'CalamityMod/DesertScourgeHead'
    evidence = {}
    try:
        for key, npc in cases.items():
            resolved = await dispatch(bridge, 'resolve_content', {'kind': 'item', 'query': key})
            candidate, = resolved['data']['candidates']
            result = await dispatch(bridge, 'query_item_sources', {'typeId': candidate['typeId']})
            evidence[key] = result
            (profile / 'item-sources.json').write_text(json.dumps(evidence, ensure_ascii=False, indent=2), encoding='utf-8')
            validator.validate(result)
            assert result['valid'] and result['data']['contentKey'] == key
            rows = result['data']['sources']
            if npc is None:
                assert rows == [], rows
                continue
            matches = [row for row in rows if row['contentKey'] == npc]
            if key == 'Terraria/Lens':
                assert any(not r['boss'] and math.isclose(r['probability'], 0.33, rel_tol=1e-6)
                           and r['minStack'] == r['maxStack'] == 1 for r in matches), matches
            elif key == 'CalamityMod/PearlShard':
                assert any(r['boss'] and r['probability'] == 1 and r['minStack'] == 25
                           and r['maxStack'] == 30 and r['conditions'] for r in matches), matches
            else:
                assert any(r['boss'] and r['probability'] == 1 and r['minStack'] == r['maxStack'] == 1
                           and any('Expert' in c['id'] for c in r['conditions']) for r in matches), matches
        missing = await dispatch(bridge, 'query_item_sources', {'typeId': 2147483647})
        assert not missing['valid'] and missing['reason'] == 'unknown_item_type'
    finally:
        bridge.close()


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--port', type=int, required=True)
    parser.add_argument('--profile', type=Path, required=True)
    parser.add_argument('--calamity', action='store_true')
    args = parser.parse_args()
    asyncio.run(verify(args.port, args.profile, args.calamity))
