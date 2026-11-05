"""Observed regions on generated Terraria terrain; no LLM, Tile edits or explore()."""
import argparse
import asyncio
import json
from pathlib import Path
from native_landing_regions import candidates, PROGRAM, wait_file
from lua_runtime import LuaRuntime, Limits
from server_ai import ServerAiBridge


async def verify(args):
    profile = args.profile.resolve()
    assert (profile/'isolated-natural-world').is_file()
    results = []
    for offset in args.offsets:
        for direction in (1, -1):
            case = f"region-{'p' if offset >= 0 else 'm'}{abs(offset)}-{'r' if direction == 1 else 'l'}"
            ready = profile/'natural-ready.json'
            ready.unlink(missing_ok=True)
            temp = profile/'natural-request.tmp'
            temp.write_text(json.dumps(dict(caseId=case, offsetTiles=offset)))
            temp.replace(profile/'natural-request.json')
            fixture = await wait_file(ready)
            row = dict(case=case, offset=offset, direction=direction, fixture=fixture, legs=[])
            bridge = ServerAiBridge(args.port)
            try:
                if fixture['Settled']:
                    for leg in range(1 if args.autonomous else 2):
                        p = await asyncio.to_thread(bridge.command, 'query_atomic', operation='get_player_state', arguments={})
                        state = p['data']['data']['player']
                        view = await asyncio.to_thread(bridge.command, 'query_atomic', operation='observe_terrain', arguments={})
                        x, feet = state['position']['x'], state['position']['y']+42
                        goals = []
                        for tile_y in range(int(feet//16)-4, int(feet//16)+5):
                            if .5 < abs(tile_y*16-feet) <= 64:
                                goals += candidates(view['data'], direction, x, tile_y*16)
                        if not goals:
                            row['stop'] = 'no_observed_flat_region_at_different_height'
                            break
                        lua_goals = '{'+','.join('{'+','.join(f'{k}={v}' for k, v in g.items())+'}' for g in goals)+'}'
                        if args.autonomous:
                            goal = min(goals, key=lambda g: abs(g['minPlayerLeftX'] - x))
                            destination = goal['minPlayerLeftX'] + 10
                            source = f"return api('navigate_to',{{x={destination},y={goal['surfaceY']}}})"
                        else:
                            source = PROGRAM.replace('CANDIDATES', lua_goals)
                        (profile/f'natural-region-{case}-{leg}.lua').write_text(source, encoding='utf-8')
                        r = await LuaRuntime(bridge, evidence_root=profile/'region-lua').execute(
                            source, limits=Limits(seconds=58 if args.autonomous else 35, calls=10, actions=1))
                        a = (r.get('value') or {}).get('action') or r.get('lastAction') or {}
                        e, end = a.get('evidence') or {}, a.get('endEvidence') or {}
                        row['legs'].append(dict(before=state, observation=view, candidates=goals, result=r))
                        assert end.get('inputsReleased') is True, a
                        if a.get('completed') is True:
                            if args.autonomous:
                                assert r['actions'] == 1 and a['valid'], a
                                arrival = e['task']['result']['evidence']
                                assert arrival['x'] <= destination <= arrival['x']+20
                                assert abs(arrival['feetY']-goal['surfaceY']) <= .5
                                break
                            g = a['targetRegion']
                            assert a['valid'] and e['settled'] and e['safeSupport'] and e['grounded']
                            assert g['minPlayerLeftX'] <= e['x'] <= g['maxPlayerLeftX']
                            assert abs(e['y']+42-g['surfaceY']) <= .5
                            assert abs(e['y']+42-feet) > .5
                        else:
                            row['stop'] = a.get('reasonDetail') or a.get('reason') or r['status']
                            break  # Never replay or choose another target after an action failure.
                else:
                    row['stop'] = 'unsupported_natural_start'
            finally:
                bridge.close()
            (profile/'natural-finish').write_text(case)
            await wait_file(profile/f'natural-motion-{case}.json')
            row.setdefault('stop', 'arrived' if args.autonomous else 'two_leg_budget')
            results.append(row)
            (profile/'natural-results.json').write_text(json.dumps(dict(scenarios=results), indent=2))
            print(json.dumps(dict(case=case, legs=len(row['legs']),
                completed=sum((leg['result'].get('lastAction') or {}).get('completed') is True for leg in row['legs']),
                stop=row['stop'])), flush=True)
            if row['legs'] and row['legs'][-1]['result']['actionState'] == 'unknown':
                return False
    if args.autonomous:
        return any((leg['result'].get('lastAction') or {}).get('completed') is True
                   for row in results for leg in row['legs'])
    return True


if __name__ == '__main__':
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--profile', type=Path, required=True)
    p.add_argument('--port', type=int, required=True)
    p.add_argument('--offsets', type=int, nargs='+', default=[0, 32, -32])
    p.add_argument('--autonomous', action='store_true')
    raise SystemExit(0 if asyncio.run(verify(p.parse_args())) else 1)
