"""Isolated full native world frames + generated-style Lua; no LLM or production task script."""
import argparse
import asyncio
import json
from pathlib import Path
import sys
import time
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'python'))
from lua_runtime import LuaRuntime, Limits
from server_ai import ServerAiBridge


async def wait_file(path):
    end = time.monotonic() + 15
    while not path.exists():
        if time.monotonic() >= end:
            raise TimeoutError(str(path))
        await asyncio.sleep(.05)
    return json.loads(path.read_text(encoding='utf-8-sig'))


# The harness projects candidates from the real collision observation, as a model
# can do before generating Lua. Lua sorts/selects an observed target and uses the
# original Worker/dispatcher; it cannot synthesize movement or completion evidence.
PROGRAM = """
local before=api('observe_terrain',{})
local p=api('get_player_state',{})
local candidates=CANDIDATES
table.sort(candidates,function(a,b)
  return math.abs(a.minPlayerLeftX-p.player.position.x)<math.abs(b.minPlayerLeftX-p.player.position.x)
end)
assert(#candidates>0)
local goal=candidates[1]
local action=api('navigate_to_landing_region',goal)
return {before=before,goal=goal,action=action,after=api('get_player_state',{})}
"""


def candidates(view, direction, start_x, surface):
    t = view['data']['terrain']
    rows, ox, oy = t['rows'], t['originX'], t['originY']
    def cell(x, y):
        return rows[y-oy][x-ox] if 0 <= y-oy < len(rows) and 0 <= x-ox < len(rows[y-oy]) else '?'
    result = []
    for x in range(ox+1, ox+len(rows[0])-2):
        if not 90 <= direction*(x*16-start_x) <= 176:
            continue
        y = round(surface/16)
        if all(cell(cx, y) in '#=' for cx in (x, x+1)) and all(
                cell(cx, cy) == '.' for cx in (x, x+1) for cy in range(y-3, y)):
            result.append(dict(minPlayerLeftX=x*16, maxPlayerLeftX=x*16+4, surfaceY=y*16))
    # Merge each contiguous observed run into one region. Keeping one 4px
    # candidate per column accidentally selects the rightmost pin when going left.
    regions = []
    for goal in result:
        if regions and goal['minPlayerLeftX'] == regions[-1]['maxPlayerLeftX']+12:
            regions[-1]['maxPlayerLeftX'] = goal['maxPlayerLeftX']
        else:
            regions.append(goal)
    return regions



async def verify(args):
    profile = args.profile.resolve()
    assert (profile/'isolated-action-fixture').is_file()
    results = []
    scenes = ('flat', 'step_up', 'step_down', 'slope_up', 'slope_down',
              'short_gap', 'low_obstacle', 'platform_up', 'platform_down', 'high_platform')
    for scene in scenes:
        for direction in (1, -1):
            ready = profile/'terrain-ready.json'
            ready.unlink(missing_ok=True)
            temp = profile/'terrain-request.tmp'
            temp.write_text(json.dumps(dict(scene=scene, direction=direction)))
            temp.replace(profile/'terrain-request.json')
            fixture = await wait_file(ready)
            surface = (fixture['floor'] + {'step_up': -1, 'step_down': 1, 'slope_up': -4,
                'slope_down': 4, 'platform_up': -4, 'high_platform': -12}.get(scene, 0))*16
            bridge = ServerAiBridge(args.port)
            try:
                view = await asyncio.to_thread(bridge.command, 'query_atomic', operation='observe_terrain', arguments={})
                goals = candidates(view['data'], direction, fixture['startX'], surface)
                assert goals, (scene, direction, view)
                lua_goals = '{'+','.join('{'+','.join(f'{k}={v}' for k, v in g.items())+'}' for g in goals)+'}'
                source = PROGRAM.replace('CANDIDATES', lua_goals)
                (profile/f'region-program-{scene}-{direction}.lua').write_text(source, encoding='utf-8')
                r = await LuaRuntime(bridge, evidence_root=profile/'region-lua').execute(
                    source, limits=Limits(seconds=35, calls=10, actions=1))
            finally:
                bridge.close()
            action = (r.get('value') or {}).get('action') or r.get('lastAction') or {}
            e, end = action.get('evidence') or {}, action.get('endEvidence') or {}
            expected = scene != 'high_platform'
            goal = action.get('targetRegion') or {}
            passed = (action.get('valid') is True and action.get('completed') is True
                and all(e.get(k) is True for k in ('arrived', 'grounded', 'safeSupport', 'settled'))
                and goal['minPlayerLeftX'] <= e['x'] <= goal['maxPlayerLeftX']
                and abs(e['y']+42-goal['surfaceY']) <= .5) if expected else (
                action.get('valid') is True and action.get('status') == 'failed' and e.get('settled') is True)
            passed = passed and end.get('inputsReleased') is True and action.get('endEvidenceStatus') == 'known'
            trace = await wait_file(profile/f'terrain-motion-{scene}-{direction}.json')
            row = dict(scene=scene, direction=direction, expectedPass=expected, passed=passed,
                       fixture=fixture, candidates=goals, observation=view, result=r, motion=trace)
            results.append(row)
            (profile/'terrain-result.json').write_text(json.dumps(dict(passed=all(x['passed'] for x in results), scenarios=results), indent=2))
            print(json.dumps(dict(scene=scene, direction=direction, passed=passed, status=action.get('status'),
                                  reason=action.get('reasonDetail'), x=e.get('x'), y=e.get('y'))), flush=True)
            if not passed:
                return False
    return all(x['passed'] for x in results)


if __name__ == '__main__':
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--port', type=int, required=True)
    p.add_argument('--profile', type=Path, required=True)
    raise SystemExit(0 if asyncio.run(verify(p.parse_args())) else 1)
