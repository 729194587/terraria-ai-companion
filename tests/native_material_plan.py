"""Exercise material planning through one model-visible Lua API call per target."""
from collections import Counter
import argparse
import asyncio
import json
from pathlib import Path
import sys

from jsonschema import Draft202012Validator

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'python'))
from api_result_contracts import schema_for
from atomic_operations import dispatch
from lua_runtime import Limits, LuaRuntime
from server_ai import ServerAiBridge


def check_balance(data):
    # Replay the chosen path using only the declared inventory and external inputs.
    balance = Counter()
    for item in data['inventoryUsed'] + data['deficits']:
        balance[item['typeId']] += item['count']
    for step in data['steps']:
        assert step['recipeId'] in step['recipeIds']
        for item in step['inputs']:
            balance[item['typeId']] -= item['count']
            assert balance[item['typeId']] >= 0, (step, item)
        balance[step['typeId']] += step['batches'] * step['outputPerBatch']
    assert balance[data['typeId']] >= data['count']


async def verify(args):
    bridge = ServerAiBridge(args.port)
    validator = Draft202012Validator(schema_for('plan_materials'))
    evidence = {}

    async def plan(key, count):
        resolved = await dispatch(bridge, 'resolve_content', {'kind': 'item', 'query': key})
        candidate, = resolved['data']['candidates']
        source = f'return terraria.material_plan.plan({candidate["typeId"]}, {count})'
        run = await LuaRuntime(bridge, evidence_root=args.profile/'planner-lua').execute(
            source, limits=Limits(calls=1, actions=0, seconds=30))
        assert run['status'] == 'completed' and run['calls'] == 1 and run['actions'] == 0, run
        result = run['value']
        validator.validate(result)
        assert result['valid'], result
        data = result['data']
        check_balance(data)
        evidence[f'{key} x {count}'] = data
        (args.profile/'material-plan.json').write_text(json.dumps(evidence, indent=2), encoding='utf-8')
        return data

    async def fixture(case):
        ready = args.profile/'craft-ready.json'
        ready.unlink(missing_ok=True)
        request = args.profile/'craft-request.tmp'
        request.write_text(json.dumps({"caseId": case}))
        request.replace(args.profile/'craft-request.json')
        for _ in range(200):
            if (args.profile/'craft-ready.json').is_file():
                break
            await asyncio.sleep(.05)
        else:
            raise TimeoutError('craft fixture')

    try:
        await fixture('group')  # 30 boreal wood and 3 gel
        result = await plan('Terraria/Zenith', 1)
        target = result['steps'][-1]
        assert target['contentKey'] == 'Terraria/Zenith'
        if args.calamity:
            assert 'CalamityMod/CosmicAnvil' in target['stations']
            assert any(i['contentKey'] == 'CalamityMod/AuricBar' and i['count'] == 5 for i in target['inputs'])
        else:
            assert len(target['inputs']) == 10
            assert any(i['reason'] == 'cycle' for i in result['deficits'])
        assert 'Terraria/TerraBlade' in [r['contentKey'] for r in result['steps']]
        assert any(r['contentKey'] == 'Terraria/Meowmere' for r in result['deficits'])
        gel = await plan('Terraria/Gel', 5)
        assert gel['inventoryUsed'][0]['count'] == 3 and gel['deficits'][0]['count'] == 2
        torches = await plan('Terraria/Torch', 8)
        assert not torches['deficits'] and torches['steps'][-1]['batches'] == 3
        assert {r['contentKey']: r['count'] for r in torches['inventoryUsed']} == {
            'Terraria/Gel': 3, 'Terraria/BorealWood': 3}
        await fixture('material_plan')
        two = await plan('Terraria/Zenith', 2)
        used = {i['contentKey']: i['count'] for i in two['inventoryUsed']}
        assert used == {'Terraria/Zenith': 1, 'Terraria/BloodButcherer': 1, 'Terraria/CopperShortsword': 1}, used
        assert two['steps'][-1]['batches'] == 1
        night = next(s for s in two['steps'] if s['contentKey'] == 'Terraria/NightsEdge')
        assert len(night['recipeIds']) > 1 and any(i['contentKey'] == 'Terraria/BloodButcherer' for i in night['inputs'])
        owned = await plan('Terraria/CopperShortsword', 1)
        assert not owned['steps'] and not owned['deficits'] and owned['inventoryUsed'][0]['count'] == 1
        if args.calamity:
            mod = await plan('CalamityMod/DesertMedallion', 2)
            assert mod['steps'][-1]['contentKey'] == mod['contentKey'] and mod['deficits']
    finally:
        bridge.close()


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--port', type=int, required=True)
    parser.add_argument('--profile', type=Path, required=True)
    parser.add_argument('--calamity', action='store_true')
    asyncio.run(verify(parser.parse_args()))
