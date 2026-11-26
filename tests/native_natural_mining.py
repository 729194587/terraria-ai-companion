"""Exercise one mine_target call per unmodified vein in a disposable dedicated world."""
import argparse
import asyncio
import json
from pathlib import Path

from native_landing_regions import Limits, LuaRuntime, ServerAiBridge, wait_file
from agent import read_runtime_inventory


REGRESSION_VEINS = {
    ('1606771215', 'enclosed'): (2092, 484, 13),
    ('20261028', 'change-stance'): (2213, 519, 15),
}


async def read_fixture(path):
    # The server publishes the large motion trace with File.WriteAllText.
    # The directory entry can appear while the JSON body is still being written.
    async with asyncio.timeout(30):
        while True:
            try:
                return await wait_file(path)
            except (PermissionError, json.JSONDecodeError):
                await asyncio.sleep(0.1)


async def run(args):
    profile = args.profile
    assert (profile / 'isolated-natural-world').is_file()
    rows = []
    cases = await read_fixture(profile / 'natural-veins.json')
    if args.cases:
        cases = {case: cases[case] for case in args.cases}
    for case, start in cases.items():
        ready = profile / 'natural-ready.json'
        ready.unlink(missing_ok=True)
        request = profile / 'natural-request.tmp'
        request.write_text(json.dumps(dict(caseId=case, offsetTiles=start['offset'], surfaceY=start['floor'])))
        request.replace(profile / 'natural-request.json')
        fixture = await read_fixture(ready)
        bridge = ServerAiBridge(args.port)
        try:
            scene = bridge.command('query_atomic', operation='observe_scene',
                                   arguments=dict(layers=['resources']))['data']['data']
            ores = [r for r in scene['resources']['items'] if r['kind'] == 'ore_component']
            row = dict(case=case, fixture=fixture, observed=ores, start=start)
            if fixture['Settled']:
                row['before'] = await read_runtime_inventory(bridge)
                source = 'return api("mine_target",{x=%d,y=%d,cluster=true})' % (start['x'], start['y'])

                def capture(name, arguments, receipt):
                    if name == 'mine_target':
                        row['receipt'] = receipt

                runtime = LuaRuntime(bridge, evidence_root=profile / 'mining-lua')
                result = await runtime.execute(source, limits=Limits(seconds=60, calls=2, actions=1),
                                               on_result=capture)
                row.setdefault('receipt', result.get('lastAction'))
                row['after'] = await read_runtime_inventory(bridge)
            # Pin the two regression veins to their surveyed coordinates and seed.
            # Other worlds can use the same case labels with different terrain.
            seed = str(fixture['fixture']['seed'])
            scenario = (seed, case)
            rows.append(row)
            (profile / 'natural-results.json').write_text(json.dumps(dict(scenarios=rows)), encoding='utf8')
            receipt = row.get('receipt') or {}
            print(case, receipt.get('reason', 'unsettled'), flush=True)
            if fixture['Settled']:
                assert receipt.get('inputsReleased') is True and receipt['status'] != 'unknown', receipt
                evidence = receipt['task']['result']['evidence']
                if receipt['status'] == 'completed':
                    assert evidence['discoveryComplete'] and len(evidence['targets']) == start['count']
                    assert all(t['status'] == 'verified' and t['proof']['Strikes'] > 0 for t in evidence['targets'])
            if scenario in REGRESSION_VEINS:
                assert (start['x'], start['y'], start['count']) == REGRESSION_VEINS[scenario]
                assert fixture['Settled'], fixture
                task = receipt['task']
                targets = evidence['targets']
                assert task['observedGameUpdate'] < task['deadlineGameUpdate'], receipt['reason']
                assert evidence['discoveryComplete'] and len(targets) == start['count']
                assert len({(t['x'], t['y']) for t in targets}) == len(targets)
                if case == 'change-stance':
                    assert receipt['status'] == 'completed' and len(targets) == 15
                    assert any(c['taskName'] == 'navigate_to_landing_region' and c['status'] == 'completed'
                               for c in task['progress']['children'])
                elif receipt['status'] != 'completed':
                    assert receipt['status'] in {'timeout', 'failed'}
                    assert receipt['reason'] in {'excavation_work_search_budget_exhausted',
                                                 'excavation_route_search_budget_exhausted',
                                                 'excavation_no_work_position'}
                    assert any(t['status'] == 'pending' for t in targets)
                for target in targets:
                    proof = target['proof']
                    if target['status'] == 'verified':
                        assert (proof['X'], proof['Y'], proof['Type']) == (target['x'], target['y'], target['typeId'])
                        assert proof['Strikes'] > 0
                    elif target['status'] == 'pending':
                        assert proof is None
        finally:
            bridge.close()
        (profile / 'natural-finish').write_text(case)
        await read_fixture(profile / f'natural-motion-{case}.json')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--profile', type=Path, required=True)
    parser.add_argument('--port', type=int, required=True)
    parser.add_argument('--cases', nargs='+')
    asyncio.run(run(parser.parse_args()))
